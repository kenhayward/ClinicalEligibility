Imports System
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

' Shared implementation of the `normalize-umls` maintenance job. For each distinct
' UMLS-unresolved residue concept it asks the normalizer for a canonical term,
' re-looks that term up against the configured UMLS backend, and records the
' outcome (the gateway also UPDATEs every matching public.eligibility row in place
' on a hit). No extraction LLM call. Identical work whether driven by the CLI
' `normalize-umls` command or the web Tools tab.
'
' Scoped: ICriteriaNormalizer / IUmlsClient are scoped (the per-run UMLS cache),
' so callers MUST resolve this job inside a DI scope - exactly as the orchestrator
' is resolved per run.
Public NotInheritable Class UmlsNormalizeJob
    Implements IUmlsNormalizeJob

    Private ReadOnly _gateway As IPostgresGateway
    Private ReadOnly _normalizer As ICriteriaNormalizer
    Private ReadOnly _umlsClient As IUmlsClient
    Private ReadOnly _scorer As UmlsMatchScorer

    Public Sub New(gateway As IPostgresGateway,
                   normalizer As ICriteriaNormalizer,
                   umlsClient As IUmlsClient,
                   scorer As UmlsMatchScorer)
        _gateway = gateway
        _normalizer = normalizer
        _umlsClient = umlsClient
        _scorer = scorer
    End Sub

    Public Function CountRemainingAsync(includeAttempted As Boolean, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IUmlsNormalizeJob.CountRemainingAsync
        Return _gateway.CountConceptsToNormalizeAsync(includeAttempted, cancellationToken)
    End Function

    Public Async Function RunAsync(options As NormalizeUmlsOptions,
                                   progress As IProgress(Of ToolJobSnapshot),
                                   cancellationToken As CancellationToken) As Task(Of NormalizeCounters) _
            Implements IUmlsNormalizeJob.RunAsync

        Dim concepts = Await _gateway.SelectConceptsToNormalizeAsync(
                Math.Max(1, options.Count), options.Force, cancellationToken).ConfigureAwait(False)
        Dim counters As New NormalizeCounters()
        Dim total = concepts.Count
        Dim sw = Stopwatch.StartNew()

        Dim build As Func(Of ToolJobSnapshot) =
            Function() New ToolJobSnapshot(
                ToolJobKind.NormalizeUmls, total, counters.Done + counters.Errors, sw.Elapsed,
                New ToolMetric() {
                    New ToolMetric("Resolved", counters.Resolved),
                    New ToolMetric("Not a concept", counters.NoneCount),
                    New ToolMetric("Rows updated", counters.RowsUpdated),
                    New ToolMetric("Errors", counters.Errors)})

        progress?.Report(build())
        If total = 0 Then Return counters

        Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        Dim progressTask As Task = If(progress Is Nothing,
                                      Task.CompletedTask,
                                      ToolJobProgressPump.PumpAsync(progress, build, progressCts.Token))
        Dim caught As Exception = Nothing

        ' Capture (don't propagate from inside the loop) so the progress reporter is
        ' always stopped and a final snapshot is always emitted. VB.NET cannot Await
        ' in a Finally, so drain + report after the Try, then re-throw.
        Try
            Dim parallelOptions As New ParallelOptions With {
                    .MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
                    .CancellationToken = cancellationToken}
            ' Graceful cancellation: when the token fires, Parallel.ForEachAsync stops
            ' launching NEW concepts and awaits the in-flight ones, then surfaces an
            ' OperationCanceledException. We deliberately pass CancellationToken.None to
            ' the per-concept work (not innerCt) so each in-flight normalization runs to
            ' completion - the run "stops after the current UMLS normalization" rather
            ' than abandoning a half-done concept.
            Await Parallel.ForEachAsync(concepts, parallelOptions,
                    Function(c As ConceptToNormalize, innerCt As CancellationToken) As ValueTask
                        Return New ValueTask(NormalizeOneAsync(c, options.DryRun, counters, CancellationToken.None))
                    End Function).ConfigureAwait(False)
        Catch ex As Exception
            caught = ex
        End Try

        progressCts.Cancel()
        Try
            Await progressTask.ConfigureAwait(False)
        Catch ex As OperationCanceledException
        End Try
        progress?.Report(build())

        If caught IsNot Nothing Then Throw caught
        Return counters
    End Function

    ' One concept end-to-end (normalize -> re-lookup -> record), safe to run
    ' concurrently (counters via Interlocked). A transport failure is counted but
    ' not recorded (so it retries next run); any other per-concept failure is
    ' swallowed/counted so one bad concept can't abort the batch; cancellation
    ' propagates.
    Private Async Function NormalizeOneAsync(c As ConceptToNormalize,
                                             dryRun As Boolean,
                                             counters As NormalizeCounters,
                                             cancellationToken As CancellationToken) As Task
        Try
            Dim norm = Await _normalizer.NormalizeConceptAsync(c.Concept, cancellationToken).ConfigureAwait(False)
            If Not norm.Succeeded Then
                Interlocked.Increment(counters.Errors)
                Return
            End If

            Dim term = If(norm.NormalizedText, "").Trim()
            Dim match = UmlsMatch.Unresolved
            Dim semantic = ""
            If Not IsNoneTerm(term) Then
                Dim candidates = Await _umlsClient.SearchAsync(term, cancellationToken).ConfigureAwait(False)
                match = _scorer.PickBestMatch(term, candidates)
                If match.IsResolved Then
                    Dim semTypes = Await _umlsClient.GetSemanticTypeAssignmentsAsync(match.ConceptCode, cancellationToken).ConfigureAwait(False)
                    ' Sorted by name, matching ResolvedRecord's canonical form -
                    ' this string is cached in umls.concept_normalization and
                    ' would otherwise be a second, differently-ordered rendering
                    ' of the same concept.
                    semantic = If(semTypes Is Nothing OrElse semTypes.Count = 0,
                                  "",
                                  String.Join(", ", semTypes.Select(Function(a) a.Sty).OrderBy(Function(s) s, StringComparer.Ordinal)))
                End If
            End If

            If match.IsResolved Then
                Interlocked.Increment(counters.Resolved)
            ElseIf IsNoneTerm(term) Then
                Interlocked.Increment(counters.NoneCount)
            End If

            If Not dryRun Then
                Dim r = Await _gateway.RecordConceptNormalizationAsync(
                        c.ConceptNorm, term, match, semantic, cancellationToken).ConfigureAwait(False)
                Interlocked.Add(counters.RowsUpdated, r)
            End If

            Interlocked.Increment(counters.Done)
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            Interlocked.Increment(counters.Errors)
        End Try
    End Function

    ' The concept-normalize prompt emits the single word NONE for non-biomedical
    ' phrases; treat that (and an empty answer) as "not a resolvable concept".
    Friend Shared Function IsNoneTerm(term As String) As Boolean
        Dim t = If(term, "").Trim()
        Return t = "" OrElse String.Equals(t, "NONE", StringComparison.OrdinalIgnoreCase)
    End Function
End Class

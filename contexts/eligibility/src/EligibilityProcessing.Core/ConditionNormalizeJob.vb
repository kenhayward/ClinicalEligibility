Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' The `normalize-conditions` maintenance job: seed the dictionary from the
''' corpus, then resolve pending rows highest study_count first.
'''
''' Sequential rather than parallel, unlike embed-studies. Tier 1 is a single
''' indexed lookup and only tier 2 costs a ~30ms search, so the bottleneck is
''' modest and a parallel loop would add contention on one Postgres connection
''' pool for little gain. Ordering by study_count DESC means a cancelled run
''' still leaves the corpus measurably better off.
''' </summary>
Public NotInheritable Class ConditionNormalizeJob
    Implements IConditionNormalizeJob

    ' Sentinel for "no explicit --count": resolve everything pending.
    Private Const AllPending As Integer = Integer.MaxValue

    Private ReadOnly _store As IConditionConceptStore
    Private ReadOnly _normalizer As ConditionNormalizer

    Public Sub New(store As IConditionConceptStore, normalizer As ConditionNormalizer)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        If normalizer Is Nothing Then Throw New ArgumentNullException(NameOf(normalizer))
        _store = store
        _normalizer = normalizer
    End Sub

    Public Function CountRemainingAsync(force As Boolean, cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionNormalizeJob.CountRemainingAsync
        Return _store.CountPendingAsync(force, cancellationToken)
    End Function

    Public Async Function RunAsync(options As NormalizeConditionsOptions,
                                   progress As IProgress(Of ToolJobSnapshot),
                                   cancellationToken As CancellationToken) _
            As Task(Of ConditionCounters) Implements IConditionNormalizeJob.RunAsync

        ' Seed first so newly processed trials' conditions are present, and so
        ' raw_form / study_count reflect the current corpus before we order by them.
        Await _store.SeedFromCorpusAsync(cancellationToken).ConfigureAwait(False)

        Dim limit = If(options.Count > 0, options.Count, AllPending)
        Dim pending = Await _store.GetPendingAsync(limit, options.Force, cancellationToken).ConfigureAwait(False)

        Dim counters As New ConditionCounters()
        Dim total = pending.Count
        Dim sw = Stopwatch.StartNew()

        Dim build As Func(Of ToolJobSnapshot) =
            Function() New ToolJobSnapshot(
                ToolJobKind.NormalizeConditions, total, counters.Done, sw.Elapsed,
                New ToolMetric() {
                    New ToolMetric("Resolved", counters.Resolved),
                    New ToolMetric("Unresolved", counters.Unresolved)})

        progress?.Report(build())
        If total = 0 Then Return counters

        Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        Dim progressTask As Task = If(progress Is Nothing,
                                      Task.CompletedTask,
                                      ToolJobProgressPump.PumpAsync(progress, build, progressCts.Token))
        Dim caught As Exception = Nothing

        ' Capture rather than propagate, so the progress reporter is always
        ' stopped and a final snapshot always emitted. VB cannot Await in Finally.
        Try
            For Each entry In pending
                cancellationToken.ThrowIfCancellationRequested()

                Dim resolution = Await _normalizer.ResolveAsync(entry.RawForm, cancellationToken).ConfigureAwait(False)

                If Not options.DryRun Then
                    Await _store.UpsertAsync(New ConditionConceptEntry With {
                            .ConditionNorm = entry.ConditionNorm,
                            .RawForm = entry.RawForm,
                            .StudyCount = entry.StudyCount,
                            .ConceptCode = resolution.ConceptCode,
                            .UmlsName = resolution.UmlsName,
                            .MatchTier = resolution.Tier,
                            .MatchScore = resolution.Score
                        }, cancellationToken).ConfigureAwait(False)
                End If

                If resolution.IsResolved Then
                    counters.Resolved += 1
                Else
                    counters.Unresolved += 1
                End If
                counters.Done += 1
            Next
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

End Class

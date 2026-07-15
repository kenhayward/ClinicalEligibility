Imports System
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

' Shared implementation of the `embed-studies` maintenance job: build the topic
' text for each processed study that has a snapshot but no embedding for the
' configured model, embed it, and upsert the vector. Idempotent (only fills gaps).
' Identical work whether driven by the CLI `embed-studies` command or the web
' Tools tab.
'
' The gateway is a singleton and IEmbeddingClient is transient (HttpClientFactory),
' but this job is registered scoped to match UmlsNormalizeJob and the orchestrator -
' callers resolve both tool jobs the same way (inside a per-run DI scope).
Public NotInheritable Class StudyEmbeddingJob
    Implements IStudyEmbeddingJob

    Private ReadOnly _gateway As IPostgresGateway
    Private ReadOnly _embeddingClient As IEmbeddingClient

    Public Sub New(gateway As IPostgresGateway, embeddingClient As IEmbeddingClient)
        _gateway = gateway
        _embeddingClient = embeddingClient
    End Sub

    Public Function CountRemainingAsync(model As String, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IStudyEmbeddingJob.CountRemainingAsync
        Return _gateway.CountStudiesToEmbedAsync(If(model, ""), cancellationToken)
    End Function

    Public Async Function RunAsync(options As EmbedStudiesOptions,
                                   progress As IProgress(Of ToolJobSnapshot),
                                   cancellationToken As CancellationToken) As Task(Of EmbedCounters) _
            Implements IStudyEmbeddingJob.RunAsync

        Dim model = If(options.Model, "")
        Dim studies = Await _gateway.GetStudiesToEmbedAsync(model, cancellationToken).ConfigureAwait(False)
        Dim counters As New EmbedCounters()
        Dim total = studies.Count
        Dim sw = Stopwatch.StartNew()

        Dim build As Func(Of ToolJobSnapshot) =
            Function() New ToolJobSnapshot(
                ToolJobKind.EmbedStudies, total, counters.Processed + counters.Failed, sw.Elapsed,
                New ToolMetric() {
                    New ToolMetric("Embedded", counters.Processed),
                    New ToolMetric("Failed", counters.Failed)})

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
            ' Graceful cancellation (see UmlsNormalizeJob): stop launching new studies on
            ' cancel but let in-flight embeds finish - pass CancellationToken.None to the
            ' per-study work so a cancel stops after the current item(s) rather than
            ' abandoning a half-done embed.
            Await Parallel.ForEachAsync(studies, parallelOptions,
                    Function(study As StudyEmbeddingInput, innerCt As CancellationToken) As ValueTask
                        Return New ValueTask(EmbedOneAsync(study, model, counters, CancellationToken.None))
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

    ' One study end-to-end (build text -> embed -> upsert), safe to run
    ' concurrently (counters via Interlocked). A per-study failure is counted, not
    ' fatal; cancellation propagates.
    Private Async Function EmbedOneAsync(study As StudyEmbeddingInput,
                                         model As String,
                                         counters As EmbedCounters,
                                         cancellationToken As CancellationToken) As Task
        Try
            Dim text = EmbeddingTextBuilder.Build(study)
            Dim result = Await _embeddingClient.EmbedAsync(text, cancellationToken).ConfigureAwait(False)
            If result.Succeeded Then
                Await _gateway.UpsertStudyEmbeddingAsync(
                        study.NctId, result.Vector, model, text, cancellationToken).ConfigureAwait(False)
                Interlocked.Increment(counters.Processed)
            Else
                Interlocked.Increment(counters.Failed)
            End If
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            Interlocked.Increment(counters.Failed)
        End Try
    End Function
End Class

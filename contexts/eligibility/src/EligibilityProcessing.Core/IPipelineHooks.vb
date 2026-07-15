Imports System.Threading
Imports System.Threading.Tasks

' Observability hooks fired by <see cref="PipelineOrchestrator"/> at each
' notable point of a batch. Architecture section 2.1 lists this as the seam
' the Web SignalR hub uses to push live progress to dashboard clients.
'
' Implementations MUST be cheap and non-throwing — the orchestrator awaits
' each hook call and continues unconditionally (catches and logs any failure
' so a misbehaving sink can't tear down the batch). User cancellation is
' still allowed to surface as OperationCanceledException.
'
' Order guarantee per run:
'   1. OnBatchStartedAsync     (exactly once at start, before any trial work)
'   2. OnTrialStartedAsync     (once per trial, interleaved across trials in parallel)
'   3. OnTrialCompletedAsync   (once per trial, mirrors OnTrialStartedAsync)
'   4. OnBatchCompletedAsync   (exactly once at end, after all trials finished)
' OR (terminal alternative to 4):
'   4'. OnBatchCancelledAsync  (exactly once when the run was cancelled by the
'                               user via the dashboard's Cancel button — fired
'                               from BatchRunner, not the orchestrator).
'
' Steps 2-3 are NOT serialised across trials — multiple OnTrialStartedAsync
' calls may fan out concurrently because the orchestrator runs trials in
' parallel. Implementations must therefore be thread-safe.

Public Interface IPipelineHooks

    Function OnBatchStartedAsync(
            runId As Guid,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task

    Function OnTrialStartedAsync(
            runId As Guid,
            nctId As String,
            cancellationToken As CancellationToken) As Task

    Function OnTrialCompletedAsync(
            runId As Guid,
            nctId As String,
            rowCount As Integer,
            succeeded As Boolean,
            cancellationToken As CancellationToken) As Task

    Function OnBatchCompletedAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task

    Function OnBatchCancelledAsync(
            runId As Guid,
            cancellationToken As CancellationToken) As Task

End Interface

''' <summary>
''' No-op default. Used when no UI cares about live progress (CLI, Webhook).
''' </summary>
Public NotInheritable Class NullPipelineHooks
    Implements IPipelineHooks

    Public Shared ReadOnly Instance As IPipelineHooks = New NullPipelineHooks()

    Public Sub New()
    End Sub

    Public Function OnBatchStartedAsync(
            runId As Guid,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnBatchStartedAsync
        Return Task.CompletedTask
    End Function

    Public Function OnTrialStartedAsync(
            runId As Guid,
            nctId As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnTrialStartedAsync
        Return Task.CompletedTask
    End Function

    Public Function OnTrialCompletedAsync(
            runId As Guid,
            nctId As String,
            rowCount As Integer,
            succeeded As Boolean,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnTrialCompletedAsync
        Return Task.CompletedTask
    End Function

    Public Function OnBatchCompletedAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnBatchCompletedAsync
        Return Task.CompletedTask
    End Function

    Public Function OnBatchCancelledAsync(
            runId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnBatchCancelledAsync
        Return Task.CompletedTask
    End Function

End Class

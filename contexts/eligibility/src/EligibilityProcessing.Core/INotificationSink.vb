Imports System.Threading
Imports System.Threading.Tasks

' Channel-agnostic notification fan-out target.
'
' Spec section 2.10. Concrete sinks (SMTP, Slack, generic webhook) live in
' EligibilityProcessing.Notifications. The orchestrator enforces the
' once-per-batch contract for both methods (spec section 2.10); the sink
' itself just delivers whatever it is handed.

Public Interface INotificationSink

    ''' <summary>
    ''' Emitted exactly once per successful batch with the full metrics payload
    ''' (spec section 2.10.1).
    ''' </summary>
    Function SendCompletionAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Emitted exactly once per batch when any trial failed terminally
    ''' (spec section 2.10.2). The reference implementation collapses all
    ''' failures into one alert; result.FailedNctIds carries the list for
    ''' implementations that want structured detail.
    ''' </summary>
    Function SendErrorAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task

End Interface

''' <summary>
''' No-op INotificationSink. Used by default when the orchestrator is wired
''' without an explicit sink (tests, dev, or environments where notifications
''' are disabled).
''' </summary>
Public NotInheritable Class NullNotificationSink
    Implements INotificationSink

    Public Shared ReadOnly Instance As INotificationSink = New NullNotificationSink()

    Private Sub New()
    End Sub

    Public Function SendCompletionAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements INotificationSink.SendCompletionAsync
        Return Task.CompletedTask
    End Function

    Public Function SendErrorAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements INotificationSink.SendErrorAsync
        Return Task.CompletedTask
    End Function

End Class

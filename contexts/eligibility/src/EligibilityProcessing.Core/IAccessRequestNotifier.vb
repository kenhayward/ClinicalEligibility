Imports System.Threading
Imports System.Threading.Tasks

' Notifies administrators that an unrecognised user tried to sign in (e.g. a
' Google account whose email is not in app_user). Reuses the same delivery
' channel as the pipeline's run notifications; the concrete SMTP implementation
' lives in EligibilityProcessing.Notifications. Sends are best-effort — a mail
' failure must not change the login-denied UX.

Public Interface IAccessRequestNotifier

    ''' <summary>
    ''' Sends an "access requested" alert to the configured admin recipients
    ''' naming the would-be user. Implementations swallow delivery failures.
    ''' </summary>
    Function SendAccessRequestAsync(
            name As String,
            email As String,
            cancellationToken As CancellationToken) As Task

End Interface

''' <summary>No-op notifier — used when SMTP is not configured.</summary>
Public NotInheritable Class NullAccessRequestNotifier
    Implements IAccessRequestNotifier

    Public Shared ReadOnly Instance As IAccessRequestNotifier = New NullAccessRequestNotifier()

    Private Sub New()
    End Sub

    Public Function SendAccessRequestAsync(
            name As String,
            email As String,
            cancellationToken As CancellationToken) As Task _
            Implements IAccessRequestNotifier.SendAccessRequestAsync
        Return Task.CompletedTask
    End Function

End Class

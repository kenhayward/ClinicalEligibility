Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports MimeKit

' SMTP <see cref="IAccessRequestNotifier"/>. Emails the configured admin
' recipients (Notifications:Smtp:ToAddresses) when an unrecognised user attempts
' to sign in, so an administrator can create their account. Reuses the existing
' ISmtpEmailSender transport. Send failures are swallowed and logged — the
' login-denied response must succeed regardless of mail health.

Public NotInheritable Class SmtpAccessRequestNotifier
    Implements IAccessRequestNotifier

    Private ReadOnly _sender As ISmtpEmailSender
    Private ReadOnly _options As SmtpNotificationOptions
    Private ReadOnly _logger As ILogger(Of SmtpAccessRequestNotifier)

    Public Sub New(
            sender As ISmtpEmailSender,
            options As IOptions(Of SmtpNotificationOptions),
            Optional logger As ILogger(Of SmtpAccessRequestNotifier) = Nothing)
        If sender Is Nothing Then Throw New ArgumentNullException(NameOf(sender))
        If options Is Nothing Then Throw New ArgumentNullException(NameOf(options))
        _sender = sender
        _options = options.Value
        _logger = If(logger, CType(NullLogger(Of SmtpAccessRequestNotifier).Instance, ILogger(Of SmtpAccessRequestNotifier)))
    End Sub

    Public Async Function SendAccessRequestAsync(
            name As String,
            email As String,
            cancellationToken As CancellationToken) As Task _
            Implements IAccessRequestNotifier.SendAccessRequestAsync
        Dim message = BuildMessage(name, email)
        Try
            Await _sender.SendAsync(message, cancellationToken).ConfigureAwait(False)
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex, "Failed to send access-request email for {Email}", email)
        End Try
    End Function

    Friend Function BuildMessage(name As String, email As String) As MimeMessage
        Dim message As New MimeMessage()
        message.From.Add(New MailboxAddress(_options.FromName, _options.FromAddress))
        For Each address In SmtpNotificationSink.SplitAddresses(_options.ToAddresses)
            message.To.Add(MailboxAddress.Parse(address))
        Next
        Dim displayName = If(String.IsNullOrWhiteSpace(name), "(unknown)", name.Trim())
        message.Subject = $"[Eligibility] Access requested by {email}"
        message.Body = New TextPart("plain") With {
                .Text =
                    $"A user attempted to sign in but has no account." & Environment.NewLine &
                    Environment.NewLine &
                    $"Name:   {displayName}" & Environment.NewLine &
                    $"Email:  {email}" & Environment.NewLine &
                    Environment.NewLine &
                    "Create an account for them from Manage Accounts if access should be granted."}
        Return message
    End Function

End Class

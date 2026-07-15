Imports System.Threading
Imports System.Threading.Tasks
Imports MailKit.Net.Smtp
Imports MailKit.Security
Imports Microsoft.Extensions.Options
Imports MimeKit

' Production SMTP sender using MailKit. One connection per send — simple,
' correct, and fast enough for one notification per batch (spec section 2.10
' mandates once-per-batch emission, so we are not in a hot path).

Public NotInheritable Class MailKitSmtpEmailSender
    Implements ISmtpEmailSender

    Private ReadOnly _options As SmtpNotificationOptions

    Public Sub New(options As IOptions(Of SmtpNotificationOptions))
        _options = options.Value
    End Sub

    Public Async Function SendAsync(
            message As MimeMessage,
            cancellationToken As CancellationToken) As Task _
            Implements ISmtpEmailSender.SendAsync

        Using client As New SmtpClient()
            Dim secureOptions = If(_options.UseStartTls,
                                    SecureSocketOptions.StartTls,
                                    SecureSocketOptions.Auto)
            Await client.ConnectAsync(_options.Host, _options.Port, secureOptions, cancellationToken).ConfigureAwait(False)
            If Not String.IsNullOrEmpty(_options.Username) Then
                Await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken).ConfigureAwait(False)
            End If
            Await client.SendAsync(message, cancellationToken).ConfigureAwait(False)
            Await client.DisconnectAsync(quit:=True, cancellationToken).ConfigureAwait(False)
        End Using
    End Function

End Class

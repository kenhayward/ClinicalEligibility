Imports System.Threading
Imports System.Threading.Tasks
Imports MimeKit

' Transport seam for the SMTP sink. Lets tests substitute a fake that just
' captures MimeMessages, so the sink's message-building logic can be verified
' without standing up a real SMTP server.

Public Interface ISmtpEmailSender

    Function SendAsync(message As MimeMessage, cancellationToken As CancellationToken) As Task

End Interface

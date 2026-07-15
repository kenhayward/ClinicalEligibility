' Configuration for the SMTP notification sink. Architecture section 2.5;
' spec section 6.5 says credentials MUST come from secret storage.
'
' When <see cref="Host"/> is empty/whitespace, Hosting registers
' <see cref="EligibilityProcessing.Core.NullNotificationSink"/> instead so
' the pipeline runs without trying to talk to an unconfigured SMTP server.

Public Class SmtpNotificationOptions

    Public Property Host As String = ""
    Public Property Port As Integer = 587

    ''' <summary>
    ''' STARTTLS upgrade after connect (the standard for submission port 587).
    ''' For port 465 with implicit TLS, set this False and let MailKit pick
    ''' SecureSocketOptions.Auto.
    ''' </summary>
    Public Property UseStartTls As Boolean = True

    Public Property Username As String = ""
    Public Property Password As String = ""

    Public Property FromAddress As String = ""
    Public Property FromName As String = "Eligibility Pipeline"

    ''' <summary>Comma-separated list of recipient email addresses.</summary>
    Public Property ToAddresses As String = ""

    ''' <summary>Optional retrigger URL embedded in completion mails (spec section 2.10.1).</summary>
    Public Property RetriggerUrl As String = ""

End Class

Imports System.Globalization
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports MimeKit

' SMTP <see cref="INotificationSink"/>. Sends one email per batch on success
' (spec section 2.10.1) and one email per batch on terminal failure
' (spec section 2.10.2). Message-building (BuildCompletionMessage / BuildErrorMessage)
' is Friend so the Integration.Tests project can verify subject + body without
' standing up a real SMTP server.
'
' Send failures are swallowed and logged — a misbehaving mail server must not
' tear down a successful batch. Spec section 6.4 mandates this graceful
' tolerance for backend hiccups.

Public NotInheritable Class SmtpNotificationSink
    Implements INotificationSink

    Private ReadOnly _sender As ISmtpEmailSender
    Private ReadOnly _options As SmtpNotificationOptions
    Private ReadOnly _logger As ILogger(Of SmtpNotificationSink)

    Public Sub New(
            sender As ISmtpEmailSender,
            options As IOptions(Of SmtpNotificationOptions),
            Optional logger As ILogger(Of SmtpNotificationSink) = Nothing)
        If sender Is Nothing Then Throw New ArgumentNullException(NameOf(sender))
        If options Is Nothing Then Throw New ArgumentNullException(NameOf(options))
        _sender = sender
        _options = options.Value
        _logger = If(logger, CType(NullLogger(Of SmtpNotificationSink).Instance, ILogger(Of SmtpNotificationSink)))
    End Sub

    Public Async Function SendCompletionAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements INotificationSink.SendCompletionAsync
        Dim message = BuildCompletionMessage(result)
        Await SendOrSwallowAsync(message, "completion", result.Metrics.RunId, cancellationToken).ConfigureAwait(False)
    End Function

    Public Async Function SendErrorAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements INotificationSink.SendErrorAsync
        Dim message = BuildErrorMessage(result)
        Await SendOrSwallowAsync(message, "error", result.Metrics.RunId, cancellationToken).ConfigureAwait(False)
    End Function

    Private Async Function SendOrSwallowAsync(
            message As MimeMessage,
            category As String,
            runId As Guid,
            cancellationToken As CancellationToken) As Task
        Dim sendEx As Exception = Nothing
        Try
            Await _sender.SendAsync(message, cancellationToken).ConfigureAwait(False)
            Return
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            sendEx = ex
        End Try
        _logger.LogWarning(sendEx,
                "Failed to send {Category} email for run {RunId}", category, runId)
    End Function

    ' ============ message-building (Friend for tests) ============

    Friend Function BuildCompletionMessage(result As BatchResult) As MimeMessage
        Dim m = result.Metrics
        Dim message = StartMessage()
        message.Subject = $"[Eligibility] Run {m.Status} - {m.StudiesProcessed} studies, {m.RowsPersisted} rows"
        message.Body = New TextPart("plain") With {.Text = BuildCompletionBody(result)}
        Return message
    End Function

    Friend Function BuildErrorMessage(result As BatchResult) As MimeMessage
        Dim m = result.Metrics
        Dim message = StartMessage()
        Dim failedCount = result.FailedNctIds.Count
        message.Subject = $"[Eligibility] Run {m.Status} - {failedCount} failed trial(s)"
        message.Body = New TextPart("plain") With {.Text = BuildErrorBody(result)}
        Return message
    End Function

    Private Function StartMessage() As MimeMessage
        Dim message As New MimeMessage()
        message.From.Add(New MailboxAddress(_options.FromName, _options.FromAddress))
        For Each address In SplitAddresses(_options.ToAddresses)
            message.To.Add(MailboxAddress.Parse(address))
        Next
        Return message
    End Function

    Friend Shared Iterator Function SplitAddresses(raw As String) As IEnumerable(Of String)
        If String.IsNullOrWhiteSpace(raw) Then Return
        For Each piece In raw.Split(","c)
            Dim trimmed = piece.Trim()
            If trimmed.Length > 0 Then Yield trimmed
        Next
    End Function

    ' ============ body rendering (spec section 2.10.1) ============

    Friend Function BuildCompletionBody(result As BatchResult) As String
        Dim m = result.Metrics
        Dim sb As New StringBuilder()
        sb.AppendLine($"Status:                {m.Status}")
        sb.AppendLine($"Studies processed:     {m.StudiesProcessed} / {m.StudyCount}")
        sb.AppendLine($"Total criteria rows:   {m.RowsPersisted}")
        sb.AppendLine($"Avg criteria / study:  {FormatAvgCriteriaPerStudy(m)}")
        sb.AppendLine($"Resolution rate:       {(m.ResolutionRate * 100).ToString("F1", CultureInfo.InvariantCulture)}%")
        sb.AppendLine($"Workflow runtime:      {FormatRuntime(m)}")
        sb.AppendLine($"Avg runtime / study:   {FormatAvgRuntimePerStudy(m)}")
        sb.AppendLine($"Trigger:               {m.TriggerSource}")
        sb.AppendLine($"Run ID:                {m.RunId}")
        sb.AppendLine($"Started:               {m.StartedAt:yyyy-MM-dd HH:mm:ss 'UTC'}")
        If result.FailedNctIds.Count > 0 Then
            sb.AppendLine()
            sb.AppendLine($"Failed trials ({result.FailedNctIds.Count}):")
            For Each nctId In result.FailedNctIds
                sb.AppendLine($"  - {nctId}")
            Next
        End If
        If Not String.IsNullOrEmpty(_options.RetriggerUrl) Then
            sb.AppendLine()
            sb.AppendLine($"Re-trigger: {_options.RetriggerUrl}")
        End If
        Return sb.ToString()
    End Function

    Friend Function BuildErrorBody(result As BatchResult) As String
        Dim m = result.Metrics
        Dim sb As New StringBuilder()
        sb.AppendLine($"Status:           {m.Status}")
        sb.AppendLine($"Run ID:           {m.RunId}")
        sb.AppendLine($"Failed trials:    {result.FailedNctIds.Count}")
        If Not String.IsNullOrEmpty(m.ErrorSummary) Then
            sb.AppendLine()
            sb.AppendLine("Error summary:")
            sb.AppendLine(m.ErrorSummary)
        End If
        If result.FailedNctIds.Count > 0 Then
            sb.AppendLine()
            sb.AppendLine("Failed NCT IDs:")
            For Each nctId In result.FailedNctIds
                sb.AppendLine($"  - {nctId}")
            Next
        End If
        Return sb.ToString()
    End Function

    Friend Shared Function FormatAvgCriteriaPerStudy(m As RunMetrics) As String
        If m.StudiesProcessed <= 0 Then Return "0.0"
        Dim avg = CDbl(m.RowsPersisted) / CDbl(m.StudiesProcessed)
        Return avg.ToString("F1", CultureInfo.InvariantCulture)
    End Function

    Friend Shared Function FormatRuntime(m As RunMetrics) As String
        If Not m.EndedAt.HasValue Then Return "in progress"
        Dim duration = m.EndedAt.Value - m.StartedAt
        Dim totalSeconds = duration.TotalSeconds
        If totalSeconds < 60 Then
            Return $"{totalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s"
        End If
        Dim minutes = CInt(Math.Floor(totalSeconds / 60))
        Dim seconds = CInt(Math.Round(totalSeconds - minutes * 60))
        Return $"{minutes}m {seconds}s"
    End Function

    Friend Shared Function FormatAvgRuntimePerStudy(m As RunMetrics) As String
        If Not m.EndedAt.HasValue OrElse m.StudiesProcessed <= 0 Then Return "0.0s"
        Dim duration = m.EndedAt.Value - m.StartedAt
        Dim avg = duration.TotalSeconds / CDbl(m.StudiesProcessed)
        Return $"{avg.ToString("F1", CultureInfo.InvariantCulture)}s"
    End Function

End Class

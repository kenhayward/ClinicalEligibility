Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging

' Spec section 6.5 / architecture section 6: redact the UMLS apiKey from any URL
' before it reaches the log sink. UMLS passes the key as a query parameter, so
' a default request log line would otherwise leak the secret.
'
' Wired into the "umls" named HttpClient in the DI composition root.

Public NotInheritable Class UmlsLogRedactionHandler
    Inherits DelegatingHandler

    Private Shared ReadOnly ApiKeyPattern As New Regex(
            "(?<=[?&]apiKey=)[^&]*",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

    Private ReadOnly _logger As ILogger(Of UmlsLogRedactionHandler)

    Public Sub New(logger As ILogger(Of UmlsLogRedactionHandler))
        _logger = logger
    End Sub

    Protected Overrides Async Function SendAsync(
            request As HttpRequestMessage,
            cancellationToken As CancellationToken) As Task(Of HttpResponseMessage)

        Dim safeUri = Redact(request.RequestUri)
        _logger.LogDebug("UMLS request: {Method} {Uri}", request.Method, safeUri)
        Try
            Dim response = Await MyBase.SendAsync(request, cancellationToken).ConfigureAwait(False)
            _logger.LogDebug("UMLS response: {Status} {Uri}", CInt(response.StatusCode), safeUri)
            Return response
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogDebug(ex, "UMLS request failed: {Uri}", safeUri)
            Throw
        End Try
    End Function

    ''' <summary>
    ''' Returns a safe-for-logging version of <paramref name="uri"/> with the
    ''' apiKey query parameter value replaced by "***". Idempotent and safe to
    ''' call with Nothing.
    ''' </summary>
    Friend Shared Function Redact(uri As Uri) As String
        If uri Is Nothing Then Return ""
        Return ApiKeyPattern.Replace(uri.ToString(), "***")
    End Function

End Class

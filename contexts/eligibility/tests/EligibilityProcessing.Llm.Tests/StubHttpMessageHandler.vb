Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks

' Test double for HttpMessageHandler. Captures the outgoing request (URI,
' headers, body) and returns the configured response or throws the configured
' exception.

Friend NotInheritable Class StubHttpMessageHandler
    Inherits HttpMessageHandler

    Public Property CapturedRequest As HttpRequestMessage
    Public Property CapturedBody As String
    Public Property ResponseToReturn As HttpResponseMessage
    Public Property ExceptionToThrow As Exception
    Public Property CallCount As Integer

    Public Shared Function WithJson(json As String, Optional status As HttpStatusCode = HttpStatusCode.OK) As StubHttpMessageHandler
        Return New StubHttpMessageHandler With {
            .ResponseToReturn = New HttpResponseMessage(status) With {
                .Content = New StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }
        }
    End Function

    Public Shared Function WithStatus(status As HttpStatusCode, Optional body As String = "") As StubHttpMessageHandler
        Dim response = New HttpResponseMessage(status)
        If Not String.IsNullOrEmpty(body) Then
            response.Content = New StringContent(body, System.Text.Encoding.UTF8, "application/json")
        End If
        Return New StubHttpMessageHandler With {.ResponseToReturn = response}
    End Function

    Public Shared Function ThatThrows(exception As Exception) As StubHttpMessageHandler
        Return New StubHttpMessageHandler With {.ExceptionToThrow = exception}
    End Function

    Protected Overrides Async Function SendAsync(
            request As HttpRequestMessage,
            cancellationToken As CancellationToken) As Task(Of HttpResponseMessage)
        CallCount += 1
        CapturedRequest = request
        If request.Content IsNot Nothing Then
            CapturedBody = Await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
        End If
        cancellationToken.ThrowIfCancellationRequested()
        If ExceptionToThrow IsNot Nothing Then Throw ExceptionToThrow
        Return ResponseToReturn
    End Function

End Class

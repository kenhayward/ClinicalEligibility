Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks

' Test double for HttpMessageHandler. Records the request, then returns
' the configured response or throws the configured exception.

Friend NotInheritable Class StubHttpMessageHandler
    Inherits HttpMessageHandler

    Public Property CapturedRequest As HttpRequestMessage
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

    Public Shared Function WithStatus(status As HttpStatusCode) As StubHttpMessageHandler
        Return New StubHttpMessageHandler With {
            .ResponseToReturn = New HttpResponseMessage(status)
        }
    End Function

    Public Shared Function ThatThrows(exception As Exception) As StubHttpMessageHandler
        Return New StubHttpMessageHandler With {.ExceptionToThrow = exception}
    End Function

    Protected Overrides Async Function SendAsync(
            request As HttpRequestMessage,
            cancellationToken As CancellationToken) As Task(Of HttpResponseMessage)
        CallCount += 1
        CapturedRequest = request
        cancellationToken.ThrowIfCancellationRequested()
        If ExceptionToThrow IsNot Nothing Then Throw ExceptionToThrow
        Return Await Task.FromResult(ResponseToReturn).ConfigureAwait(False)
    End Function

End Class

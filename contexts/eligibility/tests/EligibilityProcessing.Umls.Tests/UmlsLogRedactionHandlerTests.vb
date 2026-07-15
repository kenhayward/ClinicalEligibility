Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Umls
Imports Microsoft.Extensions.Logging.Abstractions
Imports Xunit

Public Class UmlsLogRedactionHandlerTests

    ' ============ Redact (static) ============

    <Fact>
    Public Sub Redact_replaces_apikey_value_with_stars()
        Dim uri As New Uri("https://uts-ws.nlm.nih.gov/rest/search/current?string=diabetes&apiKey=secret-1234-abcd")
        Assert.Equal(
            "https://uts-ws.nlm.nih.gov/rest/search/current?string=diabetes&apiKey=***",
            UmlsLogRedactionHandler.Redact(uri))
    End Sub

    <Fact>
    Public Sub Redact_preserves_other_query_params()
        Dim uri As New Uri("https://uts-ws.nlm.nih.gov/rest/search/current?string=diabetes&searchType=words&pageSize=5&apiKey=secret&returnIdType=concept")
        Dim result = UmlsLogRedactionHandler.Redact(uri)
        Assert.Contains("string=diabetes", result)
        Assert.Contains("searchType=words", result)
        Assert.Contains("pageSize=5", result)
        Assert.Contains("returnIdType=concept", result)
        Assert.Contains("apiKey=***", result)
        Assert.DoesNotContain("secret", result)
    End Sub

    <Fact>
    Public Sub Redact_redacts_when_apikey_is_first_param()
        Dim uri As New Uri("https://uts-ws.nlm.nih.gov/rest/content/current/CUI/C0011860?apiKey=secret&foo=bar")
        Dim result = UmlsLogRedactionHandler.Redact(uri)
        Assert.Contains("apiKey=***", result)
        Assert.DoesNotContain("secret", result)
    End Sub

    <Fact>
    Public Sub Redact_redacts_when_apikey_is_only_param()
        Dim uri As New Uri("https://uts-ws.nlm.nih.gov/rest/content/current/CUI/C0011860?apiKey=secret")
        Assert.Equal(
            "https://uts-ws.nlm.nih.gov/rest/content/current/CUI/C0011860?apiKey=***",
            UmlsLogRedactionHandler.Redact(uri))
    End Sub

    <Fact>
    Public Sub Redact_is_case_insensitive_on_param_name()
        Dim uri As New Uri("https://example.com/api?ApiKey=secret")
        Assert.Contains("***", UmlsLogRedactionHandler.Redact(uri))
        Assert.DoesNotContain("secret", UmlsLogRedactionHandler.Redact(uri))
    End Sub

    <Fact>
    Public Sub Redact_is_noop_when_no_apikey_present()
        Dim uri As New Uri("https://uts-ws.nlm.nih.gov/rest/search/current?string=diabetes&pageSize=5")
        Assert.Equal(uri.ToString(), UmlsLogRedactionHandler.Redact(uri))
    End Sub

    <Fact>
    Public Sub Redact_returns_empty_for_null_uri()
        Assert.Equal("", UmlsLogRedactionHandler.Redact(Nothing))
    End Sub

    <Fact>
    Public Sub Redact_does_not_truncate_other_params_after_apikey()
        Dim uri As New Uri("https://example.com/api?apiKey=secret&follow=true&page=2")
        Dim result = UmlsLogRedactionHandler.Redact(uri)
        Assert.Contains("apiKey=***", result)
        Assert.Contains("follow=true", result)
        Assert.Contains("page=2", result)
    End Sub

    ' ============ SendAsync forwards through the pipeline ============

    <Fact>
    Public Async Function SendAsync_passes_request_to_inner_handler() As Task
        Dim inner As New StubHttpMessageHandler With {
            .ResponseToReturn = New HttpResponseMessage(HttpStatusCode.OK)
        }
        Dim handler As New UmlsLogRedactionHandler(NullLogger(Of UmlsLogRedactionHandler).Instance) With {
            .InnerHandler = inner
        }
        Dim invoker As New HttpMessageInvoker(handler)
        Dim request As New HttpRequestMessage(HttpMethod.Get, "https://example.com/?apiKey=secret")

        Dim response = Await invoker.SendAsync(request, CancellationToken.None)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        Assert.Equal(1, inner.CallCount)
        ' The handler does NOT mutate the outgoing request — the real apiKey
        ' must still reach UMLS. Only the log output is redacted.
        Assert.Contains("apiKey=secret", inner.CapturedRequest.RequestUri.ToString())
    End Function

End Class

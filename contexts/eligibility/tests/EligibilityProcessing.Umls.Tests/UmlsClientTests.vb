Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Umls
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports Xunit

Public Class UmlsClientTests

    Private Const ApiKey As String = "test-api-key-1234"

    ' ============ Search: input gate ============

    <Theory>
    <InlineData("")>
    <InlineData("   ")>
    Public Async Function Search_returns_empty_for_empty_concept(concept As String) As Task
        Dim handler = StubHttpMessageHandler.WithJson("{}")
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync(concept, CancellationToken.None)
        Assert.Empty(result)
        Assert.Equal(0, handler.CallCount)  ' no HTTP call should be made
    End Function

    <Fact>
    Public Async Function Search_returns_empty_for_null_concept() As Task
        Dim handler = StubHttpMessageHandler.WithJson("{}")
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync(Nothing, CancellationToken.None)
        Assert.Empty(result)
        Assert.Equal(0, handler.CallCount)
    End Function

    ' ============ Search: URL construction per spec section 2.6.1 ============

    <Fact>
    Public Async Function Search_url_uses_canonical_query_params() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmptyResultsJson)
        Dim client = MakeClient(handler)
        Await client.SearchAsync("diabetes", CancellationToken.None)

        Dim uri = handler.CapturedRequest.RequestUri
        Assert.Equal("/rest/search/current", uri.AbsolutePath)
        Dim qs = ParseQuery(uri)
        Assert.Equal("diabetes", qs("string"))
        Assert.Equal("words", qs("searchType"))
        Assert.Equal("concept", qs("returnIdType"))
        Assert.Equal("5", qs("pageSize"))
        Assert.Equal(ApiKey, qs("apiKey"))
    End Function

    <Fact>
    Public Async Function Search_url_encodes_special_chars_in_concept() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmptyResultsJson)
        Dim client = MakeClient(handler)
        Await client.SearchAsync("type II diabetes (mellitus)", CancellationToken.None)

        Dim qs = ParseQuery(handler.CapturedRequest.RequestUri)
        Assert.Equal("type II diabetes (mellitus)", qs("string"))
    End Function

    <Fact>
    Public Async Function Search_url_uses_configured_page_size() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmptyResultsJson)
        Dim client = MakeClient(handler, opts:=New UmlsOptions With {.ApiKey = ApiKey, .PageSize = 10})
        Await client.SearchAsync("diabetes", CancellationToken.None)

        Assert.Equal("10", ParseQuery(handler.CapturedRequest.RequestUri)("pageSize"))
    End Function

    ' ============ Search: response parsing ============

    <Fact>
    Public Async Function Search_parses_results_array_into_candidates() As Task
        Const ResponseJson As String = "{
  ""result"": {
    ""results"": [
      { ""ui"": ""C0011860"", ""rootSource"": ""MSH"", ""name"": ""Diabetes Mellitus, Non-Insulin-Dependent"" },
      { ""ui"": ""C0011854"", ""rootSource"": ""SNOMEDCT_US"", ""name"": ""Diabetes Mellitus, Insulin-Dependent"" }
    ]
  }
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)

        Assert.Equal(2, result.Count)
        Assert.Equal("C0011860", result(0).Ui)
        Assert.Equal("Diabetes Mellitus, Non-Insulin-Dependent", result(0).Name)
        Assert.Equal("MSH", result(0).RootSource)
        Assert.Equal("C0011854", result(1).Ui)
        Assert.Equal("SNOMEDCT_US", result(1).RootSource)
    End Function

    <Fact>
    Public Async Function Search_filters_NONE_sentinel_returned_when_no_results() As Task
        ' UMLS quirk: when nothing matches, the API returns one row with ui="NONE", name="NO RESULTS".
        Const ResponseJson As String = "{
  ""result"": {
    ""results"": [
      { ""ui"": ""NONE"", ""rootSource"": """", ""name"": ""NO RESULTS"" }
    ]
  }
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("nonsense", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function Search_returns_empty_when_results_key_missing() As Task
        Const ResponseJson As String = "{ ""result"": {} }"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function Search_returns_empty_when_result_key_missing() As Task
        Dim handler = StubHttpMessageHandler.WithJson("{}")
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)
        Assert.Empty(result)
    End Function

    ' ============ Search: error swallowing (spec section 2.6.1) ============

    <Fact>
    Public Async Function Search_returns_empty_on_500_response() As Task
        Dim handler = StubHttpMessageHandler.WithStatus(HttpStatusCode.InternalServerError)
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function Search_returns_empty_on_404_response() As Task
        Dim handler = StubHttpMessageHandler.WithStatus(HttpStatusCode.NotFound)
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function Search_returns_empty_on_network_exception() As Task
        Dim handler = StubHttpMessageHandler.ThatThrows(New HttpRequestException("connection refused"))
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function Search_returns_empty_on_malformed_json() As Task
        Dim handler = StubHttpMessageHandler.WithJson("not valid json {")
        Dim client = MakeClient(handler)
        Dim result = Await client.SearchAsync("diabetes", CancellationToken.None)
        Assert.Empty(result)
    End Function

    ' ============ Search: cancellation propagates (does not swallow) ============

    <Fact>
    Public Async Function Search_propagates_user_cancellation() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmptyResultsJson)
        Dim client = MakeClient(handler)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() client.SearchAsync("diabetes", cts.Token))
        End Using
    End Function

    ' ============ GetSemanticTypes: input gate ============

    <Theory>
    <InlineData("")>
    <InlineData("   ")>
    Public Async Function SemanticTypes_returns_empty_for_empty_cui(cui As String) As Task
        Dim handler = StubHttpMessageHandler.WithJson("{}")
        Dim client = MakeClient(handler)
        Dim result = Await client.GetSemanticTypeAssignmentsAsync(cui, CancellationToken.None)
        Assert.Empty(result)
        Assert.Equal(0, handler.CallCount)
    End Function

    ' ============ GetSemanticTypes: URL construction (spec section 2.6.3) ============

    <Fact>
    Public Async Function SemanticTypes_url_uses_cui_in_path_and_apikey_in_query() As Task
        Const ResponseJson As String = "{ ""result"": { ""semanticTypes"": [] } }"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Await client.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)

        Dim uri = handler.CapturedRequest.RequestUri
        Assert.Equal("/rest/content/current/CUI/C0011860", uri.AbsolutePath)
        Dim qs = ParseQuery(uri)
        Assert.Equal(ApiKey, qs("apiKey"))
        Assert.False(qs.ContainsKey("string"))  ' no search params on this endpoint
    End Function

    ' ============ GetSemanticTypes: response parsing ============

    <Fact>
    Public Async Function SemanticTypes_parses_name_list_in_order() As Task
        Const ResponseJson As String = "{
  ""result"": {
    ""ui"": ""C0011860"",
    ""semanticTypes"": [
      { ""name"": ""Disease or Syndrome"" },
      { ""name"": ""Mental or Behavioral Dysfunction"" }
    ]
  }
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)

        Assert.Equal(2, result.Count)
        Assert.Equal("Disease or Syndrome", result(0).Sty)
        Assert.Equal("Mental or Behavioral Dysfunction", result(1).Sty)
    End Function

    <Fact>
    Public Async Function SemanticTypes_returns_empty_when_semanticTypes_missing() As Task
        Const ResponseJson As String = "{ ""result"": { ""ui"": ""C0011860"" } }"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function SemanticTypes_skips_entries_without_name() As Task
        Const ResponseJson As String = "{
  ""result"": {
    ""semanticTypes"": [
      { ""name"": ""Disease or Syndrome"" },
      { ""uri"": ""..."" },
      { ""name"": ""Body Part"" }
    ]
  }
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)

        Assert.Equal(New String() {"Disease or Syndrome", "Body Part"},
                     result.Select(Function(a) a.Sty).ToArray())
    End Function

    ' ============ GetSemanticTypes: error swallowing ============

    <Fact>
    Public Async Function SemanticTypes_returns_empty_on_500_response() As Task
        Dim handler = StubHttpMessageHandler.WithStatus(HttpStatusCode.InternalServerError)
        Dim client = MakeClient(handler)
        Dim result = Await client.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function SemanticTypes_returns_empty_on_network_exception() As Task
        Dim handler = StubHttpMessageHandler.ThatThrows(New HttpRequestException("dns failure"))
        Dim client = MakeClient(handler)
        Dim result = Await client.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)
        Assert.Empty(result)
    End Function

    <Fact>
    Public Async Function SemanticTypes_propagates_user_cancellation() As Task
        Dim handler = StubHttpMessageHandler.WithJson("{}")
        Dim client = MakeClient(handler)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() client.GetSemanticTypeAssignmentsAsync("C0011860", cts.Token))
        End Using
    End Function

    ' ============ helpers ============

    Private Const EmptyResultsJson As String = "{ ""result"": { ""results"": [] } }"

    Private Shared Function MakeClient(
            handler As HttpMessageHandler,
            Optional opts As UmlsOptions = Nothing) As UmlsClient
        Dim httpClient As New HttpClient(handler) With {
            .BaseAddress = New Uri("https://uts-ws.nlm.nih.gov/rest/")
        }
        Dim resolved = If(opts, New UmlsOptions With {.ApiKey = ApiKey, .PageSize = 5})
        Return New UmlsClient(httpClient, Options.Create(resolved), NullLogger(Of UmlsClient).Instance)
    End Function

    Private Shared Function ParseQuery(uri As Uri) As IReadOnlyDictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)
        Dim qs = uri.Query
        If qs.StartsWith("?") Then qs = qs.Substring(1)
        For Each pair In qs.Split("&"c)
            If pair = "" Then Continue For
            Dim parts = pair.Split({"="c}, 2)
            If parts.Length = 2 Then
                d(Uri.UnescapeDataString(parts(0))) = Uri.UnescapeDataString(parts(1))
            End If
        Next
        Return d
    End Function

End Class

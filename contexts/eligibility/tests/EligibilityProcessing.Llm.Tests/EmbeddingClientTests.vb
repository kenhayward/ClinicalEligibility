Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Llm
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports Xunit

Public Class EmbeddingClientTests

    Private Const EmbeddingJson As String =
        "{ ""data"": [ { ""embedding"": [0.5, -0.25, 0.75] } ] }"

    ' ============ URL construction ============

    <Fact>
    Public Async Function EmbedAsync_posts_to_embeddings_endpoint() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmbeddingJson)
        Dim client = MakeClient(handler)
        Await client.EmbedAsync("some text", CancellationToken.None)

        Assert.Equal(HttpMethod.Post, handler.CapturedRequest.Method)
        Assert.Equal("/v1/embeddings", handler.CapturedRequest.RequestUri.AbsolutePath)
    End Function

    <Fact>
    Public Sub BuildEmbeddingsUrl_falls_back_to_llm_base_url_when_embedding_blank()
        Dim client = MakeClient(
                StubHttpMessageHandler.WithJson(EmbeddingJson),
                New EmbeddingOptions With {.BaseUrl = ""},
                New LlmOptions With {.BaseUrl = "http://llm-host:9000/v1/"})
        Assert.Equal("http://llm-host:9000/v1/embeddings", client.BuildEmbeddingsUrl())
    End Sub

    <Fact>
    Public Sub BuildEmbeddingsUrl_prefers_embedding_base_url_when_set()
        Dim client = MakeClient(
                StubHttpMessageHandler.WithJson(EmbeddingJson),
                New EmbeddingOptions With {.BaseUrl = "http://embed-host:1234/v1"},
                New LlmOptions With {.BaseUrl = "http://llm-host:9000/v1"})
        Assert.Equal("http://embed-host:1234/v1/embeddings", client.BuildEmbeddingsUrl())
    End Sub

    ' ============ request payload + auth ============

    <Fact>
    Public Async Function EmbedAsync_payload_includes_model_and_input() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmbeddingJson)
        Dim client = MakeClient(handler, New EmbeddingOptions With {.Model = "embed-model-x"})
        Await client.EmbedAsync("criteria text here", CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("embed-model-x", doc.RootElement.GetProperty("model").GetString())
            Assert.Equal("criteria text here", doc.RootElement.GetProperty("input").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function EmbedAsync_truncates_input_to_max_chars() As Task
        ' Embedding models have a fixed max sequence length; over-long input is
        ' capped before the request so the endpoint never rejects it.
        Dim handler = StubHttpMessageHandler.WithJson(EmbeddingJson)
        Dim client = MakeClient(handler, New EmbeddingOptions With {.Model = "m", .MaxInputChars = 100})
        Await client.EmbedAsync(New String("x"c, 5000), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal(100, doc.RootElement.GetProperty("input").GetString().Length)
        End Using
    End Function

    <Fact>
    Public Async Function EmbedAsync_does_not_truncate_when_within_limit() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmbeddingJson)
        Dim client = MakeClient(handler, New EmbeddingOptions With {.Model = "m", .MaxInputChars = 100})
        Await client.EmbedAsync("short input", CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("short input", doc.RootElement.GetProperty("input").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function EmbedAsync_warns_when_input_is_truncated() As Task
        ' Truncation silently drops the tail of the text from the vector — it must
        ' surface as a warning so an over-long corpus is visible in the logs.
        Dim logger As New CapturingLogger(Of EmbeddingClient)
        Dim client = MakeClient(
                StubHttpMessageHandler.WithJson(EmbeddingJson),
                New EmbeddingOptions With {.Model = "m", .MaxInputChars = 100},
                logger:=logger)
        Await client.EmbedAsync(New String("x"c, 5000), CancellationToken.None)

        Assert.Contains(logger.Entries, Function(e) e.Level = LogLevel.Warning)
    End Function

    <Fact>
    Public Async Function EmbedAsync_does_not_warn_when_within_limit() As Task
        Dim logger As New CapturingLogger(Of EmbeddingClient)
        Dim client = MakeClient(
                StubHttpMessageHandler.WithJson(EmbeddingJson),
                New EmbeddingOptions With {.Model = "m", .MaxInputChars = 100},
                logger:=logger)
        Await client.EmbedAsync("short input", CancellationToken.None)

        Assert.DoesNotContain(logger.Entries, Function(e) e.Level = LogLevel.Warning)
    End Function

    <Fact>
    Public Async Function EmbedAsync_falls_back_to_llm_api_key() As Task
        Dim handler = StubHttpMessageHandler.WithJson(EmbeddingJson)
        Dim client = MakeClient(handler,
                New EmbeddingOptions With {.ApiKey = ""},
                New LlmOptions With {.ApiKey = "llm-key-abc"})
        Await client.EmbedAsync("x", CancellationToken.None)

        Dim auth = handler.CapturedRequest.Headers.Authorization
        Assert.NotNull(auth)
        Assert.Equal("Bearer", auth.Scheme)
        Assert.Equal("llm-key-abc", auth.Parameter)
    End Function

    ' ============ configured model ============

    <Fact>
    Public Sub Model_reflects_the_configured_embedding_model()
        Dim client = MakeClient(
                StubHttpMessageHandler.WithJson(EmbeddingJson),
                New EmbeddingOptions With {.Model = "embed-model-z"})
        Assert.Equal("embed-model-z", client.Model)
    End Sub

    ' ============ response parsing ============

    <Fact>
    Public Async Function EmbedAsync_returns_vector_on_success() As Task
        Dim client = MakeClient(StubHttpMessageHandler.WithJson(EmbeddingJson))
        Dim result = Await client.EmbedAsync("x", CancellationToken.None)

        Assert.True(result.Succeeded)
        Assert.Equal(3, result.Vector.Count)
        Assert.Equal(0.5F, result.Vector(0))
        Assert.Equal(-0.25F, result.Vector(1))
    End Function

    <Fact>
    Public Async Function EmbedAsync_returns_failure_on_http_error() As Task
        Dim client = MakeClient(StubHttpMessageHandler.WithStatus(HttpStatusCode.InternalServerError, "boom"))
        Dim result = Await client.EmbedAsync("x", CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Empty(result.Vector)
        Assert.Contains("500", result.ErrorMessage)
    End Function

    <Fact>
    Public Async Function EmbedAsync_returns_failure_when_transport_throws() As Task
        Dim client = MakeClient(StubHttpMessageHandler.ThatThrows(New HttpRequestException("connection refused")))
        Dim result = Await client.EmbedAsync("x", CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Contains("connection refused", result.ErrorMessage)
    End Function

    <Fact>
    Public Sub ParseEmbeddingResponse_fails_on_missing_data_array()
        Using doc = JsonDocument.Parse("{ ""object"": ""list"" }")
            Dim result = EmbeddingClient.ParseEmbeddingResponse(doc.RootElement)
            Assert.False(result.Succeeded)
        End Using
    End Sub

    <Fact>
    Public Sub ParseEmbeddingResponse_fails_on_empty_embedding()
        Using doc = JsonDocument.Parse("{ ""data"": [ { ""embedding"": [] } ] }")
            Dim result = EmbeddingClient.ParseEmbeddingResponse(doc.RootElement)
            Assert.False(result.Succeeded)
        End Using
    End Sub

    ' ============ helpers ============

    Private Shared Function MakeClient(
            handler As HttpMessageHandler,
            Optional opts As EmbeddingOptions = Nothing,
            Optional llmOpts As LlmOptions = Nothing,
            Optional logger As ILogger(Of EmbeddingClient) = Nothing) As EmbeddingClient
        Dim httpClient As New HttpClient(handler) With {.BaseAddress = New Uri("http://example.com/")}
        Dim resolvedEmbed = If(opts, New EmbeddingOptions With {
            .BaseUrl = "http://example.com/v1", .Model = "test-embed-model"})
        Dim resolvedLlm = If(llmOpts, New LlmOptions())
        Return New EmbeddingClient(
                httpClient,
                Options.Create(resolvedEmbed),
                Options.Create(resolvedLlm),
                If(logger, NullLogger(Of EmbeddingClient).Instance))
    End Function

    ' Minimal ILogger that records the level of each entry, so tests can assert a
    ' warning was (or was not) emitted without standing up a logging framework.
    Private NotInheritable Class CapturingLogger(Of T)
        Implements ILogger(Of T)

        Public ReadOnly Entries As New List(Of (Level As LogLevel, Message As String))

        Public Function BeginScope(Of TState)(state As TState) As IDisposable _
                Implements ILogger.BeginScope
            Return Nothing
        End Function

        Public Function IsEnabled(logLevel As LogLevel) As Boolean Implements ILogger.IsEnabled
            Return True
        End Function

        Public Sub Log(Of TState)(
                logLevel As LogLevel, eventId As EventId, state As TState,
                exception As Exception, formatter As Func(Of TState, Exception, String)) _
                Implements ILogger.Log
            Entries.Add((logLevel, formatter(state, exception)))
        End Sub
    End Class

End Class

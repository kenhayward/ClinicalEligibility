Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Llm
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports Xunit

Public Class CriteriaNormalizerTests

    Private Const ApiKey As String = "test-llm-key"

    Private Shared ReadOnly Phrasings As String() =
        {"Adults over 18", "Patients aged 18 years or older", "Age >= 18"}

    Private Const SuccessJson As String = "{
  ""choices"": [
    { ""message"": { ""content"": ""Adults aged 18 years or older."" }, ""finish_reason"": ""stop"" }
  ],
  ""usage"": { ""prompt_tokens"": 0, ""completion_tokens"": 0 }
}"

    ' ============ request shape ============

    <Fact>
    Public Async Function NormalizeAsync_posts_to_chat_completions_endpoint() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler)
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.Equal(HttpMethod.Post, handler.CapturedRequest.Method)
        Assert.Equal("/v1/chat/completions", handler.CapturedRequest.RequestUri.AbsolutePath)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_sends_normalize_prompt_and_phrasings() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler)
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Dim messages = doc.RootElement.GetProperty("messages")
            Assert.Equal("system", messages(0).GetProperty("role").GetString())
            Assert.Equal(PromptBuilder.NormalizeSystemPrompt, messages(0).GetProperty("content").GetString())
            Dim userMsg = messages(1).GetProperty("content").GetString()
            Assert.Contains("Adults over 18", userMsg)
            Assert.Contains("Age >= 18", userMsg)
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_sends_bearer_authorization() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler)
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Dim auth = handler.CapturedRequest.Headers.Authorization
        Assert.NotNull(auth)
        Assert.Equal(ApiKey, auth.Parameter)
    End Function

    ' ============ outcomes ============

    <Fact>
    Public Async Function NormalizeAsync_returns_canonical_text_on_success() As Task
        Dim normalizer = MakeNormalizer(StubHttpMessageHandler.WithJson(SuccessJson))
        Dim result = Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.True(result.Succeeded)
        Assert.Equal("Adults aged 18 years or older.", result.NormalizedText)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_returns_failure_on_http_error() As Task
        Dim normalizer = MakeNormalizer(StubHttpMessageHandler.WithStatus(HttpStatusCode.InternalServerError, "boom"))
        Dim result = Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Contains("500", result.ErrorMessage)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_returns_failure_when_transport_throws() As Task
        Dim normalizer = MakeNormalizer(StubHttpMessageHandler.ThatThrows(New HttpRequestException("refused")))
        Dim result = Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Contains("refused", result.ErrorMessage)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_fails_without_calling_endpoint_when_all_input_blank() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler)
        Dim result = Await normalizer.NormalizeAsync(New String() {"", "   "}, CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Equal(0, handler.CallCount)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_reports_finish_reason_when_content_empty() As Task
        ' Empty content + finish_reason length = the token budget ran out
        ' (e.g. a thinking model). The error must say so, actionably.
        Const EmptyLengthJson As String =
            "{ ""choices"": [ { ""message"": { ""content"": """" }, ""finish_reason"": ""length"" } ] }"
        Dim normalizer = MakeNormalizer(StubHttpMessageHandler.WithJson(EmptyLengthJson))
        Dim result = Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Contains("length", result.ErrorMessage)
        Assert.Contains("NormalizeMaxTokens", result.ErrorMessage)
    End Function

    ' ============ NormalizeConceptAsync (concept -> canonical term) ============

    <Fact>
    Public Async Function NormalizeConceptAsync_sends_concept_prompt_and_concept() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler)
        Await normalizer.NormalizeConceptAsync("low blood sugar", CancellationToken.None)

        Assert.Equal("/v1/chat/completions", handler.CapturedRequest.RequestUri.AbsolutePath)
        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Dim messages = doc.RootElement.GetProperty("messages")
            Assert.Equal(PromptBuilder.ConceptNormalizeSystemPrompt, messages(0).GetProperty("content").GetString())
            Assert.Contains("low blood sugar", messages(1).GetProperty("content").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeConceptAsync_returns_canonical_term() As Task
        Dim result = Await MakeNormalizer(StubHttpMessageHandler.WithJson(SuccessJson)) _
                .NormalizeConceptAsync("adults over 18", CancellationToken.None)
        Assert.True(result.Succeeded)
        Assert.Equal("Adults aged 18 years or older.", result.NormalizedText)
    End Function

    <Fact>
    Public Async Function NormalizeConceptAsync_passes_through_NONE() As Task
        Const NoneJson As String =
            "{ ""choices"": [ { ""message"": { ""content"": ""NONE"" }, ""finish_reason"": ""stop"" } ] }"
        Dim result = Await MakeNormalizer(StubHttpMessageHandler.WithJson(NoneJson)) _
                .NormalizeConceptAsync("smartphone ownership", CancellationToken.None)
        Assert.True(result.Succeeded)
        Assert.Equal("NONE", result.NormalizedText)
    End Function

    <Fact>
    Public Async Function NormalizeConceptAsync_fails_without_calling_endpoint_when_blank() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim result = Await MakeNormalizer(handler).NormalizeConceptAsync("   ", CancellationToken.None)
        Assert.False(result.Succeeded)
        Assert.Equal(0, handler.CallCount)
    End Function

    ' ============ CleanNormalizedText ============

    <Fact>
    Public Sub CleanNormalizedText_strips_a_code_fence()
        Dim cleaned = CriteriaNormalizer.CleanNormalizedText("```" & vbLf & "Adults over 18." & vbLf & "```")
        Assert.Equal("Adults over 18.", cleaned)
    End Sub

    <Fact>
    Public Sub CleanNormalizedText_strips_surrounding_quotes()
        ' Input is the 17-char string: "Adults over 18."  (quotes included).
        Assert.Equal("Adults over 18.", CriteriaNormalizer.CleanNormalizedText("""Adults over 18."""))
    End Sub

    <Fact>
    Public Sub CleanNormalizedText_leaves_plain_text_untouched()
        Assert.Equal("Adults over 18.", CriteriaNormalizer.CleanNormalizedText("  Adults over 18.  "))
    End Sub

    ' ============ helpers ============

    Private Shared Function MakeNormalizer(
            handler As HttpMessageHandler,
            Optional normalizeOverrides As LlmNormalizeOptions = Nothing,
            Optional llmReasoningEffort As String = "medium") As CriteriaNormalizer
        Dim httpClient As New HttpClient(handler) With {.BaseAddress = New Uri("http://example.com/")}
        Dim opts = New LlmOptions With {
            .ApiKey = ApiKey, .BaseUrl = "http://example.com/v1", .Model = "test-model",
            .ReasoningEffort = llmReasoningEffort}
        Dim normOpts = If(normalizeOverrides, New LlmNormalizeOptions())
        Return New CriteriaNormalizer(
                httpClient,
                Options.Create(opts),
                Options.Create(normOpts),
                NullLogger(Of CriteriaNormalizer).Instance)
    End Function

    ' ============ LlmNormalize overrides ============

    <Fact>
    Public Async Function NormalizeAsync_uses_override_endpoint_when_set() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.BaseUrl = "http://small-llm.local/v1"})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.Equal("small-llm.local", handler.CapturedRequest.RequestUri.Host)
        Assert.Equal("/v1/chat/completions", handler.CapturedRequest.RequestUri.AbsolutePath)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_uses_override_model_in_payload_when_set() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.Model = "tiny-3b"})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("tiny-3b", doc.RootElement.GetProperty("model").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_uses_override_api_key_when_set() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.ApiKey = "small-llm-key"})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.Equal("small-llm-key", handler.CapturedRequest.Headers.Authorization.Parameter)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_falls_back_to_llm_options_when_overrides_blank() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.BaseUrl = "  ", .Model = "", .ApiKey = "   "})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Assert.Equal("example.com", handler.CapturedRequest.RequestUri.Host)
        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("test-model", doc.RootElement.GetProperty("model").GetString())
        End Using
        Assert.Equal(ApiKey, handler.CapturedRequest.Headers.Authorization.Parameter)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_uses_override_temperature_when_set() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.Temperature = 0.1})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal(0.1, doc.RootElement.GetProperty("temperature").GetDouble())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_uses_override_max_tokens_when_set() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.MaxTokens = 4096})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_falls_back_to_llm_temperature_and_max_tokens_when_unset() As Task
        ' MakeNormalizer's LlmOptions defaults: Temperature 0.3, NormalizeMaxTokens 2000.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler, New LlmNormalizeOptions())
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal(0.3, doc.RootElement.GetProperty("temperature").GetDouble())
            Assert.Equal(2000, doc.RootElement.GetProperty("max_tokens").GetInt32())
        End Using
    End Function

    ' ============ reasoning effort ============

    <Fact>
    Public Async Function NormalizeAsync_defaults_reasoning_effort_to_low() As Task
        ' A bare LlmNormalizeOptions carries ReasoningEffort = "low" by default.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler, New LlmNormalizeOptions())
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("low", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_uses_override_reasoning_effort_when_set() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.ReasoningEffort = "high"})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("high", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_falls_back_to_llm_reasoning_effort_when_blank() As Task
        ' Blank normalize override inherits LlmOptions.ReasoningEffort.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.ReasoningEffort = "  "},
            llmReasoningEffort:="medium")
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function NormalizeAsync_omits_reasoning_effort_when_reasoning_disabled() As Task
        ' EnableReasoning=False is the master switch: the field is never sent even
        ' with a non-empty effort (non-reasoning normalize model).
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.EnableReasoning = False, .ReasoningEffort = "high"})
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Dim ignored As JsonElement = Nothing
            Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", ignored))
        End Using
        Assert.DoesNotContain("reasoning_effort", handler.CapturedBody)
    End Function

    <Fact>
    Public Async Function NormalizeAsync_omits_reasoning_effort_when_both_blank() As Task
        ' Blank on both the override and LlmOptions keeps the field off the wire.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessJson)
        Dim normalizer = MakeNormalizer(handler,
            New LlmNormalizeOptions With {.ReasoningEffort = ""},
            llmReasoningEffort:="")
        Await normalizer.NormalizeAsync(Phrasings, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Dim ignored As JsonElement = Nothing
            Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", ignored))
        End Using
        Assert.DoesNotContain("reasoning_effort", handler.CapturedBody)
    End Function

End Class

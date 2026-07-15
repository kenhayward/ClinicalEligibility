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

Public Class LlmClientTests

    Private Const ApiKey As String = "test-llm-key-xyz"

    ' ============ argument validation ============

    <Fact>
    Public Async Function CompleteAsync_throws_on_null_request() As Task
        Dim client = MakeClient(StubHttpMessageHandler.WithJson("{}"))
        Await Assert.ThrowsAsync(Of ArgumentNullException)(
            Function() client.CompleteAsync(Nothing, CancellationToken.None))
    End Function

    ' ============ URL construction ============

    <Fact>
    Public Async Function CompleteAsync_posts_to_chat_completions_endpoint() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim client = MakeClient(handler)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Assert.Equal(HttpMethod.Post, handler.CapturedRequest.Method)
        Assert.Equal("/v1/chat/completions", handler.CapturedRequest.RequestUri.AbsolutePath)
    End Function

    <Fact>
    Public Async Function CompleteAsync_strips_trailing_slash_from_baseurl() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .BaseUrl = "http://example.com/v1/"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Assert.Equal("http://example.com/v1/chat/completions", handler.CapturedRequest.RequestUri.ToString())
    End Function

    ' ============ Authorization header ============

    <Fact>
    Public Async Function CompleteAsync_sends_bearer_authorization_when_apikey_present() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim client = MakeClient(handler)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Dim auth = handler.CapturedRequest.Headers.Authorization
        Assert.NotNull(auth)
        Assert.Equal("Bearer", auth.Scheme)
        Assert.Equal(ApiKey, auth.Parameter)
    End Function

    <Fact>
    Public Async Function CompleteAsync_omits_authorization_when_apikey_empty() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = "", .Model = "test-model"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Assert.Null(handler.CapturedRequest.Headers.Authorization)
    End Function

    ' ============ request payload shape (spec section 2.4.1 + 2.4.2 + 2.4.3) ============

    <Fact>
    Public Async Function CompleteAsync_payload_includes_model_from_options() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .Model = "custom-model-42b"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("custom-model-42b", doc.RootElement.GetProperty("model").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_has_system_and_user_messages() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim client = MakeClient(handler)
        Await client.CompleteAsync(SampleRequest("NCT00000123", "Inclusion: adult"), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Dim messages = doc.RootElement.GetProperty("messages")
            Assert.Equal(2, messages.GetArrayLength())

            Dim systemMsg = messages(0)
            Assert.Equal("system", systemMsg.GetProperty("role").GetString())
            Assert.Equal(PromptBuilder.SystemPrompt, systemMsg.GetProperty("content").GetString())

            Dim userMsg = messages(1)
            Assert.Equal("user", userMsg.GetProperty("role").GetString())
            Dim userContent = userMsg.GetProperty("content").GetString()
            Assert.Contains("NCT_ID: NCT00000123", userContent)
            Assert.Contains("Inclusion: adult", userContent)
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_uses_options_temperature_and_max_tokens() As Task
        ' Per-deployment LlmOptions wins over LlmRequest fields. The Request's
        ' Temperature/MaxTokens are advisory and ignored at the wire layer
        ' (see LlmClient.BuildRequestPayload doc comment).
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {
                .ApiKey = ApiKey,
                .BaseUrl = "http://example.com/v1",
                .Model = "test-model",
                .Temperature = 0.7,
                .MaxTokens = 12345
        }
        Dim client = MakeClient(handler, opts)

        ' Even when the LlmRequest specifies different values, the options win.
        Dim request = New LlmRequest(
                nctId:="NCT0", criteriaText:="x",
                temperature:=0.1, maxTokens:=42)
        Await client.CompleteAsync(request, CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal(0.7, doc.RootElement.GetProperty("temperature").GetDouble(), 5)
            Assert.Equal(12345, doc.RootElement.GetProperty("max_tokens").GetInt32())
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_defaults_to_LlmOptions_defaults() As Task
        ' LlmOptions defaults: temperature 0.3 (spec section 2.4.1), MaxTokens
        ' 8000 (raised from the spec's 3500 reference value — see appsettings.json
        ' and the architecture doc's section 2.3).
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim client = MakeClient(handler)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal(0.3, doc.RootElement.GetProperty("temperature").GetDouble(), 5)
            Assert.Equal(8000, doc.RootElement.GetProperty("max_tokens").GetInt32())
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_uses_snake_case_json_keys() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim client = MakeClient(handler)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        ' OpenAI / llama.cpp require these exact keys on the wire.
        Assert.Contains("""model""", handler.CapturedBody)
        Assert.Contains("""messages""", handler.CapturedBody)
        Assert.Contains("""max_tokens""", handler.CapturedBody)
        Assert.DoesNotContain("MaxTokens", handler.CapturedBody)
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_includes_reasoning_effort_from_options() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .Model = "gpt-oss-20b", .ReasoningEffort = "high"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("high", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_defaults_reasoning_effort_to_medium() As Task
        ' LlmOptions default is "medium" — at "low", gpt-oss-class models bail on
        ' long trials and emit [] (recorded as parse_empty downstream).
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .Model = "test-model"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_reasoning_effort_override_wins_over_options() As Task
        ' The escalation retry passes an override that must win over the
        ' configured default for that single call.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .Model = "gpt-oss-20b", .ReasoningEffort = "low"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None, reasoningEffortOverride:="high")

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("high", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_uses_configured_effort_when_override_blank() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .Model = "test-model", .ReasoningEffort = "low"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None, reasoningEffortOverride:="")

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Assert.Equal("low", doc.RootElement.GetProperty("reasoning_effort").GetString())
        End Using
    End Function

    ' ============ escalation plan (EscalationReasoningEffort) ============

    <Fact>
    Public Sub EscalationReasoningEffort_returns_level_when_enabled_and_levels_differ()
        Dim opts = New LlmOptions With {
                .EnableReasoningEscalation = True, .ReasoningEffort = "low", .EscalateReasoningEffort = "medium"}
        Dim client = MakeClient(StubHttpMessageHandler.WithJson("{}"), opts)
        Assert.Equal("medium", client.EscalationReasoningEffort)
    End Sub

    <Fact>
    Public Sub EscalationReasoningEffort_empty_when_flag_off()
        Dim opts = New LlmOptions With {
                .EnableReasoningEscalation = False, .ReasoningEffort = "low", .EscalateReasoningEffort = "medium"}
        Dim client = MakeClient(StubHttpMessageHandler.WithJson("{}"), opts)
        Assert.Equal("", client.EscalationReasoningEffort)
    End Sub

    <Fact>
    Public Sub EscalationReasoningEffort_empty_when_levels_are_the_same()
        ' Escalating low → low is a no-op; the property reports "" so the
        ' orchestrator doesn't waste a second call.
        Dim opts = New LlmOptions With {
                .EnableReasoningEscalation = True, .ReasoningEffort = "medium", .EscalateReasoningEffort = "medium"}
        Dim client = MakeClient(StubHttpMessageHandler.WithJson("{}"), opts)
        Assert.Equal("", client.EscalationReasoningEffort)
    End Sub

    <Fact>
    Public Async Function CompleteAsync_payload_omits_reasoning_effort_when_empty() As Task
        ' Empty (or whitespace) ReasoningEffort keeps the wire shape free of the
        ' field, so non-reasoning deployments that reject it are unaffected.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {.ApiKey = ApiKey, .Model = "test-model", .ReasoningEffort = ""}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Using doc = JsonDocument.Parse(handler.CapturedBody)
            Dim ignored As JsonElement = Nothing
            Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", ignored))
        End Using
        Assert.DoesNotContain("reasoning_effort", handler.CapturedBody)
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_omits_reasoning_effort_when_reasoning_disabled() As Task
        ' EnableReasoning is the master switch: off => the field is never sent,
        ' even with a non-empty effort (non-reasoning instruct model mode).
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {
                .ApiKey = ApiKey, .Model = "instruct-model", .EnableReasoning = False, .ReasoningEffort = "high"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Assert.DoesNotContain("reasoning_effort", handler.CapturedBody)
    End Function

    <Fact>
    Public Async Function CompleteAsync_payload_omits_reasoning_effort_when_disabled_even_with_override() As Task
        ' The master switch wins over an escalation override too.
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim opts = New LlmOptions With {
                .ApiKey = ApiKey, .Model = "instruct-model", .EnableReasoning = False, .ReasoningEffort = "low"}
        Dim client = MakeClient(handler, opts)
        Await client.CompleteAsync(SampleRequest(), CancellationToken.None, reasoningEffortOverride:="high")

        Assert.DoesNotContain("reasoning_effort", handler.CapturedBody)
    End Function

    <Fact>
    Public Sub EscalationReasoningEffort_empty_when_reasoning_disabled()
        ' No escalation for a non-reasoning model, even with escalation configured.
        Dim opts = New LlmOptions With {
                .EnableReasoning = False, .EnableReasoningEscalation = True,
                .ReasoningEffort = "low", .EscalateReasoningEffort = "medium"}
        Dim client = MakeClient(StubHttpMessageHandler.WithJson("{}"), opts)
        Assert.Equal("", client.EscalationReasoningEffort)
    End Sub

    ' ============ response parsing — success ============

    <Fact>
    Public Async Function CompleteAsync_parses_content_finish_reason_and_tokens_on_success() As Task
        Const ResponseJson As String = "{
  ""choices"": [
    {
      ""message"": { ""role"": ""assistant"", ""content"": ""[{\""concept\"":\""x\""}]"" },
      ""finish_reason"": ""stop""
    }
  ],
  ""usage"": { ""prompt_tokens"": 120, ""completion_tokens"": 350 }
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.True(result.Succeeded)
        Assert.Equal("NCT0", result.NctId)
        Assert.Equal("[{""concept"":""x""}]", result.RawText)
        Assert.Equal("stop", result.FinishReason)
        Assert.Equal(120, result.PromptTokens)
        Assert.Equal(350, result.CompletionTokens)
        Assert.Equal("", result.ErrorMessage)
        ' OpenAI-proper responses don't carry the llama.cpp vendor fields.
        Assert.Null(result.StoppedEos)
        Assert.Null(result.StoppedLimit)
        Assert.Null(result.StoppedWord)
        Assert.Equal("", result.StoppingWord)
        Assert.Null(result.Truncated)
    End Function

    <Fact>
    Public Async Function CompleteAsync_captures_llamacpp_stop_diagnostics_when_present() As Task
        ' llama.cpp emits these root-level fields alongside choices / usage.
        ' We don't make our shape depend on them — they're optional — but a
        ' length-truncated response should carry them forward to the audit
        ' row so operators can tell "max_tokens hit" from "EOS suppressed".
        Const ResponseJson As String = "{
  ""choices"": [
    { ""message"": { ""content"": ""[{}"" }, ""finish_reason"": ""length"" }
  ],
  ""usage"": { ""prompt_tokens"": 1488, ""completion_tokens"": 6704 },
  ""stopped_eos"": false,
  ""stopped_limit"": true,
  ""stopped_word"": false,
  ""stopping_word"": """",
  ""truncated"": true
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT1"), CancellationToken.None)

        Assert.True(result.Succeeded)
        Assert.Equal("length", result.FinishReason)
        Assert.Equal(1488, result.PromptTokens)
        Assert.Equal(6704, result.CompletionTokens)
        Assert.False(result.StoppedEos)
        Assert.True(result.StoppedLimit)
        Assert.False(result.StoppedWord)
        Assert.Equal("", result.StoppingWord)
        Assert.True(result.Truncated)
    End Function

    <Fact>
    Public Async Function CompleteAsync_success_when_usage_block_missing() As Task
        Const ResponseJson As String = "{
  ""choices"": [
    { ""message"": { ""content"": ""[]"" }, ""finish_reason"": ""stop"" }
  ]
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Assert.True(result.Succeeded)
        Assert.Equal("[]", result.RawText)
        Assert.Equal(0, result.PromptTokens)
        Assert.Equal(0, result.CompletionTokens)
    End Function

    <Fact>
    Public Async Function CompleteAsync_success_with_empty_content_when_choice_has_no_content() As Task
        Const ResponseJson As String = "{
  ""choices"": [
    { ""message"": { ""role"": ""assistant"" }, ""finish_reason"": ""length"" }
  ]
}"
        Dim handler = StubHttpMessageHandler.WithJson(ResponseJson)
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest(), CancellationToken.None)

        Assert.True(result.Succeeded)
        Assert.Equal("", result.RawText)
        Assert.Equal("length", result.FinishReason)
    End Function

    ' ============ response parsing — failure modes ============

    <Fact>
    Public Async Function CompleteAsync_returns_Failure_when_choices_missing() As Task
        Dim handler = StubHttpMessageHandler.WithJson("{ ""id"": ""x"" }")
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Equal("NCT0", result.NctId)
        Assert.Contains("choices", result.ErrorMessage, StringComparison.OrdinalIgnoreCase)
    End Function

    <Fact>
    Public Async Function CompleteAsync_returns_Failure_when_choices_array_empty() As Task
        Dim handler = StubHttpMessageHandler.WithJson("{ ""choices"": [] }")
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Equal("NCT0", result.NctId)
    End Function

    ' ============ transport-level failure (per spec 2.4.4: surface as Failure, do not throw) ============

    <Fact>
    Public Async Function CompleteAsync_returns_Failure_on_500_response() As Task
        Dim handler = StubHttpMessageHandler.WithStatus(HttpStatusCode.InternalServerError, "server exploded")
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Contains("500", result.ErrorMessage)
        Assert.Contains("server exploded", result.ErrorMessage)
    End Function

    <Fact>
    Public Async Function CompleteAsync_returns_Failure_on_401_response() As Task
        Dim handler = StubHttpMessageHandler.WithStatus(HttpStatusCode.Unauthorized)
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Contains("401", result.ErrorMessage)
    End Function

    <Fact>
    Public Async Function CompleteAsync_returns_Failure_on_network_exception() As Task
        Dim handler = StubHttpMessageHandler.ThatThrows(New HttpRequestException("connection refused"))
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.False(result.Succeeded)
        Assert.Equal("NCT0", result.NctId)
        Assert.Contains("connection refused", result.ErrorMessage)
    End Function

    <Fact>
    Public Async Function CompleteAsync_returns_Failure_on_malformed_json() As Task
        Dim handler = StubHttpMessageHandler.WithJson("not valid json {")
        Dim client = MakeClient(handler)
        Dim result = Await client.CompleteAsync(SampleRequest("NCT0"), CancellationToken.None)

        Assert.False(result.Succeeded)
    End Function

    ' ============ cancellation propagates (does not become a Failure) ============

    <Fact>
    Public Async Function CompleteAsync_propagates_user_cancellation() As Task
        Dim handler = StubHttpMessageHandler.WithJson(SuccessResponseJson)
        Dim client = MakeClient(handler)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() client.CompleteAsync(SampleRequest(), cts.Token))
        End Using
    End Function

    ' ============ helpers ============

    Private Const SuccessResponseJson As String = "{
  ""choices"": [
    { ""message"": { ""content"": ""[]"" }, ""finish_reason"": ""stop"" }
  ],
  ""usage"": { ""prompt_tokens"": 0, ""completion_tokens"": 0 }
}"

    Private Shared Function SampleRequest(
            Optional nctId As String = "NCT00000123",
            Optional criteriaText As String = "Inclusion: x") As LlmRequest
        Return New LlmRequest(nctId, criteriaText)
    End Function

    Private Shared Function MakeClient(
            handler As HttpMessageHandler,
            Optional opts As LlmOptions = Nothing) As LlmClient
        Dim httpClient As New HttpClient(handler) With {
            .BaseAddress = New Uri("http://example.com/")
        }
        Dim resolved = If(opts, New LlmOptions With {
            .ApiKey = ApiKey,
            .BaseUrl = "http://example.com/v1",
            .Model = "test-model"
        })
        Return New LlmClient(httpClient, Options.Create(resolved), NullLogger(Of LlmClient).Instance)
    End Function

End Class

Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options

' Wire-level OpenAI-compatible chat-completions client.
'
' Spec section 2.4 (model contract, prompts, retry/failure semantics).
' Architecture section 2.3 (named HttpClient "llamacpp", Polly retry/timeout
' wired at the DI composition root — NOT here). This class owns:
'   - Request payload construction (model + messages + temperature + max_tokens)
'   - Authorization header forwarding (Bearer token)
'   - Response parsing (content + finish_reason + usage)
'   - Surfacing transport failures as LlmResponse.Failure (per spec 2.4.4 —
'     a single trial MUST NOT abort the batch)
'   - Re-throwing user cancellation (timeout still surfaces as Failure)

Public NotInheritable Class LlmClient
    Implements ILlmClient

    Private ReadOnly _httpClient As HttpClient
    Private ReadOnly _options As LlmOptions
    Private ReadOnly _logger As ILogger(Of LlmClient)

    Public Sub New(
            httpClient As HttpClient,
            options As IOptions(Of LlmOptions),
            logger As ILogger(Of LlmClient))
        _httpClient = httpClient
        _options = options.Value
        _logger = logger
    End Sub

    ''' <summary>
    ''' Reasoning effort to retry an empty-array trial at, or "" when escalation
    ''' is off / unconfigured / would retry at the same level (no-op).
    ''' </summary>
    Public ReadOnly Property EscalationReasoningEffort As String _
            Implements ILlmClient.EscalationReasoningEffort
        Get
            ' Reasoning disabled => never escalate (escalation only makes sense
            ' for a reasoning model).
            If _options.EnableReasoning AndAlso
               _options.EnableReasoningEscalation AndAlso
               Not String.IsNullOrWhiteSpace(_options.EscalateReasoningEffort) AndAlso
               Not String.Equals(_options.EscalateReasoningEffort.Trim(),
                                 If(_options.ReasoningEffort, "").Trim(),
                                 StringComparison.OrdinalIgnoreCase) Then
                Return _options.EscalateReasoningEffort.Trim()
            End If
            Return ""
        End Get
    End Property

    Public Async Function CompleteAsync(
            request As LlmRequest,
            cancellationToken As CancellationToken,
            Optional reasoningEffortOverride As String = Nothing) As Task(Of LlmResponse) _
            Implements ILlmClient.CompleteAsync

        If request Is Nothing Then
            Throw New ArgumentNullException(NameOf(request))
        End If

        Dim payload = BuildRequestPayload(request, reasoningEffortOverride)
        Dim payloadJson = JsonSerializer.Serialize(payload)
        Dim url = BuildCompletionsUrl()

        Try
            Using content As New StringContent(payloadJson, Encoding.UTF8, "application/json")
                Using requestMsg As New HttpRequestMessage(HttpMethod.Post, url) With {.Content = content}
                    If Not String.IsNullOrEmpty(_options.ApiKey) Then
                        requestMsg.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _options.ApiKey)
                    End If
                    Using response = Await _httpClient.SendAsync(requestMsg, cancellationToken).ConfigureAwait(False)
                        If Not response.IsSuccessStatusCode Then
                            Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                            _logger.LogWarning(
                                    "LLM /chat/completions returned {Status} for trial {Nct}",
                                    CInt(response.StatusCode), request.NctId)
                            Return LlmResponse.Failure(
                                    request.NctId,
                                    $"HTTP {CInt(response.StatusCode)}: {Truncate(body, 500)}")
                        End If
                        Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                            Using doc = Await JsonDocument.ParseAsync(stream, cancellationToken:=cancellationToken).ConfigureAwait(False)
                                Return ParseChatCompletionResponse(request.NctId, doc.RootElement)
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex, "LLM /chat/completions failed for trial {Nct}", request.NctId)
            Return LlmResponse.Failure(request.NctId, ex.Message)
        End Try
    End Function

    ' --- request construction ---

    Friend Function BuildCompletionsUrl() As String
        Dim baseUrl = If(_options.BaseUrl, "").TrimEnd("/"c)
        If String.IsNullOrEmpty(baseUrl) Then baseUrl = "http://localhost:8080/v1"
        Return $"{baseUrl}/chat/completions"
    End Function

    ' Anonymous-type payload with snake_case property names so System.Text.Json
    ' emits the wire shape OpenAI / llama.cpp expect without naming-policy config.
    '
    ' Temperature and max_tokens come from the per-deployment LlmOptions, NOT
    ' from the LlmRequest. The LlmRequest carries those fields for backwards
    ' compatibility but they're advisory — the deployment configuration
    ' (appsettings.json + .env) is the source of truth. This avoids the bug
    ' where bumping appsettings.json:Llm:MaxTokens has no effect because the
    ' orchestrator builds LlmRequest with the constructor's Optional defaults
    ' instead of passing the options through.
    Friend Function BuildRequestPayload(request As LlmRequest,
                                        Optional reasoningEffortOverride As String = Nothing) As Object
        Dim payload As New Dictionary(Of String, Object) From {
            {"model", _options.Model},
            {"messages", New Object() {
                New With {.role = "system", .content = PromptBuilder.SystemPrompt},
                New With {.role = "user", .content = PromptBuilder.BuildUserMessage(request.NctId, request.CriteriaText)}
            }},
            {"temperature", _options.Temperature},
            {"max_tokens", _options.MaxTokens}
        }

        ' Reasoning effort is emitted only when reasoning is enabled AND an effort
        ' is configured. EnableReasoning is the master switch: when off, the field
        ' is never sent so the endpoint is treated as a plain non-reasoning model.
        ' Reasoning models (gpt-oss, o-series) honour the field; non-reasoning
        ' models ignore it. A non-empty override (escalation retry) wins over the
        ' configured default. See LlmOptions.EnableReasoning / ReasoningEffort.
        Dim effort = If(Not String.IsNullOrWhiteSpace(reasoningEffortOverride),
                        reasoningEffortOverride, _options.ReasoningEffort)
        If _options.EnableReasoning AndAlso Not String.IsNullOrWhiteSpace(effort) Then
            payload("reasoning_effort") = effort.Trim()
        End If

        Return payload
    End Function

    ' --- response parsing ---

    Friend Shared Function ParseChatCompletionResponse(nctId As String, root As JsonElement) As LlmResponse
        If root.ValueKind <> JsonValueKind.Object Then
            Return LlmResponse.Failure(nctId, "Response root was not a JSON object")
        End If

        Dim choicesProp As JsonElement = Nothing
        If Not root.TryGetProperty("choices", choicesProp) OrElse choicesProp.ValueKind <> JsonValueKind.Array Then
            Return LlmResponse.Failure(nctId, "Response missing choices array")
        End If

        Dim firstChoice = choicesProp.EnumerateArray().FirstOrDefault()
        If firstChoice.ValueKind <> JsonValueKind.Object Then
            Return LlmResponse.Failure(nctId, "Response choices array was empty")
        End If

        Dim content As String = ""
        Dim messageProp As JsonElement = Nothing
        If firstChoice.TryGetProperty("message", messageProp) AndAlso messageProp.ValueKind = JsonValueKind.Object Then
            content = GetStringOrEmpty(messageProp, "content")
        End If

        Dim finishReason = GetStringOrEmpty(firstChoice, "finish_reason")

        Dim promptTokens As Integer = 0
        Dim completionTokens As Integer = 0
        Dim usageProp As JsonElement = Nothing
        If root.TryGetProperty("usage", usageProp) AndAlso usageProp.ValueKind = JsonValueKind.Object Then
            promptTokens = GetIntOrZero(usageProp, "prompt_tokens")
            completionTokens = GetIntOrZero(usageProp, "completion_tokens")
        End If

        ' llama.cpp adds vendor-specific stop diagnostics at the response
        ' root. They are not part of the OpenAI schema, so OpenAI-proper
        ' responses won't carry them — read defensively and leave Nothing
        ' when the field is absent.
        Dim stoppedEos = GetBoolOrNothing(root, "stopped_eos")
        Dim stoppedLimit = GetBoolOrNothing(root, "stopped_limit")
        Dim stoppedWord = GetBoolOrNothing(root, "stopped_word")
        Dim stoppingWord = GetStringOrEmpty(root, "stopping_word")
        Dim truncated = GetBoolOrNothing(root, "truncated")

        Return LlmResponse.Success(
                nctId:=nctId,
                rawText:=content,
                finishReason:=finishReason,
                promptTokens:=promptTokens,
                completionTokens:=completionTokens,
                stoppedEos:=stoppedEos,
                stoppedLimit:=stoppedLimit,
                stoppedWord:=stoppedWord,
                stoppingWord:=stoppingWord,
                truncated:=truncated)
    End Function

    Private Shared Function GetStringOrEmpty(element As JsonElement, propertyName As String) As String
        Dim child As JsonElement = Nothing
        If Not element.TryGetProperty(propertyName, child) Then Return ""
        If child.ValueKind <> JsonValueKind.String Then Return ""
        Return If(child.GetString(), "")
    End Function

    Private Shared Function GetIntOrZero(element As JsonElement, propertyName As String) As Integer
        Dim child As JsonElement = Nothing
        If Not element.TryGetProperty(propertyName, child) Then Return 0
        If child.ValueKind <> JsonValueKind.Number Then Return 0
        Dim val As Integer
        Return If(child.TryGetInt32(val), val, 0)
    End Function

    Private Shared Function GetBoolOrNothing(element As JsonElement, propertyName As String) As Boolean?
        Dim child As JsonElement = Nothing
        If Not element.TryGetProperty(propertyName, child) Then Return Nothing
        If child.ValueKind = JsonValueKind.True Then Return True
        If child.ValueKind = JsonValueKind.False Then Return False
        Return Nothing
    End Function

    Private Shared Function Truncate(s As String, maxLen As Integer) As String
        If String.IsNullOrEmpty(s) Then Return ""
        If s.Length <= maxLen Then Return s
        Return s.Substring(0, maxLen) & "..."
    End Function

End Class

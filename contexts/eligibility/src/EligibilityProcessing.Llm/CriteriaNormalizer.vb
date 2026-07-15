Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options

' LLM-backed criterion normalizer (authoring specification §3.5). Sends a
' cluster of original-text phrasings to the chat-completions endpoint with the
' normalize system prompt and returns one canonical statement.
'
' Endpoint / model / API key come from LlmNormalizeOptions when set, otherwise
' fall back to LlmOptions — so a deployment can point the normalize call at a
' smaller, faster model without touching the extraction LLM config. Shared
' values (Temperature, NormalizeMaxTokens, retry/timeout on the named
' "normalizer" HttpClient) stay on LlmOptions. The extraction LlmClient and
' its prompt are untouched. The normalize prompt asks for plain text out, so
' the response needs no JSON envelope parsing — just a defensive trim of
' stray fences / quotes.

Public NotInheritable Class CriteriaNormalizer
    Implements ICriteriaNormalizer

    Private ReadOnly _httpClient As HttpClient
    Private ReadOnly _options As LlmOptions
    Private ReadOnly _normalizeOptions As LlmNormalizeOptions
    Private ReadOnly _logger As ILogger(Of CriteriaNormalizer)

    Public Sub New(
            httpClient As HttpClient,
            options As IOptions(Of LlmOptions),
            normalizeOptions As IOptions(Of LlmNormalizeOptions),
            logger As ILogger(Of CriteriaNormalizer))
        _httpClient = httpClient
        _options = options.Value
        _normalizeOptions = normalizeOptions.Value
        _logger = logger
    End Sub

    Public Async Function NormalizeAsync(
            originalTexts As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of NormalizationResult) _
            Implements ICriteriaNormalizer.NormalizeAsync

        Dim usable = If(originalTexts, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        If usable.All(Function(t) String.IsNullOrWhiteSpace(t)) Then
            Return NormalizationResult.Failure("No criterion text to normalize.")
        End If

        Return Await PostNormalizeAsync(
                JsonSerializer.Serialize(BuildNormalizePayload(usable)), cancellationToken).ConfigureAwait(False)
    End Function

    Public Async Function NormalizeConceptAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of NormalizationResult) _
            Implements ICriteriaNormalizer.NormalizeConceptAsync

        If String.IsNullOrWhiteSpace(concept) Then
            Return NormalizationResult.Failure("No concept text to normalize.")
        End If

        Return Await PostNormalizeAsync(
                JsonSerializer.Serialize(BuildConceptNormalizePayload(concept)), cancellationToken).ConfigureAwait(False)
    End Function

    ' Shared transport for both normalize flavours: POST the payload to the
    ' chat-completions endpoint, parse the plain-text answer, defensively clean it.
    ' Never throws (failures -> NormalizationResult.Failure); cancellation re-thrown.
    Private Async Function PostNormalizeAsync(
            payloadJson As String,
            cancellationToken As CancellationToken) As Task(Of NormalizationResult)

        Try
            Using content As New StringContent(payloadJson, Encoding.UTF8, "application/json")
                Using requestMsg As New HttpRequestMessage(HttpMethod.Post, BuildCompletionsUrl()) With {.Content = content}
                    Dim apiKey = EffectiveApiKey()
                    If Not String.IsNullOrEmpty(apiKey) Then
                        requestMsg.Headers.Authorization = New AuthenticationHeaderValue("Bearer", apiKey)
                    End If
                    Using response = Await _httpClient.SendAsync(requestMsg, cancellationToken).ConfigureAwait(False)
                        If Not response.IsSuccessStatusCode Then
                            Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                            _logger.LogWarning("Normalize /chat/completions returned {Status}", CInt(response.StatusCode))
                            Return NormalizationResult.Failure($"HTTP {CInt(response.StatusCode)}: {Truncate(body, 300)}")
                        End If
                        Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                            Using doc = Await JsonDocument.ParseAsync(stream, cancellationToken:=cancellationToken).ConfigureAwait(False)
                                Dim parsed = LlmClient.ParseChatCompletionResponse("", doc.RootElement)
                                If Not parsed.Succeeded Then
                                    Return NormalizationResult.Failure(parsed.ErrorMessage)
                                End If
                                Dim cleaned = CleanNormalizedText(parsed.RawText)
                                If String.IsNullOrWhiteSpace(cleaned) Then
                                    Dim reason = If(parsed.FinishReason, "")
                                    Dim hint = If(String.Equals(reason, "length", StringComparison.OrdinalIgnoreCase),
                                                  " — the token budget was exhausted; raise LlmNormalize:MaxTokens (or Llm:NormalizeMaxTokens)", "")
                                    Return NormalizationResult.Failure(
                                            $"Model returned an empty normalization (finish_reason: " &
                                            $"{If(reason = "", "unknown", reason)}){hint}.")
                                End If
                                Return NormalizationResult.Success(cleaned)
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex, "Normalize /chat/completions failed")
            Return NormalizationResult.Failure(ex.Message)
        End Try
    End Function

    ' Dictionary payload (not an anonymous type) so reasoning_effort can be
    ' emitted conditionally — present only when EffectiveReasoningEffort is
    ' non-empty, matching LlmClient.BuildRequestPayload's wire shape.
    Friend Function BuildNormalizePayload(usable As IReadOnlyList(Of String)) As Object
        Dim payload As New Dictionary(Of String, Object) From {
            {"model", EffectiveModel()},
            {"messages", New Object() {
                New With {.role = "system", .content = PromptBuilder.NormalizeSystemPrompt},
                New With {.role = "user", .content = PromptBuilder.BuildNormalizeUserMessage(usable)}
            }},
            {"temperature", EffectiveTemperature()},
            {"max_tokens", EffectiveMaxTokens()}
        }

        ' EnableReasoning is the master switch: off => never send reasoning_effort
        ' (non-reasoning normalize model), regardless of the effort value.
        Dim effort = EffectiveReasoningEffort()
        If _normalizeOptions.EnableReasoning AndAlso Not String.IsNullOrWhiteSpace(effort) Then
            payload("reasoning_effort") = effort.Trim()
        End If

        Return payload
    End Function

    ' Same options/wire shape as BuildNormalizePayload, but the single-concept
    ' system prompt + a one-line user message (the concept to canonicalize).
    Friend Function BuildConceptNormalizePayload(concept As String) As Object
        Dim payload As New Dictionary(Of String, Object) From {
            {"model", EffectiveModel()},
            {"messages", New Object() {
                New With {.role = "system", .content = PromptBuilder.ConceptNormalizeSystemPrompt},
                New With {.role = "user", .content = PromptBuilder.BuildConceptNormalizeUserMessage(concept)}
            }},
            {"temperature", EffectiveTemperature()},
            {"max_tokens", EffectiveMaxTokens()}
        }

        Dim effort = EffectiveReasoningEffort()
        If _normalizeOptions.EnableReasoning AndAlso Not String.IsNullOrWhiteSpace(effort) Then
            payload("reasoning_effort") = effort.Trim()
        End If

        Return payload
    End Function

    Friend Function BuildCompletionsUrl() As String
        Dim overrideUrl = If(_normalizeOptions.BaseUrl, "").Trim()
        Dim baseUrl = If(overrideUrl <> "", overrideUrl, If(_options.BaseUrl, "")).TrimEnd("/"c)
        If String.IsNullOrEmpty(baseUrl) Then baseUrl = "http://localhost:8080/v1"
        Return $"{baseUrl}/chat/completions"
    End Function

    Friend Function EffectiveModel() As String
        Dim m = If(_normalizeOptions.Model, "").Trim()
        Return If(m <> "", m, If(_options.Model, ""))
    End Function

    Friend Function EffectiveApiKey() As String
        Dim k = If(_normalizeOptions.ApiKey, "").Trim()
        Return If(k <> "", k, If(_options.ApiKey, ""))
    End Function

    Friend Function EffectiveTemperature() As Double
        Return If(_normalizeOptions.Temperature, _options.Temperature)
    End Function

    ' Normalize reasoning effort: the LlmNormalize override when non-empty,
    ' otherwise the main extraction LlmOptions value. Returns "" only when both
    ' are blank, in which case the field is omitted from the payload.
    Friend Function EffectiveReasoningEffort() As String
        Dim e = If(_normalizeOptions.ReasoningEffort, "").Trim()
        Return If(e <> "", e, If(_options.ReasoningEffort, ""))
    End Function

    ' LlmNormalize:MaxTokens takes precedence; falls back to the legacy
    ' Llm:NormalizeMaxTokens so existing deployments don't break.
    Friend Function EffectiveMaxTokens() As Integer
        Return If(_normalizeOptions.MaxTokens, _options.NormalizeMaxTokens)
    End Function

    ' The prompt asks for plain text, but models occasionally wrap the answer
    ' in a code fence or quotes — strip those so the caller gets clean text.
    Friend Shared Function CleanNormalizedText(raw As String) As String
        Dim text = If(raw, "").Trim()
        If text.StartsWith("```", StringComparison.Ordinal) Then
            Dim firstBreak = text.IndexOf(vbLf, StringComparison.Ordinal)
            text = If(firstBreak >= 0, text.Substring(firstBreak + 1), "")
            Dim lastFence = text.LastIndexOf("```", StringComparison.Ordinal)
            If lastFence >= 0 Then text = text.Substring(0, lastFence)
            text = text.Trim()
        End If
        If text.Length >= 2 AndAlso text.StartsWith("""", StringComparison.Ordinal) AndAlso
           text.EndsWith("""", StringComparison.Ordinal) Then
            text = text.Substring(1, text.Length - 2).Trim()
        End If
        Return text
    End Function

    Private Shared Function Truncate(s As String, maxLen As Integer) As String
        If String.IsNullOrEmpty(s) Then Return ""
        If s.Length <= maxLen Then Return s
        Return s.Substring(0, maxLen) & "..."
    End Function

End Class

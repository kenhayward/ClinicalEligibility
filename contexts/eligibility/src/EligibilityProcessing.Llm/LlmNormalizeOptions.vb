' Per-call override for the Authoring criterion-normalization LLM endpoint.
'
' The normalize prompt is short and the answer is a single sentence, so a much
' smaller (and faster) model than the extraction one is often sufficient. This
' options class lets a deployment point the normalizer at a different host /
' model / key — and tune its temperature, token budget, retry, and timeout —
' independently of the main extraction LlmOptions.
'
' Strings default to empty and numerics to Nothing; CriteriaNormalizer (and the
' normalizer's HttpClient resilience pipeline) fall back to the matching
' LlmOptions value when the override is unset, so an unset section preserves
' today's behavior. MaxTokens here replaces the awkwardly-placed
' Llm:NormalizeMaxTokens; the latter remains as the fallback for now so
' existing deployments keep working.

Public Class LlmNormalizeOptions

    Public Property BaseUrl As String = ""
    Public Property ApiKey As String = ""
    Public Property Model As String = ""
    Public Property Temperature As Double? = Nothing
    Public Property MaxTokens As Integer? = Nothing
    Public Property TimeoutSeconds As Integer? = Nothing
    Public Property RetryCount As Integer? = Nothing
    Public Property RetryDelaySeconds As Integer? = Nothing

    ' Reasoning effort for the normalize call, mirroring LlmOptions.ReasoningEffort
    ' for the main extraction call. Sent as the OpenAI `reasoning_effort` field on
    ' the chat-completions request only when non-empty, so non-reasoning models are
    ' unaffected. Unlike the inherit-when-blank overrides above, this carries its
    ' own default ("low") rather than Nothing: the normalize answer is one short
    ' sentence, so the cheapest reasoning level is the sensible default. Set it
    ' blank to fall back to LlmOptions.ReasoningEffort; blank both to omit the
    ' field entirely.
    Public Property ReasoningEffort As String = "low"

    ' Master switch for the normalize call's `reasoning_effort` field, mirroring
    ' LlmOptions.EnableReasoning for the extraction call. When False, the field is
    ' never sent on the normalize request regardless of ReasoningEffort — treat
    ' the normalize endpoint as a plain non-reasoning model. Independent of the
    ' extraction toggle (so the two calls can use different model types) and
    ' surfaced as its own checkbox in the Runtime Parameters panel. Default True.
    Public Property EnableReasoning As Boolean = True

End Class

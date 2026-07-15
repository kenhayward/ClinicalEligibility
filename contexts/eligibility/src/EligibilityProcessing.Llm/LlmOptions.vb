' Configuration for the LLM chat-completions client.
'
' Architecture section 3.2 / spec section 2.4.1 + 2.4.4. Defaults track the
' production reference (Gemma 4 26B served via llama.cpp, 8-way concurrency,
' temperature 0.3). ApiKey is forwarded transparently and MUST be sourced from
' secret storage per spec section 6.5.

Public Class LlmOptions

    Public Property BaseUrl As String = "http://localhost:8080/v1"
    Public Property ApiKey As String = ""
    Public Property Model As String = "gemma-4-26B-A4B-it-Q8_0"
    Public Property Temperature As Double = 0.3
    Public Property MaxTokens As Integer = 8000

    ' Reasoning effort for "thinking" models (gpt-oss, OpenAI o-series, and
    ' compatible llama.cpp / LM Studio deployments) that honour the OpenAI
    ' `reasoning_effort` field. Sent on the chat-completions call only when
    ' non-empty, so non-reasoning models are unaffected. This is the *first*
    ' attempt's effort. Code default "medium" is the safe single-level value;
    ' the shipped appsettings runs "low" + escalation (below). At "low", gpt-oss
    ' reasons about its own budget and bails on long trials, emitting `[]`
    ' (parse_empty) — which is exactly what escalation recovers. Set empty to
    ' omit the field; "low" / "high" are the other OpenAI-recognised values.
    Public Property ReasoningEffort As String = "medium"

    ' Master switch for the OpenAI `reasoning_effort` field. When False, the
    ' field is NEVER sent (and escalation is suppressed) regardless of
    ' ReasoningEffort / EnableReasoningEscalation — i.e. treat the endpoint as a
    ' plain non-reasoning model. When True (default), behaviour is governed by
    ' ReasoningEffort + the escalation settings below. This is the on/off toggle
    ' surfaced as a checkbox in the Runtime Parameters panel, letting an operator
    ' swap between a reasoning model (gpt-oss, o-series) and a non-reasoning
    ' instruct model without editing the effort level.
    Public Property EnableReasoning As Boolean = True

    ' Adaptive reasoning escalation. When enabled, a trial whose first attempt
    ' parses to an empty array (the model bailed at ReasoningEffort) is retried
    ' once at EscalateReasoningEffort before being accepted as parse_empty. This
    ' lets the common case run fast at "low" while complex trials transparently
    ' get more reasoning. Only empty_array escalates — invalid_json is usually
    ' truncation, which more reasoning makes worse. Toggle with the flag; the
    ' two effort levels can stay fixed at low/medium.
    Public Property EnableReasoningEscalation As Boolean = False
    Public Property EscalateReasoningEffort As String = "medium"
    ' Per-attempt LLM call timeout. The resilience pipeline (CompositionRoot)
    ' applies this as a timeout strategy around each attempt, so the configured
    ' RetryCount attempts each get the full budget.
    Public Property TimeoutSeconds As Integer = 1200
    Public Property RetryCount As Integer = 2
    Public Property RetryDelaySeconds As Integer = 5

    ' VESTIGIAL: not wired to anything. The pipeline's actual parallelism is
    ' OrchestratorOptions.LlmConcurrencyCap (bound from the Pipeline section and
    ' used as Parallel.ForEachAsync MaxDegreeOfParallelism). This field is left
    ' only to avoid breaking config that still sets Llm:ConcurrencyCap; do NOT
    ' use it to throttle and do NOT surface it as "the" concurrency cap.
    Public Property ConcurrencyCap As Integer = 8

    ' Token budget for the Authoring criterion-normalization call. A normalized
    ' criterion is one short sentence, but the budget must also cover any
    ' reasoning the model emits before the answer — otherwise a "thinking"
    ' model exhausts the budget and returns empty content. Raise this if
    ' normalization reports an empty result with finish_reason = length.
    Public Property NormalizeMaxTokens As Integer = 2000

End Class

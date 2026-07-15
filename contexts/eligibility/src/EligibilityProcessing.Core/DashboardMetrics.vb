Imports System.Collections.Generic

' Aggregate counters surfaced on the dashboard's headline panel. Computed in a
' single round-trip against the output database. Every field is non-negative;
' an empty database yields zeros / 0.0 resolution rate (the gateway handles
' the divide-by-zero protection).
'
' "Latest attempt per nct_id" semantics — StudiesSuccessful and
' StudiesFailedLatest both group eligibility_study by nct_id and look at the
' most recent row's status only. That matches the Studies-tab "Hide
' superseded attempts" view: a trial whose latest attempt is success counts
' as successful regardless of earlier failed attempts; a trial whose latest
' attempt is still parse_invalid_json counts as failed even if the same
' nct_id had succeeded in some far older run.
'
' StudiesFailedLatest excludes valid terminal states alongside success and
' running: parse_empty (LLM legitimately said "no criteria here") and
' cancelled (operator stopped the run). Only llm_failed, parse_invalid_json,
' persist_failed, and the generic 'failed' status count as failure.
'
' PromptTokens / CompletionTokens sum llm_prompt_tokens and
' llm_completion_tokens respectively across every eligibility_study row
' (NULLs treated as 0). Includes failed and cancelled attempts — any token
' billed by the LLM counts, regardless of pipeline outcome. TokensUsed is the
' combined total, kept as a convenience for callers that want one number.
'
' StudiesWithoutEmbeddings is the count of trials that have been successfully
' processed by the eligibility pipeline but have no row in
' eligibility_study_embedding — i.e. the backlog that `embed-studies` (CLI)
' would pick up on its next run.

Public NotInheritable Class DashboardMetrics

    Public Sub New(
            eligibilityRowCount As Long,
            studiesSuccessful As Long,
            studiesFailedLatest As Long,
            resolutionRate As Double,
            promptTokens As Long,
            completionTokens As Long,
            failuresByStatus As IReadOnlyDictionary(Of String, Long),
            studiesWithoutEmbeddings As Long,
            parseEmpty As Long)
        Me.EligibilityRowCount = eligibilityRowCount
        Me.StudiesSuccessful = studiesSuccessful
        Me.StudiesFailedLatest = studiesFailedLatest
        Me.ResolutionRate = resolutionRate
        Me.PromptTokens = promptTokens
        Me.CompletionTokens = completionTokens
        Me.FailuresByStatus = failuresByStatus
        Me.StudiesWithoutEmbeddings = studiesWithoutEmbeddings
        Me.ParseEmpty = parseEmpty
    End Sub

    Public ReadOnly Property EligibilityRowCount As Long
    Public ReadOnly Property StudiesSuccessful As Long
    Public ReadOnly Property StudiesFailedLatest As Long
    Public ReadOnly Property ResolutionRate As Double   ' 0.0 .. 1.0
    Public ReadOnly Property PromptTokens As Long
    Public ReadOnly Property CompletionTokens As Long

    ' Latest-attempt failure count split by status — the same "latest attempt
    ' per nct_id" grouping as StudiesFailedLatest, but keyed by the individual
    ' failure status (llm_failed / parse_invalid_json / persist_failed /
    ' failed). The values sum to StudiesFailedLatest. Lets the dashboard show
    ' one line per failure type instead of a single opaque total.
    Public ReadOnly Property FailuresByStatus As IReadOnlyDictionary(Of String, Long)

    ' Studies with persisted eligibility rows but no entry in
    ' eligibility_study_embedding — the backlog the `embed-studies` CLI command
    ' would process on its next run. Counts a study as "without embeddings" if
    ' *any* embedding row is missing, regardless of which model produced it,
    ' since the embedding table has one row per nct_id (model is metadata).
    Public ReadOnly Property StudiesWithoutEmbeddings As Long

    ' Studies whose latest attempt is parse_empty — the LLM call succeeded and
    ' returned valid JSON, but zero records survived parsing (the model judged
    ' the trial to have no discrete eligibility criteria). A VALID terminal
    ' state, not a failure: it is deliberately excluded from StudiesFailedLatest
    ' and from FailuresByStatus (whose values must keep summing to
    ' StudiesFailedLatest). Surfaced as its own line so operators can see how
    ' many trials produced empty output without it inflating the failure count.
    Public ReadOnly Property ParseEmpty As Long

    Public ReadOnly Property TokensUsed As Long
        Get
            Return PromptTokens + CompletionTokens
        End Get
    End Property

    Public Shared ReadOnly Empty As New DashboardMetrics(
        0, 0, 0, 0.0, 0, 0, New Dictionary(Of String, Long)(), 0, 0)

End Class

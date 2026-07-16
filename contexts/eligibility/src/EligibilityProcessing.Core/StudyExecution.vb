' One row from public.eligibility_study — the per-trial audit record. Keyed
' by (RunId, NctId): every time a batch processes a trial, a new row is
' inserted (status="running") before the LLM call and updated to its
' terminal state once the trial completes or fails.
'
' Status values (free text in the table, enumerated here for callers):
'   running            - in flight; finished_at = Nothing
'   success            - everything worked, persisted_row_count >= 0
'   llm_failed         - the LLM call returned a transport / model failure
'   parse_empty        - LLM returned content but parser emitted 0 records
'   parse_invalid_json - LLM output was unparseable (typically truncated)
'   persist_failed     - the DB write threw after parse succeeded
'   failed             - generic post-LLM, pre-persist failure (UMLS, scoring)
'   cancelled          - user cancellation interrupted the trial mid-flight
'   interrupted        - the host process stopped before this trial reached a
'                        terminal status, leaving the row stranded at 'running'.
'                        NOT written by the pipeline: the web host reconciles
'                        stale 'running' rows at startup. See
'                        PostgresGateway.ReconcileInterruptedStudiesAsync.

Public NotInheritable Class StudyExecution

    Public Const StatusRunning As String = "running"
    Public Const StatusSuccess As String = "success"
    Public Const StatusLlmFailed As String = "llm_failed"
    Public Const StatusParseEmpty As String = "parse_empty"           ' LLM returned valid JSON, parser emitted zero records (legitimate "nothing here")
    Public Const StatusParseInvalidJson As String = "parse_invalid_json" ' LLM output was unparseable — typically truncated by max_tokens
    Public Const StatusPersistFailed As String = "persist_failed"
    Public Const StatusFailed As String = "failed"           ' generic post-LLM, pre-persist failure (UMLS, scoring, etc.)
    Public Const StatusCancelled As String = "cancelled"

    ' Reconciled, not observed: the host died mid-trial and left the row at
    ' 'running', so NOTHING is known about the outcome - unlike StatusFailed,
    ' which records a failure we actually saw. Written only by
    ' PostgresGateway.ReconcileInterruptedStudiesAsync at web-host startup, once
    ' the row is older than the configured threshold. This literal is persisted:
    ' renaming it would be a data migration.
    Public Const StatusInterrupted As String = "interrupted"

    Public Sub New(
            runId As Guid,
            nctId As String,
            startedAt As DateTimeOffset,
            finishedAt As DateTimeOffset?,
            status As String,
            llmSucceeded As Boolean?,
            llmFinishReason As String,
            llmPromptTokens As Integer?,
            llmCompletionTokens As Integer?,
            parsedRecordCount As Integer?,
            persistedRowCount As Integer?,
            errorMessage As String,
            Optional llmRawResponse As String = "",
            Optional llmStoppedEos As Boolean? = Nothing,
            Optional llmStoppedLimit As Boolean? = Nothing,
            Optional llmStoppedWord As Boolean? = Nothing,
            Optional llmStoppingWord As String = "",
            Optional llmTruncated As Boolean? = Nothing,
            Optional llmMs As Integer? = Nothing,
            Optional umlsMs As Integer? = Nothing,
            Optional persistMs As Integer? = Nothing)
        Me.RunId = runId
        Me.NctId = If(nctId, "")
        Me.StartedAt = startedAt
        Me.FinishedAt = finishedAt
        Me.Status = If(status, "")
        Me.LlmSucceeded = llmSucceeded
        Me.LlmFinishReason = If(llmFinishReason, "")
        Me.LlmPromptTokens = llmPromptTokens
        Me.LlmCompletionTokens = llmCompletionTokens
        Me.ParsedRecordCount = parsedRecordCount
        Me.PersistedRowCount = persistedRowCount
        Me.ErrorMessage = If(errorMessage, "")
        Me.LlmRawResponse = If(llmRawResponse, "")
        Me.LlmStoppedEos = llmStoppedEos
        Me.LlmStoppedLimit = llmStoppedLimit
        Me.LlmStoppedWord = llmStoppedWord
        Me.LlmStoppingWord = If(llmStoppingWord, "")
        Me.LlmTruncated = llmTruncated
        Me.LlmMs = llmMs
        Me.UmlsMs = umlsMs
        Me.PersistMs = persistMs
    End Sub

    Public ReadOnly Property RunId As Guid
    Public ReadOnly Property NctId As String
    Public ReadOnly Property StartedAt As DateTimeOffset
    Public ReadOnly Property FinishedAt As DateTimeOffset?
    Public ReadOnly Property Status As String
    Public ReadOnly Property LlmSucceeded As Boolean?
    Public ReadOnly Property LlmFinishReason As String
    Public ReadOnly Property LlmPromptTokens As Integer?
    Public ReadOnly Property LlmCompletionTokens As Integer?
    Public ReadOnly Property ParsedRecordCount As Integer?
    Public ReadOnly Property PersistedRowCount As Integer?
    Public ReadOnly Property ErrorMessage As String
    Public ReadOnly Property LlmRawResponse As String   ' raw model output, before parsing

    ' llama.cpp vendor diagnostics persisted to public.eligibility_study (V9).
    ' All Nothing / empty for OpenAI-proper deployments and for rows written
    ' before V9 — the History tab renders whichever fields are present.
    Public ReadOnly Property LlmStoppedEos As Boolean?
    Public ReadOnly Property LlmStoppedLimit As Boolean?
    Public ReadOnly Property LlmStoppedWord As Boolean?
    Public ReadOnly Property LlmStoppingWord As String
    Public ReadOnly Property LlmTruncated As Boolean?

    ' Per-phase wall-clock instrumentation (milliseconds), persisted to
    ' eligibility_study (V16). Diagnostic for concurrency tuning — the Runs
    ' table averages these per run to show the LLM/UMLS/persist split. Nullable:
    ' a phase the trial never reached (e.g. persist after an LLM failure) and
    ' rows written before V16 stay NULL.
    Public ReadOnly Property LlmMs As Integer?
    Public ReadOnly Property UmlsMs As Integer?
    Public ReadOnly Property PersistMs As Integer?

    Public ReadOnly Property Duration As TimeSpan?
        Get
            If FinishedAt.HasValue Then Return FinishedAt.Value - StartedAt
            Return Nothing
        End Get
    End Property

    ' Completion tokens per second — a rough throughput guide for the History
    ' tab. Deliberately uses *completion* tokens only over the full trial
    ' Duration: it ignores prompt/prefill (PP) tokens and folds any reasoning
    ' tokens into the decode time, so it understates raw decode tok/s but tracks
    ' overall per-trial performance well enough for tuning. Nothing when the
    ' trial is still running (no Duration), when no completion-token count was
    ' captured, or when Duration is non-positive (clock skew / sub-ms guard).
    Public ReadOnly Property CompletionTokensPerSecond As Double?
        Get
            If Not LlmCompletionTokens.HasValue Then Return Nothing
            If Not Duration.HasValue Then Return Nothing
            Dim seconds = Duration.Value.TotalSeconds
            If seconds <= 0 Then Return Nothing
            Return LlmCompletionTokens.Value / seconds
        End Get
    End Property

End Class

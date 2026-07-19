' One row in public.eligibility_run (architecture section 2.2 / spec section 6.6).
'
' Captures per-batch observability data so a historical run table can be queried
' for trend analysis. The reference n8n implementation tracks this informally
' ("Run 75 et al."); we persist it.

Public NotInheritable Class RunMetrics

    Public Sub New(
            runId As Guid,
            startedAt As DateTimeOffset,
            endedAt As DateTimeOffset?,
            triggerSource As String,
            studyCount As Integer,
            studiesProcessed As Integer,
            rowsPersisted As Integer,
            resolutionRate As Double,
            status As String,
            errorSummary As String,
            Optional completionTokens As Long = 0,
            Optional concurrencyCap As Integer? = Nothing,
            Optional avgLlmMs As Double? = Nothing,
            Optional avgUmlsMs As Double? = Nothing,
            Optional avgPersistMs As Double? = Nothing)
        Me.RunId = runId
        Me.StartedAt = startedAt
        Me.EndedAt = endedAt
        Me.TriggerSource = triggerSource
        Me.StudyCount = studyCount
        Me.StudiesProcessed = studiesProcessed
        Me.RowsPersisted = rowsPersisted
        Me.ResolutionRate = resolutionRate
        Me.Status = status
        Me.ErrorSummary = errorSummary
        Me.CompletionTokens = completionTokens
        Me.ConcurrencyCap = concurrencyCap
        Me.AvgLlmMs = avgLlmMs
        Me.AvgUmlsMs = avgUmlsMs
        Me.AvgPersistMs = avgPersistMs
    End Sub

    Public ReadOnly Property RunId As Guid
    Public ReadOnly Property StartedAt As DateTimeOffset
    Public ReadOnly Property EndedAt As DateTimeOffset?  ' Nullable: still in-flight when null
    Public ReadOnly Property TriggerSource As String     ' "form" | "webhook" | "subworkflow"
    Public ReadOnly Property StudyCount As Integer       ' requested batch size
    Public ReadOnly Property StudiesProcessed As Integer ' studies actually persisted
    Public ReadOnly Property RowsPersisted As Integer    ' total criterion rows
    Public ReadOnly Property ResolutionRate As Double    ' [0, 1] rounded to 3 dp
    ' See RunStatus for the vocabulary. "running" is written first (in-flight,
    ' EndedAt = Nothing) and overwritten on completion; "interrupted" is only
    ' ever written by a manual resolve from the Runs tab.
    Public ReadOnly Property Status As String            ' running | success | failed | cancelled | interrupted
    Public ReadOnly Property ErrorSummary As String      ' empty when status = "success"

    ' Sum of llm_completion_tokens across the run's eligibility_study rows.
    ' Populated only on the READ path (GetRecentRunsAsync joins eligibility_study);
    ' the orchestrator's write-path RunMetrics leave this 0 because
    ' eligibility_run does not store a token total. 0 on the write side is
    ' harmless — only the History (Runs) table reads it.
    Public ReadOnly Property CompletionTokens As Long

    ' The trial concurrency cap (Pipeline:LlmConcurrencyCap) in effect for this
    ' run, persisted to eligibility_run so the History table records which cap
    ' produced which throughput. Nothing for runs recorded before migration V15.
    Public ReadOnly Property ConcurrencyCap As Integer?

    ' Average per-trial phase wall-clock (ms) across the run's SUCCESSFUL trials,
    ' aggregated at read time from eligibility_study (V16). The Runs table shows
    ' the LLM/UMLS/persist split so concurrency sweeps reveal where time goes and
    ' whether a phase inflates under load. Nothing when no timed trials exist yet.
    Public ReadOnly Property AvgLlmMs As Double?
    Public ReadOnly Property AvgUmlsMs As Double?
    Public ReadOnly Property AvgPersistMs As Double?

    ' Aggregate decode throughput for the whole batch: total completion tokens
    ' over the run's wall clock. This is the concurrency-relevant number — adding
    ' parallel slots shortens wall clock for a fixed study count, so this rises
    ' even though per-trial tok/s is roughly flat. Like the per-trial guide it
    ' ignores prompt/prefill and folds reasoning into decode time. Nothing while
    ' the run is in flight (no EndedAt) or when wall clock is non-positive.
    Public ReadOnly Property CompletionTokensPerSecond As Double?
        Get
            If Not EndedAt.HasValue Then Return Nothing
            Dim seconds = (EndedAt.Value - StartedAt).TotalSeconds
            If seconds <= 0 Then Return Nothing
            Return CompletionTokens / seconds
        End Get
    End Property

End Class

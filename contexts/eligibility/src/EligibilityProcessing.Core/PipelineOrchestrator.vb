Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Runtime.ExceptionServices
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions

' End-to-end batch pipeline. Implements spec section 5 (the 16-step sequence)
' on top of the four boundary contracts (gateway, LLM, UMLS, notifications)
' plus the two pure-logic helpers (parser, scorer).
'
' Concurrency model (spec section 7.1):
'   - Trial-level: Parallel.ForEachAsync, capped at OrchestratorOptions.LlmConcurrencyCap.
'   - Criterion-level (within a trial): sequential. A trial's criterion count
'     is bounded by the LLM token budget (no fixed entry cap — spec section
'     2.4.2 rule 1), so each trial is a bounded amount of UMLS work that hits
'     the UmlsCache decorator for repeated concepts.
'
' Failure semantics (spec section 2.4.4 / 6.4):
'   - A single trial failure (LLM, parse, UMLS, persistence) records the trial
'     to public.eligibility_failed and continues the batch.
'   - User cancellation propagates — never swallowed as a per-trial failure.
'   - Catastrophic failure (e.g. DB unreachable before any trial runs) writes
'     a "failed" RunMetrics row, returns a BatchResult with status="failed",
'     and does NOT throw.
'
' VB.NET notes:
'   - Async lambdas cannot return ValueTask, so the Parallel.ForEachAsync body
'     is a non-async lambda that wraps an Async Function helper in New ValueTask.
'   - Await is not allowed inside Catch blocks; failure paths capture the
'     exception into a local and await follow-up work after the Try/End Try.

Public NotInheritable Class PipelineOrchestrator

    Private ReadOnly _gateway As IPostgresGateway
    Private ReadOnly _llmClient As ILlmClient
    Private ReadOnly _umlsClient As IUmlsClient
    Private ReadOnly _parser As LlmResponseParser
    Private ReadOnly _scorer As UmlsMatchScorer
    Private ReadOnly _notificationSink As INotificationSink
    Private ReadOnly _hooks As IPipelineHooks
    Private ReadOnly _embeddingClient As IEmbeddingClient
    Private ReadOnly _options As OrchestratorOptions
    Private ReadOnly _logger As ILogger(Of PipelineOrchestrator)

    Public Sub New(
            gateway As IPostgresGateway,
            llmClient As ILlmClient,
            umlsClient As IUmlsClient,
            Optional parser As LlmResponseParser = Nothing,
            Optional scorer As UmlsMatchScorer = Nothing,
            Optional notificationSink As INotificationSink = Nothing,
            Optional hooks As IPipelineHooks = Nothing,
            Optional embeddingClient As IEmbeddingClient = Nothing,
            Optional options As OrchestratorOptions = Nothing,
            Optional logger As ILogger(Of PipelineOrchestrator) = Nothing)
        If gateway Is Nothing Then Throw New ArgumentNullException(NameOf(gateway))
        If llmClient Is Nothing Then Throw New ArgumentNullException(NameOf(llmClient))
        If umlsClient Is Nothing Then Throw New ArgumentNullException(NameOf(umlsClient))
        _gateway = gateway
        _llmClient = llmClient
        _umlsClient = umlsClient
        ' Optional — when no embedding client is wired the inline embedding
        ' step is skipped and the Authoring similarity index is populated only
        ' by the CLI embed-studies backfill.
        _embeddingClient = embeddingClient
        _parser = If(parser, New LlmResponseParser())
        _scorer = If(scorer, New UmlsMatchScorer())
        _notificationSink = If(notificationSink, NullNotificationSink.Instance)
        _hooks = If(hooks, NullPipelineHooks.Instance)
        _options = If(options, New OrchestratorOptions())
        _logger = If(logger, CType(NullLogger(Of PipelineOrchestrator).Instance, ILogger(Of PipelineOrchestrator)))
    End Sub

    Public Async Function ExecuteAsync(
            config As RunConfiguration,
            cancellationToken As CancellationToken) As Task(Of BatchResult)

        If config Is Nothing Then Throw New ArgumentNullException(NameOf(config))

        Dim runId = Guid.NewGuid()
        Dim startedAt = DateTimeOffset.UtcNow
        Dim failedNctIds As New ConcurrentBag(Of String)
        Dim counters As New RunCounters()

        _logger.LogInformation("Starting batch run {RunId} (StudyCount={Count}, Trigger={Trigger})",
                runId, config.StudyCount, config.TriggerSource)

        ' Write an initial run row with status='running' so the dashboard's
        ' Runs tab shows the in-flight run immediately. Best-effort — a DB
        ' failure here logs a warning but does not abort the run. The same
        ' UPSERT path will be re-fired after every trial completes and once
        ' more at the end with the terminal status.
        Await TryRecordRunAsync(
                BuildRunMetrics(runId, startedAt, endedAt:=Nothing, config, counters,
                                status:="running", errorSummary:=""),
                cancellationToken).ConfigureAwait(False)

        Await SafeNotifyAsync(
                Function() _hooks.OnBatchStartedAsync(runId, config.StudyCount, cancellationToken),
                "OnBatchStarted", cancellationToken).ConfigureAwait(False)

        Dim catastrophic As Exception = Nothing
        Dim cancelDispatch As ExceptionDispatchInfo = Nothing
        Try
            Dim trials As IReadOnlyList(Of Trial)

            If config.IsRerun Then
                ' --- Re-run path: skip watermark + batch select. Fetch every
                ' requested trial directly by NCT_ID and assemble the batch.
                ' The watermark is left untouched because a re-run is "out of
                ' order" by definition. List may be one (dashboard's Run Trial
                ' button) or many (Studies tab's Rerun selection); semantics
                ' are identical. ---
                _logger.LogInformation(
                        "Run {RunId} is a re-run for {Count} trial(s): {NctIds}",
                        runId, config.RerunNctIds.Count, String.Join(", ", config.RerunNctIds))

                ' Single batch round-trip rather than one query per trial. The AACT
                ' source is typically remote, so N sequential fetches for a large
                ' selection (hundreds of trials) left a multi-second window with no
                ' history rows and an idle LLM before processing began -- which read
                ' as a hung run and got cancelled. ANY(@ids) collapses that to one
                ' query. Diff the returned set against the request so genuinely
                ' missing trials are still logged individually.
                Dim collected = Await _gateway.GetSourceTrialsAsync(
                        config.RerunNctIds, cancellationToken).ConfigureAwait(False)

                Dim foundIds As New HashSet(Of String)(
                        collected.Select(Function(t) t.NctId), StringComparer.OrdinalIgnoreCase)
                For Each nctId In config.RerunNctIds
                    If Not foundIds.Contains(nctId) Then
                        _logger.LogWarning(
                                "Run {RunId} re-run target {Nct} not found in ctgov.eligibilities; skipping",
                                runId, nctId)
                    End If
                Next
                trials = collected
            Else
                ' --- Spec section 5 step 2-4: select next batch.
                ' Fetch the "already attempted" set from eligibility_study and
                ' anti-join against it inside SelectNextTrialsAsync. This
                ' replaces the older `nct_id > MAX(nct_id)` cutoff — that
                ' approach silently breaks once Recent-direction batches
                ' enter the mix (MAX would jump to recent NCT_IDs and
                ' Forward-mode would skip the gap). The anti-join keeps
                ' both directions correct.
                '
                ' For a fresh DB the exclusion set is empty and the result
                ' is identical to the original behaviour; for a DB with N
                ' attempted trials it picks the next StudyCount in the
                ' requested direction. ---
                Dim excluded = Await _gateway.GetAttemptedNctIdsAsync(
                        cancellationToken).ConfigureAwait(False)

                trials = Await _gateway.SelectNextTrialsAsync(
                        excluded, config.Direction, config.StudyCount,
                        cancellationToken).ConfigureAwait(False)

                _logger.LogInformation(
                        "Run {RunId} selected {N} trials ({Direction}; {Excluded} excluded)",
                        runId, trials.Count, config.Direction, excluded.Count)
            End If

            ' --- Steps 5-14: per-trial processing in parallel. ---
            If trials.Count > 0 Then
                Dim parallelOptions As New ParallelOptions With {
                        .MaxDegreeOfParallelism = _options.LlmConcurrencyCap,
                        .CancellationToken = cancellationToken}

                ' Non-async lambda wraps an Async helper because VB.NET cannot
                ' produce an Async Function As ValueTask directly. The lambda
                ' closes over startedAt and config so each trial worker can
                ' write a fresh progress snapshot into eligibility_run after
                ' it finishes.
                Await Parallel.ForEachAsync(trials, parallelOptions,
                        Function(trial As Trial, innerCt As CancellationToken) As ValueTask
                            Return New ValueTask(ProcessTrialAsync(runId, startedAt, config, trial, failedNctIds, counters, innerCt))
                        End Function).ConfigureAwait(False)
            End If
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            ' Capture and defer the await — VB.NET does not allow Await
            ' inside Catch. The cancellation finalise below runs outside the
            ' Try/Catch so the await is legal.
            cancelDispatch = ExceptionDispatchInfo.Capture(ex)
        Catch ex As Exception
            catastrophic = ex
        End Try

        ' Cancellation path: finalise the run row as status='cancelled' before
        ' re-throwing. Without this the row stays at status='running' forever
        ' and the dashboard shows a ghost in-flight run. Use a non-cancelled
        ' token so the write actually fires; swallow secondary failures so the
        ' original OCE remains the propagated exception.
        If cancelDispatch IsNot Nothing Then
            Try
                Await _gateway.RecordRunAsync(
                        BuildRunMetrics(runId, startedAt, endedAt:=DateTimeOffset.UtcNow,
                                        config, counters,
                                        status:="cancelled",
                                        errorSummary:="Cancelled by host or user"),
                        CancellationToken.None).ConfigureAwait(False)
            Catch writeEx As Exception
                _logger.LogWarning(writeEx, "Failed to record cancellation row for {RunId}", runId)
            End Try
            cancelDispatch.Throw()
        End If

        ' --- Steps 15-16: metrics + notifications (once per batch). ---
        Dim endedAt = DateTimeOffset.UtcNow
        Dim status As String = If(catastrophic Is Nothing, "success", "failed")
        Dim errorSummary As String = If(catastrophic Is Nothing, "", catastrophic.Message)
        Dim metrics = BuildRunMetrics(runId, startedAt, endedAt, config, counters, status, errorSummary)

        Await TryRecordRunAsync(metrics, cancellationToken).ConfigureAwait(False)

        Dim result = New BatchResult(metrics, failedNctIds.ToArray())

        If catastrophic IsNot Nothing Then
            _logger.LogError(catastrophic, "Run {RunId} failed catastrophically", runId)
            Await TrySendNotificationAsync(AddressOf _notificationSink.SendErrorAsync, result, cancellationToken).ConfigureAwait(False)
        Else
            _logger.LogInformation(
                    "Run {RunId} complete: studies={S}, rows={R}, resolved={Resolved}, failed={F}",
                    runId, counters.TrialsProcessed, counters.RowsPersisted,
                    counters.ResolvedCount, failedNctIds.Count)
            Await TrySendNotificationAsync(AddressOf _notificationSink.SendCompletionAsync, result, cancellationToken).ConfigureAwait(False)
            If failedNctIds.Count > 0 Then
                Await TrySendNotificationAsync(AddressOf _notificationSink.SendErrorAsync, result, cancellationToken).ConfigureAwait(False)
            End If
        End If

        Await SafeNotifyAsync(
                Function() _hooks.OnBatchCompletedAsync(result, cancellationToken),
                "OnBatchCompleted", cancellationToken).ConfigureAwait(False)

        Return result
    End Function

    ' One trial end-to-end: extract criteria via the LLM, resolve each against
    ' UMLS, persist the trial transactionally. Catches and records per-trial
    ' failures; never throws back to Parallel.ForEachAsync unless the caller
    ' has cancelled.
    '
    ' Audit lifecycle (public.eligibility_study via StartStudy / FinishStudy):
    '   StartStudy fires before the LLM call with status='running'.
    '   FinishStudy fires in every exit path with the terminal status, the
    '   LLM token counts, parsed record count, and persisted row count. Audit
    '   writes are best-effort — failures log a warning but never abort the
    '   trial. The two helpers (TryStartStudyAsync / TryFinishStudyAsync) own
    '   that swallow.
    Private Async Function ProcessTrialAsync(
            runId As Guid,
            runStartedAt As DateTimeOffset,
            config As RunConfiguration,
            trial As Trial,
            failedNctIds As ConcurrentBag(Of String),
            counters As RunCounters,
            cancellationToken As CancellationToken) As Task

        Dim startedAt = DateTimeOffset.UtcNow
        Await TryStartStudyAsync(runId, trial.NctId, startedAt, cancellationToken).ConfigureAwait(False)

        ' Snapshot the trial's AACT study metadata + eligibility detail into the
        ' output DB (public.eligibility_study_detail). Fires here, before the
        ' LLM call, so even a trial that later fails still gets a snapshot;
        ' refreshed on every run. Best-effort — see TryCaptureStudySnapshotAsync.
        Await TryCaptureStudySnapshotAsync(trial.NctId, cancellationToken).ConfigureAwait(False)

        Await SafeNotifyAsync(
                Function() _hooks.OnTrialStartedAsync(runId, trial.NctId, cancellationToken),
                "OnTrialStarted", cancellationToken).ConfigureAwait(False)

        ' Per-stage diagnostics that feed both FinishStudy and the per-batch
        ' counters. Initialised to "implicit success" and specialised as the
        ' stages run; if a Catch fires below it picks the most-specific status
        ' already set, falling back to "failed".
        Dim trialFailure As Exception = Nothing
        Dim status As String = StudyExecution.StatusSuccess
        Dim rowsPersistedHere As Integer = 0
        Dim parsedRecordCount As Integer = 0
        Dim llmSucceeded As Boolean? = Nothing
        Dim llmFinishReason As String = ""
        Dim promptTokens As Integer? = Nothing
        Dim completionTokens As Integer? = Nothing
        Dim llmRawResponse As String = ""
        Dim llmStoppedEos As Boolean? = Nothing
        Dim llmStoppedLimit As Boolean? = Nothing
        Dim llmStoppedWord As Boolean? = Nothing
        Dim llmStoppingWord As String = ""
        Dim llmTruncated As Boolean? = Nothing
        Dim errorMessage As String = ""

        ' Per-phase wall-clock instrumentation (ms). Accumulated as each phase
        ' runs and persisted on the audit row (V16) so the Runs table can show
        ' the LLM/UMLS/persist split across a concurrency sweep. Nullable: a
        ' phase the trial never reaches stays Nothing.
        Dim llmMs As Integer? = Nothing
        Dim umlsMs As Integer? = Nothing
        Dim persistMs As Integer? = Nothing

        ' VB doesn't permit Await inside a Catch — capture the cancellation
        ' exception here and re-throw after we've finished the audit-row write
        ' below.
        Dim cancelDispatch As ExceptionDispatchInfo = Nothing

        Try
            ' --- Step 5: LLM call. ---
            Dim request = New LlmRequest(trial.NctId, trial.Criteria)
            Dim llmWatch = Stopwatch.StartNew()
            Dim response = Await _llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(False)
            llmWatch.Stop()
            llmMs = CInt(llmWatch.ElapsedMilliseconds)
            llmSucceeded = response.Succeeded
            llmFinishReason = response.FinishReason
            promptTokens = response.PromptTokens
            completionTokens = response.CompletionTokens
            ' Capture the raw text the model emitted (empty when the call
            ' didn't succeed). Stored on the audit row so operators can
            ' inspect exactly what the parser saw — critical for diagnosing
            ' parse_invalid_json / parse_empty.
            llmRawResponse = response.RawText
            ' Vendor stop diagnostics (Nothing on OpenAI-proper responses).
            llmStoppedEos = response.StoppedEos
            llmStoppedLimit = response.StoppedLimit
            llmStoppedWord = response.StoppedWord
            llmStoppingWord = response.StoppingWord
            llmTruncated = response.Truncated

            If Not response.Succeeded Then
                status = StudyExecution.StatusLlmFailed
                errorMessage = response.ErrorMessage
                ' Surface to the outer Catch so failedNctIds + eligibility_failed
                ' continue to track terminal LLM failures (spec section 2.4.4).
                Throw New InvalidOperationException(
                        $"LLM call failed for {trial.NctId}: {response.ErrorMessage}")
            End If

            ' --- Step 6: parse. ParseWithOutcome surfaces the distinction
            ' between "LLM legitimately returned []" and "couldn't parse the
            ' LLM's output" — both produce zero records but mean different
            ' things, so the audit row records them separately. ---
            Dim parseResult = _parser.ParseWithOutcome(response.RawText, trial.NctId)

            ' --- Adaptive reasoning escalation. The first attempt runs at the
            ' deployment's base reasoning effort (typically "low" — fast). If
            ' the model bailed with an empty array, retry once at the escalated
            ' effort before accepting "no criteria here". Only empty_array
            ' escalates: invalid_json is usually max_tokens truncation, which
            ' more reasoning makes worse. The escalated attempt replaces the
            ' first only when it actually yields records, so the audit row
            ' reflects what produced the persisted data (and a still-empty
            ' escalation doesn't regress the diagnostics). ---
            Dim escalationEffort = _llmClient.EscalationReasoningEffort
            If parseResult.Outcome = LlmParseResult.OutcomeEmptyArray AndAlso
               Not String.IsNullOrEmpty(escalationEffort) Then
                _logger.LogInformation(
                        "Trial {Nct} returned empty at base reasoning; escalating to {Effort}.",
                        trial.NctId, escalationEffort)
                Dim escalationWatch = Stopwatch.StartNew()
                Dim escalated = Await _llmClient.CompleteAsync(
                        request, cancellationToken, escalationEffort).ConfigureAwait(False)
                escalationWatch.Stop()
                llmMs = CInt(CLng(If(llmMs, 0)) + escalationWatch.ElapsedMilliseconds)
                If escalated.Succeeded Then
                    Dim escalatedParse = _parser.ParseWithOutcome(escalated.RawText, trial.NctId)
                    If escalatedParse.Records.Count > 0 Then
                        response = escalated
                        parseResult = escalatedParse
                        ' Refresh the captured audit/diagnostic fields so the
                        ' row records the escalated attempt, not the bailout.
                        llmFinishReason = escalated.FinishReason
                        promptTokens = escalated.PromptTokens
                        completionTokens = escalated.CompletionTokens
                        llmRawResponse = escalated.RawText
                        llmStoppedEos = escalated.StoppedEos
                        llmStoppedLimit = escalated.StoppedLimit
                        llmStoppedWord = escalated.StoppedWord
                        llmStoppingWord = escalated.StoppingWord
                        llmTruncated = escalated.Truncated
                    End If
                End If
            End If

            Dim parsed = parseResult.Records
            parsedRecordCount = parsed.Count

            ' Observability: log every time the repair pass had to rescue
            ' malformed JSON. Frequent warnings mean the model is misbehaving
            ' in a pattern we should add to the prompt or to TryRepairJson.
            If parseResult.WasRepaired Then
                _logger.LogWarning(
                        "Trial {Nct} required JSON repair before parsing (model emitted malformed JSON; check audit row's llm_raw_response).",
                        trial.NctId)
            End If

            ' --- Steps 7-11: per-criterion UMLS resolution. Timed: this sequential
            ' remote-lookup loop is the suspected throughput ceiling, so its
            ' per-trial wall clock feeds the Runs table's phase split. ---
            Dim umlsWatch = Stopwatch.StartNew()

            ' Pass 1 — lexical resolution per criterion (local store / REST API).
            Dim lexical As New List(Of (Criterion As CriterionRecord, Match As UmlsMatch, SemTypes As IReadOnlyList(Of String)))(parsed.Count)
            For Each criterion In parsed
                Dim candidates = Await _umlsClient.SearchAsync(criterion.Concept, cancellationToken).ConfigureAwait(False)
                Dim match = _scorer.PickBestMatch(criterion.Concept, candidates)

                Dim semanticTypes As IReadOnlyList(Of String) = Array.Empty(Of String)()
                If match.IsResolved Then
                    semanticTypes = Await _umlsClient.GetSemanticTypesAsync(
                            match.ConceptCode, cancellationToken).ConfigureAwait(False)
                End If

                lexical.Add((criterion, match, semanticTypes))
            Next

            ' Pass 2 (hybrid hook) — for criteria the lexical store couldn't resolve,
            ' consult the offline-built normalization cache by concept (one batched,
            ' indexed lookup, no LLM) and apply a cached resolution in place so repeat
            ' concepts persist on first pass. Non-fatal: a failed/empty consult leaves
            ' those criteria unresolved (the UMLS path never throws). Gated by config.
            Dim cacheHits As IReadOnlyDictionary(Of String, CachedConceptResolution) = Nothing
            If _options.UseNormalizationCache Then
                Dim unresolvedConcepts = lexical _
                        .Where(Function(x) Not x.Match.IsResolved AndAlso Not String.IsNullOrWhiteSpace(x.Criterion.Concept)) _
                        .Select(Function(x) x.Criterion.Concept) _
                        .Distinct().ToList()
                If unresolvedConcepts.Count > 0 Then
                    Try
                        cacheHits = Await _gateway.GetCachedNormalizationsAsync(
                                unresolvedConcepts, cancellationToken).ConfigureAwait(False)
                    Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                        Throw
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                                "Normalization-cache consult failed for trial {Nct}; treating as miss", trial.NctId)
                    End Try
                End If
            End If

            Dim resolved As New List(Of ResolvedRecord)(parsed.Count)
            For Each item In lexical
                Dim match = item.Match
                Dim semanticTypes = item.SemTypes
                If Not match.IsResolved AndAlso cacheHits IsNot Nothing Then
                    Dim hit As CachedConceptResolution = Nothing
                    If cacheHits.TryGetValue(item.Criterion.Concept, hit) Then
                        match = New UmlsMatch(hit.ConceptCode, hit.UmlsName, hit.MatchSource, hit.MatchScore)
                        ' Cache stores semantic_type as the already comma-joined string;
                        ' wrap as a one-element list (ResolvedRecord re-joins it verbatim).
                        semanticTypes = If(String.IsNullOrEmpty(hit.SemanticType),
                                           CType(Array.Empty(Of String)(), IReadOnlyList(Of String)),
                                           New String() {hit.SemanticType})
                    End If
                End If
                resolved.Add(New ResolvedRecord(item.Criterion, match, semanticTypes))
            Next

            umlsWatch.Stop()
            umlsMs = CInt(umlsWatch.ElapsedMilliseconds)

            ' --- Step 11b: collapse duplicate concept resolutions. The LLM
            ' frequently extracts the same medical concept from two separate
            ' source sentences; both resolve to the same UMLS code and would
            ' otherwise persist as two rows that say the same thing. Merge
            ' them into one row whose OriginalText concatenates the source
            ' snippets. Unresolved records are passed through untouched. ---
            Dim beforeDedup = resolved.Count
            resolved = DuplicateConceptMerger.Merge(resolved).ToList()
            If resolved.Count < beforeDedup Then
                _logger.LogInformation(
                        "Trial {Nct}: merged {Delta} duplicate concept rows ({Before} → {After}).",
                        trial.NctId, beforeDedup - resolved.Count, beforeDedup, resolved.Count)
            End If

            ' --- Step 12: DELETE+INSERT in one transaction (spec section 2.8.2).
            ' Even when resolved.Count = 0, we DELETE — re-processing a trial
            ' that previously had rows must clear them (spec section 6.1).
            Dim persistWatch = Stopwatch.StartNew()
            Try
                Await _gateway.PersistTrialAsync(trial.NctId, resolved, cancellationToken).ConfigureAwait(False)
            Catch persistEx As Exception When Not (TypeOf persistEx Is OperationCanceledException AndAlso cancellationToken.IsCancellationRequested)
                status = StudyExecution.StatusPersistFailed
                errorMessage = persistEx.Message
                Throw
            Finally
                persistWatch.Stop()
                persistMs = CInt(persistWatch.ElapsedMilliseconds)
            End Try

            Interlocked.Increment(counters.TrialsProcessed)
            rowsPersistedHere = resolved.Count
            If resolved.Count > 0 Then
                Interlocked.Add(counters.RowsPersisted, resolved.Count)
                Dim resolvedHere As Integer = 0
                For Each r In resolved
                    If Not String.IsNullOrEmpty(r.ConceptCode) Then resolvedHere += 1
                Next
                Interlocked.Add(counters.ResolvedCount, resolvedHere)
            End If

            ' Generate the study's topic embedding for the Authoring
            ' similarity index now that the trial is persisted — so a
            ' processed study is immediately searchable without waiting for
            ' the embed-studies backfill. Runs for every processed trial,
            ' including ones that extracted zero criteria: the embedding is
            ' built from the study's metadata snapshot, not its criteria, so
            ' a zero-row study is still a useful similarity-search candidate.
            ' Best-effort — see TryEmbedStudyAsync.
            Await TryEmbedStudyAsync(trial.NctId, cancellationToken).ConfigureAwait(False)

            ' Diagnostic statuses over success. The trial still completes
            ' cleanly (DELETE+empty INSERT, counters tick); the audit row
            ' surfaces *why* zero records came out so operators can tell
            ' "the LLM saw nothing here" (parse_empty) from "the LLM's
            ' output was unusable, probably truncated" (parse_invalid_json).
            If parseResult.Outcome = LlmParseResult.OutcomeInvalidJson Then
                status = StudyExecution.StatusParseInvalidJson
                If String.IsNullOrEmpty(errorMessage) Then
                    errorMessage = BuildInvalidJsonError(response)
                End If
            ElseIf parsedRecordCount = 0 Then
                status = StudyExecution.StatusParseEmpty
            End If
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            status = StudyExecution.StatusCancelled
            If String.IsNullOrEmpty(errorMessage) Then errorMessage = "Cancelled by host or user"
            cancelDispatch = ExceptionDispatchInfo.Capture(ex)
        Catch ex As Exception
            trialFailure = ex
            If status = StudyExecution.StatusSuccess Then
                ' No more-specific status set (so it's a post-LLM, pre-persist
                ' failure — typically UMLS or scorer). Use the generic "failed".
                status = StudyExecution.StatusFailed
            End If
            If String.IsNullOrEmpty(errorMessage) Then errorMessage = ex.Message
        End Try

        ' Per-trial phase split — the immediate diagnostic for the LLM-starvation
        ' question. Aggregated per run onto the Runs table via eligibility_study.
        _logger.LogInformation(
                "Trial {Nct} phase ms: llm={LlmMs} umls={UmlsMs} persist={PersistMs}",
                trial.NctId, llmMs, umlsMs, persistMs)

        ' Cancellation path: write the audit row with status=cancelled (using
        ' a non-cancelled token so the write actually happens), then re-throw.
        ' Skip the OnTrialCompleted hook so the dashboard doesn't render a
        ' spurious "succeeded=false" event for a user-cancelled trial.
        If cancelDispatch IsNot Nothing Then
            Await TryFinishStudyAsync(runId, trial.NctId, startedAt, status, llmSucceeded,
                                      llmFinishReason, promptTokens, completionTokens,
                                      parsedRecordCount, rowsPersistedHere, errorMessage,
                                      llmRawResponse,
                                      llmStoppedEos, llmStoppedLimit, llmStoppedWord,
                                      llmStoppingWord, llmTruncated,
                                      llmMs, umlsMs, persistMs,
                                      CancellationToken.None).ConfigureAwait(False)
            cancelDispatch.Throw()
        End If

        Dim succeededFlag As Boolean = (trialFailure Is Nothing)
        Await SafeNotifyAsync(
                Function() _hooks.OnTrialCompletedAsync(runId, trial.NctId, rowsPersistedHere, succeededFlag, cancellationToken),
                "OnTrialCompleted", cancellationToken).ConfigureAwait(False)

        Await TryFinishStudyAsync(runId, trial.NctId, startedAt, status, llmSucceeded,
                                  llmFinishReason, promptTokens, completionTokens,
                                  parsedRecordCount, rowsPersistedHere, errorMessage,
                                  llmRawResponse,
                                  llmStoppedEos, llmStoppedLimit, llmStoppedWord,
                                  llmStoppingWord, llmTruncated,
                                  llmMs, umlsMs, persistMs,
                                  cancellationToken).ConfigureAwait(False)

        If trialFailure IsNot Nothing Then
            _logger.LogWarning(trialFailure, "Trial {Nct} failed; recording and continuing batch", trial.NctId)
            failedNctIds.Add(trial.NctId)
            Await TryRecordFailedTrialAsync(trial.NctId, trialFailure.Message, cancellationToken).ConfigureAwait(False)
        End If

        ' Per-trial progress snapshot — keep the eligibility_run row alive with
        ' the latest counters so the dashboard's Runs tab can render
        ' in-flight progress. Best-effort: failure logs a warning but does
        ' not abort the trial (the orchestrator's terminal write at end-of-
        ' run will reconcile). Concurrent trial workers may race on this
        ' UPSERT; the counters only grow monotonically so the final value
        ' settles correctly even if intermediate writes land out of order.
        Await TryRecordRunAsync(
                BuildRunMetrics(runId, runStartedAt, endedAt:=Nothing, config, counters,
                                status:="running", errorSummary:=""),
                cancellationToken).ConfigureAwait(False)
    End Function

    ' Best-effort helpers — failures here are logged but do not propagate.

    Private Async Function TryRecordRunAsync(
            metrics As RunMetrics,
            cancellationToken As CancellationToken) As Task
        Dim ex As Exception = Nothing
        Try
            Await _gateway.RecordRunAsync(metrics, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to record run metrics for {RunId}", metrics.RunId)
    End Function

    Private Async Function TryStartStudyAsync(
            runId As Guid,
            nctId As String,
            startedAt As DateTimeOffset,
            cancellationToken As CancellationToken) As Task
        Dim ex As Exception = Nothing
        Try
            Await _gateway.StartStudyAsync(runId, nctId, startedAt, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to record StartStudy audit for {RunId}/{Nct}", runId, nctId)
    End Function

    Private Async Function TryFinishStudyAsync(
            runId As Guid,
            nctId As String,
            startedAt As DateTimeOffset,
            status As String,
            llmSucceeded As Boolean?,
            llmFinishReason As String,
            llmPromptTokens As Integer?,
            llmCompletionTokens As Integer?,
            parsedRecordCount As Integer?,
            persistedRowCount As Integer?,
            errorMessage As String,
            llmRawResponse As String,
            llmStoppedEos As Boolean?,
            llmStoppedLimit As Boolean?,
            llmStoppedWord As Boolean?,
            llmStoppingWord As String,
            llmTruncated As Boolean?,
            llmMs As Integer?,
            umlsMs As Integer?,
            persistMs As Integer?,
            cancellationToken As CancellationToken) As Task
        Dim execution = New StudyExecution(
                runId:=runId,
                nctId:=nctId,
                startedAt:=startedAt,
                finishedAt:=DateTimeOffset.UtcNow,
                status:=status,
                llmSucceeded:=llmSucceeded,
                llmFinishReason:=llmFinishReason,
                llmPromptTokens:=llmPromptTokens,
                llmCompletionTokens:=llmCompletionTokens,
                parsedRecordCount:=parsedRecordCount,
                persistedRowCount:=persistedRowCount,
                errorMessage:=errorMessage,
                llmRawResponse:=llmRawResponse,
                llmStoppedEos:=llmStoppedEos,
                llmStoppedLimit:=llmStoppedLimit,
                llmStoppedWord:=llmStoppedWord,
                llmStoppingWord:=llmStoppingWord,
                llmTruncated:=llmTruncated,
                llmMs:=llmMs,
                umlsMs:=umlsMs,
                persistMs:=persistMs)
        Dim ex As Exception = Nothing
        Try
            Await _gateway.FinishStudyAsync(execution, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to record FinishStudy audit for {RunId}/{Nct}", runId, nctId)
    End Function

    Private Async Function TryCaptureStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task
        Dim ex As Exception = Nothing
        Try
            Await _gateway.CaptureStudySnapshotAsync(nctId, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to capture study snapshot for {Nct}", nctId)
    End Function

    ' Embeds the study's topic text and UPSERTs it into
    ' eligibility_study_embedding for the Authoring similarity index
    ' (authoring specification sections 5.1 / 5.2). Runs inline after the
    ' study's eligibility rows are persisted.
    '
    ' Best-effort: the extraction pipeline's output does not depend on the
    ' embedding, so any failure here (no embedding client wired, missing
    ' snapshot, embedding endpoint down) logs a warning and leaves the trial
    ' counted as a success. The embed-studies CLI backfill remains the
    ' recovery path for studies whose inline embedding did not land.
    Private Async Function TryEmbedStudyAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task
        If _embeddingClient Is Nothing Then Return

        Dim ex As Exception = Nothing
        Try
            Dim input = Await _gateway.GetStudyEmbeddingInputAsync(nctId, cancellationToken).ConfigureAwait(False)
            If input Is Nothing Then
                _logger.LogWarning(
                        "Skipped embedding {Nct}: no eligibility_study_detail snapshot to embed", nctId)
                Return
            End If

            Dim text = EmbeddingTextBuilder.Build(input)
            If String.IsNullOrWhiteSpace(text) Then
                _logger.LogWarning(
                        "Skipped embedding {Nct}: study snapshot produced no embeddable text", nctId)
                Return
            End If

            Dim result = Await _embeddingClient.EmbedAsync(text, cancellationToken).ConfigureAwait(False)
            If Not result.Succeeded Then
                _logger.LogWarning("Embedding {Nct} failed: {Error}", nctId, result.ErrorMessage)
                Return
            End If

            Await _gateway.UpsertStudyEmbeddingAsync(
                    nctId, result.Vector, _embeddingClient.Model, text, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to embed study {Nct} for the Authoring similarity index", nctId)
    End Function

    Private Async Function TryRecordFailedTrialAsync(
            nctId As String,
            errorMessage As String,
            cancellationToken As CancellationToken) As Task
        Dim ex As Exception = Nothing
        Try
            Await _gateway.RecordFailedTrialAsync(nctId, errorMessage, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to record trial {Nct} into eligibility_failed", nctId)
    End Function

    Private Async Function TrySendNotificationAsync(
            dispatcher As Func(Of BatchResult, CancellationToken, Task),
            result As BatchResult,
            cancellationToken As CancellationToken) As Task
        Dim ex As Exception = Nothing
        Try
            Await dispatcher(result, cancellationToken).ConfigureAwait(False)
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Notification dispatch failed for run {RunId}", result.Metrics.RunId)
    End Function

    ''' <summary>
    ''' Awaits an IPipelineHooks invocation and swallows any exception except
    ''' user cancellation. Hook failures must NOT abort the batch.
    ''' </summary>
    Private Async Function SafeNotifyAsync(
            taskFactory As Func(Of Task),
            hookName As String,
            cancellationToken As CancellationToken) As Task
        Dim hookEx As Exception = Nothing
        Try
            Dim t = taskFactory()
            If t IsNot Nothing Then Await t.ConfigureAwait(False)
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            hookEx = ex
        End Try
        If hookEx IsNot Nothing Then
            _logger.LogWarning(hookEx, "Pipeline hook {Hook} failed", hookName)
        End If
    End Function

    Friend Shared Function ComputeResolutionRate(resolvedCount As Integer, rowsPersisted As Integer) As Double
        If rowsPersisted <= 0 Then Return 0.0
        Return Math.Round(CDbl(resolvedCount) / CDbl(rowsPersisted), 3, MidpointRounding.AwayFromZero)
    End Function

    ' Build the audit-row error message for a parse_invalid_json trial. Folds
    ' in any vendor stop-diagnostics llama.cpp surfaced (stopped_limit,
    ' stopped_eos, etc.) so an operator can tell "model hit max_tokens" from
    ' "model wanted to stop but EOS was suppressed" without re-running the
    ' call. Fields the server didn't provide are omitted.
    Friend Shared Function BuildInvalidJsonError(response As LlmResponse) As String
        Dim parts As New List(Of String) From {
            "finish_reason=" & response.FinishReason,
            "completion_tokens=" & response.CompletionTokens.ToString(CultureInfo.InvariantCulture)
        }
        If response.StoppedLimit.HasValue Then parts.Add("stopped_limit=" & response.StoppedLimit.Value.ToString().ToLowerInvariant())
        If response.StoppedEos.HasValue Then parts.Add("stopped_eos=" & response.StoppedEos.Value.ToString().ToLowerInvariant())
        If response.StoppedWord.HasValue Then parts.Add("stopped_word=" & response.StoppedWord.Value.ToString().ToLowerInvariant())
        If Not String.IsNullOrEmpty(response.StoppingWord) Then parts.Add("stopping_word=" & response.StoppingWord)
        If response.Truncated.HasValue Then parts.Add("truncated=" & response.Truncated.Value.ToString().ToLowerInvariant())
        Return "LLM response was not parseable as JSON (" & String.Join(", ", parts) & ")"
    End Function

    ' Snapshot the in-memory counters and project them into a RunMetrics for
    ' RecordRunAsync. Pulling reads of the Interlocked'd counter fields into a
    ' single helper keeps the call sites (initial insert, per-trial progress,
    ' terminal write, cancellation finalise) consistent.
    Private Function BuildRunMetrics(
            runId As Guid,
            startedAt As DateTimeOffset,
            endedAt As DateTimeOffset?,
            config As RunConfiguration,
            counters As RunCounters,
            status As String,
            errorSummary As String) As RunMetrics
        Dim trialsDone = counters.TrialsProcessed
        Dim rows = counters.RowsPersisted
        Dim resolved = counters.ResolvedCount
        Return New RunMetrics(
                runId:=runId,
                startedAt:=startedAt,
                endedAt:=endedAt,
                triggerSource:=config.TriggerSource,
                studyCount:=config.StudyCount,
                studiesProcessed:=trialsDone,
                rowsPersisted:=rows,
                resolutionRate:=ComputeResolutionRate(resolved, rows),
                status:=status,
                errorSummary:=errorSummary,
                concurrencyCap:=_options.LlmConcurrencyCap)
    End Function

End Class

' Mutable counters shared across the per-trial parallel body. Fields (not
' properties) so Interlocked can pass them ByRef. Friend so tests can inspect.
Friend NotInheritable Class RunCounters
    Public TrialsProcessed As Integer
    Public RowsPersisted As Integer
    Public ResolvedCount As Integer
End Class

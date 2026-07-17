Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

' Behaviour tests for the orchestrator. Drives the §5 pipeline end-to-end
' against in-memory fakes, asserting on the contracts that matter:
'   - Exclusion-set + direction passed to SelectNextTrialsAsync
'   - Per-trial DELETE+INSERT order independent of resolution (spec 2.8.2)
'   - Continue-on-error per trial (spec 2.4.4)
'   - Once-per-batch notifications (spec 2.10)
'   - Cancellation propagates, never silently swallowed
'   - Catastrophic failure produces a "failed" RunMetrics row + error notification

Public Class PipelineOrchestratorTests

    Private Const SourceTrigger As String = "webhook"

    ' ============ happy path: full pipeline produces persistence + notifications ============

    <Fact>
    Public Async Function Happy_path_extracts_resolves_and_persists_each_trial() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {
            New Trial("NCT00000001", "Inclusion: adult, diabetes"),
            New Trial("NCT00000002", "Inclusion: pregnant women")
        }

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "Diabetes", "Has diabetes"))
        llm.Responses("NCT00000002") = LlmResponse.Success(
                "NCT00000002",
                CriterionJson("NCT00000002", "Inclusion", "Pregnancy", "Pregnancy", "Pregnant women"))

        Dim umls = New FakeUmlsClient()
        umls.SearchResults("Diabetes") = New UmlsCandidate() {
                New UmlsCandidate("C0011860", "Diabetes", "MSH")}
        umls.SearchResults("Pregnancy") = New UmlsCandidate() {
                New UmlsCandidate("C0032961", "Pregnancy", "MSH")}
        umls.SemanticTypesResults("C0011860") = New String() {"Disease or Syndrome"}
        umls.SemanticTypesResults("C0032961") = New String() {"Organism Function"}

        Dim notifications = New FakeNotificationSink()
        Dim orch = NewOrchestrator(gateway, llm:=llm, umls:=umls, sink:=notifications)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal(2, gateway.PersistTrialCalls.Count)
        Dim persisted = gateway.PersistTrialCalls.OrderBy(Function(p) p.NctId).ToList()
        Assert.Equal("NCT00000001", persisted(0).NctId)
        Assert.Equal("C0011860", persisted(0).Records(0).ConceptCode)
        Assert.Equal("Disease or Syndrome", persisted(0).Records(0).SemanticType)
        Assert.Equal("NCT00000002", persisted(1).NctId)
        Assert.Equal("C0032961", persisted(1).Records(0).ConceptCode)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Equal(2, result.Metrics.StudiesProcessed)
        Assert.Equal(2, result.Metrics.RowsPersisted)
        Assert.Equal(1.0, result.Metrics.ResolutionRate)
        Assert.Empty(result.FailedNctIds)

        Assert.Single(notifications.CompletionCalls)
        Assert.Empty(notifications.ErrorCalls)
    End Function

    ' ============ empty batch ============

    <Fact>
    Public Async Function Empty_trial_list_returns_success_with_zero_counters_and_completion_notification() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = Array.Empty(Of Trial)()
        Dim notifications = New FakeNotificationSink()
        Dim orch = NewOrchestrator(gateway, sink:=notifications)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Equal(0, result.Metrics.StudiesProcessed)
        Assert.Equal(0, result.Metrics.RowsPersisted)
        Assert.Equal(0.0, result.Metrics.ResolutionRate)
        Assert.Empty(gateway.PersistTrialCalls)
        Assert.Single(notifications.CompletionCalls)
        Assert.Empty(notifications.ErrorCalls)
    End Function

    ' ============ unresolved criteria (UMLS empty / below threshold) ============

    <Fact>
    Public Async Function Unresolved_criteria_persist_with_unresolved_sentinels() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: gobbledygook")}

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Other", "Gobbledygook", "Some gobbledygook"))

        ' UMLS knows nothing about "Gobbledygook" — returns empty -> unresolved.
        Dim umls = New FakeUmlsClient()
        Dim orch = NewOrchestrator(gateway, llm:=llm, umls:=umls)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Dim persisted = gateway.PersistTrialCalls.Single()
        Assert.Equal("", persisted.Records(0).ConceptCode)
        Assert.Equal(0.0, persisted.Records(0).MatchScore)
        Assert.Equal(1, result.Metrics.RowsPersisted)
        Assert.Equal(0.0, result.Metrics.ResolutionRate)  ' 0 resolved / 1 persisted = 0
        Assert.Empty(umls.SemanticTypesCalls)  ' no CUI fetch for unresolved
    End Function

    ' ============ inline normalization-cache consult (hybrid hook) ============

    <Fact>
    Public Async Function Lexically_unresolved_criterion_resolves_from_normalization_cache() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: low blood sugar")}
        ' The cache holds a resolved mapping for the (raw) concept the lexical store missed.
        gateway.CachedNormalizations("low blood sugar") =
                New CachedConceptResolution("C0020615", "Hypoglycemia", "SNOMEDCT_US", 0.83, "Disease or Syndrome")

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "low blood sugar", "has low blood sugar"))

        ' Lexical UMLS knows nothing about "low blood sugar" -> the cache fills it in.
        Dim umls = New FakeUmlsClient()
        Dim orch = NewOrchestrator(gateway, llm:=llm, umls:=umls)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Dim record = gateway.PersistTrialCalls.Single().Records(0)
        Assert.Equal("C0020615", record.ConceptCode)
        Assert.Equal("Hypoglycemia", record.UmlsName)
        Assert.Equal("SNOMEDCT_US", record.MatchSource)
        Assert.Equal("Disease or Syndrome", record.SemanticType)
        Assert.Equal(1.0, result.Metrics.ResolutionRate)
        ' The cache was consulted for the unresolved concept, and no UMLS CUI fetch
        ' happened (the cache already carries the semantic type).
        Assert.NotEmpty(gateway.GetCachedNormalizationsCalls)
        Assert.Empty(umls.SemanticTypesCalls)
    End Function

    <Fact>
    Public Async Function Cache_miss_leaves_criterion_unresolved() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: smartphone ownership")}
        ' Cache has an entry for a DIFFERENT concept -> this one misses.
        gateway.CachedNormalizations("low blood sugar") =
                New CachedConceptResolution("C0020615", "Hypoglycemia", "SNOMEDCT_US", 0.83, "")

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Other", "smartphone ownership", "owns a smartphone"))

        Dim orch = NewOrchestrator(gateway, llm:=llm, umls:=New FakeUmlsClient())
        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal("", gateway.PersistTrialCalls.Single().Records(0).ConceptCode)
    End Function

    <Fact>
    Public Async Function Cache_consult_skipped_when_UseNormalizationCache_disabled() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: low blood sugar")}
        gateway.CachedNormalizations("low blood sugar") =
                New CachedConceptResolution("C0020615", "Hypoglycemia", "SNOMEDCT_US", 0.83, "")

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "low blood sugar", "has low blood sugar"))

        Dim orch = NewOrchestrator(gateway, llm:=llm, umls:=New FakeUmlsClient(),
                options:=New OrchestratorOptions With {.LlmConcurrencyCap = 4, .UseNormalizationCache = False})
        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        ' Cache never consulted, so the criterion stays unresolved despite the entry.
        Assert.Empty(gateway.GetCachedNormalizationsCalls)
        Assert.Equal("", gateway.PersistTrialCalls.Single().Records(0).ConceptCode)
    End Function

    ' ============ resolution rate calculation ============

    <Fact>
    Public Async Function Resolution_rate_is_resolved_over_persisted_rounded_3dp() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT0", "criteria")}

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT0") = LlmResponse.Success("NCT0", "[" &
                CriterionLiteral("NCT0", "Inclusion", "Disease", "A", "ax") & "," &
                CriterionLiteral("NCT0", "Inclusion", "Disease", "B", "bx") & "," &
                CriterionLiteral("NCT0", "Inclusion", "Disease", "C", "cx") &
                "]")

        Dim umls = New FakeUmlsClient()
        ' Resolve only B; A and C return empty.
        umls.SearchResults("B") = New UmlsCandidate() {New UmlsCandidate("CB", "B", "MSH")}

        Dim orch = NewOrchestrator(gateway, llm:=llm, umls:=umls)
        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal(3, result.Metrics.RowsPersisted)
        ' 1 resolved out of 3 = 0.333 (3dp)
        Assert.Equal(0.333, result.Metrics.ResolutionRate)
    End Function

    ' ============ LLM terminal failure: per-trial continue-on-error (spec 2.4.4) ============

    <Fact>
    Public Async Function Llm_failure_for_one_trial_records_it_and_other_trials_succeed() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {
            New Trial("NCT00000001", "ok"),
            New Trial("NCT00000002", "broken")
        }

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success("NCT00000001", "[]")
        llm.Responses("NCT00000002") = LlmResponse.Failure("NCT00000002", "model timeout")

        Dim notifications = New FakeNotificationSink()
        Dim orch = NewOrchestrator(gateway, llm:=llm, sink:=notifications)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        ' Successful trial persisted (with 0 rows from "[]").
        Assert.Contains(gateway.PersistTrialCalls, Function(p) p.NctId = "NCT00000001")
        ' Failed trial recorded.
        Assert.Single(gateway.RecordFailedTrialCalls)
        Assert.Equal("NCT00000002", gateway.RecordFailedTrialCalls.Single().NctId)
        Assert.Contains("model timeout", gateway.RecordFailedTrialCalls.Single().ErrorMessage)
        Assert.Contains("NCT00000002", result.FailedNctIds)

        ' Both notifications fire (completion + error).
        Assert.Single(notifications.CompletionCalls)
        Assert.Single(notifications.ErrorCalls)
        Assert.Equal("success", result.Metrics.Status)  ' batch as a whole succeeded
    End Function

    <Fact>
    Public Async Function Persistence_failure_for_one_trial_records_it_and_others_succeed() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {
            New Trial("NCT0001", "ok"),
            New Trial("NCT0002", "ok")
        }
        gateway.PersistFailures.Add("NCT0002")

        Dim llm = New FakeLlmClient()
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Contains("NCT0002", result.FailedNctIds)
        Assert.Single(gateway.RecordFailedTrialCalls)
        Assert.Equal("NCT0002", gateway.RecordFailedTrialCalls.Single().NctId)
    End Function

    ' ============ DELETE+INSERT semantics: empty-criteria trial still hits Persist (spec 6.1) ============

    <Fact>
    Public Async Function Trial_with_zero_criteria_still_calls_persist_to_clear_prior_rows() As Task
        ' Re-processing a trial that previously had rows must DELETE them even
        ' if this run extracted nothing new. PersistTrialAsync handles the DELETE
        ' unconditionally; the orchestrator must invoke it.
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "criteria")}

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success("NCT00000001", "[]")  ' empty array
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Single(gateway.PersistTrialCalls)
        Assert.Empty(gateway.PersistTrialCalls.Single().Records)
    End Function

    ' ============ notification semantics (spec 2.10) ============

    <Fact>
    Public Async Function Notifications_fire_exactly_once_per_batch_for_success() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {
            New Trial("NCT0001", "ok"), New Trial("NCT0002", "ok"), New Trial("NCT0003", "ok")
        }
        Dim notifications = New FakeNotificationSink()
        Dim orch = NewOrchestrator(gateway, sink:=notifications)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Single(notifications.CompletionCalls)
        Assert.Empty(notifications.ErrorCalls)
    End Function

    <Fact>
    Public Async Function Error_notification_fires_once_when_any_trial_failed() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT0001", "ok"), New Trial("NCT0002", "x")}
        gateway.PersistFailures.Add("NCT0001")
        gateway.PersistFailures.Add("NCT0002")

        Dim notifications = New FakeNotificationSink()
        Dim orch = NewOrchestrator(gateway, sink:=notifications)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Single(notifications.CompletionCalls)
        Assert.Single(notifications.ErrorCalls)  ' exactly once, not once-per-failure
    End Function

    ' ============ cancellation ============

    <Fact>
    Public Async Function Cancellation_propagates_does_not_return_failed_result() As Task
        Dim gateway = NewGateway()
        Dim orch = NewOrchestrator(gateway)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() orch.ExecuteAsync(MakeConfig(studyCount:=10), cts.Token))
        End Using
    End Function

    ' ============ catastrophic failure (gateway throws before any trial runs) ============

    <Fact>
    Public Async Function Catastrophic_failure_returns_failed_status_with_error_summary() As Task
        Dim gateway = New ThrowingGateway()  ' GetAttemptedNctIdsAsync throws synchronously
        Dim notifications = New FakeNotificationSink()
        Dim orch = NewOrchestrator(gateway, sink:=notifications)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal("failed", result.Metrics.Status)
        Assert.Contains("boom", result.Metrics.ErrorSummary)
        Assert.Single(notifications.ErrorCalls)
        Assert.Empty(notifications.CompletionCalls)
    End Function

    ' ============ argument validation ============

    <Fact>
    Public Async Function ExecuteAsync_throws_on_null_config() As Task
        Dim orch = NewOrchestrator(NewGateway())
        Await Assert.ThrowsAsync(Of ArgumentNullException)(
            Function() orch.ExecuteAsync(Nothing, CancellationToken.None))
    End Function

    <Fact>
    Public Sub Constructor_throws_on_null_gateway()
        Assert.Throws(Of ArgumentNullException)(
            Function() New PipelineOrchestrator(Nothing, New FakeLlmClient(), New FakeUmlsClient()))
    End Sub

    <Fact>
    Public Sub Constructor_throws_on_null_llm()
        Assert.Throws(Of ArgumentNullException)(
            Function() New PipelineOrchestrator(NewGateway(), Nothing, New FakeUmlsClient()))
    End Sub

    <Fact>
    Public Sub Constructor_throws_on_null_umls()
        Assert.Throws(Of ArgumentNullException)(
            Function() New PipelineOrchestrator(NewGateway(), New FakeLlmClient(), Nothing))
    End Sub

    ' ============ IPipelineHooks ============

    <Fact>
    Public Async Function Hooks_fire_in_order_for_happy_path_run() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT0001", "x"), New Trial("NCT0002", "y")}
        Dim hooks = New FakePipelineHooks()
        Dim orch = NewOrchestrator(gateway, hooks:=hooks)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=2), CancellationToken.None)

        Dim events = hooks.Events.ToList()
        Assert.Contains("BatchStarted:2", events)
        Assert.Contains("TrialStarted:NCT0001", events)
        Assert.Contains("TrialStarted:NCT0002", events)
        Assert.Contains(events, Function(e) e.StartsWith("TrialCompleted:NCT0001"))
        Assert.Contains(events, Function(e) e.StartsWith("TrialCompleted:NCT0002"))
        Assert.Contains(events, Function(e) e.StartsWith("BatchCompleted:success"))

        ' BatchStarted must come first; BatchCompleted must come last.
        Assert.StartsWith("BatchStarted:", events.First())
        Assert.StartsWith("BatchCompleted:", events.Last())
    End Function

    <Fact>
    Public Async Function Hooks_fire_for_empty_batch_with_BatchStarted_and_BatchCompleted_only() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = Array.Empty(Of Trial)()
        Dim hooks = New FakePipelineHooks()
        Dim orch = NewOrchestrator(gateway, hooks:=hooks)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Dim events = hooks.Events.ToList()
        Assert.Contains("BatchStarted:10", events)
        Assert.Contains(events, Function(e) e.StartsWith("BatchCompleted:"))
        Assert.DoesNotContain(events, Function(e) e.StartsWith("TrialStarted:"))
        Assert.DoesNotContain(events, Function(e) e.StartsWith("TrialCompleted:"))
    End Function

    <Fact>
    Public Async Function Hooks_TrialCompleted_marks_succeeded_false_when_trial_fails() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT0001", "ok"), New Trial("NCT0002", "broken")}
        gateway.PersistFailures.Add("NCT0002")
        Dim hooks = New FakePipelineHooks()
        Dim orch = NewOrchestrator(gateway, hooks:=hooks)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=2), CancellationToken.None)

        Assert.Contains(hooks.Events, Function(e) e.StartsWith("TrialCompleted:NCT0001") AndAlso e.EndsWith(":True"))
        Assert.Contains(hooks.Events, Function(e) e.StartsWith("TrialCompleted:NCT0002") AndAlso e.EndsWith(":False"))
    End Function

    <Fact>
    Public Async Function Hooks_failure_in_OnBatchStarted_does_not_abort_batch() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT0001", "ok")}
        Dim hooks = New FakePipelineHooks With {.ThrowFromOnBatchStarted = True}
        Dim orch = NewOrchestrator(gateway, hooks:=hooks)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        ' Batch completes successfully despite the hook failure; the failed hook
        ' is logged but does not propagate.
        Assert.Equal("success", result.Metrics.Status)
        Assert.Contains(hooks.Events, Function(e) e.StartsWith("BatchStarted:"))
        Assert.Contains(hooks.Events, Function(e) e.StartsWith("BatchCompleted:"))
    End Function

    ' ============ Run row lifecycle (initial + per-trial progress + final) ============

    <Fact>
    Public Async Function Run_row_is_written_at_start_after_every_trial_and_at_end() As Task
        ' Three eligibility_run UPSERTs are expected for a 2-trial batch:
        '   1. Initial insert with status='running', counters at 0
        '   2. Progress write after trial 1 completes (status still 'running')
        '   3. Progress write after trial 2 completes
        '   4. Final write with status='success' and EndedAt populated
        ' The dashboard's Runs tab needs the initial row so an in-flight run
        ' is visible immediately rather than appearing only after completion.
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {
                New Trial("NCT00000001", "Inclusion: a"),
                New Trial("NCT00000002", "Inclusion: b")
        }
        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success("NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "A", "Has A"))
        llm.Responses("NCT00000002") = LlmResponse.Success("NCT00000002",
                CriterionJson("NCT00000002", "Inclusion", "Disease", "B", "Has B"))
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=2), CancellationToken.None)

        ' Initial + 2 per-trial progress + final = 4 UPSERTs.
        Assert.Equal(4, gateway.RecordRunCalls.Count)
        ' First call is the start-of-run snapshot: status='running', counters at 0.
        Dim initial = gateway.RecordRunCalls(0)
        Assert.Equal("running", initial.Status)
        Assert.Equal(0, initial.StudiesProcessed)
        Assert.False(initial.EndedAt.HasValue)
        ' Last call is the terminal write: status='success', EndedAt populated.
        Dim final = gateway.RecordRunCalls(gateway.RecordRunCalls.Count - 1)
        Assert.Equal("success", final.Status)
        Assert.True(final.EndedAt.HasValue)
        Assert.Equal(2, final.StudiesProcessed)
    End Function

    <Fact>
    Public Async Function Run_row_finalised_as_cancelled_when_cancellation_fires_in_outer_block() As Task
        ' Cancellation that fires AFTER trial assembly (e.g. between writing
        ' the initial row and reaching the Parallel.ForEachAsync, or during
        ' it) must finalise the run row as status='cancelled' before
        ' re-throwing. Otherwise the row sits at 'running' forever and the
        ' dashboard shows a ghost in-flight run. Simulated here by cancelling
        ' the token before ExecuteAsync gets past the start-of-run insert.
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "x")}
        Dim orch = NewOrchestrator(gateway)
        Dim cts As New CancellationTokenSource()
        cts.Cancel()

        Await Assert.ThrowsAsync(Of OperationCanceledException)(
                Function() orch.ExecuteAsync(MakeConfig(studyCount:=1), cts.Token))

        ' Initial insert ran with status='running'; cancellation finalise
        ' wrote status='cancelled'. Last UPSERT in the list must reflect that.
        Assert.NotEmpty(gateway.RecordRunCalls)
        Dim last = gateway.RecordRunCalls(gateway.RecordRunCalls.Count - 1)
        Assert.Equal("cancelled", last.Status)
        Assert.True(last.EndedAt.HasValue)
    End Function

    ' ============ Re-run path (RunConfiguration.RerunNctId) ============

    <Fact>
    Public Async Function Rerun_skips_batch_select_and_processes_single_trial() As Task
        Dim gateway = NewGateway()
        ' Stash a populated batch result that would be returned if the
        ' orchestrator went through SelectNextTrialsAsync — should NOT be
        ' touched on the re-run path.
        gateway.TrialsToReturn = New Trial() {
                New Trial("NCT99999998", "should-not-be-processed"),
                New Trial("NCT99999999", "neither-this-one")
        }
        gateway.SingleTrials("NCT00000005") = New Trial("NCT00000005", "Inclusion: diabetes")

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000005") = LlmResponse.Success(
                "NCT00000005",
                CriterionJson("NCT00000005", "Inclusion", "Disease", "Diabetes", "Has diabetes"))
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Dim config = New RunConfiguration(studyCount:=1, triggerSource:="rerun", rerunNctId:="NCT00000005")
        Dim result = Await orch.ExecuteAsync(config, CancellationToken.None)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Equal(1, result.Metrics.StudiesProcessed)
        Assert.Empty(gateway.SelectNextTrialsCalls)             ' batch select never fires
        Assert.Single(gateway.GetSourceTrialsCalls)             ' one batch round-trip
        Assert.Equal({"NCT00000005"}, gateway.GetSourceTrialsCalls.First().ToArray())
        Assert.Single(gateway.PersistTrialCalls)
        Assert.Equal("NCT00000005", gateway.PersistTrialCalls.First().NctId)
    End Function

    <Fact>
    Public Async Function Rerun_for_missing_trial_completes_with_zero_processed() As Task
        Dim gateway = NewGateway()
        ' SingleTrials map left empty — GetSourceTrialAsync returns Nothing.

        Dim orch = NewOrchestrator(gateway)
        Dim config = New RunConfiguration(studyCount:=1, triggerSource:="rerun", rerunNctId:="NCT_DOES_NOT_EXIST")
        Dim result = Await orch.ExecuteAsync(config, CancellationToken.None)

        ' Run is considered successful (no failure), just empty.
        Assert.Equal("success", result.Metrics.Status)
        Assert.Equal(0, result.Metrics.StudiesProcessed)
        Assert.Equal(0, result.Metrics.RowsPersisted)
        Assert.Single(gateway.GetSourceTrialsCalls)
        Assert.Empty(gateway.PersistTrialCalls)
        Assert.Empty(gateway.SelectNextTrialsCalls)
    End Function

    <Fact>
    Public Async Function Rerun_processes_multi_trial_batch_as_single_run() As Task
        ' Studies tab's "Rerun selection" submits N nct_ids that the
        ' orchestrator must process under ONE run_id. Batch select is
        ' skipped on the rerun path; missing trials are individually dropped
        ' without failing the others.
        Dim gateway = NewGateway()
        gateway.SingleTrials("NCT00000010") = New Trial("NCT00000010", "Inclusion: a")
        gateway.SingleTrials("NCT00000020") = New Trial("NCT00000020", "Inclusion: b")
        ' NCT00000030 intentionally omitted — simulates a missing source row.

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000010") = LlmResponse.Success("NCT00000010",
                CriterionJson("NCT00000010", "Inclusion", "Disease", "A", "Has A"))
        llm.Responses("NCT00000020") = LlmResponse.Success("NCT00000020",
                CriterionJson("NCT00000020", "Inclusion", "Disease", "B", "Has B"))

        Dim orch = NewOrchestrator(gateway, llm:=llm)
        Dim config = New RunConfiguration(
                studyCount:=3,
                triggerSource:="rerun",
                rerunNctIds:=New String() {"NCT00000010", "NCT00000020", "NCT00000030"})
        Dim result = Await orch.ExecuteAsync(config, CancellationToken.None)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Equal(2, result.Metrics.StudiesProcessed)         ' the two found
        Assert.Empty(gateway.SelectNextTrialsCalls)              ' batch select skipped
        Assert.Single(gateway.GetSourceTrialsCalls)              ' one batch round-trip, not three
        Assert.Equal(3, gateway.GetSourceTrialsCalls.First().Count) ' all three ids requested in it
        Assert.Equal(2, gateway.PersistTrialCalls.Count)         ' persisted the two found
        Dim persistedNcts = gateway.PersistTrialCalls.Select(Function(c) c.NctId).OrderBy(Function(s) s).ToArray()
        Assert.Equal({"NCT00000010", "NCT00000020"}, persistedNcts)
    End Function

    ' ============ Study audit lifecycle (StartStudy / FinishStudy) ============

    <Fact>
    Public Async Function StartStudy_fires_before_LLM_and_FinishStudy_after_persist() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: diabetes")}

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "Diabetes", "Has diabetes"),
                finishReason:="stop", promptTokens:=42, completionTokens:=17)
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Assert.Single(gateway.StartStudyCalls)
        Assert.Equal("NCT00000001", gateway.StartStudyCalls.First().NctId)
        Assert.Single(gateway.FinishStudyCalls)
        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusSuccess, finish.Status)
        Assert.True(finish.LlmSucceeded)
        Assert.Equal("stop", finish.LlmFinishReason)
        Assert.Equal(42, finish.LlmPromptTokens)
        Assert.Equal(17, finish.LlmCompletionTokens)
        Assert.Equal(1, finish.ParsedRecordCount)
        Assert.Equal(1, finish.PersistedRowCount)
        Assert.True(finish.FinishedAt.HasValue)
    End Function

    <Fact>
    Public Async Function FinishStudy_records_llm_failed_on_terminal_LLM_failure() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000002", "Inclusion: stuff")}

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000002") = LlmResponse.Failure("NCT00000002", "HTTP 503")
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Assert.Single(gateway.FinishStudyCalls)
        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusLlmFailed, finish.Status)
        Assert.False(finish.LlmSucceeded)
        Assert.Contains("HTTP 503", finish.ErrorMessage)
    End Function

    <Fact>
    Public Async Function FinishStudy_records_parse_empty_when_LLM_returns_empty_array() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000003", "Inclusion: stuff")}

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000003") = LlmResponse.Success("NCT00000003", "[]", finishReason:="stop")
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusParseEmpty, finish.Status)
        Assert.Equal(0, finish.ParsedRecordCount)
        Assert.Equal(0, finish.PersistedRowCount)
        Assert.True(finish.LlmSucceeded)
    End Function

    <Fact>
    Public Async Function Escalation_retries_at_higher_effort_when_first_attempt_empty() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000050", "Inclusion: stuff")}

        Dim llm = New FakeLlmClient()
        llm.EscalationEffort = "medium"
        ' First (base) attempt bails with []; escalated attempt extracts a criterion.
        llm.Responses("NCT00000050") = LlmResponse.Success(
                "NCT00000050", "[]", finishReason:="stop", completionTokens:=20)
        llm.EscalatedResponses("NCT00000050") = LlmResponse.Success(
                "NCT00000050",
                CriterionJson("NCT00000050", "Inclusion", "Disease", "Diabetes", "Has diabetes"),
                finishReason:="stop", completionTokens:=5000)
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Assert.Contains("NCT00000050", llm.EscalationCalls)
        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusSuccess, finish.Status)
        Assert.Equal(1, finish.ParsedRecordCount)
        Assert.Equal(1, finish.PersistedRowCount)
        ' Audit row reflects the escalated attempt, not the bailout.
        Assert.Equal(5000, finish.LlmCompletionTokens)
    End Function

    <Fact>
    Public Async Function Escalation_does_not_fire_when_disabled() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000051", "Inclusion: stuff")}

        Dim llm = New FakeLlmClient()
        llm.EscalationEffort = ""   ' escalation off
        llm.Responses("NCT00000051") = LlmResponse.Success("NCT00000051", "[]", finishReason:="stop")
        ' An escalated response that WOULD succeed — present to prove it's never used.
        llm.EscalatedResponses("NCT00000051") = LlmResponse.Success(
                "NCT00000051", CriterionJson("NCT00000051", "Inclusion", "Disease", "Diabetes", "x"))
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Assert.Empty(llm.EscalationCalls)
        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusParseEmpty, finish.Status)
    End Function

    <Fact>
    Public Async Function Escalation_keeps_parse_empty_when_retry_also_empty() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000052", "Inclusion: stuff")}

        Dim llm = New FakeLlmClient()
        llm.EscalationEffort = "medium"
        llm.Responses("NCT00000052") = LlmResponse.Success("NCT00000052", "[]", finishReason:="stop")
        llm.EscalatedResponses("NCT00000052") = LlmResponse.Success("NCT00000052", "[]", finishReason:="stop")
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        ' The retry happened, but a still-empty result is recorded as parse_empty
        ' (no regression vs. the first attempt's outcome).
        Assert.Contains("NCT00000052", llm.EscalationCalls)
        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusParseEmpty, finish.Status)
        Assert.Equal(0, finish.ParsedRecordCount)
    End Function

    <Fact>
    Public Async Function FinishStudy_captures_llm_raw_response_on_audit_row() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000010", "Inclusion: stuff")}

        ' Anything the LLM returned (truncated, valid, garbage) should round-trip
        ' into the audit row so operators can inspect the parser's input.
        Const RawResponseText As String = "[{""NCT_ID"":""NCT00000010"",""Concept"":""Coronary Artery Disease"""
        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000010") = LlmResponse.Success(
                "NCT00000010", RawResponseText, finishReason:="length", completionTokens:=8000)
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(RawResponseText, finish.LlmRawResponse)
    End Function

    <Fact>
    Public Async Function FinishStudy_records_parse_invalid_json_when_LLM_output_is_truncated() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000099", "Inclusion: stuff")}

        ' Simulate a max_tokens truncation — finish_reason='length', JSON
        ' that opens [ but never closes. Parser returns invalid_json outcome;
        ' orchestrator surfaces parse_invalid_json on the audit row plus the
        ' finish_reason and completion_tokens in the error message.
        Dim truncated = "[{""NCT_ID"":""NCT00000099"",""Criterion"":""Inclusion"",""Concept"":""Coronary"
        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000099") = LlmResponse.Success(
                "NCT00000099", truncated, finishReason:="length", completionTokens:=8000)
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusParseInvalidJson, finish.Status)
        Assert.Equal(0, finish.ParsedRecordCount)
        Assert.True(finish.LlmSucceeded)
        Assert.Contains("finish_reason=length", finish.ErrorMessage)
        Assert.Contains("completion_tokens=8000", finish.ErrorMessage)
    End Function

    <Fact>
    Public Async Function FinishStudy_records_persist_failed_when_persist_throws() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000004", "Inclusion: diabetes")}
        gateway.PersistFailures.Add("NCT00000004")

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000004") = LlmResponse.Success(
                "NCT00000004",
                CriterionJson("NCT00000004", "Inclusion", "Disease", "Diabetes", "Has diabetes"))
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        Dim finish = gateway.FinishStudyCalls.First()
        Assert.Equal(StudyExecution.StatusPersistFailed, finish.Status)
        Assert.Contains("Persist failed", finish.ErrorMessage)
    End Function

    <Fact>
    Public Async Function StartStudy_failure_is_swallowed_and_trial_still_processed() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000005", "Inclusion: diabetes")}
        gateway.StartStudyThrowFor = "NCT00000005"  ' simulate audit-write failure

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000005") = LlmResponse.Success(
                "NCT00000005",
                CriterionJson("NCT00000005", "Inclusion", "Disease", "Diabetes", "Has diabetes"))
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        ' Audit-write failure must not abort the trial.
        Assert.Equal("success", result.Metrics.Status)
        Assert.Single(gateway.PersistTrialCalls)
    End Function

    ' ============ Study snapshot capture ============

    <Fact>
    Public Async Function Study_snapshot_is_captured_once_per_trial() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {
            New Trial("NCT00000001", "Inclusion: diabetes"),
            New Trial("NCT00000002", "Inclusion: pregnancy")
        }
        Dim orch = NewOrchestrator(gateway)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal(2, gateway.CaptureStudySnapshotCalls.Count)
        Assert.Contains("NCT00000001", gateway.CaptureStudySnapshotCalls)
        Assert.Contains("NCT00000002", gateway.CaptureStudySnapshotCalls)
    End Function

    <Fact>
    Public Async Function Study_snapshot_capture_failure_is_swallowed_and_trial_still_processed() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000006", "Inclusion: diabetes")}
        gateway.CaptureStudySnapshotThrowFor = "NCT00000006"  ' simulate AACT/snapshot failure

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000006") = LlmResponse.Success(
                "NCT00000006",
                CriterionJson("NCT00000006", "Inclusion", "Disease", "Diabetes", "Has diabetes"))
        Dim orch = NewOrchestrator(gateway, llm:=llm)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=1), CancellationToken.None)

        ' Snapshot capture is best-effort — failure must not abort the trial.
        Assert.Equal("success", result.Metrics.Status)
        Assert.Single(gateway.PersistTrialCalls)
    End Function

    ' ============ Direction + exclusion-set wiring ============

    <Fact>
    Public Async Function Forward_direction_passes_attempted_set_and_Forward_to_gateway() As Task
        ' Default RunConfiguration uses Forward direction. Orchestrator must
        ' fetch the attempted-NCT set first, then pass both (set + direction)
        ' through to SelectNextTrialsAsync. This is what replaces the old
        ' watermark read.
        Dim gateway = NewGateway()
        gateway.AttemptedNctIds = New String() {"NCT00000001", "NCT00000002"}
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000003", "ok")}
        Dim orch = NewOrchestrator(gateway)

        Await orch.ExecuteAsync(MakeConfig(studyCount:=5), CancellationToken.None)

        Assert.Single(gateway.GetAttemptedNctIdsCalls)
        Dim call_ = gateway.SelectNextTrialsCalls.Single()
        Assert.Equal(TrialSelectionDirection.Forward, call_.Direction)
        Assert.Equal(5, call_.StudyCount)
        Assert.Equal(2, call_.ExcludedNctIds.Count)
        Assert.Contains("NCT00000001", call_.ExcludedNctIds)
        Assert.Contains("NCT00000002", call_.ExcludedNctIds)
    End Function

    <Fact>
    Public Async Function Recent_direction_propagates_through_to_gateway() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000099", "ok")}
        Dim orch = NewOrchestrator(gateway)

        Dim config = New RunConfiguration(
                studyCount:=3,
                triggerSource:="recent",
                rerunNctIds:=Nothing,
                direction:=TrialSelectionDirection.Recent)
        Await orch.ExecuteAsync(config, CancellationToken.None)

        Dim call_ = gateway.SelectNextTrialsCalls.Single()
        Assert.Equal(TrialSelectionDirection.Recent, call_.Direction)
        Assert.Equal(3, call_.StudyCount)
    End Function

    <Fact>
    Public Async Function Rerun_path_does_not_fetch_attempted_ids() As Task
        ' Re-run path bypasses SelectNextTrialsAsync entirely, so the
        ' exclusion-set fetch is wasted I/O if it fired. Orchestrator must
        ' skip GetAttemptedNctIdsAsync on the rerun branch.
        Dim gateway = NewGateway()
        gateway.SingleTrials("NCT00000050") = New Trial("NCT00000050", "ok")
        Dim orch = NewOrchestrator(gateway)

        Dim config = New RunConfiguration(studyCount:=1, triggerSource:="rerun", rerunNctId:="NCT00000050")
        Await orch.ExecuteAsync(config, CancellationToken.None)

        Assert.Empty(gateway.GetAttemptedNctIdsCalls)
        Assert.Empty(gateway.SelectNextTrialsCalls)
    End Function

    ' ============ ComputeResolutionRate edge cases ============

    <Fact>
    Public Sub ComputeResolutionRate_returns_0_for_zero_rows()
        Assert.Equal(0.0, PipelineOrchestrator.ComputeResolutionRate(0, 0))
    End Sub

    <Fact>
    Public Sub ComputeResolutionRate_rounds_to_3dp()
        ' 7 / 8 = 0.875
        Assert.Equal(0.875, PipelineOrchestrator.ComputeResolutionRate(7, 8))
        ' 2 / 3 = 0.6667 -> 0.667
        Assert.Equal(0.667, PipelineOrchestrator.ComputeResolutionRate(2, 3))
    End Sub

    ' ============ inline topic embedding (Authoring similarity index) ============

    <Fact>
    Public Async Function Inline_embedding_writes_study_embedding_after_a_trial_persists_rows() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: diabetes")}
        gateway.EmbeddingInputs("NCT00000001") = New StudyEmbeddingInput(
                "NCT00000001", "Diabetes study", "", "A study of diabetes",
                New String() {"Diabetes"}, Array.Empty(Of Intervention)())

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "Diabetes", "Has diabetes"))

        Dim embed = New FakeEmbeddingClient()
        Dim orch = NewOrchestrator(gateway, llm:=llm, embeddingClient:=embed)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Single(embed.EmbedCalls)
        Dim upsert = gateway.UpsertStudyEmbeddingCalls.Single()
        Assert.Equal("NCT00000001", upsert.NctId)
        Assert.Equal("fake-embed-model", upsert.Model)
        Assert.Equal(embed.Vector, upsert.Embedding)
    End Function

    <Fact>
    Public Async Function Inline_embedding_skipped_when_no_embedding_client_is_wired() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: diabetes")}
        gateway.EmbeddingInputs("NCT00000001") = New StudyEmbeddingInput(
                "NCT00000001", "Diabetes study", "", "",
                Array.Empty(Of String)(), Array.Empty(Of Intervention)())

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "Diabetes", "Has diabetes"))

        Dim orch = NewOrchestrator(gateway, llm:=llm)   ' no embeddingClient

        Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Empty(gateway.GetStudyEmbeddingInputCalls)
        Assert.Empty(gateway.UpsertStudyEmbeddingCalls)
    End Function

    <Fact>
    Public Async Function Inline_embedding_runs_for_a_trial_that_persists_zero_rows() As Task
        ' A trial whose LLM returns [] persists zero public.eligibility rows,
        ' but its topic embedding comes from the metadata snapshot — so it is
        ' still embedded and remains a similarity-search candidate.
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: nothing extractable")}
        gateway.EmbeddingInputs("NCT00000001") = New StudyEmbeddingInput(
                "NCT00000001", "Some study", "", "",
                Array.Empty(Of String)(), Array.Empty(Of Intervention)())
        Dim llm = New FakeLlmClient() With {.DefaultRawText = "[]"}
        Dim embed = New FakeEmbeddingClient()
        Dim orch = NewOrchestrator(gateway, llm:=llm, embeddingClient:=embed)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal(0, result.Metrics.RowsPersisted)
        Assert.Single(embed.EmbedCalls)
        Assert.Equal("NCT00000001", gateway.UpsertStudyEmbeddingCalls.Single().NctId)
    End Function

    <Fact>
    Public Async Function Inline_embedding_failure_is_best_effort_and_does_not_fail_the_trial() As Task
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: diabetes")}
        gateway.EmbeddingInputs("NCT00000001") = New StudyEmbeddingInput(
                "NCT00000001", "Diabetes study", "", "",
                Array.Empty(Of String)(), Array.Empty(Of Intervention)())

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "Diabetes", "Has diabetes"))

        Dim embed = New FakeEmbeddingClient() With {.ForceFailure = True}
        Dim orch = NewOrchestrator(gateway, llm:=llm, embeddingClient:=embed)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Equal(1, result.Metrics.StudiesProcessed)
        Assert.Single(embed.EmbedCalls)
        Assert.Empty(gateway.UpsertStudyEmbeddingCalls)  ' a failed embed is never written
    End Function

    <Fact>
    Public Async Function Inline_embedding_skipped_when_no_study_snapshot_exists() As Task
        ' GetStudyEmbeddingInputAsync returns Nothing when the snapshot capture
        ' failed earlier; the embedding step degrades quietly.
        Dim gateway = NewGateway()
        gateway.TrialsToReturn = New Trial() {New Trial("NCT00000001", "Inclusion: diabetes")}
        ' EmbeddingInputs deliberately not seeded.

        Dim llm = New FakeLlmClient()
        llm.Responses("NCT00000001") = LlmResponse.Success(
                "NCT00000001",
                CriterionJson("NCT00000001", "Inclusion", "Disease", "Diabetes", "Has diabetes"))

        Dim embed = New FakeEmbeddingClient()
        Dim orch = NewOrchestrator(gateway, llm:=llm, embeddingClient:=embed)

        Dim result = Await orch.ExecuteAsync(MakeConfig(studyCount:=10), CancellationToken.None)

        Assert.Equal("success", result.Metrics.Status)
        Assert.Single(gateway.GetStudyEmbeddingInputCalls)
        Assert.Empty(embed.EmbedCalls)
        Assert.Empty(gateway.UpsertStudyEmbeddingCalls)
    End Function

    ' ============ helpers ============

    Private Shared Function NewGateway() As FakeGateway
        Return New FakeGateway()
    End Function

    Private Shared Function NewOrchestrator(
            gateway As IPostgresGateway,
            Optional llm As ILlmClient = Nothing,
            Optional umls As IUmlsClient = Nothing,
            Optional sink As INotificationSink = Nothing,
            Optional hooks As IPipelineHooks = Nothing,
            Optional embeddingClient As IEmbeddingClient = Nothing,
            Optional options As OrchestratorOptions = Nothing) As PipelineOrchestrator
        Return New PipelineOrchestrator(
                gateway:=gateway,
                llmClient:=If(llm, New FakeLlmClient()),
                umlsClient:=If(umls, New FakeUmlsClient()),
                notificationSink:=sink,
                hooks:=hooks,
                embeddingClient:=embeddingClient,
                options:=If(options, New OrchestratorOptions With {.LlmConcurrencyCap = 4}))
    End Function

    Private Shared Function MakeConfig(Optional studyCount As Integer = 10) As RunConfiguration
        Return New RunConfiguration(studyCount, SourceTrigger)
    End Function

    Private Shared Function CriterionJson(
            nctId As String,
            criterion As String,
            domain As String,
            concept As String,
            originalText As String) As String
        Return "[" & CriterionLiteral(nctId, criterion, domain, concept, originalText) & "]"
    End Function

    Private Shared Function CriterionLiteral(
            nctId As String,
            criterion As String,
            domain As String,
            concept As String,
            originalText As String) As String
        Return $"{{""NCT_ID"":""{nctId}"",""Criterion"":""{criterion}"",""Domain"":""{domain}"",""Concept"":""{concept}"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""{originalText}""}}"
    End Function

    ' ============ BuildInvalidJsonError ============

    <Fact>
    Public Sub BuildInvalidJsonError_falls_back_to_openai_fields_when_vendor_diagnostics_absent()
        ' OpenAI-proper responses don't carry stopped_limit / stopped_eos etc.
        ' The error string must still render usefully from just finish_reason +
        ' completion_tokens.
        Dim response = LlmResponse.Success(
                nctId:="NCT0", rawText:="{[",
                finishReason:="length", promptTokens:=10, completionTokens:=4096)
        Dim msg = PipelineOrchestrator.BuildInvalidJsonError(response)
        Assert.Equal(
                "LLM response was not parseable as JSON (finish_reason=length, completion_tokens=4096)",
                msg)
    End Sub

    <Fact>
    Public Sub BuildInvalidJsonError_includes_llamacpp_stop_diagnostics_when_present()
        ' When llama.cpp's vendor fields are present they should be folded
        ' into the message so the History tab tells the operator exactly
        ' which cap fired — the original "(finish_reason=length, …)" alone
        ' was ambiguous between "max_tokens hit" and "EOS suppressed".
        Dim response = LlmResponse.Success(
                nctId:="NCT0", rawText:="{[",
                finishReason:="length", promptTokens:=1488, completionTokens:=6704,
                stoppedEos:=False, stoppedLimit:=True, stoppedWord:=False,
                stoppingWord:="", truncated:=True)
        Dim msg = PipelineOrchestrator.BuildInvalidJsonError(response)
        Assert.Contains("finish_reason=length", msg)
        Assert.Contains("completion_tokens=6704", msg)
        Assert.Contains("stopped_limit=true", msg)
        Assert.Contains("stopped_eos=false", msg)
        Assert.Contains("stopped_word=false", msg)
        Assert.Contains("truncated=true", msg)
        Assert.DoesNotContain("stopping_word=", msg)   ' empty value is suppressed
    End Sub

End Class

' Gateway stand-in that throws synchronously on the first call. Used to drive
' the catastrophic-failure branch.
Friend NotInheritable Class ThrowingGateway
    Implements IPostgresGateway

    Public Function CountSelectableSourceTrialsAsync(
            cancellationToken As CancellationToken) As Task(Of Long?) _
            Implements IPostgresGateway.CountSelectableSourceTrialsAsync
        Throw New InvalidOperationException("boom")
    End Function

    Public Function CountSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.CountSupersededStudiesAsync
        Throw New InvalidOperationException("boom")
    End Function

    Public Function DeleteSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.DeleteSupersededStudiesAsync
        Throw New InvalidOperationException("boom")
    End Function

    Public Function GetAttemptedNctIdsAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IPostgresGateway.GetAttemptedNctIdsAsync
        Throw New InvalidOperationException("boom")
    End Function

    Public Function CountConceptsToNormalizeAsync(
            includeAttempted As Boolean, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountConceptsToNormalizeAsync
        Throw New NotImplementedException()
    End Function

    Public Function CountStudiesToEmbedAsync(
            model As String, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountStudiesToEmbedAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetEmbeddingStatsAsync(cancellationToken As CancellationToken) As Task(Of EmbeddingStats) _
            Implements IPostgresGateway.GetEmbeddingStatsAsync
        Throw New NotImplementedException()
    End Function

    Public Function ClearStudyEmbeddingsAsync(cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.ClearStudyEmbeddingsAsync
        Throw New NotImplementedException()
    End Function

    Public Function SelectNextTrialsAsync(
            excludedNctIds As IReadOnlyList(Of String),
            direction As TrialSelectionDirection,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial)) _
            Implements IPostgresGateway.SelectNextTrialsAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetSourceTrialAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of Trial) _
            Implements IPostgresGateway.GetSourceTrialAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetSourceTrialsAsync(
            nctIds As IReadOnlyList(Of String), cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial)) _
            Implements IPostgresGateway.GetSourceTrialsAsync
        Throw New NotImplementedException()
    End Function

    Public Function PersistTrialAsync(
            nctId As String, records As IReadOnlyList(Of ResolvedRecord), cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.PersistTrialAsync
        Throw New NotImplementedException()
    End Function

    Public Function SelectTrialsToRetryUmlsAsync(
            direction As TrialSelectionDirection, count As Integer, includeRetried As Boolean, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IPostgresGateway.SelectTrialsToRetryUmlsAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetUnresolvedRowsForTrialAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsRetryRow)) _
            Implements IPostgresGateway.GetUnresolvedRowsForTrialAsync
        Throw New NotImplementedException()
    End Function

    Public Function ApplyUmlsRetryAsync(
            nctId As String, results As IReadOnlyList(Of UmlsRetryResult), rowsAttempted As Integer, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.ApplyUmlsRetryAsync
        Throw New NotImplementedException()
    End Function

    Public Function SelectConceptsToNormalizeAsync(
            count As Integer, includeAttempted As Boolean, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptToNormalize)) _
            Implements IPostgresGateway.SelectConceptsToNormalizeAsync
        Throw New NotImplementedException()
    End Function

    Public Function RecordConceptNormalizationAsync(
            conceptNorm As String, normalizedTerm As String, match As UmlsMatch, semanticType As String, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.RecordConceptNormalizationAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetCachedNormalizationsAsync(
            conceptNorms As IReadOnlyList(Of String), cancellationToken As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, CachedConceptResolution)) _
            Implements IPostgresGateway.GetCachedNormalizationsAsync
        Throw New NotImplementedException()
    End Function

    Public Function RecordRunAsync(metrics As RunMetrics, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordRunAsync
        Return Task.CompletedTask  ' allow the orchestrator to record the failed metrics row
    End Function

    Public Function RecordFailedTrialAsync(
            nctId As String, errorMessage As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordFailedTrialAsync
        Return Task.CompletedTask
    End Function

    Public Function GetRecentRunsAsync(
            limit As Integer, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics)) _
            Implements IPostgresGateway.GetRecentRunsAsync
        Return Task.FromResult(CType(Array.Empty(Of RunMetrics)(), IReadOnlyList(Of RunMetrics)))
    End Function

    Public Function GetRunsPageAsync(
            limit As Integer, offset As Integer, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics)) _
            Implements IPostgresGateway.GetRunsPageAsync
        Return Task.FromResult(CType(Array.Empty(Of RunMetrics)(), IReadOnlyList(Of RunMetrics)))
    End Function

    Public Function CountRunsAsync(cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.CountRunsAsync
        Return Task.FromResult(0L)
    End Function

    Public Function SearchEligibilityAsync(
            filter As EligibilityFilter, sortBy As String, page As Integer, pageSize As Integer, cancellationToken As CancellationToken) As Task(Of EligibilityResultPage) _
            Implements IPostgresGateway.SearchEligibilityAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer, cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions) _
            Implements IPostgresGateway.GetEligibilityFilterOptionsAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetStudyDetailsAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of StudyDetails) _
            Implements IPostgresGateway.GetStudyDetailsAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetSourceEligibilityAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of SourceEligibilityDetails) _
            Implements IPostgresGateway.GetSourceEligibilityAsync
        Throw New NotImplementedException()
    End Function

    Public Function CaptureStudySnapshotAsync(
            nctId As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CaptureStudySnapshotAsync
        Return Task.CompletedTask
    End Function

    Public Function GetStudySnapshotAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of StudySnapshot) _
            Implements IPostgresGateway.GetStudySnapshotAsync
        Return Task.FromResult(Of StudySnapshot)(Nothing)
    End Function

    Public Function StartStudyAsync(
            runId As Guid, nctId As String, startedAt As DateTimeOffset,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.StartStudyAsync
        Return Task.CompletedTask
    End Function

    Public Function FinishStudyAsync(
            execution As StudyExecution, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.FinishStudyAsync
        Return Task.CompletedTask
    End Function

    Public Function GetStudiesAsync(
            filter As StudyFilter, sortBy As String, page As Integer, pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of StudyExecutionPage) _
            Implements IPostgresGateway.GetStudiesAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetStudyHistoryAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyExecution)) _
            Implements IPostgresGateway.GetStudyHistoryAsync
        Throw New NotImplementedException()
    End Function

    Public Function SearchStudyDetailsAsync(
            filter As StudySearchFilter, limit As Integer, cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of StudySearchResult)) _
            Implements IPostgresGateway.SearchStudyDetailsAsync
        Throw New NotImplementedException()
    End Function

    Public Function FindSimilarTrialsToAsync(
            nctId As String, limit As Integer, matchPhase As Boolean,
            matchStudyType As Boolean, cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of SimilarStudy)) _
            Implements IPostgresGateway.FindSimilarTrialsToAsync
        Throw New NotImplementedException()
    End Function

    Public Function DeleteStudyAsync(
            runId As Guid, nctId As String, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteStudyAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics) _
            Implements IPostgresGateway.GetDashboardMetricsAsync
        Throw New NotImplementedException()
    End Function

    ' Authoring CRUD — not exercised by orchestrator tests.

    Public Function ListAuthoringStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuthoringStudySummary)) _
            Implements IPostgresGateway.ListAuthoringStudiesAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetAuthoringStudyAsync(
            authoringStudyId As Guid, cancellationToken As CancellationToken) As Task(Of AuthoringStudyAggregate) _
            Implements IPostgresGateway.GetAuthoringStudyAsync
        Throw New NotImplementedException()
    End Function

    Public Function StudyIdExistsAsync(
            studyId As String, cancellationToken As CancellationToken) As Task(Of Boolean) _
            Implements IPostgresGateway.StudyIdExistsAsync
        Throw New NotImplementedException()
    End Function

    Public Function CreateAuthoringStudyAsync(
            study As AuthoringStudy, eligibility As AuthoringEligibility,
            userId As Guid, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CreateAuthoringStudyAsync
        Throw New NotImplementedException()
    End Function

    Public Function UpdateAuthoringStudyAsync(
            study As AuthoringStudy, userId As Guid, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateAuthoringStudyAsync
        Throw New NotImplementedException()
    End Function

    Public Function SetAuthoringStudyIdAsync(
            authoringStudyId As Guid, studyId As String, userId As Guid,
            cancellationToken As CancellationToken) As Task(Of Boolean) _
            Implements IPostgresGateway.SetAuthoringStudyIdAsync
        Throw New NotImplementedException()
    End Function

    Public Function SaveAuthoringEligibilityAsync(
            eligibility As AuthoringEligibility, userId As Guid, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.SaveAuthoringEligibilityAsync
        Throw New NotImplementedException()
    End Function

    Public Function SaveAuthoringCriteriaAsync(
            authoringStudyId As Guid, criteria As IReadOnlyList(Of AuthoringCriterion),
            userId As Guid, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.SaveAuthoringCriteriaAsync
        Throw New NotImplementedException()
    End Function

    Public Function DeleteAuthoringStudyAsync(
            authoringStudyId As Guid, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteAuthoringStudyAsync
        Throw New NotImplementedException()
    End Function

    Public Function FindSimilarStudiesAsync(
            queryVector As IReadOnlyList(Of Single), limit As Integer,
            cancellationToken As CancellationToken,
            Optional filterPhase As String = "",
            Optional filterStudyType As String = "") As Task(Of IReadOnlyList(Of SimilarStudy)) _
            Implements IPostgresGateway.FindSimilarStudiesAsync
        Throw New NotImplementedException()
    End Function

    Public Function ClusterCommonCriteriaAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of CriterionCluster)) _
            Implements IPostgresGateway.ClusterCommonCriteriaAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetClusterRecordsAsync(
            nctIds As IReadOnlyList(Of String), criterion As String, groupKey As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of EligibilityRow)) _
            Implements IPostgresGateway.GetClusterRecordsAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyEmbeddingInput)) _
            Implements IPostgresGateway.GetStudiesToEmbedAsync
        Throw New NotImplementedException()
    End Function

    Public Function UpsertStudyEmbeddingAsync(
            nctId As String, embedding As IReadOnlyList(Of Single), model As String,
            sourceText As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpsertStudyEmbeddingAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetStudyEmbeddingInputAsync(
            nctId As String, cancellationToken As CancellationToken) As Task(Of StudyEmbeddingInput) _
            Implements IPostgresGateway.GetStudyEmbeddingInputAsync
        Throw New NotImplementedException()
    End Function

    Public Function CountUsersAsync(cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountUsersAsync
        Throw New NotImplementedException()
    End Function

    Public Function CountOwnersAsync(cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountOwnersAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetUserByUserNameAsync(userName As String, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByUserNameAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetUserByEmailAsync(email As String, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByEmailAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetUserByGoogleSubjectAsync(googleSubject As String, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByGoogleSubjectAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetUserAsync(userId As Guid, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserAsync
        Throw New NotImplementedException()
    End Function

    Public Function ListUsersAsync(cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AppUser)) _
            Implements IPostgresGateway.ListUsersAsync
        Throw New NotImplementedException()
    End Function

    Public Function CreateUserAsync(user As AppUser, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CreateUserAsync
        Throw New NotImplementedException()
    End Function

    Public Function UpdateUserRoleAsync(userId As Guid, role As Role, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateUserRoleAsync
        Throw New NotImplementedException()
    End Function

    Public Function UpdateUserPasswordHashAsync(userId As Guid, passwordHash As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateUserPasswordHashAsync
        Throw New NotImplementedException()
    End Function

    Public Function LinkGoogleSubjectAsync(userId As Guid, googleSubject As String, pictureUrl As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.LinkGoogleSubjectAsync
        Throw New NotImplementedException()
    End Function

    Public Function RecordLoginAsync(userId As Guid, whenUtc As DateTimeOffset, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordLoginAsync
        Throw New NotImplementedException()
    End Function

    Public Function DeleteUserAsync(userId As Guid, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteUserAsync
        Throw New NotImplementedException()
    End Function

    Public Function InsertAuditAsync(entry As AuditEntry, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.InsertAuditAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetAuditLogAsync(filter As AuditLogFilter, page As Integer, pageSize As Integer, cancellationToken As CancellationToken) As Task(Of AuditLogPage) _
            Implements IPostgresGateway.GetAuditLogAsync
        Throw New NotImplementedException()
    End Function

    Public Function GetAuditLogForExportAsync(filter As AuditLogFilter, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuditEntry)) _
            Implements IPostgresGateway.GetAuditLogForExportAsync
        Throw New NotImplementedException()
    End Function

End Class

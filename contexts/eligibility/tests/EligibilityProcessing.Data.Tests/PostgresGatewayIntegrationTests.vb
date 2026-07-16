Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Xunit

' Integration tests against a real Postgres instance, spun up by Testcontainers.
'
' If Docker is unavailable, the fixture captures SkipReason and every test
' calls Skip.If(...) to skip cleanly. On developer machines without Docker
' the suite shows these as Skipped; on CI / machines with Docker they execute.
'
' Each test calls _fixture.ResetAsync() to start from a clean slate.

Public Class PostgresGatewayIntegrationTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    ' ============ Superseded study attempts (Tools tab: Remove superseded results) ============
    '
    ' Every test here runs against the Testcontainers Postgres from PostgresFixture.
    ' This is a destructive feature; nothing in this file may ever point at a real
    ' database.

    ' Records `attempts` attempts for one trial, oldest first, one minute apart so
    ' started_at ordering is unambiguous. Returns the run id of the LAST (newest)
    ' attempt - the one that must survive.
    Private Async Function SeedAttemptsAsync(
            nctId As String, attempts As Integer,
            Optional lastStatus As String = StudyExecution.StatusSuccess) As Task(Of Guid)
        Dim lastRun As Guid = Guid.Empty
        For i = 1 To attempts
            Dim runId = Guid.NewGuid()
            ' i=1 is the oldest; the newest gets the most recent started_at.
            Dim startedAt = DateTimeOffset.UtcNow.AddMinutes(-(attempts - i) - 1)
            Dim status = If(i = attempts, lastStatus, StudyExecution.StatusLlmFailed)
            Await _fixture.Gateway.StartStudyAsync(runId, nctId, startedAt, CancellationToken.None)
            Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                    runId:=runId, nctId:=nctId, startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(2),
                    status:=status, llmSucceeded:=(status = StudyExecution.StatusSuccess),
                    llmFinishReason:="stop", llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                    parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)
            lastRun = runId
        Next
        Return lastRun
    End Function

    Private Async Function CountStudyRowsAsync(nctId As String) As Task(Of Long)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM public.eligibility_study WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Return Convert.ToInt64(Await cmd.ExecuteScalarAsync())
            End Using
        End Using
    End Function

    Private Async Function SurvivingRunIdAsync(nctId As String) As Task(Of Guid)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT run_id FROM public.eligibility_study WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Return CType(Await cmd.ExecuteScalarAsync(), Guid)
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function CountSuperseded_counts_every_attempt_except_the_latest() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await SeedAttemptsAsync("NCT00000001", 3)   ' 2 superseded
        Await SeedAttemptsAsync("NCT00000002", 1)   ' 0 superseded - single attempt
        Await SeedAttemptsAsync("NCT00000003", 2)   ' 1 superseded

        Assert.Equal(3L, Await _fixture.Gateway.CountSupersededStudiesAsync(CancellationToken.None))
    End Function

    ' "whether successful or failed" - a superseded SUCCESS still counts. Guards
    ' against someone narrowing this to failures only.
    <SkippableFact>
    Public Async Function CountSuperseded_counts_superseded_successes_too() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Older attempt succeeded, newer one failed: the old success is superseded.
        Dim nct = "NCT00000009"
        Dim oldRun = Guid.NewGuid()
        Dim oldStart = DateTimeOffset.UtcNow.AddMinutes(-5)
        Await _fixture.Gateway.StartStudyAsync(oldRun, nct, oldStart, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=oldRun, nctId:=nct, startedAt:=oldStart, finishedAt:=oldStart.AddSeconds(1),
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=1, persistedRowCount:=1, errorMessage:=""), CancellationToken.None)
        Dim newRun = Guid.NewGuid()
        Dim newStart = DateTimeOffset.UtcNow
        Await _fixture.Gateway.StartStudyAsync(newRun, nct, newStart, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=newRun, nctId:=nct, startedAt:=newStart, finishedAt:=newStart.AddSeconds(1),
                status:=StudyExecution.StatusLlmFailed, llmSucceeded:=False, llmFinishReason:="",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:="boom"), CancellationToken.None)

        Assert.Equal(1L, Await _fixture.Gateway.CountSupersededStudiesAsync(CancellationToken.None))

        Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None)

        ' The FAILED newer attempt survives - recency wins, not success.
        Assert.Equal(newRun, Await SurvivingRunIdAsync(nct))
    End Function

    <SkippableFact>
    Public Async Function CountSuperseded_is_zero_on_an_empty_table() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Assert.Equal(0L, Await _fixture.Gateway.CountSupersededStudiesAsync(CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function DeleteSuperseded_keeps_exactly_the_latest_attempt_per_trial() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim latest1 = Await SeedAttemptsAsync("NCT00000001", 3)
        Dim latest2 = Await SeedAttemptsAsync("NCT00000002", 1)
        Dim latest3 = Await SeedAttemptsAsync("NCT00000003", 2)

        Dim deleted = Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None)

        Assert.Equal(3L, deleted)
        Assert.Equal(1L, Await CountStudyRowsAsync("NCT00000001"))
        Assert.Equal(1L, Await CountStudyRowsAsync("NCT00000002"))
        Assert.Equal(1L, Await CountStudyRowsAsync("NCT00000003"))
        ' The surviving row is the NEWEST attempt, not just any one.
        Assert.Equal(latest1, Await SurvivingRunIdAsync("NCT00000001"))
        Assert.Equal(latest2, Await SurvivingRunIdAsync("NCT00000002"))
        Assert.Equal(latest3, Await SurvivingRunIdAsync("NCT00000003"))
        ' Nothing left to remove.
        Assert.Equal(0L, Await _fixture.Gateway.CountSupersededStudiesAsync(CancellationToken.None))
    End Function

    ' THE load-bearing test. The pipeline's only progress marker is the DISTINCT
    ' NCT_ID set of eligibility_study (spec section 2.2). If this delete ever
    ' removed a trial's last row, that trial would silently be reprocessed.
    <SkippableFact>
    Public Async Function DeleteSuperseded_leaves_the_attempted_set_unchanged() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await SeedAttemptsAsync("NCT00000001", 3)
        Await SeedAttemptsAsync("NCT00000002", 1)
        Await SeedAttemptsAsync("NCT00000003", 2)
        Dim before = (Await _fixture.Gateway.GetAttemptedNctIdsAsync(CancellationToken.None)).
                OrderBy(Function(s) s).ToArray()

        Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None)

        Dim after = (Await _fixture.Gateway.GetAttemptedNctIdsAsync(CancellationToken.None)).
                OrderBy(Function(s) s).ToArray()
        Assert.Equal(before, after)
        Assert.Equal({"NCT00000001", "NCT00000002", "NCT00000003"}, after)
    End Function

    ' The extracted criteria are a separate table with per-trial DELETE+INSERT
    ' semantics; trimming audit history must not touch them.
    <SkippableFact>
    Public Async Function DeleteSuperseded_does_not_touch_eligibility_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.PersistTrialAsync("NCT00000001",
                {MakeResolvedWithCriterion("NCT00000001", "Inclusion", "diabetes"),
                 MakeResolvedWithCriterion("NCT00000001", "Inclusion", "hypertension")},
                CancellationToken.None)
        Await SeedAttemptsAsync("NCT00000001", 3)

        Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(2L, m.EligibilityRowCount)
    End Function

    <SkippableFact>
    Public Async Function DeleteSuperseded_is_idempotent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAttemptsAsync("NCT00000001", 3)

        Assert.Equal(2L, Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None))
        ' Second run has nothing to do and must not touch the survivor.
        Assert.Equal(0L, Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None))
        Assert.Equal(1L, Await CountStudyRowsAsync("NCT00000001"))
    End Function

    <SkippableFact>
    Public Async Function DeleteSuperseded_on_an_empty_table_is_a_no_op() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Assert.Equal(0L, Await _fixture.Gateway.DeleteSupersededStudiesAsync(CancellationToken.None))
    End Function

    ' The count and the Studies tab's "Hide superseded attempts" toggle MUST agree:
    ' the card offers to delete exactly the rows that view hides. If these ever
    ' diverge, the card would delete rows the operator still sees as current.
    <SkippableFact>
    Public Async Function CountSuperseded_agrees_with_the_studies_tab_hide_superseded_view() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await SeedAttemptsAsync("NCT00000001", 3)
        Await SeedAttemptsAsync("NCT00000002", 1)
        Await SeedAttemptsAsync("NCT00000003", 2)

        Dim all = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(hideSuperseded:=False), "started_at_desc", 1, 200, CancellationToken.None)
        Dim latestOnly = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(hideSuperseded:=True), "started_at_desc", 1, 200, CancellationToken.None)
        Dim superseded = Await _fixture.Gateway.CountSupersededStudiesAsync(CancellationToken.None)

        ' hidden-by-the-toggle == offered-for-deletion
        Assert.Equal(all.TotalRows - latestOnly.TotalRows, superseded)
    End Function

    ' ============ CountSelectableSourceTrialsAsync (dashboard backlog figure) ============

    ' The count MUST apply the same filter as SelectNextTrialsAsync, or the
    ' backlog figure counts trials the pipeline would never pick up. Seeds one
    ' trial per exclusion rule plus two selectable ones.
    <SkippableFact>
    Public Async Function CountSelectableSourceTrials_applies_the_selection_filter() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim longEnough = New String("a"c, 60)
        ' Selectable.
        Await _fixture.InsertSourceTrialAsync("NCT00000001", longEnough)
        Await _fixture.InsertSourceTrialAsync("NCT00000002", longEnough)
        ' Excluded by each rule in spec section 2.3.
        Await _fixture.InsertSourceTrialAsync("NCT00000003", Nothing)                        ' NULL criteria
        Await _fixture.InsertSourceTrialAsync("NCT00000004", "too short")                    ' < 50 chars
        Await _fixture.InsertSourceTrialAsync("NCT00000005", longEnough & " Please Contact the site") ' please contact
        Await _fixture.InsertSourceTrialAsync("NCT00000006", longEnough & " contact site for details")
        Await _fixture.InsertSourceTrialAsync("NCT00000007", longEnough & " CONTACT STUDY team")

        Dim actual = Await _fixture.Gateway.CountSelectableSourceTrialsAsync(CancellationToken.None)

        Assert.Equal(2L, actual)
    End Function

    ' The backlog is source-total minus attempted, computed from two independent
    ' counts. Assert the whole chain end to end against a real database.
    <SkippableFact>
    Public Async Function DashboardMetrics_reports_remaining_as_source_total_minus_attempted() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim longEnough = New String("a"c, 60)
        For i = 1 To 5
            Await _fixture.InsertSourceTrialAsync("NCT" & i.ToString("D8"), longEnough)
        Next

        ' Attempt two of the five.
        For Each nct In {"NCT00000001", "NCT00000002"}
            Dim runId = Guid.NewGuid()
            Dim startedAt = DateTimeOffset.UtcNow
            Await _fixture.Gateway.StartStudyAsync(runId, nct, startedAt, CancellationToken.None)
            Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                    runId:=runId, nctId:=nct, startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(1),
                    status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                    llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                    parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)
        Next

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(5L, m.SourceSelectableTotal)
        Assert.Equal(2L, m.StudiesAttempted)
        Assert.Equal(3L, m.TrialsRemaining)
    End Function

    ' No AACT source at all - the seeded quickstart shape. A gateway with no source
    ' data source must report Nothing (not 0, and not throw), so the dashboard
    ' omits the figure rather than claiming an empty backlog.
    <SkippableFact>
    Public Async Function CountSelectableSourceTrials_returns_nothing_when_source_is_absent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim noSource As New PostgresGateway(outputDataSource:=_fixture.DataSource, sourceDataSource:=Nothing)

        Dim actual = Await noSource.CountSelectableSourceTrialsAsync(CancellationToken.None)

        Assert.False(actual.HasValue)
    End Function

    ' ... and the dashboard read must still render, minus the backlog figure.
    <SkippableFact>
    Public Async Function DashboardMetrics_reports_no_remaining_when_source_is_absent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.Gateway.PersistTrialAsync("NCT00000010",
                {MakeResolvedWithCriterion("NCT00000010", "Inclusion", "diabetes")},
                CancellationToken.None)

        Dim noSource As New PostgresGateway(outputDataSource:=_fixture.DataSource, sourceDataSource:=Nothing)
        Dim m = Await noSource.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.False(m.SourceSelectableTotal.HasValue)
        Assert.False(m.TrialsRemaining.HasValue)
        ' The rest of the dashboard still works.
        Assert.Equal(1L, m.EligibilityRowCount)
    End Function

    ' StudiesAttempted counts distinct trials, not attempts: the figure is
    ' subtracted from the source total, so double-counting a re-run trial would
    ' understate the backlog.
    <SkippableFact>
    Public Async Function DashboardMetrics_counts_attempted_trials_once_per_nct_id() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Same trial attempted three times, plus one other trial => 2 distinct.
        Dim nct = "NCT00000042"
        For i = 1 To 3
            Dim runId = Guid.NewGuid()
            Dim startedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            Await _fixture.Gateway.StartStudyAsync(runId, nct, startedAt, CancellationToken.None)
            Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                    runId:=runId, nctId:=nct, startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(1),
                    status:=If(i = 3, StudyExecution.StatusSuccess, StudyExecution.StatusLlmFailed),
                    llmSucceeded:=(i = 3), llmFinishReason:="stop",
                    llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                    parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)
        Next
        Dim other = "NCT00000043"
        Dim otherRun = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(otherRun, other, DateTimeOffset.UtcNow, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=otherRun, nctId:=other, startedAt:=DateTimeOffset.UtcNow,
                finishedAt:=DateTimeOffset.UtcNow.AddSeconds(1),
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(2L, m.StudiesAttempted)
    End Function

    ' ============ GetEligibilityFilterOptionsAsync + the pg_stats pre-filter ============
    '
    ' The pre-filter skips the SELECT DISTINCT scan for columns the planner says
    ' are provably over the dropdown cap. It is a pure optimization, so the
    ' load-bearing test is the equivalence one: the answer must not depend on
    ' whether statistics happen to exist.

    ' Seeds `distinctConcepts` trials, each contributing one distinct concept but
    ' all sharing the same two criterion values. That gives one high-cardinality
    ' column (concept) and one low-cardinality column (criterion) in the same table.
    Private Async Function SeedFilterOptionRowsAsync(distinctConcepts As Integer) As Task
        For i = 1 To distinctConcepts
            Dim nct = "NCT" & i.ToString("D8")
            Dim criterion = If(i Mod 2 = 0, "Inclusion", "Exclusion")
            Await _fixture.Gateway.PersistTrialAsync(nct,
                    {MakeResolvedWithCriterion(nct, criterion, "concept-" & i.ToString("D4"))},
                    CancellationToken.None)
        Next
    End Function

    Private Async Function AnalyzeEligibilityAsync() As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "ANALYZE public.eligibility"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ' The one that matters: statistics are an optimization input, never a
    ' correctness input. Same corpus, same cap, with and without stats -> the
    ' same answer. Without ANALYZE there are no estimates, so every column is
    ' scanned (the pre-filter's fallback path); after ANALYZE the concept column
    ' is skipped on the estimate. Both must agree.
    <SkippableFact>
    Public Async Function FilterOptions_skip_path_matches_scan_path() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedFilterOptionRowsAsync(60)

        ' No ANALYZE yet: no estimates, so this is the full-scan path.
        Dim scanned = Await _fixture.Gateway.GetEligibilityFilterOptionsAsync(10, CancellationToken.None)

        Await AnalyzeEligibilityAsync()

        ' Estimates now exist: concept (60 distinct) clears 10 * 2, so it is skipped.
        Dim skipped = Await _fixture.Gateway.GetEligibilityFilterOptionsAsync(10, CancellationToken.None)

        Assert.Equal(scanned.Concepts, skipped.Concepts)
        Assert.Equal(scanned.Criteria, skipped.Criteria)
        Assert.Equal(scanned.NctIds, skipped.NctIds)
        Assert.Equal(scanned.Domains, skipped.Domains)
        Assert.Equal(scanned.ConceptCodes, skipped.ConceptCodes)
        Assert.Equal(scanned.SemanticTypes, skipped.SemanticTypes)
    End Function

    ' A column under the cap must still produce a dropdown after ANALYZE - the
    ' pre-filter must not blank a low-cardinality column.
    <SkippableFact>
    Public Async Function FilterOptions_low_cardinality_column_still_populated_after_analyze() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedFilterOptionRowsAsync(60)
        Await AnalyzeEligibilityAsync()

        Dim options = Await _fixture.Gateway.GetEligibilityFilterOptionsAsync(10, CancellationToken.None)

        ' criterion has exactly 2 distinct values, well under the cap of 10.
        Assert.Equal({"Exclusion", "Inclusion"}, options.Criteria.OrderBy(Function(s) s).ToArray())
        ' concept has 60 distinct, over the cap: empty list means "render a text input".
        Assert.Empty(options.Concepts)
    End Function

    ' The pre-filter assumes pg_stats tracks reality after ANALYZE. Assert that
    ' directly, so a future Postgres/Npgsql change that breaks the estimate query
    ' fails loudly here rather than silently degrading into always-scan.
    <SkippableFact>
    Public Async Function EstimatedDistinctCounts_reflect_analyze() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedFilterOptionRowsAsync(60)

        ' Before ANALYZE: no statistics, so no usable estimates. (Either the row
        ' is absent entirely, or n_distinct is 0 - both mean "scan".)
        Dim before = Await _fixture.Gateway.LoadEstimatedDistinctCountsAsync(
                {"concept", "criterion"}, CancellationToken.None)
        Dim conceptBefore As Double = 0
        before.TryGetValue("concept", conceptBefore)
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(conceptBefore, 10))

        Await AnalyzeEligibilityAsync()

        Dim after = Await _fixture.Gateway.LoadEstimatedDistinctCountsAsync(
                {"concept", "criterion"}, CancellationToken.None)

        ' 60 rows is small enough that ANALYZE samples all of them, so the
        ' estimates should be exact. Assert on the decision rather than the raw
        ' number to avoid coupling the test to sampling behaviour.
        Assert.True(PostgresGateway.ShouldSkipDistinctScan(after("concept"), 10),
                    $"expected concept estimate to clear the cap, got {after("concept")}")
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(after("criterion"), 10),
                     $"expected criterion estimate to stay under the cap, got {after("criterion")}")
    End Function

    ' ============ EnsureSchemaAsync ============

    <SkippableFact>
    Public Async Function EnsureSchema_creates_all_output_tables() As Task
        ' V1 created eligibility, eligibility_watermark, eligibility_run,
        ' eligibility_failed. V2 added eligibility_study. V4 dropped
        ' eligibility_watermark. Net result after applying all migrations:
        ' four tables present.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT COUNT(*) FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_name IN ('eligibility','eligibility_run','eligibility_failed','eligibility_study')"
                Dim count = Convert.ToInt32(Await cmd.ExecuteScalarAsync())
                Assert.Equal(4, count)
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function EnsureSchema_drops_eligibility_watermark_via_V4() As Task
        ' V1 creates the watermark table; V4 drops it. After EnsureSchema
        ' the table must not exist on a fresh DB.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT COUNT(*) FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_name = 'eligibility_watermark'"
                Assert.Equal(0, Convert.ToInt32(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function EnsureSchema_is_idempotent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        ' Already applied in fixture init; running again must not error.
        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)
        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)
    End Function

    <SkippableFact>
    Public Async Function EnsureSchema_creates_V8_performance_indexes() As Task
        ' V8 pins the embedding dimension and adds the HNSW vector index plus
        ' the Results-browser indexes on public.eligibility.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT COUNT(*) FROM pg_indexes
                    WHERE schemaname = 'public'
                      AND indexname IN (
                          'ix_eligibility_study_embedding_hnsw',
                          'ix_eligibility_created_at',
                          'ix_eligibility_domain',
                          'ix_eligibility_concept_code',
                          'ix_eligibility_semantic_type',
                          'ix_eligibility_criterion_trgm',
                          'ix_eligibility_concept_trgm')"
                Assert.Equal(7, Convert.ToInt32(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    ' ============ GetAttemptedNctIdsAsync ============

    <SkippableFact>
    Public Async Function GetAttemptedNctIds_returns_empty_when_no_studies_recorded() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim ids = Await _fixture.Gateway.GetAttemptedNctIdsAsync(CancellationToken.None)
        Assert.Empty(ids)
    End Function

    <SkippableFact>
    Public Async Function GetAttemptedNctIds_returns_distinct_ids_across_all_statuses() As Task
        ' Any row in eligibility_study counts — running, success, failed,
        ' parse_invalid_json, parse_empty. Duplicates (same nct_id across
        ' multiple runs) collapse to a single entry.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim now = DateTimeOffset.UtcNow
        Dim runA = Guid.NewGuid()
        Dim runB = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runA, "NCT00000001", now, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runA, nctId:="NCT00000001", startedAt:=now, finishedAt:=now,
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=1, persistedRowCount:=1, errorMessage:=""), CancellationToken.None)
        Await _fixture.Gateway.StartStudyAsync(runB, "NCT00000001", now, CancellationToken.None) ' duplicate trial, different run
        Await _fixture.Gateway.StartStudyAsync(Guid.NewGuid(), "NCT00000002", now, CancellationToken.None) ' running only

        Dim ids = Await _fixture.Gateway.GetAttemptedNctIdsAsync(CancellationToken.None)
        Assert.Equal(2, ids.Count)
        Assert.Contains("NCT00000001", ids)
        Assert.Contains("NCT00000002", ids)
    End Function

    ' ============ SelectNextTrialsAsync (spec section 2.3 filters + anti-join) ============

    ' ============ GetDashboardMetricsAsync ============

    <SkippableFact>
    Public Async Function GetDashboardMetrics_returns_zeros_on_empty_database() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(0L, m.EligibilityRowCount)
        Assert.Equal(0L, m.StudiesSuccessful)
        Assert.Equal(0L, m.StudiesFailedLatest)
        Assert.Equal(0.0, m.ResolutionRate)
        Assert.Equal(0L, m.PromptTokens)
        Assert.Equal(0L, m.CompletionTokens)
        Assert.Equal(0L, m.TokensUsed)
        Assert.Equal(0L, m.FailuresByStatus("llm_failed"))
        Assert.Equal(0L, m.FailuresByStatus("parse_invalid_json"))
        Assert.Equal(0L, m.FailuresByStatus("persist_failed"))
        Assert.Equal(0L, m.FailuresByStatus("failed"))
        Assert.Equal(0L, m.StudiesWithoutEmbeddings)
        Assert.Equal(0L, m.ParseEmpty)
    End Function

    <SkippableFact>
    Public Async Function GetDashboardMetrics_counts_runs_rows_and_latest_status_per_nct_id() As Task
        ' Seed:
        '   * 2 run rows
        '   * 3 eligibility rows (2 resolved, 1 unresolved) → 66.67% resolution
        '   * Trial A: failed then succeeded → counts as 1 successful (latest)
        '   * Trial B: still in parse_invalid_json → counts as 1 failed (latest)
        '   * Trial C: currently running → neither successful nor failed
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' --- two run rows ---
        For Each i In {1, 2}
            Await _fixture.Gateway.RecordRunAsync(New RunMetrics(
                    runId:=Guid.NewGuid(), startedAt:=DateTimeOffset.UtcNow,
                    endedAt:=DateTimeOffset.UtcNow, triggerSource:="cli",
                    studyCount:=1, studiesProcessed:=1, rowsPersisted:=1,
                    resolutionRate:=1.0, status:="success", errorSummary:=""),
                    CancellationToken.None)
        Next

        ' --- 3 eligibility rows: 2 resolved, 1 unresolved ---
        Await _fixture.Gateway.PersistTrialAsync("NCT00000010",
                {MakeResolvedWithCriterion("NCT00000010", "Inclusion", "diabetes"),
                 MakeResolvedWithCriterion("NCT00000010", "Inclusion", "hypertension")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000020",
                {MakeUnresolvedWithCriterion("NCT00000020", "Inclusion", "unknown")},
                CancellationToken.None)

        ' --- Trial A: failed then succeeded ---
        Dim trialA = "NCT00000010"
        Dim oldStart = DateTimeOffset.UtcNow.AddMinutes(-5)
        Dim runA1 = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runA1, trialA, oldStart, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runA1, nctId:=trialA, startedAt:=oldStart, finishedAt:=oldStart.AddSeconds(2),
                status:=StudyExecution.StatusParseInvalidJson, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)
        Dim runA2 = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runA2, trialA, DateTimeOffset.UtcNow, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runA2, nctId:=trialA, startedAt:=DateTimeOffset.UtcNow, finishedAt:=DateTimeOffset.UtcNow,
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=2, persistedRowCount:=2, errorMessage:=""), CancellationToken.None)

        ' --- Trial B: parse_invalid_json, no follow-up ---
        Dim trialB = "NCT00000020"
        Dim runB = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runB, trialB, DateTimeOffset.UtcNow, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runB, nctId:=trialB, startedAt:=DateTimeOffset.UtcNow, finishedAt:=DateTimeOffset.UtcNow,
                status:=StudyExecution.StatusParseInvalidJson, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)

        ' --- Trial C: still running (no finish) ---
        Await _fixture.Gateway.StartStudyAsync(Guid.NewGuid(), "NCT00000030",
                DateTimeOffset.UtcNow, CancellationToken.None)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(3L, m.EligibilityRowCount)
        Assert.Equal(1L, m.StudiesSuccessful)        ' trial A latest=success
        Assert.Equal(1L, m.StudiesFailedLatest)      ' trial B latest=parse_invalid_json
        Assert.Equal(1L, m.FailuresByStatus("parse_invalid_json"))
        Assert.Equal(0L, m.FailuresByStatus("llm_failed"))
        Assert.Equal(0L, m.FailuresByStatus("persist_failed"))
        Assert.Equal(0L, m.FailuresByStatus("failed"))
        ' 2 resolved / 3 total = 0.6667 (with float rounding tolerance)
        Assert.InRange(m.ResolutionRate, 0.66, 0.67)
        ' Every audit row in this seed has llm_prompt_tokens / llm_completion_tokens
        ' left as Nothing → COALESCE-to-0 → TokensUsed = 0.
        Assert.Equal(0L, m.TokensUsed)
    End Function

    <SkippableFact>
    Public Async Function GetDashboardMetrics_sums_prompt_and_completion_tokens_across_all_attempts() As Task
        ' TokensUsed is sum of prompt + completion across every eligibility_study
        ' row regardless of status. NULL token columns are treated as 0.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim now = DateTimeOffset.UtcNow
        Dim runA = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runA, "NCT00000001", now, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runA, nctId:="NCT00000001", startedAt:=now, finishedAt:=now,
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=100, llmCompletionTokens:=250,
                parsedRecordCount:=1, persistedRowCount:=1, errorMessage:=""), CancellationToken.None)

        Dim runB = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runB, "NCT00000002", now, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runB, nctId:="NCT00000002", startedAt:=now, finishedAt:=now,
                status:=StudyExecution.StatusLlmFailed, llmSucceeded:=False, llmFinishReason:="error",
                llmPromptTokens:=80, llmCompletionTokens:=0,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:="timeout"), CancellationToken.None)

        ' Row with NULL token columns must not break the SUM.
        Dim runC = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(runC, "NCT00000003", now, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runC, nctId:="NCT00000003", startedAt:=now, finishedAt:=now,
                status:=StudyExecution.StatusParseEmpty, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(180L, m.PromptTokens)        ' 100 + 80 + 0
        Assert.Equal(250L, m.CompletionTokens)    ' 250 + 0 + 0
        Assert.Equal(430L, m.TokensUsed)          ' combined
    End Function

    <SkippableFact>
    Public Async Function GetDashboardMetrics_excludes_parse_empty_and_cancelled_from_failure_count() As Task
        ' Valid terminal states that must not bump the failure metric:
        '   parse_empty — LLM legitimately reported "no eligibility criteria here"
        '   cancelled   — operator stopped the run mid-flight
        ' One control trial with status=failed verifies the metric still
        ' fires for actual failures alongside the excluded ones.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim now = DateTimeOffset.UtcNow
        Dim seedTerminal = Sub(nct As String, status As String)
                               Dim runId = Guid.NewGuid()
                               _fixture.Gateway.StartStudyAsync(runId, nct, now, CancellationToken.None).Wait()
                               _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                                       runId:=runId, nctId:=nct,
                                       startedAt:=now, finishedAt:=now,
                                       status:=status, llmSucceeded:=True, llmFinishReason:="stop",
                                       llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                                       parsedRecordCount:=0, persistedRowCount:=0,
                                       errorMessage:=""), CancellationToken.None).Wait()
                           End Sub

        seedTerminal("NCT00000100", StudyExecution.StatusParseEmpty)
        seedTerminal("NCT00000200", StudyExecution.StatusCancelled)
        seedTerminal("NCT00000300", StudyExecution.StatusFailed)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(0L, m.StudiesSuccessful)
        Assert.Equal(1L, m.StudiesFailedLatest)   ' only NCT00000300 (status=failed) counts
        Assert.Equal(1L, m.FailuresByStatus("failed"))
        ' parse_empty is excluded from the failure count but surfaced on its own.
        Assert.Equal(1L, m.ParseEmpty)            ' NCT00000100 (status=parse_empty)
    End Function

    <SkippableFact>
    Public Async Function GetDashboardMetrics_splits_failures_by_status() As Task
        ' One latest-attempt trial in each of the four failure statuses. The
        ' per-status breakdown must report 1 in every bucket and the buckets
        ' must sum to StudiesFailedLatest.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim now = DateTimeOffset.UtcNow
        Dim seedTerminal = Sub(nct As String, status As String)
                               Dim runId = Guid.NewGuid()
                               _fixture.Gateway.StartStudyAsync(runId, nct, now, CancellationToken.None).Wait()
                               _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                                       runId:=runId, nctId:=nct,
                                       startedAt:=now, finishedAt:=now,
                                       status:=status, llmSucceeded:=True, llmFinishReason:="stop",
                                       llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                                       parsedRecordCount:=0, persistedRowCount:=0,
                                       errorMessage:=""), CancellationToken.None).Wait()
                           End Sub

        seedTerminal("NCT00000401", StudyExecution.StatusLlmFailed)
        seedTerminal("NCT00000402", StudyExecution.StatusParseInvalidJson)
        seedTerminal("NCT00000403", StudyExecution.StatusPersistFailed)
        seedTerminal("NCT00000404", StudyExecution.StatusFailed)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(4L, m.StudiesFailedLatest)
        Assert.Equal(1L, m.FailuresByStatus("llm_failed"))
        Assert.Equal(1L, m.FailuresByStatus("parse_invalid_json"))
        Assert.Equal(1L, m.FailuresByStatus("persist_failed"))
        Assert.Equal(1L, m.FailuresByStatus("failed"))
        Assert.Equal(m.StudiesFailedLatest,
                     m.FailuresByStatus.Values.Sum())
        ' parse_empty is tracked separately and must NOT leak into the failure
        ' breakdown — no parse_empty trials were seeded here.
        Assert.Equal(0L, m.ParseEmpty)
    End Function

    <SkippableFact>
    Public Async Function GetDashboardMetrics_counts_embedding_backlog() As Task
        ' Seed:
        '   NCT01: detail + eligibility + embedding  → covered, NOT in backlog
        '   NCT02: detail + eligibility, no embedding → in backlog
        '   NCT03: detail + eligibility, no embedding → in backlog
        '   NCT04: detail only, no eligibility       → ignored (the embed-studies
        '                                              query requires eligibility
        '                                              data to exist first)
        ' Expected: StudiesWithoutEmbeddings = 2.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertStudyDetailAsync("NCT00000001")
        Await _fixture.InsertStudyDetailAsync("NCT00000002")
        Await _fixture.InsertStudyDetailAsync("NCT00000003")
        Await _fixture.InsertStudyDetailAsync("NCT00000004")

        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "diabetes")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "hypertension")
        Await _fixture.InsertEligibilityRowAsync("NCT00000003", "Inclusion", "asthma")

        ' Embedding only for NCT01. Value is irrelevant for this test — only
        ' the row's presence matters for the anti-join — but the column was
        ' pinned to vector(1024) in migration V8, so the vector must be that
        ' width regardless.
        Dim embedding(1023) As Single
        embedding(0) = 1.0F
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT00000001", embedding, "test-model", "source text", CancellationToken.None)

        Dim m = Await _fixture.Gateway.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(2L, m.StudiesWithoutEmbeddings)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_excludes_ids_in_excludedNctIds() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeTrials()

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                excludedNctIds:=New String() {"NCT00000001"},
                direction:=TrialSelectionDirection.Forward,
                studyCount:=10,
                cancellationToken:=CancellationToken.None)

        ' The anti-join drops NCT00000001 even though it satisfies every other filter.
        Dim ids = trials.Select(Function(t) t.NctId).ToArray()
        Assert.DoesNotContain("NCT00000001", ids)
        Assert.Contains("NCT00000002", ids)
        Assert.Contains("NCT00000003", ids)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_returns_all_when_excludedNctIds_empty() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeTrials()

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(),
                TrialSelectionDirection.Forward,
                10,
                CancellationToken.None)

        Assert.Equal(3, trials.Count)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_excludes_criteria_below_50_chars() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000001", "too short")
        Await _fixture.InsertSourceTrialAsync("NCT00000002", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)
        Assert.Single(trials)
        Assert.Equal("NCT00000002", trials(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_excludes_null_criteria() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000001", Nothing)
        Await _fixture.InsertSourceTrialAsync("NCT00000002", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)
        Assert.Single(trials)
        Assert.Equal("NCT00000002", trials(0).NctId)
    End Function

    <SkippableTheory>
    <InlineData("Please contact site for information about this trial. " & "Adults 18+ may apply.")>
    <InlineData("Please CONTACT site for information about this trial. " & "Adults 18+ may apply.")>
    <InlineData("For details, contact site for trial info please. " & "Adults must be 18+.")>
    <InlineData("To enroll, contact study coordinator. " & "Patients aged 18+ are eligible.")>
    Public Async Function SelectNextTrials_excludes_please_contact_variants(criteria As String) As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000001", criteria)

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)
        Assert.Empty(trials)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_Forward_sorts_ascending_by_nct_id() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000003", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000001", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000002", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)
        Assert.Equal(New String() {"NCT00000001", "NCT00000002", "NCT00000003"},
                trials.Select(Function(t) t.NctId).ToArray())
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_Recent_sorts_descending_by_nct_id() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000003", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000001", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000002", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Recent, 10, CancellationToken.None)
        Assert.Equal(New String() {"NCT00000003", "NCT00000002", "NCT00000001"},
                trials.Select(Function(t) t.NctId).ToArray())
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_Recent_with_exclusion_returns_next_unexcluded() As Task
        ' Most-recent-N driver: a Recent run skips trials already attempted.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000003", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000002", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000001", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                New String() {"NCT00000003"},
                TrialSelectionDirection.Recent,
                10,
                CancellationToken.None)

        Assert.Equal(New String() {"NCT00000002", "NCT00000001"},
                trials.Select(Function(t) t.NctId).ToArray())
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_respects_LIMIT_studyCount() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        For i = 1 To 5
            Await _fixture.InsertSourceTrialAsync($"NCT0000000{i}", New String("a"c, 60))
        Next

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 3, CancellationToken.None)
        Assert.Equal(3, trials.Count)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_clamps_studyCount_to_configured_max() As Task
        ' A gateway configured with MaxStudyCount caps an oversized request, so a
        ' fat-fingered StudyCount can't turn the source anti-join into a giant scan.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        For i = 1 To 5
            Await _fixture.InsertSourceTrialAsync($"NCT0000000{i}", New String("a"c, 60))
        Next

        Dim clamped As New PostgresGateway(
                outputDataSource:=_fixture.DataSource,
                sourceDataSource:=_fixture.DataSource,
                logger:=Nothing,
                maxStudyCount:=2)

        Dim trials = Await clamped.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)

        ' 5 trials qualify and 10 were requested, but the clamp caps the batch at 2.
        Assert.Equal(2, trials.Count)
    End Function

    <SkippableFact>
    Public Async Function SelectNextTrials_does_not_clamp_when_max_is_zero() As Task
        ' MaxStudyCount = 0 disables the clamp (the fixture gateway's default), so
        ' a request larger than the qualifying set returns the whole set.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        For i = 1 To 5
            Await _fixture.InsertSourceTrialAsync($"NCT0000000{i}", New String("a"c, 60))
        Next

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)
        Assert.Equal(5, trials.Count)
    End Function

    ' ============ EnsureSourcePerformanceIndexesAsync (co-location gate) ============

    <SkippableFact>
    Public Async Function EnsureSourcePerformanceIndexes_creates_index_when_colocated() As Task
        ' The fixture's source and output share one data source (same host/port/db),
        ' so the co-location gate opens and the partial selection index is created.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await DropSelectionIndexAsync()

        Await _fixture.Gateway.EnsureSourcePerformanceIndexesAsync(CancellationToken.None)

        Assert.True(Await SelectionIndexExistsAsync(),
                "Expected the selection index to exist after a co-located ensure.")
    End Function

    <SkippableFact>
    Public Async Function EnsureSourcePerformanceIndexes_is_idempotent() As Task
        ' CREATE INDEX IF NOT EXISTS — a second call is a clean no-op, not an error.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.Gateway.EnsureSourcePerformanceIndexesAsync(CancellationToken.None)
        Await _fixture.Gateway.EnsureSourcePerformanceIndexesAsync(CancellationToken.None)
        Assert.True(Await SelectionIndexExistsAsync())
    End Function

    <SkippableFact>
    Public Async Function EnsureSourcePerformanceIndexes_skips_when_not_colocated() As Task
        ' A source DS pointing at a different database name fails the co-location
        ' gate, so no DDL runs (the bogus source is never even opened).
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await DropSelectionIndexAsync()

        Dim otherSource = NpgsqlDataSource.Create(
                "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=not_the_output_db")
        Dim gw As New PostgresGateway(
                outputDataSource:=_fixture.DataSource,
                sourceDataSource:=otherSource,
                logger:=Nothing)

        Await gw.EnsureSourcePerformanceIndexesAsync(CancellationToken.None)

        Assert.False(Await SelectionIndexExistsAsync(),
                "Index must not be created when source and output are not co-located.")
        Await otherSource.DisposeAsync()
    End Function

    <SkippableFact>
    Public Async Function SourceHasCtgovEligibilities_true_when_present() As Task
        ' The fixture DB carries the ctgov.eligibilities table, so the probe
        ' reports it present.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Assert.True(Await _fixture.Gateway.SourceHasCtgovEligibilitiesAsync(CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function EnsureSourcePerformanceIndexes_skips_cleanly_when_ctgov_absent() As Task
        ' The seeded-quickstart shape: source co-located with output, but the
        ' database has no ctgov schema at all. The probe must report absent, and
        ' the ensure step must skip WITHOUT attempting the DDL (previously this
        ' raised "schema ctgov does not exist", caught and logged as a scary
        ' warning + stack trace). We verify against a throwaway database that has
        ' no ctgov, standing in for the standalone output-only seed DB.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        ' Cleanup is linear (not try/finally) because VB forbids Await in a Finally
        ' block; a failed assertion leaves the throwaway DB behind, which the
        ' ephemeral test container drops on fixture teardown anyway.
        Dim dbName = "elig_no_ctgov_" & Guid.NewGuid().ToString("N").Substring(0, 12)
        Await ExecuteOnFixtureAsync($"CREATE DATABASE ""{dbName}""")

        Dim builder As New NpgsqlConnectionStringBuilder(_fixture.ConnectionString) With {
                .Database = dbName}
        Dim noCtgov = NpgsqlDataSource.Create(builder.ConnectionString)

        ' Source == output == the ctgov-less DB, so the co-location gate opens but
        ' the schema probe reports absent.
        Dim gw As New PostgresGateway(
                outputDataSource:=noCtgov,
                sourceDataSource:=noCtgov,
                logger:=Nothing)

        Assert.False(Await gw.SourceHasCtgovEligibilitiesAsync(CancellationToken.None),
                "Probe must report ctgov.eligibilities absent on a DB without the schema.")

        ' Must complete cleanly - no throw, no DDL attempted against ctgov.
        Await gw.EnsureSourcePerformanceIndexesAsync(CancellationToken.None)

        Await noCtgov.DisposeAsync()
        ' FORCE terminates any lingering pooled connection so the drop succeeds.
        Await ExecuteOnFixtureAsync($"DROP DATABASE IF EXISTS ""{dbName}"" WITH (FORCE)")
    End Function

    ' Runs a single autocommit statement (e.g. CREATE/DROP DATABASE, which cannot
    ' run inside a transaction) against the fixture's default database.
    Private Async Function ExecuteOnFixtureAsync(sql As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function DropSelectionIndexAsync() As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DROP INDEX IF EXISTS ctgov.ix_eligibilities_selectable_nct_id"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SelectionIndexExistsAsync() As Task(Of Boolean)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT to_regclass('ctgov.ix_eligibilities_selectable_nct_id') IS NOT NULL"
                Return CBool(Await cmd.ExecuteScalarAsync())
            End Using
        End Using
    End Function

    ' ============ PersistTrialAsync (spec section 2.8.2 transaction semantics) ============

    <SkippableFact>
    Public Async Function PersistTrial_inserts_all_rows_for_trial() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim records = New ResolvedRecord() {
            MakeResolved("NCT00000001", "Diabetes"),
            MakeResolved("NCT00000001", "Hypertension")
        }
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001", records, CancellationToken.None)

        Assert.Equal(2, Await _fixture.CountEligibilityRowsAsync("NCT00000001"))
    End Function

    <SkippableFact>
    Public Async Function PersistTrial_replaces_existing_rows_on_re_run() As Task
        ' Spec section 6.1: re-running a trial replaces its persisted output entirely.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim first = New ResolvedRecord() {
            MakeResolved("NCT00000001", "Concept A"),
            MakeResolved("NCT00000001", "Concept B"),
            MakeResolved("NCT00000001", "Concept C")
        }
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001", first, CancellationToken.None)
        Assert.Equal(3, Await _fixture.CountEligibilityRowsAsync("NCT00000001"))

        Dim second = New ResolvedRecord() {MakeResolved("NCT00000001", "Concept X")}
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001", second, CancellationToken.None)
        Assert.Equal(1, Await _fixture.CountEligibilityRowsAsync("NCT00000001"))
    End Function

    <SkippableFact>
    Public Async Function PersistTrial_with_empty_records_deletes_existing_and_inserts_nothing() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.PersistTrialAsync(
                "NCT00000001",
                New ResolvedRecord() {MakeResolved("NCT00000001", "X")},
                CancellationToken.None)
        Assert.Equal(1, Await _fixture.CountEligibilityRowsAsync("NCT00000001"))

        Await _fixture.Gateway.PersistTrialAsync(
                "NCT00000001",
                Array.Empty(Of ResolvedRecord)(),
                CancellationToken.None)
        Assert.Equal(0, Await _fixture.CountEligibilityRowsAsync("NCT00000001"))
    End Function

    <SkippableFact>
    Public Async Function PersistTrial_does_not_touch_other_trials_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.PersistTrialAsync(
                "NCT00000001",
                New ResolvedRecord() {MakeResolved("NCT00000001", "A")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync(
                "NCT00000002",
                New ResolvedRecord() {MakeResolved("NCT00000002", "B")},
                CancellationToken.None)

        ' Re-running trial 1 must not affect trial 2.
        Await _fixture.Gateway.PersistTrialAsync(
                "NCT00000001",
                New ResolvedRecord() {MakeResolved("NCT00000001", "A2")},
                CancellationToken.None)

        Assert.Equal(1, Await _fixture.CountEligibilityRowsAsync("NCT00000001"))
        Assert.Equal(1, Await _fixture.CountEligibilityRowsAsync("NCT00000002"))
    End Function

    <SkippableFact>
    Public Async Function PersistTrial_writes_unresolved_fields_as_null() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim unresolved = MakeResolved("NCT00000001", "Adult", unresolved:=True)
        Await _fixture.Gateway.PersistTrialAsync(
                "NCT00000001",
                New ResolvedRecord() {unresolved},
                CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT concept_code, umls_name, match_source, match_score
                    FROM public.eligibility WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", "NCT00000001")
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.True(reader.IsDBNull(0))         ' concept_code NULL
                    Assert.True(reader.IsDBNull(1))         ' umls_name NULL
                    Assert.True(reader.IsDBNull(2))         ' match_source NULL
                    Assert.Equal(0D, reader.GetDecimal(3))  ' match_score 0
                End Using
            End Using
        End Using
    End Function

    ' ============ RecordRunAsync ============

    <SkippableFact>
    Public Async Function RecordRun_inserts_new_row() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Dim metrics = New RunMetrics(
                runId:=runId,
                startedAt:=DateTimeOffset.UtcNow,
                endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="webhook",
                studyCount:=50,
                studiesProcessed:=50,
                rowsPersisted:=374,
                resolutionRate:=0.882,
                status:="success",
                errorSummary:="")
        Await _fixture.Gateway.RecordRunAsync(metrics, CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT status, rows_persisted FROM public.eligibility_run WHERE run_id = @id"
                cmd.Parameters.AddWithValue("id", runId)
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.Equal("success", reader.GetString(0))
                    Assert.Equal(374, reader.GetInt32(1))
                End Using
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function RecordRun_updates_existing_row_on_conflict() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Dim started As New DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero)

        ' First write: in-flight (no ended_at, 0 rows)
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(runId, started, Nothing, "form", 50, 0, 0, 0, "running", ""),
                CancellationToken.None)

        ' Second write: complete
        Dim ended = started.AddMinutes(11)
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(runId, started, ended, "form", 50, 50, 374, 0.882, "success", ""),
                CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT status, rows_persisted, ended_at IS NOT NULL FROM public.eligibility_run WHERE run_id = @id"
                cmd.Parameters.AddWithValue("id", runId)
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.Equal("success", reader.GetString(0))
                    Assert.Equal(374, reader.GetInt32(1))
                    Assert.True(reader.GetBoolean(2))
                End Using
            End Using
        End Using
    End Function

    ' ============ GetRecentRunsAsync ============

    <SkippableFact>
    Public Async Function GetRecentRuns_returns_rows_ordered_by_started_at_desc() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim baseline As New DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero)
        For i = 0 To 4
            Dim metrics = New RunMetrics(
                    runId:=Guid.NewGuid(),
                    startedAt:=baseline.AddMinutes(i),
                    endedAt:=baseline.AddMinutes(i).AddMinutes(10),
                    triggerSource:="webhook",
                    studyCount:=50,
                    studiesProcessed:=50,
                    rowsPersisted:=374,
                    resolutionRate:=0.882,
                    status:="success",
                    errorSummary:="")
            Await _fixture.Gateway.RecordRunAsync(metrics, CancellationToken.None)
        Next

        Dim recent = Await _fixture.Gateway.GetRecentRunsAsync(3, CancellationToken.None)
        Assert.Equal(3, recent.Count)
        ' Most recent first.
        For i = 0 To recent.Count - 2
            Assert.True(recent(i).StartedAt > recent(i + 1).StartedAt,
                        $"Expected descending StartedAt but {recent(i).StartedAt} <= {recent(i + 1).StartedAt}")
        Next
    End Function

    <SkippableFact>
    Public Async Function GetRecentRuns_aggregates_completion_tokens_from_studies() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Dim startedAt As New DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero)
        Dim endedAt = startedAt.AddSeconds(20)
        ' Record with a concurrency cap so the round-trip can be asserted too.
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(runId, startedAt, endedAt, "form", 4, 4, 2, 1.0, "success", "",
                               completionTokens:=0, concurrencyCap:=8),
                CancellationToken.None)

        ' Two SUCCESSFUL studies: 600 + 900 = 1500 completion tokens. Phase timings
        ' average to llm=4500, umls=2500, persist=150 ms.
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000001", startedAt, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runId, nctId:="NCT00000001", startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(10),
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=100, llmCompletionTokens:=600,
                parsedRecordCount:=1, persistedRowCount:=1, errorMessage:="",
                llmMs:=4000, umlsMs:=2000, persistMs:=100), CancellationToken.None)
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000002", startedAt, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runId, nctId:="NCT00000002", startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(12),
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=120, llmCompletionTokens:=900,
                parsedRecordCount:=1, persistedRowCount:=1, errorMessage:="",
                llmMs:=5000, umlsMs:=3000, persistMs:=200), CancellationToken.None)
        ' A NULL-token row (parse_empty) must not break the SUM or the averages.
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000003", startedAt, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runId, nctId:="NCT00000003", startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(8),
                status:=StudyExecution.StatusParseEmpty, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:=""), CancellationToken.None)
        ' A FAILED study burned 5000 tokens and 9s phases but must be EXCLUDED.
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000004", startedAt, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runId, nctId:="NCT00000004", startedAt:=startedAt, finishedAt:=startedAt.AddSeconds(15),
                status:=StudyExecution.StatusParseInvalidJson, llmSucceeded:=True, llmFinishReason:="length",
                llmPromptTokens:=200, llmCompletionTokens:=5000,
                parsedRecordCount:=0, persistedRowCount:=0, errorMessage:="truncated",
                llmMs:=9000, umlsMs:=9000, persistMs:=9000), CancellationToken.None)

        Dim recent = Await _fixture.Gateway.GetRecentRunsAsync(10, CancellationToken.None)
        Dim row = recent.Single(Function(r) r.RunId = runId)
        ' Only the two successful trials count: 1500, not 6500.
        Assert.Equal(1500L, row.CompletionTokens)
        ' 1500 tokens / 20 s wall clock = 75.0 tok/s aggregate.
        Assert.Equal(75.0, row.CompletionTokensPerSecond.Value, precision:=3)
        ' The concurrency cap used for the run round-trips.
        Assert.Equal(8, row.ConcurrencyCap)
        ' Phase averages over the two successful trials; the failed trial's 9s
        ' phases are excluded by the FILTER (WHERE status = 'success').
        Assert.Equal(4500.0, row.AvgLlmMs.Value, precision:=1)
        Assert.Equal(2500.0, row.AvgUmlsMs.Value, precision:=1)
        Assert.Equal(150.0, row.AvgPersistMs.Value, precision:=1)
    End Function

    <SkippableFact>
    Public Async Function GetRecentRuns_clamps_limit_to_sensible_bounds() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Insert two runs; request limit=0 (which clamps up to 1) — assert we get
        ' exactly one row back rather than a Postgres LIMIT 0 / SQL error.
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(Guid.NewGuid(), DateTimeOffset.UtcNow, Nothing, "form", 10, 0, 0, 0, "running", ""),
                CancellationToken.None)
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(Guid.NewGuid(), DateTimeOffset.UtcNow, Nothing, "form", 10, 0, 0, 0, "running", ""),
                CancellationToken.None)

        Dim zeroLimit = Await _fixture.Gateway.GetRecentRunsAsync(0, CancellationToken.None)
        Assert.Single(zeroLimit)
    End Function

    <SkippableFact>
    Public Async Function GetRecentRuns_returns_empty_when_table_empty() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim recent = Await _fixture.Gateway.GetRecentRunsAsync(10, CancellationToken.None)
        Assert.Empty(recent)
    End Function

    ' ============ RecordFailedTrialAsync ============

    <SkippableFact>
    Public Async Function RecordFailedTrial_inserts_new_row_with_attempt_count_1() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.RecordFailedTrialAsync("NCT00000001", "timeout", CancellationToken.None)
        Assert.Equal(1, Await _fixture.GetFailedTrialAttemptCountAsync("NCT00000001"))
    End Function

    <SkippableFact>
    Public Async Function RecordFailedTrial_increments_attempt_count_on_conflict() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.RecordFailedTrialAsync("NCT00000001", "timeout", CancellationToken.None)
        Await _fixture.Gateway.RecordFailedTrialAsync("NCT00000001", "another timeout", CancellationToken.None)
        Await _fixture.Gateway.RecordFailedTrialAsync("NCT00000001", "third timeout", CancellationToken.None)

        Assert.Equal(3, Await _fixture.GetFailedTrialAttemptCountAsync("NCT00000001"))
    End Function

    ' ============ GetSourceTrialAsync (single-trial re-run path) ============

    <SkippableFact>
    Public Async Function GetSourceTrial_returns_trial_when_present() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000050", "Inclusion: adult with diabetes.")

        Dim trial = Await _fixture.Gateway.GetSourceTrialAsync(
                "NCT00000050", CancellationToken.None)

        Assert.NotNull(trial)
        Assert.Equal("NCT00000050", trial.NctId)
        Assert.Contains("diabetes", trial.Criteria)
    End Function

    <SkippableFact>
    Public Async Function GetSourceTrial_returns_nothing_when_missing() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim trial = Await _fixture.Gateway.GetSourceTrialAsync(
                "NCT99999999", CancellationToken.None)
        Assert.Null(trial)
    End Function

    <SkippableFact>
    Public Async Function GetSourceTrial_bypasses_length_and_contact_filters() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Short criteria + "please contact" — SelectNextTrialsAsync would
        ' filter these out, but GetSourceTrialAsync should return them
        ' regardless (operator explicitly asked for this trial).
        Await _fixture.InsertSourceTrialAsync("NCT00000060", "short")
        Await _fixture.InsertSourceTrialAsync("NCT00000061", "please contact site for information")

        Dim shortTrial = Await _fixture.Gateway.GetSourceTrialAsync(
                "NCT00000060", CancellationToken.None)
        Dim contactTrial = Await _fixture.Gateway.GetSourceTrialAsync(
                "NCT00000061", CancellationToken.None)

        Assert.NotNull(shortTrial)
        Assert.NotNull(contactTrial)
        Assert.Equal("short", shortTrial.Criteria)
    End Function

    ' ============ GetSourceTrialsAsync (batched re-run path) ============

    <SkippableFact>
    Public Async Function GetSourceTrials_returns_all_present_in_one_call() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000010", "Inclusion: adult with diabetes.")
        Await _fixture.InsertSourceTrialAsync("NCT00000020", "Inclusion: adult with asthma.")

        Dim trials = Await _fixture.Gateway.GetSourceTrialsAsync(
                New String() {"NCT00000010", "NCT00000020"}, CancellationToken.None)

        Assert.Equal(2, trials.Count)
        Dim byId = trials.ToDictionary(Function(t) t.NctId)
        Assert.Contains("diabetes", byId("NCT00000010").Criteria)
        Assert.Contains("asthma", byId("NCT00000020").Criteria)
    End Function

    <SkippableFact>
    Public Async Function GetSourceTrials_omits_missing_ids_without_failing_the_rest() As Task
        ' The batch is the re-run collection step: present ids come back, absent
        ' ids are simply not in the result (the orchestrator diffs to log skips).
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000010", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.GetSourceTrialsAsync(
                New String() {"NCT00000010", "NCT_DOES_NOT_EXIST"}, CancellationToken.None)

        Dim trial = Assert.Single(trials)
        Assert.Equal("NCT00000010", trial.NctId)
    End Function

    <SkippableFact>
    Public Async Function GetSourceTrials_bypasses_length_and_contact_filters() As Task
        ' Same operator-driven semantics as the single-trial form: no length /
        ' "please contact" filtering on the re-run path.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000060", "short")
        Await _fixture.InsertSourceTrialAsync("NCT00000061", "please contact site for information")

        Dim trials = Await _fixture.Gateway.GetSourceTrialsAsync(
                New String() {"NCT00000060", "NCT00000061"}, CancellationToken.None)

        Assert.Equal(2, trials.Count)
    End Function

    <SkippableFact>
    Public Async Function GetSourceTrials_returns_empty_for_empty_input_without_hitting_db() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim trials = Await _fixture.Gateway.GetSourceTrialsAsync(
                Array.Empty(Of String)(), CancellationToken.None)
        Assert.Empty(trials)
    End Function

    <SkippableFact>
    Public Async Function GetSourceTrials_dedupes_and_ignores_blank_ids() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertSourceTrialAsync("NCT00000010", New String("a"c, 60))

        Dim trials = Await _fixture.Gateway.GetSourceTrialsAsync(
                New String() {"NCT00000010", "NCT00000010", " ", ""}, CancellationToken.None)

        Dim trial = Assert.Single(trials)
        Assert.Equal("NCT00000010", trial.NctId)
    End Function

    ' ============ AACT markdown-escape normalization ============

    <SkippableFact>
    Public Async Function SelectNextTrials_strips_markdown_escape_backslashes_from_criteria() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' AACT-style text with markdown-escape backslashes — should arrive
        ' downstream un-escaped so the LLM sees clean text and the dashboard's
        ' substring matching works without further repair.
        Const RawWithEscapes As String =
                "Patients who have FEV1 of \>= 60% of predicted, blood gases with PO2 \>= 60 or oxygen saturation \>= 90% are eligible."
        Await _fixture.InsertSourceTrialAsync("NCT00000001", RawWithEscapes)

        Dim trials = Await _fixture.Gateway.SelectNextTrialsAsync(
                Array.Empty(Of String)(), TrialSelectionDirection.Forward, 10, CancellationToken.None)

        Dim trial = Assert.Single(trials)
        Assert.DoesNotContain("\>", trial.Criteria)
        Assert.Contains(">= 60%", trial.Criteria)
    End Function

    <SkippableFact>
    Public Async Function GetSourceEligibility_strips_markdown_escapes_from_criteria() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertSourceEligibilityFullAsync(
                nctId:="NCT00000007",
                criteria:="Age \< 65 with FEV1 \>= 60%")

        Dim result = Await _fixture.Gateway.GetSourceEligibilityAsync(
                "NCT00000007", CancellationToken.None)

        Assert.NotNull(result)
        Assert.DoesNotContain("\<", result.Criteria)
        Assert.DoesNotContain("\>", result.Criteria)
        Assert.Contains("< 65", result.Criteria)
        Assert.Contains(">= 60%", result.Criteria)
    End Function

    ' ============ ResetOutputAsync ============

    <SkippableFact>
    Public Async Function ResetOutput_empties_every_output_table() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Seed every output table with something to confirm reset clears all of them.
        Await _fixture.Gateway.RecordRunAsync(New RunMetrics(
                runId:=Guid.NewGuid(),
                startedAt:=DateTimeOffset.UtcNow,
                endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="cli",
                studyCount:=1,
                studiesProcessed:=1,
                rowsPersisted:=1,
                resolutionRate:=0.5,
                status:="success",
                errorSummary:=""), CancellationToken.None)
        Await _fixture.Gateway.RecordFailedTrialAsync("NCT00000001", "boom", CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000002",
                {MakeResolvedWithCriterion("NCT00000002", "Inclusion", "diabetes")},
                CancellationToken.None)
        Await _fixture.Gateway.StartStudyAsync(Guid.NewGuid(), "NCT00000003",
                DateTimeOffset.UtcNow, CancellationToken.None)

        Await _fixture.Gateway.ResetOutputAsync(CancellationToken.None)

        Assert.Empty(Await _fixture.Gateway.GetAttemptedNctIdsAsync(CancellationToken.None))
        Assert.Empty(Await _fixture.Gateway.GetRecentRunsAsync(100, CancellationToken.None))
        Assert.Empty(Await _fixture.Gateway.GetStudyHistoryAsync("NCT00000003", CancellationToken.None))
        Assert.Equal(0, Await _fixture.CountEligibilityRowsAsync("NCT00000002"))
    End Function

    ' ============ SearchEligibilityAsync / GetEligibilityFilterOptionsAsync ============

    <SkippableFact>
    Public Async Function SearchEligibility_with_empty_filter_returns_all_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeEligibilityRowsAsync()

        Dim result = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(), sortBy:=Nothing, page:=1, pageSize:=100, CancellationToken.None)

        Assert.Equal(3, result.Rows.Count)
        Assert.Equal(3L, result.TotalRows)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_exact_match_filters_by_nct_id() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeEligibilityRowsAsync()

        Dim result = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(nctId:="NCT00000002"), sortBy:=Nothing, page:=1, pageSize:=100, CancellationToken.None)

        Assert.Single(result.Rows)
        Assert.Equal("NCT00000002", result.Rows(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_substring_match_on_criterion_is_case_insensitive() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Seed two trials with distinct criterion strings.
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001",
                {MakeResolvedWithCriterion("NCT00000001", "Adult Inclusion", "diabetes")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000002",
                {MakeResolvedWithCriterion("NCT00000002", "Pediatric Exclusion", "asthma")},
                CancellationToken.None)

        ' ILIKE %adult% should match "Adult Inclusion" but not "Pediatric Exclusion".
        Dim result = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(criterion:="adult"), sortBy:=Nothing, page:=1, pageSize:=100, CancellationToken.None)

        Assert.Single(result.Rows)
        Assert.Equal("NCT00000001", result.Rows(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_combines_filter_predicates_with_AND() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeEligibilityRowsAsync()

        ' nct_id matches row 2, but its concept is "concept-b" — combined filter
        ' on nct_id=2 AND concept~=concept-c should match nothing.
        Dim result = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(nctId:="NCT00000002", concept:="concept-c"),
                sortBy:=Nothing, page:=1, pageSize:=100, CancellationToken.None)

        Assert.Empty(result.Rows)
        Assert.Equal(0L, result.TotalRows)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_sort_by_nct_id_asc_orders_rows_alphabetically() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Insert rows in reverse alphabetical order; sort=nct_id_asc must flip them.
        Await _fixture.Gateway.PersistTrialAsync("NCT00000003",
                {MakeResolvedWithCriterion("NCT00000003", "Inclusion", "c")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001",
                {MakeResolvedWithCriterion("NCT00000001", "Inclusion", "a")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000002",
                {MakeResolvedWithCriterion("NCT00000002", "Inclusion", "b")},
                CancellationToken.None)

        Dim result = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(), sortBy:="nct_id_asc", page:=1, pageSize:=100, CancellationToken.None)

        Assert.Equal(3, result.Rows.Count)
        Assert.Equal("NCT00000001", result.Rows(0).NctId)
        Assert.Equal("NCT00000002", result.Rows(1).NctId)
        Assert.Equal("NCT00000003", result.Rows(2).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_unknown_sort_falls_back_to_created_at_desc() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeEligibilityRowsAsync()

        ' Bogus sortBy value must NOT escape into the SQL — gateway falls back
        ' to the default ordering instead. Three rows survive either way.
        Dim result = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(),
                sortBy:="; DROP TABLE eligibility;--",
                page:=1, pageSize:=100, CancellationToken.None)

        Assert.Equal(3, result.Rows.Count)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_pagination_returns_requested_slice_and_total() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Seed five rows across five distinct nct_ids so we can page through them.
        For i = 1 To 5
            Dim nctId = $"NCT{i:D8}"
            Await _fixture.Gateway.PersistTrialAsync(nctId,
                    {MakeResolvedWithCriterion(nctId, "Inclusion", $"concept-{i}")},
                    CancellationToken.None)
        Next

        Dim page1 = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(), sortBy:="nct_id_asc", page:=1, pageSize:=2, CancellationToken.None)
        Assert.Equal(2, page1.Rows.Count)
        Assert.Equal(5L, page1.TotalRows)
        Assert.Equal(3, page1.TotalPages)  ' ceil(5/2) = 3
        Assert.Equal("NCT00000001", page1.Rows(0).NctId)
        Assert.Equal("NCT00000002", page1.Rows(1).NctId)

        Dim page2 = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(), sortBy:="nct_id_asc", page:=2, pageSize:=2, CancellationToken.None)
        Assert.Equal("NCT00000003", page2.Rows(0).NctId)
        Assert.Equal("NCT00000004", page2.Rows(1).NctId)

        ' Last page is partial (1 row).
        Dim page3 = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(), sortBy:="nct_id_asc", page:=3, pageSize:=2, CancellationToken.None)
        Assert.Single(page3.Rows)
        Assert.Equal("NCT00000005", page3.Rows(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function GetEligibilityFilterOptions_returns_dropdown_lists_for_low_cardinality_columns() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeEligibilityRowsAsync()

        Dim options = Await _fixture.Gateway.GetEligibilityFilterOptionsAsync(
                100, CancellationToken.None)

        ' All three rows share the same domain "Disease", so Domains has 1 entry.
        Assert.Single(options.Domains)
        Assert.Equal("Disease", options.Domains(0))
        ' Three distinct nct_ids are below the threshold of 100 → dropdown.
        Assert.Equal(3, options.NctIds.Count)
        Assert.Contains("NCT00000001", options.NctIds)
    End Function

    <SkippableFact>
    Public Async Function GetEligibilityFilterOptions_returns_empty_list_when_above_threshold() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedThreeEligibilityRowsAsync()

        ' Threshold 2 < 3 distinct nct_ids → NctIds dropdown disabled (empty).
        Dim options = Await _fixture.Gateway.GetEligibilityFilterOptionsAsync(
                2, CancellationToken.None)

        Assert.Empty(options.NctIds)
        ' Domain has only 1 distinct value, still under the threshold of 2.
        Assert.Single(options.Domains)
    End Function

    ' ============ GetStudyDetailsAsync / GetSourceEligibilityAsync (Analysis tab) ============

    <SkippableFact>
    Public Async Function GetStudyDetails_returns_nothing_when_study_missing() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim result = Await _fixture.Gateway.GetStudyDetailsAsync(
                "NCT99999999", CancellationToken.None)
        Assert.Null(result)
    End Function

    <SkippableFact>
    Public Async Function GetStudyDetails_returns_full_record_when_present() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertSourceStudyAsync(
                nctId:="NCT00000001",
                briefTitle:="A Test Trial",
                officialTitle:="A Long Official Title for the Test Trial",
                overallStatus:="Recruiting",
                phase:="Phase 3",
                studyType:="Interventional",
                source:="Acme Pharma",
                enrollment:=120,
                briefSummary:="Short narrative summary.")
        Await _fixture.InsertSourceConditionAsync("NCT00000001", "Diabetes Mellitus")
        Await _fixture.InsertSourceConditionAsync("NCT00000001", "Hypertension")
        Await _fixture.InsertSourceInterventionAsync("NCT00000001", "Drug", "Metformin")
        Await _fixture.InsertSourceInterventionAsync("NCT00000001", "Behavioral", "Diet counselling")

        Dim study = Await _fixture.Gateway.GetStudyDetailsAsync(
                "NCT00000001", CancellationToken.None)

        Assert.NotNull(study)
        Assert.Equal("A Test Trial", study.BriefTitle)
        Assert.Equal("Recruiting", study.OverallStatus)
        Assert.Equal("Phase 3", study.Phase)
        Assert.Equal("Acme Pharma", study.Source)
        Assert.Equal(120, study.Enrollment)
        Assert.Equal("Short narrative summary.", study.BriefSummary)
        Assert.Equal(2, study.Conditions.Count)
        Assert.Contains("Diabetes Mellitus", study.Conditions)
        Assert.Equal(2, study.Interventions.Count)
        Assert.Contains(study.Interventions, Function(i) i.InterventionType = "Drug" AndAlso i.Name = "Metformin")
    End Function

    <SkippableFact>
    Public Async Function GetSourceEligibility_returns_structured_fields_and_criteria() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertSourceEligibilityFullAsync(
                nctId:="NCT00000002",
                criteria:="Inclusion: adult; Exclusion: pregnancy.",
                gender:="Female",
                minimumAge:="21 Years",
                maximumAge:="65 Years",
                healthyVolunteers:="No",
                samplingMethod:="",
                population:="",
                adult:=True,
                child:=False,
                olderAdult:=True)

        Dim result = Await _fixture.Gateway.GetSourceEligibilityAsync(
                "NCT00000002", CancellationToken.None)

        Assert.NotNull(result)
        Assert.Equal("Female", result.Gender)
        Assert.Equal("21 Years", result.MinimumAge)
        Assert.Equal("65 Years", result.MaximumAge)
        Assert.True(result.Adult)
        Assert.False(result.Child)
        Assert.True(result.OlderAdult)
        Assert.Contains("Inclusion", result.Criteria)
    End Function

    <SkippableFact>
    Public Async Function GetSourceEligibility_returns_nothing_when_trial_missing() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim result = Await _fixture.Gateway.GetSourceEligibilityAsync(
                "NCT99999999", CancellationToken.None)
        Assert.Null(result)
    End Function

    ' ============ Study audit (Start/Finish/GetStudies/GetStudyHistory) ============

    <SkippableFact>
    Public Async Function StartStudy_then_FinishStudy_round_trips_terminal_state() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Dim started = DateTimeOffset.UtcNow.AddSeconds(-5)
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000001", started, CancellationToken.None)

        Const RawResponse As String = "[{""NCT_ID"":""NCT00000001"",""Concept"":""Diabetes""}]"
        Dim execution = New StudyExecution(
                runId:=runId,
                nctId:="NCT00000001",
                startedAt:=started,
                finishedAt:=DateTimeOffset.UtcNow,
                status:=StudyExecution.StatusSuccess,
                llmSucceeded:=True,
                llmFinishReason:="stop",
                llmPromptTokens:=967,
                llmCompletionTokens:=2957,
                parsedRecordCount:=1,
                persistedRowCount:=1,
                errorMessage:="",
                llmRawResponse:=RawResponse)
        Await _fixture.Gateway.FinishStudyAsync(execution, CancellationToken.None)

        Dim history = Await _fixture.Gateway.GetStudyHistoryAsync("NCT00000001", CancellationToken.None)
        Assert.Single(history)
        Dim row = history(0)
        Assert.Equal(StudyExecution.StatusSuccess, row.Status)
        Assert.True(row.LlmSucceeded)
        Assert.Equal("stop", row.LlmFinishReason)
        Assert.Equal(967, row.LlmPromptTokens)
        Assert.Equal(2957, row.LlmCompletionTokens)
        Assert.Equal(1, row.ParsedRecordCount)
        Assert.Equal(1, row.PersistedRowCount)
        Assert.NotNull(row.FinishedAt)
        Assert.Equal(RawResponse, row.LlmRawResponse)
    End Function

    <SkippableFact>
    Public Async Function StartStudy_leaves_row_in_running_state_when_no_finish() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(
                runId, "NCT00000002", DateTimeOffset.UtcNow, CancellationToken.None)

        Dim history = Await _fixture.Gateway.GetStudyHistoryAsync("NCT00000002", CancellationToken.None)
        Assert.Single(history)
        Assert.Equal(StudyExecution.StatusRunning, history(0).Status)
        Assert.Null(history(0).FinishedAt)
    End Function

    <SkippableFact>
    Public Async Function StartStudy_upserts_on_second_call_same_run_and_nct() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(
                runId, "NCT00000003", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None)

        ' Finish the first attempt as failed.
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=runId, nctId:="NCT00000003",
                startedAt:=DateTimeOffset.UtcNow.AddMinutes(-1),
                finishedAt:=DateTimeOffset.UtcNow.AddSeconds(-30),
                status:=StudyExecution.StatusLlmFailed,
                llmSucceeded:=False, llmFinishReason:="", llmPromptTokens:=Nothing,
                llmCompletionTokens:=Nothing, parsedRecordCount:=Nothing,
                persistedRowCount:=Nothing, errorMessage:="boom"),
                CancellationToken.None)

        ' StartStudy again with the same (run, nct) — must reset to running and
        ' clear the terminal columns.
        Await _fixture.Gateway.StartStudyAsync(
                runId, "NCT00000003", DateTimeOffset.UtcNow, CancellationToken.None)

        Dim history = Await _fixture.Gateway.GetStudyHistoryAsync("NCT00000003", CancellationToken.None)
        Assert.Single(history)
        Assert.Equal(StudyExecution.StatusRunning, history(0).Status)
        Assert.Null(history(0).FinishedAt)
        Assert.Null(history(0).LlmSucceeded)
        Assert.Equal("", history(0).ErrorMessage)
    End Function

    <SkippableFact>
    Public Async Function GetStudies_paginates_and_filters_by_status() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Seed three studies: 2 success, 1 llm_failed.
        For i = 1 To 3
            Dim runId = Guid.NewGuid()
            Dim nct = $"NCT{i:D8}"
            Await _fixture.Gateway.StartStudyAsync(runId, nct, DateTimeOffset.UtcNow, CancellationToken.None)
            Dim status = If(i = 3, StudyExecution.StatusLlmFailed, StudyExecution.StatusSuccess)
            Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                    runId:=runId, nctId:=nct,
                    startedAt:=DateTimeOffset.UtcNow, finishedAt:=DateTimeOffset.UtcNow,
                    status:=status, llmSucceeded:=(status = StudyExecution.StatusSuccess),
                    llmFinishReason:="", llmPromptTokens:=Nothing,
                    llmCompletionTokens:=Nothing, parsedRecordCount:=Nothing,
                    persistedRowCount:=Nothing, errorMessage:=""), CancellationToken.None)
        Next

        Dim filteredFailed = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(status:=StudyExecution.StatusLlmFailed),
                sortBy:=Nothing, page:=1, pageSize:=10, CancellationToken.None)
        Assert.Single(filteredFailed.Rows)
        Assert.Equal("NCT00000003", filteredFailed.Rows(0).NctId)

        Dim allPage = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(), sortBy:=Nothing, page:=1, pageSize:=10, CancellationToken.None)
        Assert.Equal(3, allPage.Rows.Count)
        Assert.Equal(3L, allPage.TotalRows)
    End Function

    <SkippableFact>
    Public Async Function GetStudies_sorts_by_duration_descending_with_nulls_last() As Task
        ' Sanity-check that the duration sort key resolves to a valid SQL
        ' expression and orders correctly. Three rows: one finished after
        ' 10s, one finished after 30s, one still running (finished_at IS
        ' NULL). Expected order with duration_desc + NULLS LAST is
        ' 30s row → 10s row → running row.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim baseTime = DateTimeOffset.UtcNow.AddMinutes(-5)
        Dim nctIds = New String() {"NCT00000010", "NCT00000020"}
        Dim elapsedSeconds = New Integer() {10, 30}
        For i = 0 To nctIds.Length - 1
            Dim runId = Guid.NewGuid()
            Await _fixture.Gateway.StartStudyAsync(
                    runId, nctIds(i), baseTime, CancellationToken.None)
            Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                    runId:=runId, nctId:=nctIds(i),
                    startedAt:=baseTime,
                    finishedAt:=baseTime.AddSeconds(elapsedSeconds(i)),
                    status:=StudyExecution.StatusSuccess,
                    llmSucceeded:=True, llmFinishReason:="stop",
                    llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                    parsedRecordCount:=Nothing, persistedRowCount:=Nothing,
                    errorMessage:=""), CancellationToken.None)
        Next

        ' Third row stays running (no FinishStudyAsync call) — its
        ' (finished_at - started_at) is NULL.
        Await _fixture.Gateway.StartStudyAsync(
                Guid.NewGuid(), "NCT00000099", baseTime, CancellationToken.None)

        Dim page = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(),
                sortBy:="duration_desc",
                page:=1, pageSize:=10, CancellationToken.None)

        Assert.Equal(3, page.Rows.Count)
        Assert.Equal("NCT00000020", page.Rows(0).NctId)
        Assert.Equal("NCT00000010", page.Rows(1).NctId)
        Assert.Equal("NCT00000099", page.Rows(2).NctId)
    End Function

    <SkippableFact>
    Public Async Function GetStudies_sorts_by_token_count_descending() As Task
        ' Confirms the llm_prompt_tokens sort key resolves to valid SQL.
        ' Three rows with prompt token counts 100, 500, NULL — expected
        ' order with prompt_tokens_desc is 500 → 100 → NULL.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim baseTime = DateTimeOffset.UtcNow.AddMinutes(-5)
        Dim nctIds = New String() {"NCT00000011", "NCT00000022", "NCT00000033"}
        Dim tokenCounts = New Integer?() {100, 500, Nothing}
        For i = 0 To nctIds.Length - 1
            Dim runId = Guid.NewGuid()
            Await _fixture.Gateway.StartStudyAsync(
                    runId, nctIds(i), baseTime, CancellationToken.None)
            Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                    runId:=runId, nctId:=nctIds(i),
                    startedAt:=baseTime,
                    finishedAt:=baseTime.AddSeconds(1),
                    status:=StudyExecution.StatusSuccess,
                    llmSucceeded:=True, llmFinishReason:="stop",
                    llmPromptTokens:=tokenCounts(i), llmCompletionTokens:=Nothing,
                    parsedRecordCount:=Nothing, persistedRowCount:=Nothing,
                    errorMessage:=""), CancellationToken.None)
        Next

        Dim page = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(),
                sortBy:="prompt_tokens_desc",
                page:=1, pageSize:=10, CancellationToken.None)

        Assert.Equal(3, page.Rows.Count)
        Assert.Equal("NCT00000022", page.Rows(0).NctId)
        Assert.Equal("NCT00000011", page.Rows(1).NctId)
        Assert.Equal("NCT00000033", page.Rows(2).NctId)
    End Function

    <SkippableFact>
    Public Async Function GetStudyHistory_orders_newest_first() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim baseTime = DateTimeOffset.UtcNow.AddMinutes(-10)
        For i = 0 To 2
            Dim runId = Guid.NewGuid()
            Await _fixture.Gateway.StartStudyAsync(
                    runId, "NCT00000007", baseTime.AddMinutes(i), CancellationToken.None)
        Next

        Dim history = Await _fixture.Gateway.GetStudyHistoryAsync("NCT00000007", CancellationToken.None)
        Assert.Equal(3, history.Count)
        Assert.True(history(0).StartedAt > history(1).StartedAt)
        Assert.True(history(1).StartedAt > history(2).StartedAt)
    End Function

    ' ============ helpers ============

    Private Async Function SeedThreeEligibilityRowsAsync() As Task
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001",
                {MakeResolvedWithCriterion("NCT00000001", "Inclusion", "concept-a")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000002",
                {MakeResolvedWithCriterion("NCT00000002", "Inclusion", "concept-b")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000003",
                {MakeResolvedWithCriterion("NCT00000003", "Inclusion", "concept-c")},
                CancellationToken.None)
    End Function

    ' ============ UMLS-only retry (V19) ============

    <SkippableFact>
    Public Async Function RetryUmls_selects_updates_in_place_and_tracks_per_trial() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Trial A: two unresolved rows (gaps). Trial B: one resolved row (no gap).
        Await _fixture.Gateway.PersistTrialAsync("NCT00000010",
                {MakeUnresolvedWithCriterion("NCT00000010", "Inclusion", "low blood sugar"),
                 MakeUnresolvedWithCriterion("NCT00000010", "Inclusion", "stubborn term")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000020",
                {MakeResolvedWithCriterion("NCT00000020", "Inclusion", "diabetes")},
                CancellationToken.None)

        ' Selection returns only the trial with gaps (B is fully resolved).
        Dim toRetry = Await _fixture.Gateway.SelectTrialsToRetryUmlsAsync(
                TrialSelectionDirection.Forward, 10, includeRetried:=False, CancellationToken.None)
        Assert.Equal({"NCT00000010"}, toRetry)

        ' Both unresolved rows are returned with their ids + concepts.
        Dim rows = Await _fixture.Gateway.GetUnresolvedRowsForTrialAsync("NCT00000010", CancellationToken.None)
        Assert.Equal(2, rows.Count)
        Dim target = rows.First(Function(r) r.Concept = "low blood sugar")

        ' Resolve ONE of the two; record the attempt (attempted = 2, resolved = 1).
        Dim updates = {New UmlsRetryResult(
                target.Id, "C0020615", "Hypoglycemia", "SNOMEDCT_US", 0.812, "Disease or Syndrome")}
        Await _fixture.Gateway.ApplyUmlsRetryAsync("NCT00000010", updates, rowsAttempted:=2, CancellationToken.None)

        ' The targeted row now carries the five UMLS columns (in place — id preserved).
        Dim resolvedRow = Await ReadUmlsColumnsAsync(target.Id)
        Assert.Equal("C0020615", resolvedRow.ConceptCode)
        Assert.Equal("Hypoglycemia", resolvedRow.UmlsName)
        Assert.Equal("SNOMEDCT_US", resolvedRow.MatchSource)
        Assert.Equal("Disease or Syndrome", resolvedRow.SemanticType)
        Assert.Equal(0.812D, resolvedRow.MatchScore)

        ' The bookkeeping row recorded the attempt with both counts.
        Dim tracked = Await ReadRetryCountsAsync("NCT00000010")
        Assert.Equal(2, tracked.Attempted)
        Assert.Equal(1, tracked.Resolved)

        ' The trial is now anti-joined out — even though it still has 1 unresolved row.
        Dim afterRetry = Await _fixture.Gateway.SelectTrialsToRetryUmlsAsync(
                TrialSelectionDirection.Forward, 10, includeRetried:=False, CancellationToken.None)
        Assert.Empty(afterRetry)

        ' ...unless --force (includeRetried) re-includes it for another pass.
        Dim forced = Await _fixture.Gateway.SelectTrialsToRetryUmlsAsync(
                TrialSelectionDirection.Forward, 10, includeRetried:=True, CancellationToken.None)
        Assert.Equal({"NCT00000010"}, forced)
    End Function

    Private Async Function ReadUmlsColumnsAsync(
            id As Long) As Task(Of (ConceptCode As String, UmlsName As String, MatchSource As String, MatchScore As Decimal, SemanticType As String))
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText =
                    "SELECT concept_code, umls_name, match_source, match_score, semantic_type FROM public.eligibility WHERE id = @id"
                cmd.Parameters.AddWithValue("id", id)
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Return (reader.GetString(0), reader.GetString(1), reader.GetString(2),
                            reader.GetDecimal(3), reader.GetString(4))
                End Using
            End Using
        End Using
    End Function

    Private Async Function ReadRetryCountsAsync(
            nctId As String) As Task(Of (Attempted As Integer, Resolved As Integer))
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText =
                    "SELECT rows_attempted, rows_resolved FROM public.eligibility_umls_retry WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Return (reader.GetInt32(0), reader.GetInt32(1))
                End Using
            End Using
        End Using
    End Function

    ' ============ LLM concept-normalization (V20) ============

    <SkippableFact>
    Public Async Function NormalizeUmls_selects_records_updates_and_caches() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Two trials share an unresolved concept in different casing (same normalized
        ' key); a third is already resolved (must not be selected).
        Await _fixture.Gateway.PersistTrialAsync("NCT00000010",
                {MakeUnresolvedWithCriterion("NCT00000010", "Inclusion", "low blood sugar")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000011",
                {MakeUnresolvedWithCriterion("NCT00000011", "Inclusion", "Low Blood Sugar")},
                CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000012",
                {MakeResolvedWithCriterion("NCT00000012", "Inclusion", "diabetes")},
                CancellationToken.None)

        ' Selection returns ONE distinct residue concept (the casing variants dedupe).
        Dim toNormalize = Await _fixture.Gateway.SelectConceptsToNormalizeAsync(10, includeAttempted:=False, CancellationToken.None)
        Assert.Single(toNormalize)
        Assert.Equal("low blood sugar", toNormalize(0).ConceptNorm)

        ' Record a resolved mapping; both unresolved rows (either casing) are updated.
        Dim match = New UmlsMatch("C0020615", "Hypoglycemia", "SNOMEDCT_US", 0.83)
        Dim rows = Await _fixture.Gateway.RecordConceptNormalizationAsync(
                toNormalize(0).ConceptNorm, "Hypoglycemia", match, "Disease or Syndrome", CancellationToken.None)
        Assert.Equal(2, rows)
        Assert.Equal(2L, Await CountByConceptCodeAsync("C0020615"))

        ' The cache read returns the mapping keyed by the raw concept (either casing).
        Dim cached = Await _fixture.Gateway.GetCachedNormalizationsAsync(
                {"low blood sugar", "Low Blood Sugar", "unrelated"}, CancellationToken.None)
        Assert.True(cached.ContainsKey("low blood sugar"))
        Assert.True(cached.ContainsKey("Low Blood Sugar"))
        Assert.False(cached.ContainsKey("unrelated"))
        Assert.Equal("C0020615", cached("low blood sugar").ConceptCode)
        Assert.Equal("Disease or Syndrome", cached("low blood sugar").SemanticType)

        ' The concept is now anti-joined out of selection (resolved + cached)...
        Assert.Empty(Await _fixture.Gateway.SelectConceptsToNormalizeAsync(10, includeAttempted:=False, CancellationToken.None))
        ' ...unless --force (includeAttempted) re-includes it. It only reappears if a
        ' row still carries it; after the UPDATE none do, so force is also empty here —
        ' instead verify force re-includes an unresolved-but-attempted concept.
        Await _fixture.Gateway.PersistTrialAsync("NCT00000013",
                {MakeUnresolvedWithCriterion("NCT00000013", "Inclusion", "deprived of liberty")},
                CancellationToken.None)
        Dim before = Await _fixture.Gateway.SelectConceptsToNormalizeAsync(10, includeAttempted:=False, CancellationToken.None)
        Assert.Single(before)
        ' Record it as NOT a concept (NONE) -> unresolved, 0 rows updated, still in eligibility.
        Dim none = Await _fixture.Gateway.RecordConceptNormalizationAsync(
                before(0).ConceptNorm, "NONE", UmlsMatch.Unresolved, "", CancellationToken.None)
        Assert.Equal(0, none)
        Assert.Empty(Await _fixture.Gateway.SelectConceptsToNormalizeAsync(10, includeAttempted:=False, CancellationToken.None))
        Assert.Single(Await _fixture.Gateway.SelectConceptsToNormalizeAsync(10, includeAttempted:=True, CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function CountConceptsToNormalize_counts_distinct_unresolved_residue() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Two trials share one unresolved concept (casing variants -> one distinct key);
        ' a third is already resolved and must NOT count.
        Await _fixture.Gateway.PersistTrialAsync("NCT00000010",
                {MakeUnresolvedWithCriterion("NCT00000010", "Inclusion", "low blood sugar")}, CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000011",
                {MakeUnresolvedWithCriterion("NCT00000011", "Inclusion", "Low Blood Sugar")}, CancellationToken.None)
        Await _fixture.Gateway.PersistTrialAsync("NCT00000012",
                {MakeResolvedWithCriterion("NCT00000012", "Inclusion", "diabetes")}, CancellationToken.None)

        Assert.Equal(1, Await _fixture.Gateway.CountConceptsToNormalizeAsync(includeAttempted:=False, CancellationToken.None))

        ' Once recorded (even as NONE), it is anti-joined out of the not-yet-attempted
        ' count, but --force (includeAttempted) still counts it.
        Dim key = (Await _fixture.Gateway.SelectConceptsToNormalizeAsync(10, False, CancellationToken.None))(0).ConceptNorm
        Await _fixture.Gateway.RecordConceptNormalizationAsync(key, "NONE", UmlsMatch.Unresolved, "", CancellationToken.None)
        Assert.Equal(0, Await _fixture.Gateway.CountConceptsToNormalizeAsync(includeAttempted:=False, CancellationToken.None))
        Assert.Equal(1, Await _fixture.Gateway.CountConceptsToNormalizeAsync(includeAttempted:=True, CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function CountStudiesToEmbed_counts_studies_missing_an_embedding_for_the_model() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' NCT01-03: detail + eligibility; NCT04: detail only (no eligibility -> ignored,
        ' the embed query requires eligibility rows to exist first).
        Await _fixture.InsertStudyDetailAsync("NCT00000001")
        Await _fixture.InsertStudyDetailAsync("NCT00000002")
        Await _fixture.InsertStudyDetailAsync("NCT00000003")
        Await _fixture.InsertStudyDetailAsync("NCT00000004")
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "diabetes")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "hypertension")
        Await _fixture.InsertEligibilityRowAsync("NCT00000003", "Inclusion", "asthma")

        Dim embedding(1023) As Single
        embedding(0) = 1.0F
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync("NCT00000001", embedding, "test-model", "src", CancellationToken.None)

        ' For the embedded model NCT01 is covered -> 2 remain; for any other model
        ' none are covered -> all 3 with eligibility remain.
        Assert.Equal(2, Await _fixture.Gateway.CountStudiesToEmbedAsync("test-model", CancellationToken.None))
        Assert.Equal(3, Await _fixture.Gateway.CountStudiesToEmbedAsync("other-model", CancellationToken.None))
    End Function

    Private Async Function CountByConceptCodeAsync(conceptCode As String) As Task(Of Long)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM public.eligibility WHERE concept_code = @c"
                cmd.Parameters.AddWithValue("c", conceptCode)
                Return Convert.ToInt64(Await cmd.ExecuteScalarAsync())
            End Using
        End Using
    End Function

    Private Shared Function MakeResolvedWithCriterion(
            nctId As String, criterion As String, concept As String) As ResolvedRecord
        Dim criterionRecord = New CriterionRecord(
                nctId:=nctId,
                criterion:=criterion,
                domain:="Disease",
                concept:=concept,
                qualifier:="",
                timeWindow:="",
                originalText:="seed")
        Dim match = New UmlsMatch("C0000000", "Test Concept", "MSH", 0.75)
        Return New ResolvedRecord(criterionRecord, match, Array.Empty(Of String)())
    End Function

    Private Shared Function MakeUnresolvedWithCriterion(
            nctId As String, criterion As String, concept As String) As ResolvedRecord
        Dim criterionRecord = New CriterionRecord(
                nctId:=nctId,
                criterion:=criterion,
                domain:="Disease",
                concept:=concept,
                qualifier:="",
                timeWindow:="",
                originalText:="seed")
        Return New ResolvedRecord(criterionRecord, UmlsMatch.Unresolved, Array.Empty(Of String)())
    End Function

    Private Async Function SeedThreeTrials() As Task
        Await _fixture.InsertSourceTrialAsync("NCT00000001", New String("a"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000002", New String("b"c, 60))
        Await _fixture.InsertSourceTrialAsync("NCT00000003", New String("c"c, 60))
    End Function

    <SkippableFact>
    Public Async Function GetStudies_hideSuperseded_keeps_only_latest_attempt_per_NctId() As Task
        ' Seed two attempts on the same trial: an older parse_invalid_json
        ' failure, then a newer success. With HideSuperseded=true a filter for
        ' status=parse_invalid_json must return zero rows because the trial's
        ' LATEST attempt is the success, not the failure — this is the
        ' triage-cleanup behaviour the dashboard checkbox enables.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Const NctId As String = "NCT00000042"
        Dim oldStart = DateTimeOffset.UtcNow.AddMinutes(-5)
        Dim newStart = DateTimeOffset.UtcNow

        Dim oldRunId = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(oldRunId, NctId, oldStart, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=oldRunId, nctId:=NctId,
                startedAt:=oldStart, finishedAt:=oldStart.AddSeconds(2),
                status:=StudyExecution.StatusParseInvalidJson,
                llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=0, persistedRowCount:=0,
                errorMessage:="parse failed"), CancellationToken.None)

        Dim newRunId = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(newRunId, NctId, newStart, CancellationToken.None)
        Await _fixture.Gateway.FinishStudyAsync(New StudyExecution(
                runId:=newRunId, nctId:=NctId,
                startedAt:=newStart, finishedAt:=newStart.AddSeconds(2),
                status:=StudyExecution.StatusSuccess,
                llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=Nothing, llmCompletionTokens:=Nothing,
                parsedRecordCount:=3, persistedRowCount:=3,
                errorMessage:=""), CancellationToken.None)

        ' With HideSuperseded ON: the parse_invalid_json filter must return 0
        ' rows because the latest attempt for this NCT is now success.
        Dim hidden = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(status:=StudyExecution.StatusParseInvalidJson, hideSuperseded:=True),
                sortBy:=Nothing, page:=1, pageSize:=10, CancellationToken.None)
        Assert.Empty(hidden.Rows)

        ' With HideSuperseded OFF: the parse_invalid_json filter still finds the
        ' older failed row (audit history preserved).
        Dim visible = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(status:=StudyExecution.StatusParseInvalidJson, hideSuperseded:=False),
                sortBy:=Nothing, page:=1, pageSize:=10, CancellationToken.None)
        Dim row = Assert.Single(visible.Rows)
        Assert.Equal(oldRunId, row.RunId)

        ' Unfiltered + HideSuperseded ON: only one row (the success), and the
        ' total-row count is the trial count, not the attempt count.
        Dim latestOnly = Await _fixture.Gateway.GetStudiesAsync(
                New StudyFilter(hideSuperseded:=True),
                sortBy:=Nothing, page:=1, pageSize:=10, CancellationToken.None)
        Dim onlyRow = Assert.Single(latestOnly.Rows)
        Assert.Equal(newRunId, onlyRow.RunId)
        Assert.Equal(1L, latestOnly.TotalRows)
    End Function

    <SkippableFact>
    Public Async Function DeleteStudy_removes_only_the_matching_composite_key() As Task
        ' Insert two attempts on the same trial. DeleteStudy with one of the
        ' run_ids must remove that row and leave the other untouched.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Const NctId As String = "NCT00000099"
        Dim runA = Guid.NewGuid()
        Dim runB = Guid.NewGuid()
        Await _fixture.Gateway.StartStudyAsync(
                runA, NctId, DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None)
        Await _fixture.Gateway.StartStudyAsync(
                runB, NctId, DateTimeOffset.UtcNow, CancellationToken.None)

        Dim deleted = Await _fixture.Gateway.DeleteStudyAsync(runA, NctId, CancellationToken.None)
        Assert.Equal(1, deleted)

        Dim remaining = Await _fixture.Gateway.GetStudyHistoryAsync(NctId, CancellationToken.None)
        Dim row = Assert.Single(remaining)
        Assert.Equal(runB, row.RunId)
    End Function

    <SkippableFact>
    Public Async Function DeleteStudy_returns_zero_when_no_row_matches() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim deleted = Await _fixture.Gateway.DeleteStudyAsync(
                Guid.NewGuid(), "NCT00000000", CancellationToken.None)
        Assert.Equal(0, deleted)
    End Function

    ' ============ Study snapshot (eligibility_study_detail, V5) ============

    <SkippableFact>
    Public Async Function EnsureSchema_creates_eligibility_study_detail_via_V5() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT COUNT(*) FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_name = 'eligibility_study_detail'"
                Assert.Equal(1, Convert.ToInt32(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function GetStudySnapshot_returns_nothing_when_not_captured() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim snapshot = Await _fixture.Gateway.GetStudySnapshotAsync(
                "NCT00000010", CancellationToken.None)
        Assert.Null(snapshot)
    End Function

    <SkippableFact>
    Public Async Function CaptureStudySnapshot_round_trips_study_metadata_and_eligibility_detail() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Const Nct As String = "NCT00000020"
        Await _fixture.InsertSourceStudyAsync(
                Nct,
                briefTitle:="Diabetes trial",
                officialTitle:="A Phase 3 Study Of Diabetes",
                overallStatus:="Completed",
                phase:="Phase 3",
                studyType:="Interventional",
                source:="ACME Sponsor",
                enrollment:=120,
                briefSummary:="A study summary.")
        ' Columns InsertSourceStudyAsync does not cover — set them directly so
        ' the date / enrollment_type / why_stopped round-trip is exercised.
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    UPDATE ctgov.studies
                       SET start_date = DATE '2020-01-15',
                           enrollment_type = 'Actual',
                           why_stopped = 'Lost funding'
                     WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", Nct)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
        Await _fixture.InsertSourceConditionAsync(Nct, "Hypertension")
        Await _fixture.InsertSourceConditionAsync(Nct, "Diabetes Mellitus")
        Await _fixture.InsertSourceInterventionAsync(Nct, "Drug", "Metformin")
        Await _fixture.InsertSourceInterventionAsync(Nct, "Device", "Insulin Pump")
        Await _fixture.InsertSourceEligibilityFullAsync(
                Nct,
                criteria:="Inclusion Criteria: adult with diabetes",
                gender:="All",
                minimumAge:="18 Years",
                maximumAge:="65 Years",
                healthyVolunteers:="No",
                adult:=True,
                child:=False,
                olderAdult:=True)

        Await _fixture.Gateway.CaptureStudySnapshotAsync(Nct, CancellationToken.None)
        Dim snapshot = Await _fixture.Gateway.GetStudySnapshotAsync(Nct, CancellationToken.None)

        Assert.NotNull(snapshot)
        Assert.Equal(Nct, snapshot.NctId)
        Assert.NotEqual(DateTimeOffset.MinValue, snapshot.CapturedAt)

        Dim d = snapshot.Details
        Assert.Equal("Diabetes trial", d.BriefTitle)
        Assert.Equal("A Phase 3 Study Of Diabetes", d.OfficialTitle)
        Assert.Equal("Completed", d.OverallStatus)
        Assert.Equal("Phase 3", d.Phase)
        Assert.Equal("Interventional", d.StudyType)
        Assert.Equal("ACME Sponsor", d.Source)
        Assert.Equal(120, d.Enrollment)
        Assert.Equal("Actual", d.EnrollmentType)
        Assert.Equal("Lost funding", d.WhyStopped)
        Assert.Equal("A study summary.", d.BriefSummary)
        Assert.Equal(New Date(2020, 1, 15), d.StartDate.Value.Date)

        ' Conditions stored as text[], ordered by name.
        Assert.Equal(2, d.Conditions.Count)
        Assert.Equal("Diabetes Mellitus", d.Conditions(0))
        Assert.Equal("Hypertension", d.Conditions(1))

        ' Interventions stored as jsonb, ordered by (type, name).
        Assert.Equal(2, d.Interventions.Count)
        Assert.Equal("Device", d.Interventions(0).InterventionType)
        Assert.Equal("Insulin Pump", d.Interventions(0).Name)
        Assert.Equal("Drug", d.Interventions(1).InterventionType)
        Assert.Equal("Metformin", d.Interventions(1).Name)

        Dim e = snapshot.Eligibility
        Assert.Equal("Inclusion Criteria: adult with diabetes", e.Criteria)
        Assert.Equal("All", e.Gender)
        Assert.Equal("18 Years", e.MinimumAge)
        Assert.Equal("65 Years", e.MaximumAge)
        Assert.Equal("No", e.HealthyVolunteers)
        Assert.True(e.Adult)
        Assert.False(e.Child)
        Assert.True(e.OlderAdult)
    End Function

    <SkippableFact>
    Public Async Function CaptureStudySnapshot_refreshes_existing_row_on_recapture() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Const Nct As String = "NCT00000030"
        Await _fixture.InsertSourceStudyAsync(Nct, briefTitle:="Refresh trial")
        Await _fixture.InsertSourceEligibilityFullAsync(Nct, criteria:="First version")

        Await _fixture.Gateway.CaptureStudySnapshotAsync(Nct, CancellationToken.None)
        Dim first = Await _fixture.Gateway.GetStudySnapshotAsync(Nct, CancellationToken.None)
        Assert.Equal("First version", first.Eligibility.Criteria)

        ' AACT eligibility changes; re-capturing must overwrite, not duplicate.
        Await _fixture.InsertSourceEligibilityFullAsync(Nct, criteria:="Second version")
        Await _fixture.Gateway.CaptureStudySnapshotAsync(Nct, CancellationToken.None)
        Dim second = Await _fixture.Gateway.GetStudySnapshotAsync(Nct, CancellationToken.None)

        Assert.Equal("Second version", second.Eligibility.Criteria)
        Assert.True(second.CapturedAt >= first.CapturedAt)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM public.eligibility_study_detail WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", Nct)
                Assert.Equal(1, Convert.ToInt32(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function CaptureStudySnapshot_is_noop_when_trial_absent_from_AACT() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.CaptureStudySnapshotAsync("NCT00009999", CancellationToken.None)

        Dim snapshot = Await _fixture.Gateway.GetStudySnapshotAsync(
                "NCT00009999", CancellationToken.None)
        Assert.Null(snapshot)
    End Function

    ' ============ SearchStudyDetailsAsync (Analysis-tab Search modal, V5) ============

    <SkippableFact>
    Public Async Function SearchStudyDetails_returns_empty_for_empty_filter() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync("NCT10000001", briefTitle:="A study")

        ' Empty filter must NOT dump the whole table — the modal would never
        ' have a reason to ask for an unconditional listing.
        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(), limit:=100, cancellationToken:=CancellationToken.None)
        Assert.Empty(results)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_matches_nct_id_substring_case_insensitively() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync("NCT10000001")
        Await _fixture.InsertStudyDetailFullAsync("NCT10000002")
        Await _fixture.InsertStudyDetailFullAsync("NCT99999999")

        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(nctId:="nct1000"), 100, CancellationToken.None)

        Assert.Equal(2, results.Count)
        Assert.Equal("NCT10000001", results(0).NctId)
        Assert.Equal("NCT10000002", results(1).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_matches_brief_title_substring() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync("NCT00000001", briefTitle:="Diabetes outcomes trial")
        Await _fixture.InsertStudyDetailFullAsync("NCT00000002", briefTitle:="Cardiology trial")

        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(briefTitle:="DIABETES"), 100, CancellationToken.None)

        Assert.Single(results)
        Assert.Equal("NCT00000001", results(0).NctId)
        Assert.Equal("Diabetes outcomes trial", results(0).BriefTitle)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_matches_phase_and_overall_status_with_contains() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync("NCT00000001", phase:="Phase 3", overallStatus:="Completed")
        Await _fixture.InsertStudyDetailFullAsync("NCT00000002", phase:="Phase 2", overallStatus:="Completed")
        Await _fixture.InsertStudyDetailFullAsync("NCT00000003", phase:="Phase 3", overallStatus:="Recruiting")

        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(phase:="phase 3", overallStatus:="complete"),
                100, CancellationToken.None)

        Assert.Single(results)
        Assert.Equal("NCT00000001", results(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_matches_any_condition_in_array() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync(
                "NCT00000001",
                conditions:={"Type 2 Diabetes Mellitus", "Hypertension"})
        Await _fixture.InsertStudyDetailFullAsync(
                "NCT00000002",
                conditions:={"Asthma"})

        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(condition:="diabetes"), 100, CancellationToken.None)

        Assert.Single(results)
        Assert.Equal("NCT00000001", results(0).NctId)
        Assert.Contains("Type 2 Diabetes Mellitus", results(0).Conditions)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_combines_filters_with_AND() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync(
                "NCT00000001", briefTitle:="Diabetes A", source:="ACME Sponsor")
        Await _fixture.InsertStudyDetailFullAsync(
                "NCT00000002", briefTitle:="Diabetes B", source:="Other Sponsor")
        Await _fixture.InsertStudyDetailFullAsync(
                "NCT00000003", briefTitle:="Hypertension", source:="ACME Sponsor")

        ' brief_title=diabetes AND source=acme → only NCT00000001 qualifies.
        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(briefTitle:="diabetes", source:="acme"),
                100, CancellationToken.None)

        Assert.Single(results)
        Assert.Equal("NCT00000001", results(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_caps_at_supplied_limit() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        For i = 1 To 5
            Await _fixture.InsertStudyDetailFullAsync(
                    $"NCT0000010{i}", briefTitle:="Diabetes trial " & i.ToString())
        Next

        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(briefTitle:="diabetes"),
                limit:=3, cancellationToken:=CancellationToken.None)

        Assert.Equal(3, results.Count)
        ' Ordered by nct_id ascending — first three of the seeded set.
        Assert.Equal("NCT00000101", results(0).NctId)
        Assert.Equal("NCT00000102", results(1).NctId)
        Assert.Equal("NCT00000103", results(2).NctId)
    End Function

    <SkippableFact>
    Public Async Function SearchStudyDetails_returns_empty_when_no_match() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertStudyDetailFullAsync("NCT00000001", briefTitle:="Diabetes")

        Dim results = Await _fixture.Gateway.SearchStudyDetailsAsync(
                New StudySearchFilter(briefTitle:="cardiology"), 100, CancellationToken.None)

        Assert.Empty(results)
    End Function

    ' ============ Audit log (InsertAuditAsync entity_id index safety) ============

    <SkippableFact>
    Public Async Function InsertAudit_truncates_oversized_entity_id_so_btree_index_does_not_overflow() As Task
        ' Regression: a "Rerun selection" of ~500 trials wrote the full comma-joined
        ' NCT-ID list into audit_log.entity_id, which is part of ix_audit_log_entity.
        ' The value (~5.5 KB) exceeded the btree row-size limit (~2704 bytes) and the
        ' INSERT aborted with "index row size NNNN exceeds btree version 4 maximum
        ' 2704". The gateway now caps entity_id to MaxIndexedTextLength so the write
        ' always succeeds; the full list is meant to live in the unindexed detail.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' 600 NCT-like ids (~7.8 KB) - comfortably past the index limit.
        Dim nctIds = Enumerable.Range(1, 600).Select(Function(i) $"NCT{i:D8}").ToArray()
        Dim hugeEntityId = String.Join(",", nctIds)
        Assert.True(hugeEntityId.Length > 2704, "test fixture must exceed the btree limit")

        Dim entry = New AuditEntry With {
                .OccurredAt = DateTimeOffset.UtcNow,
                .UserId = Nothing,
                .UserLabel = "tester@example.com",
                .Action = "update",
                .EntityType = "eligibility_study",
                .EntityId = hugeEntityId,
                .Detail = $"rerun batch ({nctIds.Length}): {hugeEntityId}"}

        ' Must not throw (previously surfaced as a PostgresException 54000).
        Await _fixture.Gateway.InsertAuditAsync(entry, CancellationToken.None)

        Dim page = Await _fixture.Gateway.GetAuditLogAsync(
                New AuditLogFilter With {.Action = "update"}, page:=1, pageSize:=10, CancellationToken.None)
        Assert.Equal(1L, page.TotalRows)
        Dim row = page.Rows(0)
        ' entity_id stored but truncated to the index-safe cap.
        Assert.Equal(2000, row.EntityId.Length)
        Assert.StartsWith("NCT00000001,", row.EntityId)
        ' detail is unindexed text, so the full list survives there intact.
        Assert.Contains(hugeEntityId, row.Detail)
    End Function

    <SkippableFact>
    Public Async Function InsertAudit_preserves_short_entity_id_unchanged() As Task
        ' The common case (a run id, a single NCT) is well under the cap and must
        ' round-trip byte-for-byte.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid().ToString()
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = DateTimeOffset.UtcNow,
                .UserLabel = "tester@example.com",
                .Action = "update",
                .EntityType = "eligibility_study",
                .EntityId = runId,
                .Detail = "rerun batch (3) run " & runId}, CancellationToken.None)

        Dim page = Await _fixture.Gateway.GetAuditLogAsync(
                New AuditLogFilter With {.Action = "update"}, page:=1, pageSize:=10, CancellationToken.None)
        Assert.Equal(1L, page.TotalRows)
        Assert.Equal(runId, page.Rows(0).EntityId)
    End Function

    Private Shared Function MakeResolved(
            nctId As String,
            concept As String,
            Optional unresolved As Boolean = False) As ResolvedRecord
        Dim criterion = New CriterionRecord(
                nctId:=nctId,
                criterion:="Inclusion",
                domain:="Disease",
                concept:=concept,
                qualifier:="",
                timeWindow:="",
                originalText:="some text")
        Dim match As UmlsMatch
        If unresolved Then
            match = UmlsMatch.Unresolved
        Else
            match = New UmlsMatch("C0000000", "Test Concept", "MSH", 0.75)
        End If
        Return New ResolvedRecord(criterion, match, Array.Empty(Of String)())
    End Function

End Class

Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Xunit

' Integration tests for the analytics reads (V25 indexes + AnalyticsGateway).
Public Class AnalyticsGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function V25_creates_both_analytics_indexes() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim indexes As New List(Of String)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT indexname FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'eligibility'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        indexes.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using

        Assert.Contains("ix_eligibility_concept_nct", indexes)
        Assert.Contains("ix_eligibility_nct_concept", indexes)
    End Function

    Private Async Function SeedRowAsync(nctId As String, criterion As String,
                                        conceptCode As String, conceptText As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.eligibility (nct_id, criterion, domain, concept, concept_code,
                                semantic_type, qualifier, time_window, original_text,
                                umls_name, match_score, match_source)
VALUES (@n, @cr, '', @ct, @cc, '', '', '', '', '', 0, '')"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("cr", criterion)
                cmd.Parameters.AddWithValue("ct", conceptText)
                cmd.Parameters.AddWithValue("cc", conceptCode)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedConceptAsync(cui As String, prefName As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.concept (cui, pref_name, root_source) VALUES (@c, @p, 'SNOMEDCT_US')
ON CONFLICT (cui) DO UPDATE SET pref_name = excluded.pref_name"
                cmd.Parameters.AddWithValue("c", cui)
                cmd.Parameters.AddWithValue("p", prefName)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedAncestorAsync(descendant As String, ancestor As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.concept_ancestor (descendant_cui, ancestor_cui, min_distance)
VALUES (@d, @a, 1) ON CONFLICT DO NOTHING"
                cmd.Parameters.AddWithValue("d", descendant)
                cmd.Parameters.AddWithValue("a", ancestor)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedSemanticTypeAsync(cui As String, tui As String, sty As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.semantic_type (cui, tui, sty) VALUES (@c, @t, @s)
ON CONFLICT (cui, tui) DO NOTHING"
                cmd.Parameters.AddWithValue("c", cui)
                cmd.Parameters.AddWithValue("t", tui)
                cmd.Parameters.AddWithValue("s", sty)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Seeds a public.eligibility_study_detail row carrying only nct_id and
    ''' phase - used by the concept-summary phase-breakdown tests, which need
    ''' rows joined on nct_id but do not care about the rest of the snapshot.
    ''' </summary>
    Private Async Function SeedStudyDetailPhaseAsync(nctId As String, phase As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "INSERT INTO public.eligibility_study_detail (nct_id, phase) VALUES (@n, @p)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("p", phase)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Seeds a public.condition_concept dictionary row. conditionNorm must
    ''' already be the NORMALIZED form (lower-cased, trimmed, internal
    ''' whitespace collapsed) - the join under test is what does that
    ''' normalization against eligibility_study_detail.conditions, so the
    ''' dictionary side must be pre-normalized exactly like the real backfill
    ''' produces it.
    ''' </summary>
    Private Async Function SeedConditionConceptAsync(conditionNorm As String, conceptCode As String) As Task
        ' raw_form deliberately differs from condition_norm (capitalized, not
        ' lower-cased) - the "most frequent ORIGINAL casing" per the V24
        ' migration comment. Keeping it distinct from condition_norm matters
        ' for test fidelity: if the join key were wrongly cc.raw_form instead
        ' of cc.condition_norm, a raw_form that happened to equal
        ' condition_norm would mask the bug.
        Dim rawForm = If(conditionNorm.Length > 0,
                Char.ToUpperInvariant(conditionNorm(0)) & conditionNorm.Substring(1),
                conditionNorm)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.condition_concept (condition_norm, raw_form, concept_code, match_tier)
VALUES (@cn, @rf, @cc, 'exact')
ON CONFLICT (condition_norm) DO UPDATE SET concept_code = excluded.concept_code"
                cmd.Parameters.AddWithValue("cn", conditionNorm)
                cmd.Parameters.AddWithValue("rf", rawForm)
                cmd.Parameters.AddWithValue("cc", conceptCode)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Seeds a minimal public.eligibility_study_detail row carrying only
    ''' nct_id and start_date (day/month pinned to June 1st - only the year
    ''' matters to GetTrendAsync's EXTRACT(year ...) grouping). Used by the
    ''' trend tests, which need multiple distinct-year rows that the existing
    ''' condition/full-detail helpers do not populate.
    ''' </summary>
    Private Async Function SeedStudyDetailStartDateAsync(nctId As String, year As Integer) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "INSERT INTO public.eligibility_study_detail (nct_id, start_date) VALUES (@n, @sd)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.Add(New NpgsqlParameter("sd", NpgsqlTypes.NpgsqlDbType.Date) With {.Value = New Date(year, 6, 1)})
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedStudyDetailConditionsAsync(nctId As String, conditions As String()) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.eligibility_study_detail (nct_id, conditions) VALUES (@n, @c)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("c", conditions)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function Condition_cohort_resolves_trials_via_condition_concept_join() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedStudyDetailConditionsAsync("NCT001", {"breast cancer"})
        Await SeedConditionConceptAsync("breast cancer", "C_BREAST")
        ' The Condition cohort's denominator must live in the same universe as
        ' its numerator (public.eligibility) - see CohortSql's
        ' RequireResolvedEligibilitySql comment - so this trial needs a
        ' resolved eligibility row to be counted at all.
        Await SeedRowAsync("NCT001", "Inclusion", "C_ANY", "any")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim size = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Condition, "C_BREAST", False), CancellationToken.None)

        ' A wrong LATERAL alias or a join key that isn't the normalized string
        ' (e.g. cc.raw_form instead of cc.condition_norm) would compile, run,
        ' and silently return 0 here.
        Assert.Equal(1, size)
    End Function

    <SkippableFact>
    Public Async Function Condition_cohort_normalizes_casing_and_whitespace_on_the_join() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Raw AACT text is messy - mixed case, doubled internal spaces, leading/
        ' trailing whitespace. The dictionary side holds the normalized form.
        Await SeedStudyDetailConditionsAsync("NCT001", {"  Breast   Cancer  "})
        Await SeedConditionConceptAsync("breast cancer", "C_BREAST")
        Await SeedRowAsync("NCT001", "Inclusion", "C_ANY", "any")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim size = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Condition, "C_BREAST", False), CancellationToken.None)

        Assert.Equal(1, size)
    End Function

    <SkippableFact>
    Public Async Function Condition_cohort_matches_any_condition_and_counts_trial_once() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Both conditions on the same trial resolve to the same concept - the
        ' LATERAL unnest produces two matching rows for NCT001, and only
        ' SELECT DISTINCT keeps the cohort size at 1 instead of 2.
        Await SeedStudyDetailConditionsAsync("NCT001", {"breast cancer", "stage iv breast cancer"})
        Await SeedConditionConceptAsync("breast cancer", "C_BREAST")
        Await SeedConditionConceptAsync("stage iv breast cancer", "C_BREAST")
        Await SeedRowAsync("NCT001", "Inclusion", "C_ANY", "any")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim size = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Condition, "C_BREAST", False), CancellationToken.None)

        Assert.Equal(1, size)
    End Function

    <SkippableFact>
    Public Async Function Condition_cohort_includes_descendants_when_asked() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAncestorAsync("C_CHILD", "C_PARENT")
        Await SeedStudyDetailConditionsAsync("NCT001", {"child condition"})
        Await SeedConditionConceptAsync("child condition", "C_CHILD")
        Await SeedRowAsync("NCT001", "Inclusion", "C_ANY", "any")
        Await SeedStudyDetailConditionsAsync("NCT002", {"unrelated condition"})
        Await SeedConditionConceptAsync("unrelated condition", "C_OTHER")
        Await SeedRowAsync("NCT002", "Inclusion", "C_ANY", "any")

        Dim g As New AnalyticsGateway(_fixture.DataSource)

        Dim withKids = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Condition, "C_PARENT", True), CancellationToken.None)
        Assert.Equal(1, withKids)

        Dim withoutKids = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Condition, "C_PARENT", False), CancellationToken.None)
        Assert.Equal(0, withoutKids)
    End Function

    ''' <summary>
    ''' Fix 1 (2026-07 whole-branch review): public.eligibility_study_detail is
    ''' written by the pipeline BEFORE the LLM call, so it also holds trials
    ''' whose LLM call later failed or resolved zero concepts - measured,
    ''' 2,533 of 316,558 rows (0.8%) have no public.eligibility rows at all.
    ''' Without the RequireResolvedEligibilitySql guard in CohortSql, a Phase
    ''' cohort's size would include such a trial even though the numerator
    ''' (GetCohortProfileAsync, which joins on public.eligibility) never can -
    ''' silently deflating pct_cohort and excess_pp for Phase/Year/Condition
    ''' cohorts relative to Concept cohorts, whose denominator already comes
    ''' from public.eligibility itself.
    '''
    ''' Verified to FAIL when the "AND EXISTS (...)" guard was temporarily
    ''' removed from CohortSql's Phase branch (cohort size came back 1, not
    ''' 0), then verified to PASS again once the guard was restored.
    ''' </summary>
    <SkippableFact>
    Public Async Function Phase_cohort_excludes_a_trial_with_no_resolved_eligibility_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' NCT_NO_ELIG has a phase in the pre-LLM snapshot but never produced a
        ' single public.eligibility row - exactly the "LLM call failed or
        ' resolved zero concepts" case the fix targets.
        Await SeedStudyDetailPhaseAsync("NCT_NO_ELIG", "PHASE3")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim size = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Phase, "PHASE3", False), CancellationToken.None)

        Assert.Equal(0, size)
    End Function

    <SkippableFact>
    Public Async Function Condition_defining_codes_cover_the_concept_and_its_descendants() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAncestorAsync("C_CHILD", "C_PARENT")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim codes = Await g.GetCohortDefiningCodesAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Condition, "C_PARENT", True), CancellationToken.None)

        Assert.Contains("C_PARENT", codes)
        Assert.Contains("C_CHILD", codes)
    End Function

    <SkippableFact>
    Public Async Function Concept_cohort_includes_descendants_when_asked() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_PARENT", "Parent")
        Await SeedConceptAsync("C_CHILD", "Child")
        Await SeedConceptAsync("C_OTHER", "Other")
        Await SeedAncestorAsync("C_CHILD", "C_PARENT")
        Await SeedRowAsync("NCT001", "Inclusion", "C_PARENT", "parent")
        Await SeedRowAsync("NCT002", "Inclusion", "C_CHILD", "child")
        Await SeedRowAsync("NCT003", "Inclusion", "C_OTHER", "other")

        Dim g As New AnalyticsGateway(_fixture.DataSource)

        Dim withKids = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_PARENT", True), CancellationToken.None)
        Assert.Equal(2, withKids)

        Dim withoutKids = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_PARENT", False), CancellationToken.None)
        Assert.Equal(1, withoutKids)
    End Function

    <SkippableFact>
    Public Async Function Cohort_profile_counts_distinct_trials_not_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_A", "A")
        ' Same concept twice in ONE trial must count once.
        Await SeedRowAsync("NCT001", "Inclusion", "C_A", "a")
        Await SeedRowAsync("NCT001", "Exclusion", "C_A", "a again")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim prof = Await g.GetCohortProfileAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_A", False), CancellationToken.None)

        Assert.Equal(1, prof.Single(Function(p) p.ConceptCode = "C_A").Trials)
    End Function

    <SkippableFact>
    Public Async Function Defining_codes_cover_the_concept_and_its_descendants() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAncestorAsync("C_CHILD", "C_PARENT")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim codes = Await g.GetCohortDefiningCodesAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_PARENT", True), CancellationToken.None)

        Assert.Contains("C_PARENT", codes)
        Assert.Contains("C_CHILD", codes)

        ' Phase cohorts have no defining concepts - nothing is tautological.
        Dim none = Await g.GetCohortDefiningCodesAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Phase, "PHASE3", False), CancellationToken.None)
        Assert.Empty(none)
    End Function

    <SkippableFact>
    Public Async Function All_four_cohort_kinds_return_the_same_shape() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_A", "A")
        Await SeedRowAsync("NCT001", "Inclusion", "C_A", "a")
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.eligibility_study_detail (nct_id, phase, start_date, conditions)
VALUES ('NCT001', 'PHASE3', DATE '2023-05-01', ARRAY['Thing'])"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim kinds = {
            New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_A", False),
            New AnalyticsCohort(AnalyticsCohortKind.Phase, "PHASE3", False),
            New AnalyticsCohort(AnalyticsCohortKind.Year, "2023", False)
        }

        ' The view renders all four uniformly, so every kind must return a
        ' well-formed profile - never Nothing, never a throw.
        For Each k In kinds
            Dim size = Await g.GetCohortSizeAsync(k, CancellationToken.None)
            Dim prof = Await g.GetCohortProfileAsync(k, CancellationToken.None)
            Assert.True(size >= 0)
            Assert.NotNull(prof)
        Next
    End Function

    <SkippableFact>
    Public Async Function Unknown_cohort_value_returns_empty_not_an_error() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim prof = Await g.GetCohortProfileAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_NOPE", False), CancellationToken.None)

        Assert.Empty(prof)
        Assert.Equal(0, Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_NOPE", False), CancellationToken.None))
    End Function

    ' --- GetTrendAsync (Task 7) ---

    <SkippableFact>
    Public Async Function GetTrend_produces_one_point_per_year_with_correct_denominator_and_percentage() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_TREND", "Trend Concept")
        ' Four studies started in 2020; only two of them mention C_TREND.
        Await SeedStudyDetailStartDateAsync("NCT001", 2020)
        Await SeedStudyDetailStartDateAsync("NCT002", 2020)
        Await SeedStudyDetailStartDateAsync("NCT003", 2020)
        Await SeedStudyDetailStartDateAsync("NCT004", 2020)
        Await SeedRowAsync("NCT001", "Inclusion", "C_TREND", "trend")
        Await SeedRowAsync("NCT002", "Inclusion", "C_TREND", "trend")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        ' currentYear is far from 2020 so IsPartial cannot accidentally pass -
        ' the partial flag itself is covered by a dedicated test below.
        Dim points = Await g.GetTrendAsync("C_TREND", 2030, CancellationToken.None)

        Dim p = Assert.Single(points)
        Assert.Equal(2020, p.Year)
        Assert.Equal(4, p.StudiesThatYear)
        Assert.Equal(2, p.TrialsWithConcept)
        Assert.Equal(50.0, p.PctOfYear, 3)
        Assert.False(p.IsPartial)
    End Function

    ''' <summary>
    ''' The LEFT JOIN test. 2021 has three processed studies and none of them
    ''' mention C_TREND - an INNER JOIN between yr and hits would drop that
    ''' year's row entirely (hits has no 2021 row at all), so Single(Function(p)
    ''' p.Year = 2021) below would throw instead of finding a 0% point. This
    ''' test was verified to fail when LEFT JOIN was temporarily changed to
    ''' JOIN, then restored.
    ''' </summary>
    <SkippableFact>
    Public Async Function GetTrend_includes_a_year_where_the_concept_is_absent_with_zero_and_that_years_study_count() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_TREND", "Trend Concept")
        Await SeedStudyDetailStartDateAsync("NCT001", 2019)
        Await SeedRowAsync("NCT001", "Inclusion", "C_TREND", "trend")
        Await SeedStudyDetailStartDateAsync("NCT002", 2021)
        Await SeedStudyDetailStartDateAsync("NCT003", 2021)
        Await SeedStudyDetailStartDateAsync("NCT004", 2021)
        ' None of NCT002-004 mention C_TREND at all.

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim points = Await g.GetTrendAsync("C_TREND", 2030, CancellationToken.None)

        Assert.Equal(2, points.Count)

        Dim y2019 = points.Single(Function(p) p.Year = 2019)
        Assert.Equal(1, y2019.StudiesThatYear)
        Assert.Equal(1, y2019.TrialsWithConcept)
        Assert.Equal(100.0, y2019.PctOfYear, 3)

        Dim y2021 = points.Single(Function(p) p.Year = 2021)
        Assert.Equal(3, y2021.StudiesThatYear)
        Assert.Equal(0, y2021.TrialsWithConcept)
        Assert.Equal(0.0, y2021.PctOfYear, 3)
    End Function

    <SkippableFact>
    Public Async Function GetTrend_flags_only_the_supplied_current_year_as_partial() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_TREND", "Trend Concept")
        Await SeedStudyDetailStartDateAsync("NCT001", 2021)
        Await SeedStudyDetailStartDateAsync("NCT002", 2022)
        Await SeedStudyDetailStartDateAsync("NCT003", 2023)
        Await SeedRowAsync("NCT001", "Inclusion", "C_TREND", "trend")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        ' 2023 is passed in as currentYear - pinned, not read from the clock.
        Dim points = Await g.GetTrendAsync("C_TREND", 2023, CancellationToken.None)

        Assert.Equal(3, points.Count)
        Assert.True(points.Single(Function(p) p.Year = 2023).IsPartial)
        Assert.False(points.Single(Function(p) p.Year = 2021).IsPartial)
        Assert.False(points.Single(Function(p) p.Year = 2022).IsPartial)
    End Function

    ' --- GetConceptSummaryAsync / SearchConceptsAsync (Task 8) ---

    <SkippableFact>
    Public Async Function GetConceptSummary_returns_pref_name_semantic_types_and_counts_for_a_seeded_concept() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_SUM", "Type 2 Diabetes Mellitus")
        Await SeedSemanticTypeAsync("C_SUM", "T047", "Disease or Syndrome")
        ' Asymmetric counts on purpose: two ancestors (C_SUM is the
        ' descendant in both rows) and one descendant (C_SUM is the
        ' ancestor) - if AncestorCount/DescendantCount were ever swapped in
        ' production, these two distinct values (2 vs 1) would catch it,
        ' whereas equal counts would not.
        Await SeedAncestorAsync("C_SUM", "C_BROADER")
        Await SeedAncestorAsync("C_SUM", "C_BROADER2")
        Await SeedAncestorAsync("C_NARROWER", "C_SUM")
        Await SeedRowAsync("NCT001", "Inclusion", "C_SUM", "diabetes")
        Await SeedRowAsync("NCT002", "Inclusion", "C_SUM", "type 2 diabetes")
        ' Same trial twice must still count once towards Trials.
        Await SeedRowAsync("NCT002", "Exclusion", "C_SUM", "diabetes again")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim summary = Await g.GetConceptSummaryAsync("C_SUM", CancellationToken.None)

        Assert.NotNull(summary)
        Assert.Equal("C_SUM", summary.ConceptCode)
        Assert.Equal("Type 2 Diabetes Mellitus", summary.PrefName)
        Assert.Equal("SNOMEDCT_US", summary.RootSource)
        Assert.Equal("Disease or Syndrome", summary.SemanticTypes)
        Assert.Equal(2, summary.AncestorCount)
        Assert.Equal(1, summary.DescendantCount)
        Assert.Equal(2, summary.Trials)
    End Function

    <SkippableFact>
    Public Async Function GetConceptSummary_returns_nothing_for_an_unknown_cui() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim g As New AnalyticsGateway(_fixture.DataSource)

        ' A user can type anything into the URL - an unrecognised CUI must
        ' come back as a clean absence, never an exception.
        Dim summary = Await g.GetConceptSummaryAsync("C_DOES_NOT_EXIST", CancellationToken.None)

        Assert.Null(summary)
    End Function

    <SkippableFact>
    Public Async Function GetConceptSummary_caps_example_criteria_at_five() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_MANY", "Many Examples")
        For i = 1 To 7
            Await SeedRowAsync($"NCT{i:000}", $"Inclusion criterion number {i}", "C_MANY", "many")
        Next

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim summary = Await g.GetConceptSummaryAsync("C_MANY", CancellationToken.None)

        Assert.NotNull(summary)
        ' Seven distinct criterion texts exist for this CUI - the query's own
        ' LIMIT 5 must cap the result, not just happen to return few.
        Assert.Equal(5, summary.ExampleCriteria.Count)
    End Function

    <SkippableFact>
    Public Async Function GetConceptSummary_breaks_down_trials_by_phase_using_none_for_blank() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_PHASE", "Phased Concept")
        Await SeedStudyDetailPhaseAsync("NCT001", "PHASE3")
        Await SeedStudyDetailPhaseAsync("NCT002", "PHASE3")
        Await SeedStudyDetailPhaseAsync("NCT003", "")
        Await SeedRowAsync("NCT001", "Inclusion", "C_PHASE", "phase")
        Await SeedRowAsync("NCT002", "Inclusion", "C_PHASE", "phase")
        Await SeedRowAsync("NCT003", "Inclusion", "C_PHASE", "phase")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim summary = Await g.GetConceptSummaryAsync("C_PHASE", CancellationToken.None)

        Assert.NotNull(summary)
        Dim phase3 = summary.ByPhase.Single(Function(p) p.ConceptCode = "PHASE3")
        Assert.Equal(2, phase3.Trials)
        Dim none = summary.ByPhase.Single(Function(p) p.ConceptCode = "(none)")
        Assert.Equal(1, none.Trials)
    End Function

    <SkippableFact>
    Public Async Function SearchConcepts_matches_preferred_name_and_orders_by_trial_count_descending() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_DIAB_LOW", "Diabetes Insipidus")
        Await SeedConceptAsync("C_DIAB_HIGH", "Diabetes Mellitus")
        Await SeedConceptAsync("C_OTHER", "Hypertension")
        Await SeedRowAsync("NCT001", "Inclusion", "C_DIAB_LOW", "low")
        Await SeedRowAsync("NCT002", "Inclusion", "C_DIAB_HIGH", "high")
        Await SeedRowAsync("NCT003", "Inclusion", "C_DIAB_HIGH", "high")
        Await SeedRowAsync("NCT004", "Inclusion", "C_DIAB_HIGH", "high")
        Await SeedRowAsync("NCT005", "Inclusion", "C_OTHER", "other")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim results = Await g.SearchConceptsAsync("diabetes", 10, CancellationToken.None)

        Assert.Equal(2, results.Count)
        Assert.DoesNotContain(results, Function(r) r.ConceptCode = "C_OTHER")
        Assert.Equal("C_DIAB_HIGH", results(0).ConceptCode)
        Assert.Equal(3, results(0).Trials)
        Assert.Equal("C_DIAB_LOW", results(1).ConceptCode)
        Assert.Equal(1, results(1).Trials)
    End Function

    <SkippableFact>
    Public Async Function SearchConcepts_binds_a_term_containing_a_quote_as_a_parameter_rather_than_breaking_the_sql() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_APOS", "Alzheimer's Disease")

        Dim g As New AnalyticsGateway(_fixture.DataSource)

        ' If @term were concatenated into the SQL text instead of bound, this
        ' apostrophe would either break the query's syntax or open a SQL
        ' injection path - either way SearchConceptsAsync would throw or
        ' misbehave here. A properly bound parameter treats the apostrophe as
        ' ordinary data and the search just works.
        Dim results = Await g.SearchConceptsAsync("Alzheimer's", 10, CancellationToken.None)

        Dim found = Assert.Single(results)
        Assert.Equal("C_APOS", found.ConceptCode)
    End Function
End Class

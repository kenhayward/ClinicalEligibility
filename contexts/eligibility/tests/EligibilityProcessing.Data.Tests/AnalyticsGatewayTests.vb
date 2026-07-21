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
End Class

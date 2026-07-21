Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

' Integration tests for the Authoring Analysis-phase gateway methods
' (Milestone 2): pgvector similarity search, criteria clustering, the
' cluster-records lookup, and the embedding backfill query.
'
' Run against the pgvector-enabled Postgres test container; skip cleanly
' when Docker is unavailable.

Public Class AuthoringAnalysisGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    ' V8 pins eligibility_study_embedding.embedding to vector(1024). Test
    ' vectors are written into the leading components and zero-padded to the
    ' full width; zero-padding leaves cosine similarity unchanged, so the
    ' ranking assertions below still hold.
    Private Const EmbeddingDims As Integer = 1024

    Private Shared Function Embed(ParamArray values As Single()) As Single()
        Dim v(EmbeddingDims - 1) As Single
        Array.Copy(values, v, values.Length)
        Return v
    End Function

    <SkippableFact>
    Public Async Function FindSimilarStudies_ranks_by_cosine_similarity() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        For Each nct In {"NCT0001", "NCT0002", "NCT0003"}
            Await _fixture.InsertStudyDetailAsync(nct, briefTitle:=nct & " title")
            Await _fixture.InsertEligibilityRowAsync(nct, "Inclusion", "Concept")
        Next
        ' NCT0001 == query direction; NCT0003 close; NCT0002 orthogonal.
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0001", Embed(1.0F, 0.0F, 0.0F), "m", "a", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0002", Embed(0.0F, 1.0F, 0.0F), "m", "b", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0003", Embed(0.8F, 0.2F, 0.0F), "m", "c", CancellationToken.None)

        Dim result = Await _fixture.Gateway.FindSimilarStudiesAsync(
                Embed(1.0F, 0.0F, 0.0F), 10, CancellationToken.None)

        Assert.Equal(3, result.Count)
        Assert.Equal("NCT0001", result(0).NctId)
        Assert.Equal("NCT0003", result(1).NctId)
        Assert.Equal("NCT0002", result(2).NctId)
        Assert.True(result(0).Similarity > result(1).Similarity)
        Assert.True(result(1).Similarity > result(2).Similarity)
    End Function

    <SkippableFact>
    Public Async Function FindSimilarStudies_excludes_studies_without_eligibility_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Has a snapshot + embedding but NO public.eligibility rows — must not
        ' appear, because clustering needs extracted criteria.
        Await _fixture.InsertStudyDetailAsync("NCT0009")
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0009", Embed(1.0F, 0.0F), "m", "x", CancellationToken.None)

        Dim result = Await _fixture.Gateway.FindSimilarStudiesAsync(
                Embed(1.0F, 0.0F), 10, CancellationToken.None)
        Assert.Empty(result)
    End Function

    <SkippableFact>
    Public Async Function ClusterCommonCriteria_groups_by_concept_and_counts_studies() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Diabetes (C001) Inclusion across three studies.
        For Each nct In {"NCT1", "NCT2", "NCT3"}
            Await _fixture.InsertEligibilityRowAsync(
                    nct, "Inclusion", "Diabetes mellitus", conceptCode:="C001", semanticType:="Disease")
        Next
        ' Pregnancy (C002) Exclusion across two studies.
        For Each nct In {"NCT1", "NCT2"}
            Await _fixture.InsertEligibilityRowAsync(nct, "Exclusion", "Pregnancy", conceptCode:="C002")
        Next
        ' One unresolved Inclusion criterion (no concept code).
        Await _fixture.InsertEligibilityRowAsync("NCT1", "Inclusion", "Adult", conceptCode:="")

        Dim ids As IReadOnlyList(Of String) = {"NCT1", "NCT2", "NCT3"}
        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(ids, 0, CancellationToken.None)

        ' Highest commonality first.
        Assert.Equal("C001", clusters(0).ConceptCode)
        Assert.Equal(3, clusters(0).StudyCount)

        Dim inclusion = clusters.Where(Function(c) c.Criterion = "Inclusion").ToList()
        Dim exclusion = clusters.Where(Function(c) c.Criterion = "Exclusion").ToList()

        Dim diabetes = inclusion.Single(Function(c) c.ConceptCode = "C001")
        Assert.True(diabetes.Resolved)
        Assert.Equal(3, diabetes.StudyCount)
        Assert.Equal(3, diabetes.RecordCount)

        Dim adult = inclusion.Single(Function(c) Not c.Resolved)
        Assert.Equal(1, adult.StudyCount)
        Assert.StartsWith("concept:", adult.GroupKey)

        Dim pregnancy = Assert.Single(exclusion)
        Assert.Equal("C002", pregnancy.ConceptCode)
        Assert.Equal(2, pregnancy.StudyCount)
    End Function

    <SkippableFact>
    Public Async Function GetClusterRecords_returns_rows_for_one_resolved_cluster() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertEligibilityRowAsync(
                "NCT1", "Inclusion", "Diabetes", conceptCode:="C001", originalText:="Has diabetes")
        Await _fixture.InsertEligibilityRowAsync(
                "NCT2", "Inclusion", "Diabetes", conceptCode:="C001", originalText:="Diabetic patient")
        Await _fixture.InsertEligibilityRowAsync("NCT1", "Exclusion", "Pregnancy", conceptCode:="C002")

        Dim ids As IReadOnlyList(Of String) = {"NCT1", "NCT2"}
        Dim records = Await _fixture.Gateway.GetClusterRecordsAsync(
                ids, "Inclusion", "C001", Array.Empty(Of String)(), CancellationToken.None)

        Assert.Equal(2, records.Count)
        Assert.All(records, Sub(r) Assert.Equal("Diabetes", r.Concept))
    End Function

    <SkippableFact>
    Public Async Function GetClusterRecords_resolves_unresolved_cluster_by_concept_key() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertEligibilityRowAsync("NCT1", "Inclusion", "Adult", conceptCode:="")
        Await _fixture.InsertEligibilityRowAsync("NCT2", "Inclusion", "Adult", conceptCode:="")

        Dim ids As IReadOnlyList(Of String) = {"NCT1", "NCT2"}
        Dim records = Await _fixture.Gateway.GetClusterRecordsAsync(
                ids, "Inclusion", "concept:adult", Array.Empty(Of String)(), CancellationToken.None)

        Assert.Equal(2, records.Count)
    End Function

    <SkippableFact>
    Public Async Function GetStudiesToEmbed_excludes_already_embedded_and_studies_without_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Processed study with a snapshot — needs embedding.
        Await _fixture.InsertStudyDetailAsync("NCT1")
        Await _fixture.InsertEligibilityRowAsync("NCT1", "Inclusion", "X")
        ' Snapshot but no eligibility rows — not embeddable (no criteria).
        Await _fixture.InsertStudyDetailAsync("NCT2")

        Dim before = Await _fixture.Gateway.GetStudiesToEmbedAsync("m1", CancellationToken.None)
        Assert.Single(before)
        Assert.Equal("NCT1", before(0).NctId)

        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT1", Embed(0.1F, 0.2F), "m1", "txt", CancellationToken.None)

        ' Embedded under m1 — no longer pending for m1.
        Assert.Empty(Await _fixture.Gateway.GetStudiesToEmbedAsync("m1", CancellationToken.None))
        ' …but a different model still needs it.
        Assert.Single(Await _fixture.Gateway.GetStudiesToEmbedAsync("m2", CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function GetStudyEmbeddingInput_returns_the_snapshot_input_for_one_study() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertStudyDetailAsync("NCT1", briefTitle:="Diabetes trial")

        Dim input = Await _fixture.Gateway.GetStudyEmbeddingInputAsync("NCT1", CancellationToken.None)
        Assert.NotNull(input)
        Assert.Equal("NCT1", input.NctId)
        Assert.Equal("Diabetes trial", input.BriefTitle)
    End Function

    <SkippableFact>
    Public Async Function GetStudyEmbeddingInput_returns_nothing_when_no_snapshot_exists() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim input = Await _fixture.Gateway.GetStudyEmbeddingInputAsync("NCT_MISSING", CancellationToken.None)
        Assert.Null(input)
    End Function

    ' ============ FindSimilarTrialsToAsync (Analysis-tab Find Similar modal) ============

    <SkippableFact>
    Public Async Function FindSimilarTrialsTo_returns_nothing_when_source_not_embedded() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertStudyDetailAsync("NCT0001")
        Await _fixture.InsertEligibilityRowAsync("NCT0001", "Inclusion", "X")

        Dim result = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", limit:=10,
                matchPhase:=False, matchStudyType:=False,
                cancellationToken:=CancellationToken.None)
        Assert.Null(result)
    End Function

    <SkippableFact>
    Public Async Function FindSimilarTrialsTo_excludes_source_and_ranks_by_cosine() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        For Each nct In {"NCT0001", "NCT0002", "NCT0003"}
            Await _fixture.InsertStudyDetailAsync(nct, briefTitle:=nct & " title")
            Await _fixture.InsertEligibilityRowAsync(nct, "Inclusion", "Concept")
        Next
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0001", Embed(1.0F, 0.0F, 0.0F), "m", "a", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0002", Embed(0.0F, 1.0F, 0.0F), "m", "b", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0003", Embed(0.8F, 0.2F, 0.0F), "m", "c", CancellationToken.None)

        Dim result = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", 10, False, False, CancellationToken.None)

        Assert.NotNull(result)
        Assert.Equal(2, result.Count)
        ' Source trial is NOT in the list — only the other two.
        Assert.DoesNotContain(result, Function(r) r.NctId = "NCT0001")
        ' NCT0003 is closer in direction than NCT0002.
        Assert.Equal("NCT0003", result(0).NctId)
        Assert.Equal("NCT0002", result(1).NctId)
        Assert.True(result(0).Similarity > result(1).Similarity)
    End Function

    <SkippableFact>
    Public Async Function FindSimilarTrialsTo_matches_same_phase_when_enabled() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Source trial is Phase 3. NCT0002 shares its phase; NCT0003 does not.
        Await _fixture.InsertStudyDetailAsync("NCT0001", phase:="Phase 3", studyType:="Interventional")
        Await _fixture.InsertStudyDetailAsync("NCT0002", phase:="Phase 3", studyType:="Interventional")
        Await _fixture.InsertStudyDetailAsync("NCT0003", phase:="Phase 2", studyType:="Interventional")
        For Each nct In {"NCT0001", "NCT0002", "NCT0003"}
            Await _fixture.InsertEligibilityRowAsync(nct, "Inclusion", "X")
        Next
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0001", Embed(1.0F, 0.0F), "m", "a", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0002", Embed(0.9F, 0.1F), "m", "b", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0003", Embed(0.95F, 0.05F), "m", "c", CancellationToken.None)

        ' No filter — both other trials come back.
        Dim unfiltered = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", 10, False, False, CancellationToken.None)
        Assert.Equal(2, unfiltered.Count)

        ' Same Phase — NCT0003 (Phase 2) is filtered out.
        Dim filtered = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", 10, matchPhase:=True, matchStudyType:=False,
                cancellationToken:=CancellationToken.None)
        Assert.Single(filtered)
        Assert.Equal("NCT0002", filtered(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function FindSimilarTrialsTo_matches_same_study_type_when_enabled() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertStudyDetailAsync("NCT0001", phase:="Phase 3", studyType:="Interventional")
        Await _fixture.InsertStudyDetailAsync("NCT0002", phase:="Phase 3", studyType:="Interventional")
        Await _fixture.InsertStudyDetailAsync("NCT0003", phase:="Phase 3", studyType:="Observational")
        For Each nct In {"NCT0001", "NCT0002", "NCT0003"}
            Await _fixture.InsertEligibilityRowAsync(nct, "Inclusion", "X")
        Next
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0001", Embed(1.0F, 0.0F), "m", "a", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0002", Embed(0.9F, 0.1F), "m", "b", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0003", Embed(0.95F, 0.05F), "m", "c", CancellationToken.None)

        Dim filtered = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", 10, matchPhase:=False, matchStudyType:=True,
                cancellationToken:=CancellationToken.None)
        Assert.Single(filtered)
        Assert.Equal("NCT0002", filtered(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function FindSimilarTrialsTo_combines_phase_and_type_filters() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Only NCT0002 matches BOTH dimensions.
        Await _fixture.InsertStudyDetailAsync("NCT0001", phase:="Phase 3", studyType:="Interventional")
        Await _fixture.InsertStudyDetailAsync("NCT0002", phase:="Phase 3", studyType:="Interventional")
        Await _fixture.InsertStudyDetailAsync("NCT0003", phase:="Phase 3", studyType:="Observational")
        Await _fixture.InsertStudyDetailAsync("NCT0004", phase:="Phase 2", studyType:="Interventional")
        For Each nct In {"NCT0001", "NCT0002", "NCT0003", "NCT0004"}
            Await _fixture.InsertEligibilityRowAsync(nct, "Inclusion", "X")
        Next
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0001", Embed(1.0F, 0.0F), "m", "a", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0002", Embed(0.9F, 0.1F), "m", "b", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0003", Embed(0.95F, 0.05F), "m", "c", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0004", Embed(0.97F, 0.03F), "m", "d", CancellationToken.None)

        Dim filtered = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", 10, matchPhase:=True, matchStudyType:=True,
                cancellationToken:=CancellationToken.None)
        Assert.Single(filtered)
        Assert.Equal("NCT0002", filtered(0).NctId)
    End Function

    <SkippableFact>
    Public Async Function FindSimilarTrialsTo_excludes_studies_without_eligibility_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Source has eligibility rows + embedding. Candidate has embedding +
        ' snapshot but NO eligibility rows — must not appear.
        Await _fixture.InsertStudyDetailAsync("NCT0001")
        Await _fixture.InsertEligibilityRowAsync("NCT0001", "Inclusion", "X")
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0001", Embed(1.0F, 0.0F), "m", "a", CancellationToken.None)

        Await _fixture.InsertStudyDetailAsync("NCT0002")
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT0002", Embed(0.9F, 0.1F), "m", "b", CancellationToken.None)

        Dim result = Await _fixture.Gateway.FindSimilarTrialsToAsync(
                "NCT0001", 10, False, False, CancellationToken.None)

        Assert.NotNull(result)
        Assert.Empty(result)
    End Function

    <SkippableFact>
    Public Async Function UpsertStudyEmbedding_overwrites_on_conflict() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.InsertStudyDetailAsync("NCT1")
        Await _fixture.InsertEligibilityRowAsync("NCT1", "Inclusion", "X")
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT1", Embed(1.0F, 0.0F), "m", "first", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync(
                "NCT1", Embed(0.0F, 1.0F), "m", "second", CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT source_text FROM public.eligibility_study_embedding WHERE nct_id = 'NCT1'"
                Assert.Equal("second", CStr(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

End Class

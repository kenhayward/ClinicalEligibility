Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

' Integration tests for the Authoring-feature gateway methods (Milestone 1):
' CRUD over authoring_study / authoring_eligibility / authoring_criterion.
'
' Same Testcontainers-backed fixture + Skip-if-no-Docker discipline as
' PostgresGatewayIntegrationTests.

Public Class AuthoringGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    ' Acting user threaded through the attributed write methods. Attribution
    ' columns are not FK-checked, so an arbitrary id is fine for these tests.
    Private Shared ReadOnly TestUser As Guid = Guid.NewGuid()

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    Private Shared Function NewStudy(label As String) As AuthoringStudy
        Return New AuthoringStudy With {
                .AuthoringStudyId = Guid.NewGuid(),
                .Label = label,
                .SourceKind = "blank",
                .BriefTitle = "A trial of something",
                .Phase = "Phase 2",
                .StudyType = "Interventional",
                .Enrollment = 120,
                .StartDate = New Date(2026, 1, 15),
                .Conditions = New List(Of String) From {"Diabetes", "Hypertension"},
                .Interventions = New List(Of Intervention) From {New Intervention("Drug", "Metformin")}}
    End Function

    Private Shared Function NewSource(nctId As String) As AuthoringCriterionSource
        Return New AuthoringCriterionSource With {
                .EligibilityId = 1L,
                .NctId = nctId,
                .Criterion = "Inclusion",
                .Domain = "Demographics",
                .Concept = "Adult",
                .ConceptCode = "C0001675",
                .SemanticType = "Age Group",
                .Qualifier = "",
                .TimeWindow = "",
                .OriginalText = "adults",
                .MatchScore = 0.5D}
    End Function

    <SkippableFact>
    Public Async Function Create_then_get_round_trips_study_and_eligibility() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Round-trip study")
        Dim eligibility = New AuthoringEligibility With {
                .AuthoringStudyId = study.AuthoringStudyId,
                .Criteria = "Inclusion: adults",
                .Gender = "All",
                .MinimumAge = "18 Years",
                .Adult = True,
                .Child = False}

        Await _fixture.Gateway.CreateAuthoringStudyAsync(study, eligibility, TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.NotNull(loaded)
        Assert.Equal("Round-trip study", loaded.Study.Label)
        Assert.Equal("Phase 2", loaded.Study.Phase)
        Assert.Equal(120, loaded.Study.Enrollment)
        Assert.Equal(New Date(2026, 1, 15), loaded.Study.StartDate.Value)
        Assert.Equal(2, loaded.Study.Conditions.Count)
        Assert.Contains("Diabetes", loaded.Study.Conditions)
        Assert.Single(loaded.Study.Interventions)
        Assert.Equal("Metformin", loaded.Study.Interventions(0).Name)
        Assert.Equal("Inclusion: adults", loaded.Eligibility.Criteria)
        Assert.Equal("All", loaded.Eligibility.Gender)
        Assert.True(loaded.Eligibility.Adult.Value)
        Assert.False(loaded.Eligibility.Child.Value)
        Assert.Empty(loaded.Criteria)
    End Function

    <SkippableFact>
    Public Async Function GetAuthoringStudy_returns_nothing_for_unknown_id() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(Guid.NewGuid(), CancellationToken.None)
        Assert.Null(loaded)
    End Function

    <SkippableFact>
    Public Async Function Update_study_changes_characteristics() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Before")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        study.Label = "After"
        study.Phase = "Phase 3"
        study.Enrollment = 500
        Await _fixture.Gateway.UpdateAuthoringStudyAsync(study, TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal("After", loaded.Study.Label)
        Assert.Equal("Phase 3", loaded.Study.Phase)
        Assert.Equal(500, loaded.Study.Enrollment)
    End Function

    <SkippableFact>
    Public Async Function SaveEligibility_upserts_the_row() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Eligibility study")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Await _fixture.Gateway.SaveAuthoringEligibilityAsync(
                New AuthoringEligibility With {
                    .AuthoringStudyId = study.AuthoringStudyId,
                    .Criteria = "updated criteria",
                    .Gender = "Female",
                    .OlderAdult = True},
                TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal("updated criteria", loaded.Eligibility.Criteria)
        Assert.Equal("Female", loaded.Eligibility.Gender)
        Assert.True(loaded.Eligibility.OlderAdult.Value)
    End Function

    <SkippableFact>
    Public Async Function SaveCriteria_replaces_all_in_ordinal_order() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Criteria study")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Dim first As IReadOnlyList(Of AuthoringCriterion) = New List(Of AuthoringCriterion) From {
                New AuthoringCriterion With {.Criterion = "Inclusion", .NormalizedText = "First"},
                New AuthoringCriterion With {.Criterion = "Inclusion", .NormalizedText = "Second"},
                New AuthoringCriterion With {.Criterion = "Exclusion", .NormalizedText = "Third"}}
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(study.AuthoringStudyId, first, TestUser, CancellationToken.None)

        Dim afterFirst = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal(3, afterFirst.Criteria.Count)
        Assert.Equal("First", afterFirst.Criteria(0).NormalizedText)
        Assert.Equal("Second", afterFirst.Criteria(1).NormalizedText)
        Assert.Equal("Third", afterFirst.Criteria(2).NormalizedText)
        Assert.Equal(0, afterFirst.Criteria(0).Ordinal)
        Assert.Equal(2, afterFirst.Criteria(2).Ordinal)

        ' Replace-all: a shorter list fully supersedes the previous one.
        Dim second As IReadOnlyList(Of AuthoringCriterion) = New List(Of AuthoringCriterion) From {
                New AuthoringCriterion With {.Criterion = "Exclusion", .NormalizedText = "Only one"}}
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(study.AuthoringStudyId, second, TestUser, CancellationToken.None)

        Dim afterSecond = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Single(afterSecond.Criteria)
        Assert.Equal("Only one", afterSecond.Criteria(0).NormalizedText)
    End Function

    <SkippableFact>
    Public Async Function SaveCriteria_round_trips_manual_reason() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Manual reason study")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Dim criteria As IReadOnlyList(Of AuthoringCriterion) = New List(Of AuthoringCriterion) From {
                New AuthoringCriterion With {.Criterion = "Inclusion", .NormalizedText = "Manually added",
                                             .ManualReason = "Added per protocol amendment 3"}}
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(study.AuthoringStudyId, criteria, TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Single(loaded.Criteria)
        Assert.Equal("Added per protocol amendment 3", loaded.Criteria(0).ManualReason)
    End Function

    <SkippableFact>
    Public Async Function List_returns_studies_with_criterion_count() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim a = NewStudy("Study A")
        Dim b = NewStudy("Study B")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                a, New AuthoringEligibility With {.AuthoringStudyId = a.AuthoringStudyId}, TestUser, CancellationToken.None)
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                b, New AuthoringEligibility With {.AuthoringStudyId = b.AuthoringStudyId}, TestUser, CancellationToken.None)
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(
                b.AuthoringStudyId,
                New List(Of AuthoringCriterion) From {
                    New AuthoringCriterion With {.Criterion = "Inclusion", .NormalizedText = "x"}},
                TestUser, CancellationToken.None)

        Dim list = Await _fixture.Gateway.ListAuthoringStudiesAsync(CancellationToken.None)
        Assert.Equal(2, list.Count)
        Dim summaryB = list.Single(Function(s) s.AuthoringStudyId = b.AuthoringStudyId)
        Dim summaryA = list.Single(Function(s) s.AuthoringStudyId = a.AuthoringStudyId)
        Assert.Equal(1, summaryB.CriterionCount)
        Assert.Equal(0, summaryA.CriterionCount)
    End Function

    <SkippableFact>
    Public Async Function StudyId_round_trips_through_get_and_list() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Study-ID study")
        study.StudyId = "PROTO-001"
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal("PROTO-001", loaded.Study.StudyId)

        Dim list = Await _fixture.Gateway.ListAuthoringStudiesAsync(CancellationToken.None)
        Dim summary = list.Single(Function(s) s.AuthoringStudyId = study.AuthoringStudyId)
        Assert.Equal("PROTO-001", summary.StudyId)
    End Function

    <SkippableFact>
    Public Async Function StudyIdExists_is_case_insensitive_and_blank_safe() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Exists study")
        study.StudyId = "ABC-1"
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Assert.True(Await _fixture.Gateway.StudyIdExistsAsync("ABC-1", CancellationToken.None))
        Assert.True(Await _fixture.Gateway.StudyIdExistsAsync("abc-1", CancellationToken.None)) ' case-insensitive
        Assert.False(Await _fixture.Gateway.StudyIdExistsAsync("ABC-2", CancellationToken.None))
        Assert.False(Await _fixture.Gateway.StudyIdExistsAsync("", CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function StudyId_unique_index_rejects_case_insensitive_duplicate() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim first = NewStudy("First")
        first.StudyId = "DUP-1"
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                first, New AuthoringEligibility With {.AuthoringStudyId = first.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Dim second = NewStudy("Second")
        second.StudyId = "dup-1" ' differs only by case — must collide
        Await Assert.ThrowsAnyAsync(Of Exception)(
                Function() _fixture.Gateway.CreateAuthoringStudyAsync(
                    second, New AuthoringEligibility With {.AuthoringStudyId = second.AuthoringStudyId},
                    TestUser, CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function SetStudyId_assigns_when_empty_then_is_immutable() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' A legacy study: created with no Study ID (study_id stored as NULL).
        Dim study = NewStudy("Legacy study")
        study.StudyId = ""
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        ' First assignment succeeds (was empty).
        Dim setFirst = Await _fixture.Gateway.SetAuthoringStudyIdAsync(
                study.AuthoringStudyId, "LATE-1", TestUser, CancellationToken.None)
        Assert.True(setFirst)
        Dim afterFirst = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal("LATE-1", afterFirst.Study.StudyId)

        ' Second attempt is a no-op (already set) — the id is immutable.
        Dim setSecond = Await _fixture.Gateway.SetAuthoringStudyIdAsync(
                study.AuthoringStudyId, "LATE-2", TestUser, CancellationToken.None)
        Assert.False(setSecond)
        Dim afterSecond = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal("LATE-1", afterSecond.Study.StudyId) ' unchanged

        ' A blank value never writes.
        Assert.False(Await _fixture.Gateway.SetAuthoringStudyIdAsync(
                study.AuthoringStudyId, "   ", TestUser, CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function Delete_cascades_to_eligibility_and_criteria() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("To delete")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(
                study.AuthoringStudyId,
                New List(Of AuthoringCriterion) From {
                    New AuthoringCriterion With {
                        .Criterion = "Inclusion",
                        .NormalizedText = "x",
                        .Sources = New List(Of AuthoringCriterionSource) From {NewSource("NCT00000001")}}},
                TestUser, CancellationToken.None)

        Dim deleted = Await _fixture.Gateway.DeleteAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal(1, deleted)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Null(loaded)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT
                    (SELECT count(*) FROM public.authoring_eligibility WHERE authoring_study_id = @id)
                    + (SELECT count(*) FROM public.authoring_criterion WHERE authoring_study_id = @id)
                    + (SELECT count(*) FROM public.authoring_criterion_source s
                         JOIN public.authoring_criterion c ON c.authoring_criterion_id = s.authoring_criterion_id
                         WHERE c.authoring_study_id = @id)"
                cmd.Parameters.AddWithValue("id", study.AuthoringStudyId)
                Assert.Equal(0L, Convert.ToInt64(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function SaveCriteria_persists_and_round_trips_sources() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Lineage study")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Dim withId = NewSource("NCT00000010")
        withId.EligibilityId = 4242L
        withId.OriginalText = "Adults aged 18 to 65"
        withId.Concept = "Adult"
        withId.ConceptCode = "C0001675"
        withId.SemanticType = "Age Group"
        withId.MatchScore = 0.875D
        Dim withoutId = NewSource("NCT00000011")
        withoutId.EligibilityId = Nothing
        withoutId.OriginalText = "18 years or older"

        Dim criteria As IReadOnlyList(Of AuthoringCriterion) = New List(Of AuthoringCriterion) From {
                New AuthoringCriterion With {
                    .Criterion = "Inclusion",
                    .NormalizedText = "Age >= 18",
                    .Sources = New List(Of AuthoringCriterionSource) From {withId, withoutId}}}
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(study.AuthoringStudyId, criteria, TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Single(loaded.Criteria)
        Dim sources = loaded.Criteria(0).Sources
        Assert.Equal(2, sources.Count)

        Dim a = sources.Single(Function(s) s.NctId = "NCT00000010")
        Assert.Equal(4242L, a.EligibilityId.Value)
        Assert.Equal("Adults aged 18 to 65", a.OriginalText)
        Assert.Equal("C0001675", a.ConceptCode)
        Assert.Equal(0.875D, a.MatchScore)
        Assert.NotEqual(Guid.Empty, a.AuthoringCriterionSourceId)
        Assert.Equal(loaded.Criteria(0).AuthoringCriterionId, a.AuthoringCriterionId)

        Dim b = sources.Single(Function(s) s.NctId = "NCT00000011")
        Assert.False(b.EligibilityId.HasValue)
        Assert.Equal("18 years or older", b.OriginalText)
    End Function

    <SkippableFact>
    Public Async Function SaveCriteria_upsert_preserves_created_attribution() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Attribution study")
        Dim creator = Guid.NewGuid()
        Dim editor = Guid.NewGuid()
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                creator, CancellationToken.None)

        ' First save (as creator): two rows with stable ids that round-trip.
        Dim keepId = Guid.NewGuid()
        Dim dropId = Guid.NewGuid()
        Dim first As IReadOnlyList(Of AuthoringCriterion) = New List(Of AuthoringCriterion) From {
                New AuthoringCriterion With {.AuthoringCriterionId = keepId, .Criterion = "Inclusion", .NormalizedText = "Keep me"},
                New AuthoringCriterion With {.AuthoringCriterionId = dropId, .Criterion = "Exclusion", .NormalizedText = "Drop me"}}
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(study.AuthoringStudyId, first, creator, CancellationToken.None)

        Dim afterFirst = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Dim keepOriginal = afterFirst.Criteria.Single(Function(c) c.AuthoringCriterionId = keepId)
        Dim originalCreatedAt = keepOriginal.CreatedAt
        Assert.Equal(creator, keepOriginal.CreatedBy)
        Assert.Equal(creator, keepOriginal.LastUpdatedBy)

        ' Re-save (as editor): edit the kept row, drop one, add a brand-new row.
        Dim second As IReadOnlyList(Of AuthoringCriterion) = New List(Of AuthoringCriterion) From {
                New AuthoringCriterion With {.AuthoringCriterionId = keepId, .Criterion = "Inclusion", .NormalizedText = "Keep me EDITED"},
                New AuthoringCriterion With {.AuthoringCriterionId = Guid.Empty, .Criterion = "Inclusion", .NormalizedText = "Brand new"}}
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(study.AuthoringStudyId, second, editor, CancellationToken.None)

        Dim afterSecond = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Equal(2, afterSecond.Criteria.Count)
        Assert.DoesNotContain(afterSecond.Criteria, Function(c) c.AuthoringCriterionId = dropId)

        ' Surviving row keeps created_at/created_by; last_updated_by is bumped.
        Dim keptRow = afterSecond.Criteria.Single(Function(c) c.AuthoringCriterionId = keepId)
        Assert.Equal("Keep me EDITED", keptRow.NormalizedText)
        Assert.Equal(creator, keptRow.CreatedBy)
        Assert.Equal(editor, keptRow.LastUpdatedBy)
        Assert.Equal(originalCreatedAt, keptRow.CreatedAt)

        ' New row is attributed entirely to the editor.
        Dim newRow = afterSecond.Criteria.Single(Function(c) c.NormalizedText = "Brand new")
        Assert.Equal(editor, newRow.CreatedBy)
        Assert.Equal(editor, newRow.LastUpdatedBy)
    End Function

    <SkippableFact>
    Public Async Function SaveCriteria_replace_all_clears_old_sources() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim study = NewStudy("Replace sources study")
        Await _fixture.Gateway.CreateAuthoringStudyAsync(
                study, New AuthoringEligibility With {.AuthoringStudyId = study.AuthoringStudyId},
                TestUser, CancellationToken.None)

        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(
                study.AuthoringStudyId,
                New List(Of AuthoringCriterion) From {
                    New AuthoringCriterion With {
                        .Criterion = "Inclusion",
                        .NormalizedText = "first",
                        .Sources = New List(Of AuthoringCriterionSource) From {
                            NewSource("NCT00000020"), NewSource("NCT00000021")}}},
                TestUser, CancellationToken.None)

        ' Replace-all with a criterion that carries no sources — the cascade on
        ' the criterion DELETE must wipe the prior mapping rows.
        Await _fixture.Gateway.SaveAuthoringCriteriaAsync(
                study.AuthoringStudyId,
                New List(Of AuthoringCriterion) From {
                    New AuthoringCriterion With {.Criterion = "Exclusion", .NormalizedText = "second"}},
                TestUser, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetAuthoringStudyAsync(study.AuthoringStudyId, CancellationToken.None)
        Assert.Single(loaded.Criteria)
        Assert.Empty(loaded.Criteria(0).Sources)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM public.authoring_criterion_source s
                    JOIN public.authoring_criterion c ON c.authoring_criterion_id = s.authoring_criterion_id
                    WHERE c.authoring_study_id = @id"
                cmd.Parameters.AddWithValue("id", study.AuthoringStudyId)
                Assert.Equal(0L, Convert.ToInt64(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

End Class

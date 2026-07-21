Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

Public Class ConditionNormalizerTests

    <Theory>
    <InlineData("COPD", "copd")>
    <InlineData("  Breast   Cancer  ", "breast cancer")>
    <InlineData("Non" & vbTab & "Small Cell", "non small cell")>
    <InlineData("", "")>
    <InlineData("   ", "")>
    Public Sub Normalize_lowercases_trims_and_collapses_whitespace(input As String, expected As String)
        Assert.Equal(expected, ConceptKey.Normalize(input))
    End Sub

    <Fact>
    Public Sub Normalize_is_idempotent()
        Dim once = ConceptKey.Normalize("Gastrointestinal   Bleeding")
        Assert.Equal(once, ConceptKey.Normalize(once))
    End Sub

    ' ---------- fakes ----------

    Private NotInheritable Class FakeStore
        Implements IConditionConceptStore

        Public Property ExactByNorm As New Dictionary(Of String, IReadOnlyList(Of UmlsCandidate))
        Public Property Upserted As New List(Of ConditionConceptEntry)
        Public Property UnseenByStudy As New Dictionary(Of String, IReadOnlyList(Of String))

        Public Function LookupExactAsync(conditionNorm As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IConditionConceptStore.LookupExactAsync
            Dim hit As IReadOnlyList(Of UmlsCandidate) = Nothing
            If ExactByNorm.TryGetValue(conditionNorm, hit) Then Return Task.FromResult(hit)
            Return Task.FromResult(Of IReadOnlyList(Of UmlsCandidate))(Array.Empty(Of UmlsCandidate)())
        End Function

        Public Function UpsertAsync(entry As ConditionConceptEntry, cancellationToken As CancellationToken) _
                As Task Implements IConditionConceptStore.UpsertAsync
            Upserted.Add(entry)
            Return Task.CompletedTask
        End Function

        Public Function GetUnseenConditionsForStudyAsync(nctId As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of String)) Implements IConditionConceptStore.GetUnseenConditionsForStudyAsync
            Dim hit As IReadOnlyList(Of String) = Nothing
            If UnseenByStudy.TryGetValue(nctId, hit) Then Return Task.FromResult(hit)
            Return Task.FromResult(Of IReadOnlyList(Of String))(Array.Empty(Of String)())
        End Function

        Public Function SeedFromCorpusAsync(cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.SeedFromCorpusAsync
            Return Task.FromResult(0)
        End Function

        Public Function GetPendingAsync(limit As Integer, force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of ConditionConceptEntry)) Implements IConditionConceptStore.GetPendingAsync
            Return Task.FromResult(Of IReadOnlyList(Of ConditionConceptEntry))(Array.Empty(Of ConditionConceptEntry)())
        End Function

        Public Function CountPendingAsync(force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.CountPendingAsync
            Return Task.FromResult(0)
        End Function
    End Class

    Private NotInheritable Class FakeUmlsClient
        Implements IUmlsClient

        Public Property Candidates As IReadOnlyList(Of UmlsCandidate) = Array.Empty(Of UmlsCandidate)()
        Public Property LastQuery As String = ""
        Public Property SearchCallCount As Integer

        Public Function SearchAsync(concept As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IUmlsClient.SearchAsync
            LastQuery = concept
            SearchCallCount += 1
            Return Task.FromResult(Candidates)
        End Function

        Public Function GetSemanticTypeAssignmentsAsync(cui As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) Implements IUmlsClient.GetSemanticTypeAssignmentsAsync
            Return Task.FromResult(Of IReadOnlyList(Of SemanticTypeAssignment))(Array.Empty(Of SemanticTypeAssignment)())
        End Function
    End Class

    Private Shared Function NewNormalizer(store As FakeStore, client As FakeUmlsClient) As ConditionNormalizer
        Return New ConditionNormalizer(store, client, New UmlsMatchScorer())
    End Function

    ' ---------- tier 1a ----------

    ' THE regression test for spec section 2.2. An exact atom match is definitive
    ' by construction, so the preferred name is irrelevant. Routing this through
    ' PickBestMatch would score "stroke" against "CVA - Cerebrovascular accident",
    ' land far below 0.60, and reject a perfect match.
    <Fact>
    Public Async Function Tier1a_accepts_exact_match_without_consulting_the_scorer() As Task
        Dim store As New FakeStore()
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US")}
        Dim client As New FakeUmlsClient()

        Dim result = Await NewNormalizer(store, client).ResolveAsync("Stroke", CancellationToken.None)

        Assert.Equal("C0038454", result.ConceptCode)
        Assert.Equal("CVA - Cerebrovascular accident", result.UmlsName)
        Assert.Equal(ConditionMatchTier.Exact, result.Tier)
        Assert.Equal(1.0, result.Score)
        ' The fuzzy search must never have been reached.
        Assert.Equal(0, client.SearchCallCount)
    End Function

    ' ---------- tier 1b ----------

    <Fact>
    Public Async Function Tier1b_prefers_the_cui_whose_pref_name_equals_the_query() As Task
        Dim store As New FakeStore()
        store.ExactByNorm("depression") = {
            New UmlsCandidate("C9999999", "Depressive disorder", "MSH"),
            New UmlsCandidate("C0011570", "Depression", "SNOMEDCT_US")}

        Dim result = Await NewNormalizer(store, New FakeUmlsClient()).ResolveAsync("Depression", CancellationToken.None)

        Assert.Equal("C0011570", result.ConceptCode)
        Assert.Equal(ConditionMatchTier.ExactAmbiguous, result.Tier)
        Assert.Equal(1.0, result.Score)
    End Function

    <Fact>
    Public Async Function Tier1b_falls_back_to_lowest_cui_when_scores_tie() As Task
        Dim store As New FakeStore()
        ' Two candidates, neither equal to the query, both scoring identically
        ' because the names are the same string.
        store.ExactByNorm("ambiguous term") = {
            New UmlsCandidate("C0000200", "Something Else", "MSH"),
            New UmlsCandidate("C0000100", "Something Else", "MSH")}

        Dim result = Await NewNormalizer(store, New FakeUmlsClient()).ResolveAsync("Ambiguous Term", CancellationToken.None)

        Assert.Equal("C0000100", result.ConceptCode)
        Assert.Equal(ConditionMatchTier.ExactAmbiguous, result.Tier)
    End Function

    <Fact>
    Public Async Function Tier1b_accepts_even_when_every_score_is_below_the_fuzzy_threshold() As Task
        Dim store As New FakeStore()
        store.ExactByNorm("cancer") = {
            New UmlsCandidate("C0006826", "Blastoma", "MSH"),
            New UmlsCandidate("C0998888", "Neoplasm unspecified morphology", "MSH")}

        Dim result = Await NewNormalizer(store, New FakeUmlsClient()).ResolveAsync("Cancer", CancellationToken.None)

        ' The string is still an exact atom match; only WHICH concept was in doubt.
        Assert.True(result.IsResolved)
        Assert.Equal(ConditionMatchTier.ExactAmbiguous, result.Tier)
    End Function

    ' ---------- tier 2 ----------

    <Fact>
    Public Async Function Tier2_passes_the_raw_uppercase_form_to_the_search_so_acronyms_score() As Task
        Dim store As New FakeStore()   ' no exact atom -> tier 2
        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0007131", "NSCLC - Non-small cell lung cancer", "SNOMEDCT_US")}}

        Dim result = Await NewNormalizer(store, client).ResolveAsync("NSCLC", CancellationToken.None)

        ' Feeding the lowercased key would disable UmlsMatchScorer's acronym term,
        ' which requires ^[A-Z0-9]{2,6}$ on the raw query.
        Assert.Equal("NSCLC", client.LastQuery)
        Assert.Equal("C0007131", result.ConceptCode)
        Assert.Equal(ConditionMatchTier.Fuzzy, result.Tier)
    End Function

    <Fact>
    Public Async Function Tier2_rejects_a_match_below_the_condition_threshold() As Task
        Dim store As New FakeStore()
        ' "NSC762" is the real trigram best match for "nsclc" and scores ~0.30.
        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0700294", "NSC762", "MSH")}}

        Dim result = Await NewNormalizer(store, client).ResolveAsync("Zzqq Unmatchable Phrase", CancellationToken.None)

        Assert.False(result.IsResolved)
        Assert.Equal(ConditionMatchTier.Unresolved, result.Tier)
        Assert.Equal("", result.ConceptCode)
        Assert.Equal(0.0, result.Score)
    End Function

    <Fact>
    Public Sub Condition_threshold_is_stricter_than_the_pipeline_threshold()
        Assert.Equal(0.6, ConditionNormalizer.FuzzyThreshold)
        Assert.True(ConditionNormalizer.FuzzyThreshold > UmlsMatchScorer.MatchThreshold,
                    "The condition cutoff must be stricter than the criteria pipeline's 0.45")
    End Sub

    ' THE test that justifies choosing 0.60 over 0.45. Without it, every other
    ' tier-2 test would still pass with the threshold left at the pipeline's
    ' 0.45, and the spec's central risk argument would be unverified.
    '
    ' "advanced solid tumors" -> "Solid tumor" measured at 0.478 against the real
    ' corpus: above the pipeline's cutoff, below the condition one. If the scorer
    ' ever changes, the first assertion fails and says exactly why.
    <Fact>
    Public Async Function Tier2_rejects_a_score_between_the_pipeline_and_condition_thresholds() As Task
        Const Query As String = "advanced solid tumors"
        Const CandidateName As String = "Solid tumor"

        Dim score = New UmlsMatchScorer().Score(Query, CandidateName)
        Assert.True(score >= UmlsMatchScorer.MatchThreshold AndAlso score < ConditionNormalizer.FuzzyThreshold,
                    $"Fixture no longer sits in the 0.45-0.60 band (scored {score}); pick another pair.")

        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0280100", CandidateName, "SNOMEDCT_US")}}

        Dim result = Await NewNormalizer(New FakeStore(), client).ResolveAsync(Query, CancellationToken.None)

        ' PickBestMatch would have ACCEPTED this at 0.45. The condition gate rejects it.
        Assert.False(result.IsResolved)
        Assert.Equal(ConditionMatchTier.Unresolved, result.Tier)
    End Function

    <Fact>
    Public Async Function Tier2_accepts_at_exactly_the_condition_threshold() As Task
        ' Boundary is inclusive (>=), matching PickBestMatch's own convention.
        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0011860", "Diabetes Mellitus", "SNOMEDCT_US")}}

        Dim result = Await NewNormalizer(New FakeStore(), client).ResolveAsync("Diabetes Mellitus", CancellationToken.None)

        Assert.True(result.IsResolved)
        Assert.Equal(ConditionMatchTier.Fuzzy, result.Tier)
        Assert.True(result.Score >= ConditionNormalizer.FuzzyThreshold)
    End Function

    <Fact>
    Public Async Function Empty_input_resolves_to_unresolved_without_touching_the_store() As Task
        Dim client As New FakeUmlsClient()
        Dim result = Await NewNormalizer(New FakeStore(), client).ResolveAsync("   ", CancellationToken.None)

        Assert.False(result.IsResolved)
        Assert.Equal(0, client.SearchCallCount)
    End Function

    ' ---------- per-study hook ----------

    <Fact>
    Public Async Function EnsureForStudy_upserts_only_unseen_strings() As Task
        Dim store As New FakeStore()
        store.UnseenByStudy("NCT001") = {"Stroke", "COPD"}
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US")}
        store.ExactByNorm("copd") = {New UmlsCandidate("C0024117", "COPD", "SNOMEDCT_US")}

        Dim written = Await NewNormalizer(store, New FakeUmlsClient()).EnsureForStudyAsync("NCT001", CancellationToken.None)

        Assert.Equal(2, written)
        Assert.Equal(2, store.Upserted.Count)
        Assert.Contains(store.Upserted, Function(e) e.ConditionNorm = "stroke" AndAlso e.RawForm = "Stroke")
        Assert.Contains(store.Upserted, Function(e) e.ConditionNorm = "copd" AndAlso e.ConceptCode = "C0024117")
    End Function

    <Fact>
    Public Async Function EnsureForStudy_writes_nothing_when_every_string_is_known() As Task
        Dim store As New FakeStore()   ' UnseenByStudy empty -> steady state

        Dim written = Await NewNormalizer(store, New FakeUmlsClient()).EnsureForStudyAsync("NCT002", CancellationToken.None)

        Assert.Equal(0, written)
        Assert.Empty(store.Upserted)
    End Function
End Class

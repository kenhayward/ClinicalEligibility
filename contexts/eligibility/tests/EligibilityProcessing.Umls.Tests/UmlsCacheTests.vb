Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Umls
Imports Xunit

Public Class UmlsCacheTests

    ' ============ construction ============

    <Fact>
    Public Sub Constructor_throws_on_null_inner_client()
        Assert.Throws(Of ArgumentNullException)(
            Function() New UmlsCache(inner:=Nothing))
    End Sub

    ' ============ SearchAsync — input gate ============

    <Theory>
    <InlineData("")>
    <InlineData("   ")>
    Public Async Function Search_returns_empty_without_calling_inner_for_blank_concept(concept As String) As Task
        Dim fake = New FakeUmlsClient()
        Dim cache = New UmlsCache(fake)
        Dim result = Await cache.SearchAsync(concept, CancellationToken.None)
        Assert.Empty(result)
        Assert.Empty(fake.SearchCalls)
    End Function

    <Fact>
    Public Async Function Search_returns_empty_for_null_concept() As Task
        Dim fake = New FakeUmlsClient()
        Dim cache = New UmlsCache(fake)
        Dim result = Await cache.SearchAsync(Nothing, CancellationToken.None)
        Assert.Empty(result)
        Assert.Empty(fake.SearchCalls)
    End Function

    ' ============ SearchAsync — caching ============

    <Fact>
    Public Async Function Search_first_call_delegates_to_inner() As Task
        Dim fake = MakeFake()
        fake.SearchResults("diabetes") = New UmlsCandidate() {New UmlsCandidate("C001", "Diabetes", "MSH")}
        Dim cache = New UmlsCache(fake)
        Dim result = Await cache.SearchAsync("diabetes", CancellationToken.None)

        Assert.Single(result)
        Assert.Equal("C001", result(0).Ui)
        Assert.Equal(1, fake.SearchCalls.Count)
    End Function

    <Fact>
    Public Async Function Search_second_call_for_same_concept_hits_cache() As Task
        Dim fake = MakeFake()
        fake.SearchResults("diabetes") = New UmlsCandidate() {New UmlsCandidate("C001", "Diabetes", "MSH")}
        Dim cache = New UmlsCache(fake)

        Await cache.SearchAsync("diabetes", CancellationToken.None)
        Dim second = Await cache.SearchAsync("diabetes", CancellationToken.None)

        Assert.Single(second)
        Assert.Equal(1, fake.SearchCalls.Count)  ' inner called exactly once
    End Function

    <Fact>
    Public Async Function Search_caches_separately_per_distinct_concept() As Task
        Dim fake = MakeFake()
        fake.SearchResults("diabetes") = New UmlsCandidate() {New UmlsCandidate("C001", "Diabetes", "MSH")}
        fake.SearchResults("asthma") = New UmlsCandidate() {New UmlsCandidate("C002", "Asthma", "MSH")}
        Dim cache = New UmlsCache(fake)

        Dim d1 = Await cache.SearchAsync("diabetes", CancellationToken.None)
        Dim a1 = Await cache.SearchAsync("asthma", CancellationToken.None)
        Dim d2 = Await cache.SearchAsync("diabetes", CancellationToken.None)

        Assert.Equal("C001", d1(0).Ui)
        Assert.Equal("C002", a1(0).Ui)
        Assert.Equal("C001", d2(0).Ui)
        Assert.Equal(2, fake.SearchCalls.Count)  ' once per distinct concept
    End Function

    <Fact>
    Public Async Function Search_normalises_key_by_lowercasing() As Task
        Dim fake = MakeFake()
        fake.SearchResults("diabetes") = New UmlsCandidate() {New UmlsCandidate("C001", "Diabetes", "MSH")}
        Dim cache = New UmlsCache(fake)

        Await cache.SearchAsync("Diabetes", CancellationToken.None)
        Await cache.SearchAsync("DIABETES", CancellationToken.None)
        Await cache.SearchAsync("diabetes", CancellationToken.None)

        Assert.Equal(1, fake.SearchCalls.Count)  ' all three share one cache entry
    End Function

    <Fact>
    Public Async Function Search_normalises_key_by_trimming() As Task
        Dim fake = MakeFake()
        fake.SearchResults("diabetes") = New UmlsCandidate() {New UmlsCandidate("C001", "Diabetes", "MSH")}
        Dim cache = New UmlsCache(fake)

        Await cache.SearchAsync("  diabetes  ", CancellationToken.None)
        Await cache.SearchAsync("diabetes", CancellationToken.None)

        Assert.Equal(1, fake.SearchCalls.Count)
    End Function

    <Fact>
    Public Async Function Search_caches_empty_result_to_avoid_re_querying_unknown_concepts() As Task
        ' Trial-specific jargon (e.g. typo "diabetez") returns empty from UMLS;
        ' caching empty saves the redundant lookup for the rest of the run.
        Dim fake = MakeFake()
        Dim cache = New UmlsCache(fake)

        Dim r1 = Await cache.SearchAsync("diabetez", CancellationToken.None)
        Dim r2 = Await cache.SearchAsync("diabetez", CancellationToken.None)

        Assert.Empty(r1)
        Assert.Empty(r2)
        Assert.Equal(1, fake.SearchCalls.Count)
    End Function

    ' ============ GetSemanticTypeAssignmentsAsync — input gate ============

    <Theory>
    <InlineData("")>
    <InlineData("   ")>
    Public Async Function SemanticTypes_returns_empty_without_calling_inner_for_blank_cui(cui As String) As Task
        Dim fake = New FakeUmlsClient()
        Dim cache = New UmlsCache(fake)
        Dim result = Await cache.GetSemanticTypeAssignmentsAsync(cui, CancellationToken.None)
        Assert.Empty(result)
        Assert.Empty(fake.SemanticTypesCalls)
    End Function

    ' ============ GetSemanticTypeAssignmentsAsync — caching ============

    <Fact>
    Public Async Function SemanticTypes_first_call_delegates_to_inner() As Task
        Dim fake = MakeFake()
        fake.SemanticTypesResults("C0011860") = New SemanticTypeAssignment() {New SemanticTypeAssignment("T047", "Disease or Syndrome")}
        Dim cache = New UmlsCache(fake)
        Dim result = Await cache.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)

        ' Compare projections: SemanticTypeAssignment has reference equality, so
        ' comparing instances would pass only by accident of identity.
        Assert.Equal({"T047"}, result.Select(Function(a) a.Tui).ToArray())
        Assert.Equal({"Disease or Syndrome"}, result.Select(Function(a) a.Sty).ToArray())
        Assert.Equal(1, fake.SemanticTypesCalls.Count)
    End Function

    <Fact>
    Public Async Function SemanticTypes_second_call_for_same_cui_hits_cache() As Task
        Dim fake = MakeFake()
        fake.SemanticTypesResults("C0011860") = New SemanticTypeAssignment() {New SemanticTypeAssignment("T047", "Disease or Syndrome")}
        Dim cache = New UmlsCache(fake)

        Await cache.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)
        Await cache.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)

        Assert.Equal(1, fake.SemanticTypesCalls.Count)
    End Function

    <Fact>
    Public Async Function SemanticTypes_treats_cui_case_sensitively() As Task
        ' UMLS CUIs are conventionally uppercase "C" + 7 digits; we don't
        ' normalise case to avoid masking malformed inputs.
        Dim fake = MakeFake()
        fake.SemanticTypesResults("C0011860") = New SemanticTypeAssignment() {New SemanticTypeAssignment("T047", "Disease or Syndrome")}
        fake.SemanticTypesResults("c0011860") = New SemanticTypeAssignment() {New SemanticTypeAssignment("T999", "Something Else")}
        Dim cache = New UmlsCache(fake)

        Await cache.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)
        Await cache.GetSemanticTypeAssignmentsAsync("c0011860", CancellationToken.None)

        Assert.Equal(2, fake.SemanticTypesCalls.Count)
    End Function

    <Fact>
    Public Async Function SemanticTypes_caches_empty_results() As Task
        Dim fake = MakeFake()
        Dim cache = New UmlsCache(fake)

        Await cache.GetSemanticTypeAssignmentsAsync("CXXXXXXX", CancellationToken.None)
        Await cache.GetSemanticTypeAssignmentsAsync("CXXXXXXX", CancellationToken.None)

        Assert.Equal(1, fake.SemanticTypesCalls.Count)
    End Function

    ' ============ Search and semantic-type caches are independent ============

    <Fact>
    Public Async Function Two_caches_do_not_share_keys() As Task
        ' Search by "C0011860" (treated as a concept) should not satisfy a
        ' subsequent GetSemanticTypes("C0011860") and vice versa.
        Dim fake = MakeFake()
        Dim cache = New UmlsCache(fake)

        Await cache.SearchAsync("C0011860", CancellationToken.None)
        Await cache.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)

        Assert.Equal(1, fake.SearchCalls.Count)
        Assert.Equal(1, fake.SemanticTypesCalls.Count)
    End Function

    ' ============ cancellation propagates ============

    <Fact>
    Public Async Function Search_propagates_cancellation_from_inner() As Task
        Dim fake = New FakeUmlsClient()
        Dim cache = New UmlsCache(fake)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() cache.SearchAsync("diabetes", cts.Token))
        End Using
    End Function

    <Fact>
    Public Async Function SemanticTypes_propagates_cancellation_from_inner() As Task
        Dim fake = New FakeUmlsClient()
        Dim cache = New UmlsCache(fake)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() cache.GetSemanticTypeAssignmentsAsync("C0011860", cts.Token))
        End Using
    End Function

    ' ============ diagnostics ============

    <Fact>
    Public Async Function Size_counters_reflect_distinct_entries() As Task
        Dim fake = MakeFake()
        Dim cache = New UmlsCache(fake)
        Assert.Equal(0, cache.SearchCacheSize)
        Assert.Equal(0, cache.SemanticTypesCacheSize)

        Await cache.SearchAsync("diabetes", CancellationToken.None)
        Await cache.SearchAsync("asthma", CancellationToken.None)
        Await cache.SearchAsync("diabetes", CancellationToken.None)  ' cache hit, no new entry
        Await cache.GetSemanticTypeAssignmentsAsync("C001", CancellationToken.None)

        Assert.Equal(2, cache.SearchCacheSize)
        Assert.Equal(1, cache.SemanticTypesCacheSize)
    End Function

    <Fact>
    Public Async Function Clear_empties_both_caches() As Task
        Dim fake = MakeFake()
        Dim cache = New UmlsCache(fake)

        Await cache.SearchAsync("diabetes", CancellationToken.None)
        Await cache.GetSemanticTypeAssignmentsAsync("C0011860", CancellationToken.None)
        Assert.Equal(1, cache.SearchCacheSize)
        Assert.Equal(1, cache.SemanticTypesCacheSize)

        cache.Clear()
        Assert.Equal(0, cache.SearchCacheSize)
        Assert.Equal(0, cache.SemanticTypesCacheSize)

        ' After Clear, the next call delegates to inner again.
        Await cache.SearchAsync("diabetes", CancellationToken.None)
        Assert.Equal(2, fake.SearchCalls.Count)
    End Function

    ' ============ helper ============

    Private Shared Function MakeFake() As FakeUmlsClient
        Return New FakeUmlsClient()
    End Function

End Class

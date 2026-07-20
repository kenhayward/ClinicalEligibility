Imports System.Collections.Generic
Imports EligibilityProcessing.Core
Imports Xunit

' Coverage for DuplicateConceptMerger — the post-UMLS-resolution dedup
' that collapses (ConceptCode, SemanticType, Criterion)-equal records and
' combines their OriginalText snippets.
Public Class DuplicateConceptMergerTests

    <Fact>
    Public Sub Returns_empty_for_null_input()
        Dim result = DuplicateConceptMerger.Merge(Nothing)
        Assert.Empty(result)
    End Sub

    <Fact>
    Public Sub Returns_empty_for_empty_input()
        Dim result = DuplicateConceptMerger.Merge(Array.Empty(Of ResolvedRecord)())
        Assert.Empty(result)
    End Sub

    <Fact>
    Public Sub Single_record_passes_through_unchanged()
        Dim record = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                                  semanticType:="Disease or Syndrome",
                                  originalText:="has diabetes")
        Dim result = DuplicateConceptMerger.Merge({record})
        Assert.Single(result)
        Assert.Same(record, result(0))   ' identity preserved when no merge happens
    End Sub

    <Fact>
    Public Sub Two_records_with_distinct_concept_codes_are_both_preserved()
        Dim a = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="has diabetes")
        Dim b = MakeResolved("Hypertension", "Inclusion", conceptCode:="C0020538",
                             semanticType:="Disease or Syndrome",
                             originalText:="has hypertension")
        Dim result = DuplicateConceptMerger.Merge({a, b})
        Assert.Equal(2, result.Count)
        Assert.Equal("has diabetes", result(0).OriginalText)
        Assert.Equal("has hypertension", result(1).OriginalText)
    End Sub

    <Fact>
    Public Sub Two_records_with_same_key_collapse_into_one_with_joined_original_text()
        ' Same concept code + semantic type + criterion → merged into one row,
        ' OriginalText snippets joined with a space.
        Dim a = MakeResolved("Breast cancer", "Exclusion", conceptCode:="C0006142",
                             semanticType:="Neoplastic Process",
                             originalText:="history of breast cancer")
        Dim b = MakeResolved("Breast cancer survivor", "Exclusion", conceptCode:="C0006142",
                             semanticType:="Neoplastic Process",
                             originalText:="breast cancer survivor")
        Dim result = DuplicateConceptMerger.Merge({a, b})
        Dim merged = Assert.Single(result)
        Assert.Equal("history of breast cancer breast cancer survivor", merged.OriginalText)
        Assert.Equal("C0006142", merged.ConceptCode)
        Assert.Equal("Exclusion", merged.Criterion)
    End Sub

    <Fact>
    Public Sub Identical_original_text_is_deduplicated_in_the_join()
        ' Two records that resolve to the same concept code AND happen to
        ' have identical OriginalText (e.g. the LLM extracted the same
        ' sentence twice) should produce one merged record with the text
        ' present only once — not "foo foo".
        Dim a = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="patient has diabetes")
        Dim b = MakeResolved("Diabetes mellitus", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="patient has diabetes")
        Dim result = DuplicateConceptMerger.Merge({a, b})
        Dim merged = Assert.Single(result)
        Assert.Equal("patient has diabetes", merged.OriginalText)
    End Sub

    <Fact>
    Public Sub Same_concept_code_with_different_criterion_does_not_merge()
        ' Inclusion vs Exclusion are logically distinct even when the
        ' concept code matches — e.g. a trial that includes diabetics and
        ' separately excludes severely-uncontrolled diabetics.
        Dim a = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="diabetic")
        Dim b = MakeResolved("Diabetes", "Exclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="severe diabetes")
        Dim result = DuplicateConceptMerger.Merge({a, b})
        Assert.Equal(2, result.Count)
    End Sub

    <Fact>
    Public Sub Same_concept_code_with_different_semantic_type_does_not_merge()
        Dim a = MakeResolved("X", "Inclusion", conceptCode:="C0000000",
                             semanticType:="Disease or Syndrome",
                             originalText:="a")
        Dim b = MakeResolved("X", "Inclusion", conceptCode:="C0000000",
                             semanticType:="Sign or Symptom",
                             originalText:="b")
        Dim result = DuplicateConceptMerger.Merge({a, b})
        Assert.Equal(2, result.Count)
    End Sub

    <Fact>
    Public Sub Unresolved_records_pass_through_without_merging()
        ' Two records that both failed UMLS resolution (empty ConceptCode)
        ' must NOT be merged together — empty code carries no identity, we
        ' can't tell whether they refer to the same concept.
        Dim a = MakeUnresolved("Adverse event", "Inclusion", originalText:="a")
        Dim b = MakeUnresolved("Adverse event", "Inclusion", originalText:="b")
        Dim result = DuplicateConceptMerger.Merge({a, b})
        Assert.Equal(2, result.Count)
        Assert.Equal("a", result(0).OriginalText)
        Assert.Equal("b", result(1).OriginalText)
    End Sub

    <Fact>
    Public Sub Mixed_resolved_and_unresolved_preserves_unresolved_and_merges_resolved()
        Dim r1 = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                              semanticType:="Disease or Syndrome", originalText:="a")
        Dim r2 = MakeUnresolved("Something else", "Inclusion", originalText:="b")
        Dim r3 = MakeResolved("Diabetes mellitus", "Inclusion", conceptCode:="C0011860",
                              semanticType:="Disease or Syndrome", originalText:="c")
        Dim result = DuplicateConceptMerger.Merge({r1, r2, r3})
        Assert.Equal(2, result.Count)                                       ' r1+r3 merged, r2 standalone
        Assert.Equal("a c", result(0).OriginalText)                         ' merged record at r1's position
        Assert.Equal("b", result(1).OriginalText)                            ' unresolved untouched
        Assert.Empty(result(1).ConceptCode)
    End Sub

    <Fact>
    Public Sub Merged_record_keeps_first_records_qualifier_and_time_window()
        Dim a = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="a", qualifier:="confirmed",
                             timeWindow:="at screening")
        Dim b = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome",
                             originalText:="b", qualifier:="probable",
                             timeWindow:="any time")
        Dim merged = Assert.Single(DuplicateConceptMerger.Merge({a, b}))
        Assert.Equal("confirmed", merged.Qualifier)
        Assert.Equal("at screening", merged.TimeWindow)
    End Sub

    <Fact>
    Public Sub Output_order_matches_first_occurrence_in_input()
        ' a (key1), b (key2), c (key1 again — merges with a).
        ' Expected output order: merged(a, c) then b.
        Dim a = MakeResolved("Diabetes", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome", originalText:="a")
        Dim b = MakeResolved("Hypertension", "Inclusion", conceptCode:="C0020538",
                             semanticType:="Disease or Syndrome", originalText:="b")
        Dim c = MakeResolved("Diabetes mellitus", "Inclusion", conceptCode:="C0011860",
                             semanticType:="Disease or Syndrome", originalText:="c")
        Dim result = DuplicateConceptMerger.Merge({a, b, c})
        Assert.Equal(2, result.Count)
        Assert.Equal("a c", result(0).OriginalText)
        Assert.Equal("b", result(1).OriginalText)
    End Sub

    ' --- helpers ---

    Private Shared Function MakeResolved(
            concept As String,
            criterion As String,
            conceptCode As String,
            semanticType As String,
            originalText As String,
            Optional qualifier As String = "",
            Optional timeWindow As String = "") As ResolvedRecord
        Dim c = New CriterionRecord(
                nctId:="NCT00000001", criterion:=criterion, domain:="Disease",
                concept:=concept, qualifier:=qualifier,
                timeWindow:=timeWindow, originalText:=originalText)
        Dim m = New UmlsMatch(
                conceptCode:=conceptCode, umlsName:=concept,
                matchSource:="MSH", matchScore:=0.9)
        ' The helper takes a single name for brevity; a synthetic TUI keeps the
        ' merge key (which is now the TUI set) aligned with that name.
        Dim semanticTypes = If(String.IsNullOrEmpty(semanticType),
                                Array.Empty(Of SemanticTypeAssignment)(),
                                New SemanticTypeAssignment() {
                                    New SemanticTypeAssignment("T" & Math.Abs(semanticType.GetHashCode()).ToString().PadLeft(3, "0"c).Substring(0, 3), semanticType)})
        Return New ResolvedRecord(c, m, semanticTypes)
    End Function

    Private Shared Function MakeUnresolved(
            concept As String,
            criterion As String,
            originalText As String) As ResolvedRecord
        Dim c = New CriterionRecord(
                nctId:="NCT00000001", criterion:=criterion, domain:="Disease",
                concept:=concept, qualifier:="", timeWindow:="",
                originalText:=originalText)
        Return New ResolvedRecord(c, UmlsMatch.Unresolved, Array.Empty(Of SemanticTypeAssignment)())
    End Function

End Class

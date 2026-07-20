Imports System.Collections.Generic
Imports EligibilityProcessing.Core
Imports Xunit

Public Class ResolvedRecordTests

    ' ============ constructor composition ============

    <Fact>
    Public Sub Constructor_copies_criterion_fields_through()
        Dim criterion = New CriterionRecord(
                nctId:="NCT00000001",
                criterion:="Inclusion",
                domain:="Age",
                concept:="Adult",
                qualifier:="",
                timeWindow:="",
                originalText:="Adults aged 18+")
        Dim resolved = New ResolvedRecord(
                criterion,
                UmlsMatch.Unresolved,
                semanticTypes:=Array.Empty(Of SemanticTypeAssignment)())

        Assert.Equal("NCT00000001", resolved.NctId)
        Assert.Equal("Inclusion", resolved.Criterion)
        Assert.Equal("Age", resolved.Domain)
        Assert.Equal("Adult", resolved.Concept)
        Assert.Equal("", resolved.Qualifier)
        Assert.Equal("", resolved.TimeWindow)
        Assert.Equal("Adults aged 18+", resolved.OriginalText)
    End Sub

    <Fact>
    Public Sub Constructor_copies_umls_match_fields_through()
        Dim criterion = New CriterionRecord(
                nctId:="NCT00000001",
                criterion:="Inclusion",
                domain:="Disease",
                concept:="Diabetes",
                qualifier:="",
                timeWindow:="",
                originalText:="Has diabetes")
        Dim match = New UmlsMatch(
                conceptCode:="C0011860",
                umlsName:="Diabetes Mellitus",
                matchSource:="MSH",
                matchScore:=0.875)
        Dim resolved = New ResolvedRecord(
                criterion,
                match,
                semanticTypes:=Array.Empty(Of SemanticTypeAssignment)())

        Assert.Equal("C0011860", resolved.ConceptCode)
        Assert.Equal("Diabetes Mellitus", resolved.UmlsName)
        Assert.Equal("MSH", resolved.MatchSource)
        Assert.Equal(0.875, resolved.MatchScore)
    End Sub

    ' ============ semantic types ============

    <Fact>
    Public Sub Semantic_type_string_is_sorted_by_name_and_comma_joined()
        ' Sorted by STY, not by input order, so one CUI yields one string
        ' corpus-wide. Legacy REST-era values preserved UMLS API order, which is
        ' not alphabetical - the phase 2 backfill rewrites them to this form.
        Dim resolved = MakeResolved(semanticTypes:={
            New SemanticTypeAssignment("T041", "Mental Process"),
            New SemanticTypeAssignment("T047", "Disease or Syndrome")})
        Assert.Equal("Disease or Syndrome, Mental Process", resolved.SemanticType)
    End Sub

    <Fact>
    Public Sub Semantic_type_tuis_preserve_every_assignment()
        Dim resolved = MakeResolved(semanticTypes:={
            New SemanticTypeAssignment("T041", "Mental Process"),
            New SemanticTypeAssignment("T047", "Disease or Syndrome")})
        Assert.Equal({"T041", "T047"}, resolved.SemanticTypeTuis.OrderBy(Function(t) t).ToArray())
    End Sub

    ' The case the joined string cannot express. Splitting
    ' "Amino Acid, Peptide, or Protein, Enzyme" on ", " yields four fragments,
    ' none of which is a real semantic type - which is why the TUI array exists.
    <Fact>
    Public Sub Semantic_type_tuis_survive_names_containing_commas()
        Dim resolved = MakeResolved(semanticTypes:={
            New SemanticTypeAssignment("T116", "Amino Acid, Peptide, or Protein"),
            New SemanticTypeAssignment("T126", "Enzyme")})
        Assert.Equal(2, resolved.SemanticTypeTuis.Count)
        Assert.Contains("T116", resolved.SemanticTypeTuis)
        Assert.Contains("T126", resolved.SemanticTypeTuis)
        Assert.Equal("Amino Acid, Peptide, or Protein, Enzyme", resolved.SemanticType)
    End Sub

    ' An empty TUI is not a semantic type id. The REST backend derives TUIs from
    ' a URI and can come up empty; "" must not become an array member that
    ' containment queries could match.
    <Fact>
    Public Sub Semantic_type_tuis_drop_empty_ids_but_keep_the_name()
        Dim resolved = MakeResolved(semanticTypes:={
            New SemanticTypeAssignment("", "Disease or Syndrome")})
        Assert.Empty(resolved.SemanticTypeTuis)
        Assert.Equal("Disease or Syndrome", resolved.SemanticType)
    End Sub

    <Fact>
    Public Sub Semantic_types_empty_list_yields_empty_string_and_no_tuis()
        Dim resolved = MakeResolved(semanticTypes:=Array.Empty(Of SemanticTypeAssignment)())
        Assert.Equal("", resolved.SemanticType)
        Assert.Empty(resolved.SemanticTypeTuis)
    End Sub

    <Fact>
    Public Sub Semantic_types_null_list_yields_empty_string()
        Dim resolved = MakeResolved(semanticTypes:=Nothing)
        Assert.Equal("", resolved.SemanticType)
        Assert.Empty(resolved.SemanticTypeTuis)
    End Sub

    <Fact>
    Public Sub Semantic_types_single_item_has_no_comma()
        Dim resolved = MakeResolved(semanticTypes:={
            New SemanticTypeAssignment("T047", "Disease or Syndrome")})
        Assert.Equal("Disease or Syndrome", resolved.SemanticType)
    End Sub

    ' ============ unresolved sentinel (spec section 2.8.1) ============

    <Fact>
    Public Sub Unresolved_match_produces_empty_strings_and_zero_score()
        Dim resolved = MakeResolved(match:=UmlsMatch.Unresolved)
        Assert.Equal("", resolved.ConceptCode)
        Assert.Equal("", resolved.UmlsName)
        Assert.Equal("", resolved.MatchSource)
        Assert.Equal(0.0, resolved.MatchScore)
    End Sub

    ' ============ argument validation ============

    <Fact>
    Public Sub Constructor_throws_on_null_criterion()
        Assert.Throws(Of ArgumentNullException)(
            Function() New ResolvedRecord(Nothing, UmlsMatch.Unresolved, Array.Empty(Of SemanticTypeAssignment)()))
    End Sub

    <Fact>
    Public Sub Constructor_throws_on_null_umls_match()
        Dim criterion = MakeCriterion()
        Assert.Throws(Of ArgumentNullException)(
            Function() New ResolvedRecord(criterion, Nothing, Array.Empty(Of SemanticTypeAssignment)()))
    End Sub

    ' ============ helpers ============

    Private Shared Function MakeCriterion() As CriterionRecord
        Return New CriterionRecord(
                nctId:="NCT00000001",
                criterion:="Inclusion",
                domain:="Disease",
                concept:="Diabetes",
                qualifier:="",
                timeWindow:="",
                originalText:="Has diabetes")
    End Function

    Private Shared Function MakeResolved(
            Optional match As UmlsMatch = Nothing,
            Optional semanticTypes As IReadOnlyList(Of SemanticTypeAssignment) = Nothing) As ResolvedRecord
        Return New ResolvedRecord(
                MakeCriterion(),
                If(match, UmlsMatch.Unresolved),
                If(semanticTypes, Array.Empty(Of SemanticTypeAssignment)()))
    End Function

End Class

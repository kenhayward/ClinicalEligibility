Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports EligibilityProcessing.Cli
Imports EligibilityProcessing.Data
Imports Xunit

' Pure-logic tests for the UMLS local-store loader: the str_norm normalizer and
' the RRF stream parser. No database required.
Public Class UmlsLoaderUnitTests

    ' ============ UmlsMetathesaurusStore.NormalizeConcept ============

    <Theory>
    <InlineData("  Diabetes   Mellitus ", "diabetes mellitus")>
    <InlineData("HbA1c", "hba1c")>
    <InlineData("Type 2  Diabetes", "type 2 diabetes")>
    <InlineData("", "")>
    <InlineData("   ", "")>
    Public Sub NormalizeConcept_lowercases_trims_and_collapses_whitespace(input As String, expected As String)
        Assert.Equal(expected, UmlsMetathesaurusStore.NormalizeConcept(input))
    End Sub

    <Fact>
    Public Sub NormalizeConcept_is_stable_for_an_already_normalized_string()
        Dim once = UmlsMetathesaurusStore.NormalizeConcept("Gastrointestinal Bleeding")
        Assert.Equal(once, UmlsMetathesaurusStore.NormalizeConcept(once))
    End Sub

    ' ============ UmlsMetathesaurusStore.BuildOrTsQuery ============

    <Theory>
    <InlineData("10-meter walk test", "10 | meter | walk | test")>
    <InlineData("Diabetes Mellitus", "diabetes | mellitus")>
    <InlineData("131I scan", "131i | scan")>
    <InlineData("", "")>
    <InlineData("  ---  ", "")>
    Public Sub BuildOrTsQuery_joins_alphanumeric_lexemes_with_or(input As String, expected As String)
        Assert.Equal(expected, UmlsMetathesaurusStore.BuildOrTsQuery(input))
    End Sub

    ' ============ UmlsRrfReader.ReadAtoms ============

    <Fact>
    Public Sub ReadAtoms_keeps_english_unsuppressed_atoms_in_the_vocab_filter()
        Dim lines = {
            "C0011860|ENG|P|L0|PF|S0|Y|A0||||MSH|MH|D003924|Diabetes Mellitus|0|N||",
            "C0011860|FRE|P|L1|PF|S1|N|A1||||MSHFRE|MH|D003924|Diabete|0|N||",       ' non-English -> dropped
            "C0011860|ENG|S|L2|PF|S2|N|A2||||MSH|SY|D003924|DM|0|O||",                ' SUPPRESS=O -> dropped
            "C0020538|ENG|P|L3|PF|S3|Y|A3||||FOO|PT|X|Hypertension|0|N||",            ' SAB not in filter -> dropped
            "C0020615|ENG|P|L4|PF|S4|Y|A4||||SNOMEDCT_US|PT|271327008|Hypoglycemia|0|N||"
        }
        Dim path = WriteTempRrf(lines)
        Try
            Dim atoms = UmlsRrfReader.ReadAtoms(path, {"MSH", "SNOMEDCT_US"}).ToList()

            Assert.Equal(2, atoms.Count)
            Dim dm = atoms.Single(Function(a) a.Cui = "C0011860")
            Assert.Equal("Diabetes Mellitus", dm.Str)
            Assert.Equal("diabetes mellitus", dm.StrNorm)
            Assert.Equal("MSH", dm.Sab)
            Assert.True(dm.IsPref)
            Assert.Contains(atoms, Function(a) a.Cui = "C0020615" AndAlso a.Str = "Hypoglycemia")
            Assert.DoesNotContain(atoms, Function(a) a.Sab = "FOO")
        Finally
            File.Delete(path)
        End Try
    End Sub

    <Fact>
    Public Sub ReadAtoms_empty_vocab_filter_keeps_all_english_atoms()
        Dim lines = {
            "C1|ENG|P|L|PF|S|Y|A||||FOO|PT|X|Alpha|0|N||",
            "C2|ENG|P|L|PF|S|Y|A||||BAR|PT|X|Beta|0|N||"
        }
        Dim path = WriteTempRrf(lines)
        Try
            Dim atoms = UmlsRrfReader.ReadAtoms(path, Array.Empty(Of String)()).ToList()
            Assert.Equal(2, atoms.Count)
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' ============ UmlsRrfReader.ReadSemanticTypes ============

    <Fact>
    Public Sub ReadSemanticTypes_extracts_cui_tui_sty()
        Dim lines = {
            "C0011860|T047|A1.2.2.2|Disease or Syndrome|AT0|",
            "C0020615|T047|A1.2.2.2|Disease or Syndrome|AT1|"
        }
        Dim path = WriteTempRrf(lines)
        Try
            Dim rows = UmlsRrfReader.ReadSemanticTypes(path).ToList()
            Assert.Equal(2, rows.Count)
            Assert.Equal("C0011860", rows(0).Cui)
            Assert.Equal("T047", rows(0).Tui)
            Assert.Equal("Disease or Syndrome", rows(0).Sty)
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' ============ ReadRelations (MRREL) ============
    '
    ' MRREL.REL describes the relationship of the SECOND concept to the first:
    ' (CUI1, 'PAR', CUI2) means CUI2 is the parent of CUI1. 'CHD' is the inverse.
    ' Both are normalised to a single (child, parent) orientation here.
    '
    ' Getting this backwards does not error - it produces a hierarchy that rolls
    ' concepts up to MORE SPECIFIC terms, which reads as merely odd. These tests
    ' pin the mapping against a real, checkable fact.

    <Fact>
    Public Sub ReadRelations_maps_PAR_with_cui2_as_parent()
        ' C0011860 (Type 2 Diabetes) PAR C0011849 (Diabetes Mellitus)
        Dim path = WriteTempRrf({
            "C0011860|A1|SCUI|PAR|C0011849|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Dim rows = UmlsRrfReader.ReadRelations(path).ToList()
            Assert.Single(rows)
            Assert.Equal("C0011860", rows(0).ChildCui)
            Assert.Equal("C0011849", rows(0).ParentCui)
        Finally
            File.Delete(path)
        End Try
    End Sub

    <Fact>
    Public Sub ReadRelations_maps_CHD_as_the_inverse()
        ' (CUI1, 'CHD', CUI2) means CUI2 is the CHILD of CUI1.
        Dim path = WriteTempRrf({
            "C0011849|A1|SCUI|CHD|C0011860|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Dim rows = UmlsRrfReader.ReadRelations(path).ToList()
            Assert.Single(rows)
            Assert.Equal("C0011860", rows(0).ChildCui)
            Assert.Equal("C0011849", rows(0).ParentCui)
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' Only SNOMED edges. UMLS asserts no cross-source hierarchy, so mixing
    ' vocabularies produces incoherent ancestry and can introduce cycles.
    <Fact>
    Public Sub ReadRelations_skips_other_source_vocabularies()
        Dim path = WriteTempRrf({
            "C0000005|A1|SCUI|PAR|C0036775|A2|SCUI||R1||MSH|MSH|||N||",
            "C0011860|A1|SCUI|PAR|C0011849|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Dim rows = UmlsRrfReader.ReadRelations(path).ToList()
            Assert.Single(rows)
            Assert.Equal("C0011860", rows(0).ChildCui)
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' RB/RN (broader/narrower) are not the is-a hierarchy and must not be treated
    ' as one - the very first line of the real MRREL.RRF is an RB row.
    <Fact>
    Public Sub ReadRelations_skips_non_hierarchical_relationships()
        Dim path = WriteTempRrf({
            "C0000005|A13433185|SCUI|RB|C0036775|A7466261|SCUI||R86000559||SNOMEDCT_US|SNOMEDCT_US|||N||",
            "C0000005|A1|SCUI|SY|C0036775|A2|SCUI||R2||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Assert.Empty(UmlsRrfReader.ReadRelations(path).ToList())
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' A concept is not its own parent. Self-edges would make the closure loop.
    <Fact>
    Public Sub ReadRelations_skips_self_relationships()
        Dim path = WriteTempRrf({
            "C0011860|A1|SCUI|PAR|C0011860|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Assert.Empty(UmlsRrfReader.ReadRelations(path).ToList())
        Finally
            File.Delete(path)
        End Try
    End Sub

    <Fact>
    Public Sub ReadRelations_skips_malformed_and_blank_rows()
        Dim path = WriteTempRrf({
            "too|few|fields",
            "|A1|SCUI|PAR|C0011849|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||",
            "C0011860|A1|SCUI|PAR||A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Assert.Empty(UmlsRrfReader.ReadRelations(path).ToList())
        Finally
            File.Delete(path)
        End Try
    End Sub

    Private Shared Function WriteTempRrf(lines As IEnumerable(Of String)) As String
        Dim tempPath = Path.GetTempFileName()
        File.WriteAllLines(tempPath, lines)
        Return tempPath
    End Function

End Class

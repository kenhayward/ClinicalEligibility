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
End Class

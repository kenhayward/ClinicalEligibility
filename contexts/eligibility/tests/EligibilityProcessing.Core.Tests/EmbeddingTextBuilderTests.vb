Imports EligibilityProcessing.Core
Imports Xunit

Public Class EmbeddingTextBuilderTests

    <Fact>
    Public Sub Build_includes_populated_fields()
        Dim input = New StudyEmbeddingInput(
                nctId:="NCT1",
                briefTitle:="A trial of metformin in diabetes",
                officialTitle:="A randomised trial of metformin",
                briefSummary:="Studies metformin for glycaemic control.",
                conditions:={"Type 2 Diabetes", "Obesity"},
                interventions:={New Intervention("Drug", "Metformin")})

        Dim text = EmbeddingTextBuilder.Build(input)

        Assert.Contains("A trial of metformin in diabetes", text)
        Assert.Contains("A randomised trial of metformin", text)
        Assert.Contains("glycaemic control", text)
        Assert.Contains("Type 2 Diabetes, Obesity", text)
        Assert.Contains("Drug Metformin", text)
    End Sub

    <Fact>
    Public Sub Build_skips_official_title_when_same_as_brief_title()
        Dim input = New StudyEmbeddingInput(
                nctId:="NCT1",
                briefTitle:="Metformin study",
                officialTitle:="Metformin study",
                briefSummary:="",
                conditions:=Array.Empty(Of String)(),
                interventions:=Array.Empty(Of Intervention)())

        Dim text = EmbeddingTextBuilder.Build(input)

        Assert.Contains("Title: Metformin study", text)
        Assert.DoesNotContain("Official title", text)
    End Sub

    <Fact>
    Public Sub Build_skips_empty_parts()
        Dim input = New StudyEmbeddingInput(
                nctId:="NCT1",
                briefTitle:="Only a title",
                officialTitle:="",
                briefSummary:="",
                conditions:=Array.Empty(Of String)(),
                interventions:=Array.Empty(Of Intervention)())

        Dim text = EmbeddingTextBuilder.Build(input)

        Assert.Contains("Title: Only a title", text)
        Assert.DoesNotContain("Summary", text)
        Assert.DoesNotContain("Conditions", text)
        Assert.DoesNotContain("Interventions", text)
    End Sub

    <Fact>
    Public Sub Build_returns_empty_string_for_fully_empty_input()
        Dim input = New StudyEmbeddingInput(
                nctId:="",
                briefTitle:="",
                officialTitle:="",
                briefSummary:="",
                conditions:=Array.Empty(Of String)(),
                interventions:=Array.Empty(Of Intervention)())

        Assert.Equal("", EmbeddingTextBuilder.Build(input))
    End Sub

End Class

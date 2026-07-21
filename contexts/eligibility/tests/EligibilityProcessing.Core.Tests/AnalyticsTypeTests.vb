Imports System.Collections.Generic
Imports EligibilityProcessing.Core
Imports Xunit

Public Class AnalyticsTypeTests

    <Fact>
    Public Sub Null_strings_coalesce_to_empty_not_Nothing()
        Dim row As New ConceptLiftRow(Nothing, Nothing, 1, 1, 1.0, 1.0, 0.0, 1.0, False)
        Assert.Equal("", row.ConceptCode)
        Assert.Equal("", row.PrefName)
    End Sub

    <Fact>
    Public Sub Cohort_defaults_value_to_empty_and_keeps_the_kind()
        Dim c As New AnalyticsCohort(AnalyticsCohortKind.Phase, Nothing, True)
        Assert.Equal(AnalyticsCohortKind.Phase, c.Kind)
        Assert.Equal("", c.Value)
        Assert.True(c.IncludeDescendants)
    End Sub

    <Fact>
    Public Sub ConceptSummary_lists_default_to_empty_not_Nothing()
        ' The views enumerate these directly; a Nothing would throw at render.
        Dim s As New ConceptSummary("C1", "Name", "SRC", "Sty", 0, 0, 0, 0, Nothing, Nothing)
        Assert.NotNull(s.ByPhase)
        Assert.Empty(s.ByPhase)
        Assert.NotNull(s.ExampleCriteria)
        Assert.Empty(s.ExampleCriteria)
    End Sub

    <Fact>
    Public Sub TrendPoint_carries_its_denominator_and_partial_flag()
        Dim p As New TrendPoint(2026, 26498, 450, 1.7, True)
        Assert.Equal(2026, p.Year)
        Assert.Equal(26498, p.StudiesThatYear)
        Assert.True(p.IsPartial)
    End Sub
End Class

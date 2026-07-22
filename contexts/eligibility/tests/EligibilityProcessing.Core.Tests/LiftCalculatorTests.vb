Imports System.Collections.Generic
Imports System.Linq
Imports EligibilityProcessing.Core
Imports Xunit

Public Class LiftCalculatorTests

    Private Shared Function Counts(ParamArray pairs As (String, Integer)()) As IReadOnlyList(Of ConceptCount)
        Return pairs.Select(Function(p) New ConceptCount(p.Item1, p.Item2)).ToList()
    End Function

    Private Shared Function Names(ParamArray pairs As (String, String)()) As IReadOnlyDictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)
        For Each p In pairs
            d(p.Item1) = p.Item2
        Next
        Return d
    End Function

    <Fact>
    Public Sub Concept_at_exactly_corpus_rate_scores_lift_one_and_zero_excess()
        Dim rows = LiftCalculator.Build(
                cohortCounts:=Counts(("C1", 10)),
                corpusCounts:=Counts(("C1", 100)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Dim r = rows.Single()
        Assert.Equal(1.0, r.Lift, 3)
        Assert.Equal(0.0, r.ExcessPp, 3)
        Assert.Equal(10.0, r.PctCohort, 3)
        Assert.Equal(10.0, r.PctCorpus, 3)
    End Sub

    <Fact>
    Public Sub Concept_rarer_in_cohort_than_corpus_yields_negative_excess()
        Dim rows = LiftCalculator.Build(
                Counts(("C1", 1)), Counts(("C1", 500)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Dim r = rows.Single()
        Assert.True(r.ExcessPp < 0, $"expected negative excess, got {r.ExcessPp}")
        Assert.True(r.Lift < 1.0)
    End Sub

    ' THE test for the spec's central decision. Lift saturates at
    ' corpusSize/cohortSize and cannot rank concepts that reach the ceiling.
    ' If anyone reverts the sort key to lift, this fails.
    <Fact>
    Public Sub Rows_are_ordered_by_excess_not_by_lift()
        ' BIG: 50% of cohort vs 10% of corpus -> excess 40pp, lift 5
        ' TINY: 2% of cohort vs 0.1% of corpus -> excess 1.9pp, lift 20
        Dim rows = LiftCalculator.Build(
                Counts(("BIG", 500), ("TINY", 20)),
                Counts(("BIG", 1000), ("TINY", 10)),
                cohortSize:=1000, corpusSize:=10000,
                prefNames:=Names(("BIG", "Big"), ("TINY", "Tiny")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Equal("BIG", rows(0).ConceptCode)
        Assert.Equal("TINY", rows(1).ConceptCode)
        ' Confirm the orderings genuinely disagree, so the test is meaningful.
        Assert.True(rows(1).Lift > rows(0).Lift,
                    "fixture is wrong: lift ordering must disagree with excess ordering")
    End Sub

    <Fact>
    Public Sub Minimum_support_is_inclusive_at_the_threshold()
        Dim rows = LiftCalculator.Build(
                Counts(("KEEP", 10), ("DROP", 9)),
                Counts(("KEEP", 10), ("DROP", 9)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("KEEP", "Keep"), ("DROP", "Drop")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=LiftCalculator.DefaultMinimumSupport)

        Assert.Single(rows)
        Assert.Equal("KEEP", rows.Single().ConceptCode)
    End Sub

    <Fact>
    Public Sub Default_minimum_support_constant_is_ten()
        Assert.Equal(10, LiftCalculator.DefaultMinimumSupport)
    End Sub

    <Fact>
    Public Sub Zero_corpus_count_does_not_divide_by_zero()
        Dim rows = LiftCalculator.Build(
                Counts(("C1", 5)), Counts(),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Dim r = rows.Single()
        Assert.False(Double.IsNaN(r.Lift))
        Assert.False(Double.IsInfinity(r.Lift))
    End Sub

    <Fact>
    Public Sub Defining_codes_are_flagged_but_not_removed()
        Dim rows = LiftCalculator.Build(
                Counts(("DEF", 50), ("OTHER", 20)),
                Counts(("DEF", 60), ("OTHER", 200)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("DEF", "Definer"), ("OTHER", "Other")),
                definingCodes:=New HashSet(Of String)({"DEF"}),
                minimumSupport:=1)

        Assert.Equal(2, rows.Count)
        Assert.True(rows.Single(Function(r) r.ConceptCode = "DEF").DefinesCohort)
        Assert.False(rows.Single(Function(r) r.ConceptCode = "OTHER").DefinesCohort)
    End Sub

    <Fact>
    Public Sub Empty_cohort_counts_return_empty()
        Dim rows = LiftCalculator.Build(
                Counts(), Counts(("C1", 10)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Empty(rows)
    End Sub

    <Fact>
    Public Sub Zero_cohort_size_returns_empty_rather_than_dividing_by_zero()
        Dim rows = LiftCalculator.Build(
                Counts(("C1", 10)),
                Counts(("C1", 100)),
                cohortSize:=0, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Empty(rows)
    End Sub

    <Fact>
    Public Sub Zero_corpus_size_returns_empty_rather_than_dividing_by_zero()
        Dim rows = LiftCalculator.Build(
                Counts(("C1", 10)),
                Counts(("C1", 100)),
                cohortSize:=100, corpusSize:=0,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Empty(rows)
    End Sub

    <Fact>
    Public Sub Missing_pref_name_falls_back_to_the_concept_code()
        ' Never falls back to extracted concept text - that is the point.
        Dim rows = LiftCalculator.Build(
                Counts(("C9", 10)), Counts(("C9", 10)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Equal("C9", rows.Single().PrefName)
    End Sub
End Class

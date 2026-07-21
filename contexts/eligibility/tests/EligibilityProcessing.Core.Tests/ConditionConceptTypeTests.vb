Imports EligibilityProcessing.Core
Imports Xunit

Public Class ConditionConceptTypeTests

    <Fact>
    Public Sub Tier_constants_match_the_values_persisted_in_match_tier()
        ' These strings are written to public.condition_concept.match_tier and
        ' read back by the analytics queries in sub-project 2. Changing one is a
        ' data migration, not a rename.
        Assert.Equal("exact", ConditionMatchTier.Exact)
        Assert.Equal("exact_ambiguous", ConditionMatchTier.ExactAmbiguous)
        Assert.Equal("fuzzy", ConditionMatchTier.Fuzzy)
        Assert.Equal("unresolved", ConditionMatchTier.Unresolved)
    End Sub

    <Fact>
    Public Sub Unresolved_resolution_is_empty_and_scores_zero()
        Dim u = ConditionResolution.Unresolved
        Assert.False(u.IsResolved)
        Assert.Equal("", u.ConceptCode)
        Assert.Equal("", u.UmlsName)
        Assert.Equal(ConditionMatchTier.Unresolved, u.Tier)
        Assert.Equal(0.0, u.Score)
    End Sub

    <Fact>
    Public Sub IsResolved_is_driven_by_concept_code_presence()
        Assert.True(New ConditionResolution("C0038454", "CVA", ConditionMatchTier.Exact, 1.0).IsResolved)
        Assert.False(New ConditionResolution("", "CVA", ConditionMatchTier.Exact, 1.0).IsResolved)
    End Sub

    <Fact>
    Public Sub New_entry_defaults_to_unresolved()
        ' EnsureForStudyAsync and the job both construct entries with object
        ' initialisers, so an un-set MatchTier must never be Nothing - the column
        ' is NOT NULL.
        Dim e As New ConditionConceptEntry()
        Assert.Equal(ConditionMatchTier.Unresolved, e.MatchTier)
        Assert.Equal("", e.ConceptCode)
        Assert.Equal("", e.UmlsName)
        Assert.Equal(0, e.StudyCount)
    End Sub
End Class

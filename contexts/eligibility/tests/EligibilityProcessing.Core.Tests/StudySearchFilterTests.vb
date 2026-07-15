Imports EligibilityProcessing.Core
Imports Xunit

' Unit tests for the StudySearchFilter value object. Verifies the
' blank-to-Nothing normalization and IsEmpty contract that
' SearchStudyDetailsAsync relies on to skip the unconditional dump.

Public Class StudySearchFilterTests

    <Fact>
    Public Sub Default_construction_is_empty()
        Dim f = New StudySearchFilter()
        Assert.True(f.IsEmpty)
        Assert.Null(f.NctId)
        Assert.Null(f.BriefTitle)
        Assert.Null(f.Condition)
    End Sub

    <Fact>
    Public Sub Whitespace_and_blank_inputs_normalize_to_Nothing()
        Dim f = New StudySearchFilter(
                nctId:="   ",
                briefTitle:="",
                officialTitle:=Nothing,
                source:=vbTab,
                condition:="  ")
        Assert.True(f.IsEmpty)
        Assert.Null(f.NctId)
        Assert.Null(f.BriefTitle)
        Assert.Null(f.OfficialTitle)
        Assert.Null(f.Source)
        Assert.Null(f.Condition)
    End Sub

    <Fact>
    Public Sub Values_are_trimmed_and_preserved()
        Dim f = New StudySearchFilter(
                nctId:="  nct123  ",
                briefTitle:="Diabetes",
                phase:="Phase 3",
                condition:=" Hypertension ")
        Assert.False(f.IsEmpty)
        Assert.Equal("nct123", f.NctId)
        Assert.Equal("Diabetes", f.BriefTitle)
        Assert.Equal("Phase 3", f.Phase)
        Assert.Equal("Hypertension", f.Condition)
    End Sub

    <Fact>
    Public Sub IsEmpty_is_false_when_any_single_field_set()
        Assert.False(New StudySearchFilter(nctId:="x").IsEmpty)
        Assert.False(New StudySearchFilter(briefTitle:="x").IsEmpty)
        Assert.False(New StudySearchFilter(officialTitle:="x").IsEmpty)
        Assert.False(New StudySearchFilter(overallStatus:="x").IsEmpty)
        Assert.False(New StudySearchFilter(phase:="x").IsEmpty)
        Assert.False(New StudySearchFilter(studyType:="x").IsEmpty)
        Assert.False(New StudySearchFilter(source:="x").IsEmpty)
        Assert.False(New StudySearchFilter(briefSummary:="x").IsEmpty)
        Assert.False(New StudySearchFilter(condition:="x").IsEmpty)
        Assert.False(New StudySearchFilter(gender:="x").IsEmpty)
        Assert.False(New StudySearchFilter(healthyVolunteers:="x").IsEmpty)
    End Sub

End Class

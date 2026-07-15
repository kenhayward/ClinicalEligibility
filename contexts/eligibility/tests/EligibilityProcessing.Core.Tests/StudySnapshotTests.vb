Imports System
Imports EligibilityProcessing.Core
Imports Xunit

' Unit tests for the StudySnapshot holder — constructor guards and the
' NctId passthrough to the composed StudyDetails.

Public Class StudySnapshotTests

    Private Shared Function MakeDetails(nctId As String) As StudyDetails
        Return New StudyDetails(
                nctId:=nctId, briefTitle:="Title", officialTitle:="", overallStatus:="",
                phase:="", studyType:="", startDate:=Nothing, completionDate:=Nothing,
                primaryCompletionDate:=Nothing, enrollment:=Nothing, enrollmentType:="",
                source:="", whyStopped:="", briefSummary:="",
                conditions:=Nothing, interventions:=Nothing)
    End Function

    Private Shared Function MakeEligibility(nctId As String) As SourceEligibilityDetails
        Return New SourceEligibilityDetails(
                nctId:=nctId, criteria:="Criteria text", gender:="", minimumAge:="",
                maximumAge:="", healthyVolunteers:="", samplingMethod:="", population:="",
                adult:=Nothing, child:=Nothing, olderAdult:=Nothing)
    End Function

    <Fact>
    Public Sub Constructor_stores_components_and_NctId_delegates_to_Details()
        Dim captured = New DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero)
        Dim snapshot = New StudySnapshot(
                MakeDetails("NCT00000001"), MakeEligibility("NCT00000001"), captured)

        Assert.Equal("NCT00000001", snapshot.NctId)
        Assert.Equal(captured, snapshot.CapturedAt)
        Assert.Equal("Title", snapshot.Details.BriefTitle)
        Assert.Equal("Criteria text", snapshot.Eligibility.Criteria)
    End Sub

    <Fact>
    Public Sub Constructor_rejects_null_details()
        Assert.Throws(Of ArgumentNullException)(
                Function() New StudySnapshot(Nothing, MakeEligibility("NCT1"), DateTimeOffset.UtcNow))
    End Sub

    <Fact>
    Public Sub Constructor_rejects_null_eligibility()
        Assert.Throws(Of ArgumentNullException)(
                Function() New StudySnapshot(MakeDetails("NCT1"), Nothing, DateTimeOffset.UtcNow))
    End Sub

End Class

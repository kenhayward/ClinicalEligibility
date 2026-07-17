Imports System.Collections.Generic
Imports EligibilityProcessing.Core
Imports Xunit

' Pure-logic tests for DashboardMetrics' derived figures. The remaining-trials
' number is an approximation with a documented failure mode, so the cases that
' matter are the ones pinning down what it does at the edges.
Public Class DashboardMetricsTests

    Private Shared Function Make(
            Optional studiesAttempted As Long = 0,
            Optional sourceTrialTotal As Long? = Nothing) As DashboardMetrics
        Return New DashboardMetrics(
                eligibilityRowCount:=0, studiesSuccessful:=0, studiesFailedLatest:=0,
                resolutionRate:=0.0, promptTokens:=0, completionTokens:=0,
                failuresByStatus:=New Dictionary(Of String, Long)(),
                studiesWithoutEmbeddings:=0, parseEmpty:=0,
                studiesAttempted:=studiesAttempted,
                sourceTrialTotal:=sourceTrialTotal)
    End Function

    <Fact>
    Public Sub TrialsRemaining_is_source_total_minus_attempted()
        ' The production shape: 585,855 selectable minus 282,247 attempted.
        Dim m = Make(studiesAttempted:=282247, sourceTrialTotal:=585855)
        Assert.Equal(303608L, m.TrialsRemaining)
    End Sub

    ' No AACT source (the seeded quickstart has no ctgov schema at all) must yield
    ' Nothing, not 0 - the view keys off HasValue to omit the figure entirely
    ' rather than claim a backlog of zero.
    <Fact>
    Public Sub TrialsRemaining_is_Nothing_when_source_total_is_unknown()
        Dim m = Make(studiesAttempted:=1000, sourceTrialTotal:=Nothing)
        Assert.False(m.TrialsRemaining.HasValue)
    End Sub

    ' The two counts are independent, so attempted can exceed the selectable set
    ' (a trial whose criteria were later edited below the 50-char floor is still
    ' attempted but no longer selectable). Must clamp, never render negative.
    <Fact>
    Public Sub TrialsRemaining_clamps_at_zero_when_attempted_exceeds_source_total()
        Dim m = Make(studiesAttempted:=600000, sourceTrialTotal:=585855)
        Assert.Equal(0L, m.TrialsRemaining)
    End Sub

    <Fact>
    Public Sub TrialsRemaining_is_zero_when_everything_is_attempted()
        Dim m = Make(studiesAttempted:=585855, sourceTrialTotal:=585855)
        Assert.Equal(0L, m.TrialsRemaining)
    End Sub

    <Fact>
    Public Sub TrialsRemaining_is_full_total_when_nothing_attempted()
        Dim m = Make(studiesAttempted:=0, sourceTrialTotal:=585855)
        Assert.Equal(585855L, m.TrialsRemaining)
    End Sub

    ' Empty must stay inert: no source known, so no backlog claim.
    <Fact>
    Public Sub Empty_reports_no_remaining_and_no_attempted()
        Assert.False(DashboardMetrics.Empty.TrialsRemaining.HasValue)
        Assert.Equal(0L, DashboardMetrics.Empty.StudiesAttempted)
        Assert.False(DashboardMetrics.Empty.SourceTrialTotal.HasValue)
    End Sub

End Class

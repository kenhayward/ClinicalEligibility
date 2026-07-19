Imports System.Linq
Imports EligibilityProcessing.Core
Imports Xunit

' Unit tests for RunStatus: the run-level status vocabulary and the pure
' validator behind the dashboard's "Resolve" action. Validation lives in Core
' rather than the controller so it is testable without faking IPostgresGateway.

Public Class RunStatusTests

    <Fact>
    Public Sub Constants_have_their_persisted_values()
        Assert.Equal("running", RunStatus.Running)
        Assert.Equal("success", RunStatus.Success)
        Assert.Equal("failed", RunStatus.Failed)
        Assert.Equal("cancelled", RunStatus.Cancelled)
        Assert.Equal("interrupted", RunStatus.Interrupted)
    End Sub

    <Fact>
    Public Sub ManualResolvable_is_exactly_the_three_terminal_targets()
        Assert.Equal(New String() {"failed", "cancelled", "interrupted"},
                     RunStatus.ManualResolvable.ToArray())
    End Sub

    ' The load-bearing constraint: a completed run must never be rewritable
    ' through this path, and "running" is not a legal *target*.
    <Theory>
    <InlineData("running")>
    <InlineData("success")>
    Public Sub ValidateResolution_rejects_non_target_statuses(status As String)
        Dim result = RunStatus.ValidateResolution(status, "server rebooted")
        Assert.False(result.IsValid)
        Assert.Contains("status", result.ErrorMessage, StringComparison.OrdinalIgnoreCase)
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData(Nothing)>
    <InlineData("archived")>
    Public Sub ValidateResolution_rejects_unknown_status(status As String)
        Dim result = RunStatus.ValidateResolution(status, "server rebooted")
        Assert.False(result.IsValid)
    End Sub

    <Theory>
    <InlineData("FAILED")>
    <InlineData("  interrupted  ")>
    Public Sub ValidateResolution_normalizes_case_and_whitespace(status As String)
        Dim result = RunStatus.ValidateResolution(status, "server rebooted")
        Assert.True(result.IsValid)
        Assert.Equal(status.Trim().ToLowerInvariant(), result.Status)
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData(Nothing)>
    <InlineData("   ")>
    Public Sub ValidateResolution_requires_a_reason(reason As String)
        Dim result = RunStatus.ValidateResolution("failed", reason)
        Assert.False(result.IsValid)
        Assert.Contains("reason", result.ErrorMessage, StringComparison.OrdinalIgnoreCase)
    End Sub

    <Fact>
    Public Sub ValidateResolution_trims_the_reason()
        Dim result = RunStatus.ValidateResolution("failed", "  server rebooted  ")
        Assert.True(result.IsValid)
        Assert.Equal("server rebooted", result.Reason)
    End Sub

    ' error_summary is read in a table cell; an unbounded paste would wreck it.
    <Fact>
    Public Sub ValidateResolution_rejects_an_over_long_reason()
        Dim result = RunStatus.ValidateResolution("failed", New String("x"c, RunStatus.MaxReasonLength + 1))
        Assert.False(result.IsValid)
        Assert.Contains("500", result.ErrorMessage)
    End Sub

    <Fact>
    Public Sub ValidateResolution_accepts_a_reason_at_the_limit()
        Dim result = RunStatus.ValidateResolution("failed", New String("x"c, RunStatus.MaxReasonLength))
        Assert.True(result.IsValid)
    End Sub

    <Fact>
    Public Sub Valid_result_has_no_error_message()
        Dim result = RunStatus.ValidateResolution("interrupted", "server rebooted")
        Assert.True(result.IsValid)
        Assert.Equal("", result.ErrorMessage)
        Assert.Equal("interrupted", result.Status)
    End Sub

End Class

using System;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Models;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// The one place the run card's derived fields are computed, so both the initial
/// Razor render and the client-side refresh render the same values. Tested
/// directly because the arithmetic (ring fraction, ETA, time-per-study) never
/// runs through the web harness - it points at a placeholder DB, so every
/// controller request lands on the error path.
/// </summary>
public class RunCardViewTests
{
    private static RunMetrics Run(
        int studyCount, int studiesProcessed, string status = "running",
        DateTimeOffset? endedAt = null, double resolutionRate = 0.9, int rowsPersisted = 100) =>
        new(
            runId: Guid.NewGuid(),
            startedAt: new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero),
            endedAt: endedAt,
            triggerSource: "form",
            studyCount: studyCount,
            studiesProcessed: studiesProcessed,
            rowsPersisted: rowsPersisted,
            resolutionRate: resolutionRate,
            status: status,
            errorSummary: "");

    [Fact]
    public void Null_run_yields_null_view()
    {
        Assert.Null(RunCardView.From(null));
    }

    // The ring is meaningless for a single-trial run (only ever 0% or 100%), so
    // it is hidden and the "Studies processed" stat shows instead.
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(50, true)]
    public void Ring_is_shown_only_for_multi_trial_runs(int studyCount, bool expectedShowRing)
    {
        var v = RunCardView.From(Run(studyCount, studiesProcessed: 1))!;
        Assert.Equal(expectedShowRing, v.ShowRing);
    }

    [Fact]
    public void Progress_is_processed_over_planned_clamped()
    {
        var v = RunCardView.From(Run(studyCount: 10, studiesProcessed: 4))!;
        Assert.Equal(0.4, v.ProgressFraction, 3);
        Assert.Equal("40 %", v.ProgressPctText);
        Assert.Equal("4 / 10", v.StudiesProcessedText);
    }

    // A 0-study run must not divide by zero, and an overshoot must not draw a
    // ring past full.
    [Fact]
    public void Progress_guards_zero_denominator_and_overshoot()
    {
        Assert.Equal(0d, RunCardView.From(Run(studyCount: 0, studiesProcessed: 0))!.ProgressFraction);
        Assert.Equal(1d, RunCardView.From(Run(studyCount: 10, studiesProcessed: 12))!.ProgressFraction, 3);
    }

    [Fact]
    public void Running_status_sets_the_pulse_and_the_info_badge()
    {
        var v = RunCardView.From(Run(10, 4, status: "running"))!;
        Assert.True(v.IsRunning);
        Assert.Contains("bg-info", v.StatusBadgeClass);
    }

    [Fact]
    public void Terminal_status_does_not_pulse()
    {
        var v = RunCardView.From(Run(10, 10, status: "success", endedAt: new DateTimeOffset(2026, 7, 17, 10, 9, 0, TimeSpan.Zero)))!;
        Assert.False(v.IsRunning);
        Assert.Equal("bg-success", v.StatusBadgeClass);
    }

    // A finished run's ETA is its actual end; nothing to project.
    [Fact]
    public void Estimated_finish_of_a_finished_run_is_its_end_time()
    {
        var ended = new DateTimeOffset(2026, 7, 17, 10, 9, 0, TimeSpan.Zero);
        var v = RunCardView.From(Run(50, 50, status: "success", endedAt: ended))!;
        Assert.Equal(ended.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture), v.EstimatedFinishUtc);
    }

    // Nothing processed yet -> no basis for an estimate.
    [Fact]
    public void Estimated_finish_is_null_when_nothing_processed()
    {
        var v = RunCardView.From(Run(studyCount: 50, studiesProcessed: 0))!;
        Assert.Null(v.EstimatedFinishUtc);
        Assert.Equal("-", v.TimePerStudyText);
    }

    // Invariant formatting, matching the rest of the payload: "90.0 %" with a
    // space on the invariant culture the container runs, not a dev box's "90.0%".
    [Fact]
    public void Text_fields_are_invariant_formatted()
    {
        var v = RunCardView.From(Run(10, 4, resolutionRate: 0.895, rowsPersisted: 1181))!;
        Assert.Equal("89.5 %", v.ResolutionRateText);
        Assert.Equal("1,181", v.RowsPersistedText);
    }
}

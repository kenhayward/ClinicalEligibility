using System.Globalization;
using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// The "Most recent run" card's fields, computed once. Both the initial Razor
/// render (_DashboardRunCard) and the client-side refresh (hydrate() in Index)
/// consume this - Razor reads the properties, the client reads the same object
/// serialized into the /Home/Metrics payload. Neither side computes anything, so
/// the card cannot drift between its first paint and a Reload.
/// <para>
/// This exists because the card used to be server-rendered only: Reload and the
/// refresh-on-completion updated every other card but left this one frozen at
/// page-load state until a full browser reload - and this is the card whose
/// numbers move most during a run.
/// </para>
/// <para>
/// Text fields are formatted with <see cref="CultureInfo.InvariantCulture"/>, to
/// match the rest of the metrics payload and to render identically on the
/// invariant-culture Alpine container and a developer's machine.
/// </para>
/// </summary>
public sealed class RunCardView
{
    public string RunId { get; init; } = "";
    public string Status { get; init; } = "";

    /// <summary>Bootstrap badge classes for the status pill.</summary>
    public string StatusBadgeClass { get; init; } = "";

    /// <summary>Whether to pulse the RUNNING dot - true only while in flight.</summary>
    public bool IsRunning { get; init; }

    /// <summary>ISO-8601 UTC for the .local-datetime script to localize, so
    /// Started and Est. finish render in the same viewer-local zone and format.
    /// StartedText is the pre-JS fallback only.</summary>
    public string StartedUtc { get; init; } = "";
    public string StartedText { get; init; } = "";
    public string TriggerSource { get; init; } = "";
    public string RowsPersistedText { get; init; } = "";
    public string ResolutionRateText { get; init; } = "";
    public string TimePerStudyText { get; init; } = "";

    /// <summary>ISO-8601 UTC for the .local-datetime script to localize, or null
    /// when no estimate is possible (nothing processed yet).</summary>
    public string? EstimatedFinishUtc { get; init; }

    public int StudiesProcessed { get; init; }
    public int StudyCount { get; init; }

    /// <summary>"X / Y" - shown as its own stat only when the ring is hidden.</summary>
    public string StudiesProcessedText { get; init; } = "";

    /// <summary>The ring carries no information for a single-trial run (only ever
    /// 0% or 100%) and reads as broken, so it is hidden below 2 planned studies;
    /// the "Studies processed" stat shows instead.</summary>
    public bool ShowRing { get; init; }

    /// <summary>0..1, clamped, denominator-guarded.</summary>
    public double ProgressFraction { get; init; }

    /// <summary>Whole-percent progress, floored (not rounded). 4976/5000 is 99,
    /// not 100 - the ring must not read complete until the run actually is.
    /// Drives both the centre label and the ring sweep.</summary>
    public int ProgressPct { get; init; }

    /// <summary>The ring centre label, e.g. "40 %".</summary>
    public string ProgressPctText { get; init; } = "";

    public static RunCardView? From(RunMetrics? run)
    {
        if (run is null)
        {
            return null;
        }

        var inv = CultureInfo.InvariantCulture;
        var elapsed = (run.EndedAt ?? DateTimeOffset.UtcNow) - run.StartedAt;

        var timePerStudy = run.StudiesProcessed > 0
            ? FormatMmSs(elapsed / run.StudiesProcessed)
            : "-";

        DateTimeOffset? estimatedFinish =
            run.EndedAt
            ?? (run.StudiesProcessed > 0
                ? run.StartedAt + (elapsed / run.StudiesProcessed) * run.StudyCount
                : (DateTimeOffset?)null);

        var pct = run.StudyCount > 0
            ? Math.Clamp((double)run.StudiesProcessed / run.StudyCount, 0d, 1d)
            : 0d;
        // Floor, not round: at 4976/5000 (99.52%) rounding would show 100% while
        // the run is still going. Only a genuinely complete run reads 100%.
        var pctWhole = (int)Math.Floor(pct * 100d);

        return new RunCardView
        {
            RunId = run.RunId.ToString(),
            Status = run.Status,
            StatusBadgeClass = run.Status switch
            {
                "success" => "bg-success",
                "failed" => "bg-danger",
                "running" => "bg-info text-dark",
                "interrupted" => "bg-warning text-dark",
                _ => "bg-secondary"
            },
            IsRunning = run.Status == "running",
            StartedUtc = run.StartedAt.UtcDateTime.ToString("o", inv),
            StartedText = run.StartedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", inv),
            TriggerSource = run.TriggerSource,
            RowsPersistedText = run.RowsPersisted.ToString("N0", inv),
            ResolutionRateText = run.ResolutionRate.ToString("P1", inv),
            TimePerStudyText = timePerStudy,
            EstimatedFinishUtc = estimatedFinish?.UtcDateTime.ToString("o", inv),
            StudiesProcessed = run.StudiesProcessed,
            StudyCount = run.StudyCount,
            StudiesProcessedText = $"{run.StudiesProcessed} / {run.StudyCount}",
            ShowRing = run.StudyCount > 1,
            ProgressFraction = pct,
            ProgressPct = pctWhole,
            ProgressPctText = pctWhole.ToString(inv) + " %"
        };
    }

    // Duration as mm:ss.f - tenths of a second, because at a 3-4s average per
    // study whole seconds lose the resolution that matters. TotalMinutes (not
    // Minutes) so a >1h pace still reads correctly.
    private static string FormatMmSs(TimeSpan ts) =>
        $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100:D1}";
}

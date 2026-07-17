using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Shape backing <c>/runs</c>. A page of runs from <c>public.eligibility_run</c>,
/// newest first, with the total so the view can render "showing X-Y of N" and
/// page controls.
/// </summary>
public sealed class RunsViewModel
{
    public IReadOnlyList<RunMetrics> Runs { get; init; } = Array.Empty<RunMetrics>();

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;
    public int PageSize { get; init; }

    /// <summary>Total runs in the table, across all pages.</summary>
    public long TotalRuns { get; init; }

    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>1-based index of the first run shown (0 when the page is empty).</summary>
    public long FirstShown => Runs.Count == 0 ? 0 : (long)(Page - 1) * PageSize + 1;

    /// <summary>1-based index of the last run shown.</summary>
    public long LastShown => (long)(Page - 1) * PageSize + Runs.Count;

    public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Max(1, Math.Ceiling(TotalRuns / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Shape backing <c>/runs</c>. Holds the most recent N runs from
/// <c>public.eligibility_run</c>. The N is currently fixed (the page is
/// "recent history"; deep pagination is a follow-up slice).
/// </summary>
public sealed class RunsViewModel
{
    public IReadOnlyList<RunMetrics> Runs { get; init; } = Array.Empty<RunMetrics>();
    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Shape backing <c>/</c> (Home/Index). Headline counters + most-recent-run
/// summary. Any field can be null when the gateway call fails — the view
/// renders an inline error rather than returning a 500 so the dashboard
/// stays usable while the DB recovers.
///
/// The watermark display was removed once mixed-direction processing
/// (Forward + Recent) was introduced: `MAX(nct_id)` was no longer a
/// meaningful "how far have we got" signal because it could jump to the
/// recent NCT_ID range and back.
/// </summary>
public sealed class DashboardViewModel
{
    public DashboardMetrics? Metrics { get; init; }
    public RunMetrics? MostRecentRun { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>What currently holds the shared RunGate - "batch" / "normalize-umls"
    /// / "embed-studies" - or null when idle. A maintenance tool job (Tools tab) is
    /// mutually exclusive with the pipeline, so the trigger buttons disable while one
    /// runs even though it isn't an eligibility_run row.</summary>
    public string? BusyActivity { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backing model for the Analytics tab's distinctiveness (lift) view
/// (<c>/Analytics/Index</c>). A cohort is defined by <see cref="Kind"/> +
/// <see cref="Value"/> (optionally widened to descendants); <see cref="Rows"/>
/// holds the ranked <see cref="ConceptLiftRow"/> list produced by
/// <c>LiftCalculator.Build</c>, already sorted by excess percentage points.
/// </summary>
public sealed class AnalyticsLiftViewModel
{
    /// <summary>The raw <see cref="AnalyticsCohortKind"/> name as submitted/resolved
    /// (defaults to "Concept" when the query string carries none or an
    /// unrecognised value - see AnalyticsController.ParseCohortKind).</summary>
    public string Kind { get; init; } = nameof(AnalyticsCohortKind.Concept);

    /// <summary>The cohort-defining value (a concept code, a phase, or a year).
    /// Empty on the initial, not-yet-submitted form.</summary>
    public string Value { get; init; } = "";

    public bool IncludeDescendants { get; init; }

    public int MinimumSupport { get; init; } = LiftCalculator.DefaultMinimumSupport;

    public IReadOnlyList<ConceptLiftRow> Rows { get; init; } = Array.Empty<ConceptLiftRow>();

    public int CohortSize { get; init; }

    public int CorpusSize { get; init; }

    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True once a cohort value has been submitted - distinguishes
    /// the empty form from "the query ran and found nothing".</summary>
    public bool HasSearched => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// One concept's trend line: its preferred name plus the per-year points
/// returned by <c>IAnalyticsGateway.GetTrendAsync</c>.
/// </summary>
public sealed class TrendSeries
{
    public string ConceptCode { get; init; } = "";

    /// <summary>From umls.concept.pref_name; falls back to the code itself
    /// when the concept is unknown, so the series still labels sensibly.</summary>
    public string PrefName { get; init; } = "";

    public IReadOnlyList<TrendPoint> Points { get; init; } = Array.Empty<TrendPoint>();
}

/// <summary>
/// Backing model for the Analytics tab's trend view (<c>/Analytics/Trend</c>).
/// Plots up to five concepts' prevalence over time, always as a percentage of
/// that year's processed studies - never a raw count, since trial volume
/// grows year on year independent of any one concept's popularity.
/// </summary>
public sealed class AnalyticsTrendViewModel
{
    /// <summary>The raw comma-separated codes as submitted. Empty on the
    /// initial, not-yet-submitted form.</summary>
    public string CodesInput { get; init; } = "";

    public IReadOnlyList<TrendSeries> Series { get; init; } = Array.Empty<TrendSeries>();

    /// <summary>The year flagged as partial on every series - passed in from
    /// the controller so a test (and the view's own "(partial)" label) can
    /// pin it rather than depending on the clock.</summary>
    public int CurrentYear { get; init; }

    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True once at least one concept code has been submitted -
    /// distinguishes the empty form from "the query ran and found nothing".</summary>
    public bool HasSearched => !string.IsNullOrWhiteSpace(CodesInput);
}

/// <summary>
/// Backing model for the Analytics tab's concept lookup view
/// (<c>/Analytics/Concept</c>). Everything known about one concept, or a
/// clean not-found state when the code does not resolve in umls.concept - a
/// user can type anything into the URL.
/// </summary>
public sealed class AnalyticsConceptViewModel
{
    /// <summary>The concept code (CUI) as submitted/resolved. Empty on the
    /// initial, not-yet-submitted form.</summary>
    public string Code { get; init; } = "";

    /// <summary>Null when the code has not been looked up, or the gateway
    /// found nothing for it - see <see cref="NotFound"/> to distinguish those.</summary>
    public ConceptSummary? Summary { get; init; }

    /// <summary>True when <see cref="Code"/> did not look like a CUI
    /// (<c>^C\d{7}$</c>) and was searched by name via
    /// <c>IAnalyticsGateway.SearchConceptsAsync</c> instead of looked up
    /// directly - the spec's "by typing a CUI or name" lookup. When true,
    /// <see cref="NameSearchResults"/> holds the matches (possibly empty) and
    /// <see cref="Summary"/>/<see cref="NotFound"/> are not applicable.</summary>
    public bool IsNameSearch { get; init; }

    /// <summary>The name-search matches when <see cref="IsNameSearch"/> is
    /// true - each one a pick-list entry linking to the exact-CUI view.</summary>
    public IReadOnlyList<ConceptSummary> NameSearchResults { get; init; } = Array.Empty<ConceptSummary>();

    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True once a code has been submitted - distinguishes the
    /// empty form from "the lookup ran and found nothing".</summary>
    public bool HasSearched => !string.IsNullOrWhiteSpace(Code);

    /// <summary>True when a code was searched, the gateway did not throw, it
    /// was not a name search, and it came back with no matching concept - the
    /// clean not-found state the view must render instead of a blank page.</summary>
    public bool NotFound => HasSearched && !HasError && !IsNameSearch && Summary is null;
}

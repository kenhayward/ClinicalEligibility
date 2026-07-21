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
/// Backing model for the Analytics tab's trend view (<c>/Analytics/Trend</c>).
/// Minimal placeholder only - the Trend action and its view are built in a
/// later task; this shape exists so callers of AnalyticsViewModels.cs compile
/// against a stable file today and Task 7 fleshes it out without moving types.
/// </summary>
public sealed class AnalyticsTrendViewModel
{
    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>
/// Backing model for the Analytics tab's concept lookup view
/// (<c>/Analytics/Concept</c>). Minimal placeholder - see the remark on
/// <see cref="AnalyticsTrendViewModel"/>; the Concept action and its view are
/// built in a later task.
/// </summary>
public sealed class AnalyticsConceptViewModel
{
    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

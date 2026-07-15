using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backing model for the dashboard's Results table browser
/// (<c>/Home/Results</c>). Holds the rows returned for the current filter, the
/// filter that produced them, and the per-column dropdown option lists used
/// to decide between <c>&lt;select&gt;</c> and free-text inputs in the view.
///
/// <see cref="HitRowCap"/> is true when the row list is at the configured
/// cap; the view shows a hint that filters should be narrowed.
/// </summary>
public sealed class ResultsViewModel
{
    public EligibilityResultPage Page { get; init; } = EligibilityResultPage.Empty;
    public EligibilityFilter Filter { get; init; } = new();
    public EligibilityFilterOptions Options { get; init; } = EligibilityFilterOptions.Empty;
    public string SortBy { get; init; } = SortChoices.Default;
    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Convenience pass-throughs for the view.
    public IReadOnlyList<EligibilityRow> Rows => Page.Rows;
    public int CurrentPage => Page.Page;
    public int PageSize => Page.PageSize;
    public long TotalRows => Page.TotalRows;
    public int TotalPages => Page.TotalPages;
    public bool HasPrev => CurrentPage > 1;
    public bool HasNext => CurrentPage < TotalPages;
    public long FirstRowOnPage => TotalRows == 0 ? 0 : (long)(CurrentPage - 1) * PageSize + 1;
    public long LastRowOnPage => Math.Min(FirstRowOnPage + Rows.Count - 1, TotalRows);

    /// <summary>
    /// Stable URL/query-param values for the Results page's sort dropdown,
    /// paired with the human-readable label. Order = display order.
    /// The gateway's whitelist must accept every Value here; unrecognised
    /// values fall back to the default. Direction is fixed per column.
    /// </summary>
    public static class SortChoices
    {
        public const string Default = "created_at_desc";

        public static readonly IReadOnlyList<(string Value, string Label)> All = new[]
        {
            ("created_at_desc", "Created (newest first)"),
            ("created_at_asc", "Created (oldest first)"),
            ("nct_id_asc", "NCT ID (A→Z)"),
            ("criterion_asc", "Criterion (A→Z)"),
            ("domain_asc", "Domain (A→Z)"),
            ("concept_asc", "Concept (A→Z)"),
            ("concept_code_asc", "Concept code (A→Z)"),
            ("semantic_type_asc", "Semantic type (A→Z)"),
            ("match_score_desc", "Match score (best first)")
        };
    }
}

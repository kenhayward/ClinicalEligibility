using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backing model for <c>/Home/History</c> (the per-trial audit browser; the
/// route was renamed from <c>/Home/Studies</c>). Paged audit-row view from
/// <c>public.eligibility_study</c>, filterable by NCT ID, status, and run ID.
/// Mirrors the Results-tab layout (filter card + paged table + status badges).
/// </summary>
public sealed class StudiesViewModel
{
    public StudyExecutionPage Page { get; init; } = StudyExecutionPage.Empty;
    public StudyFilter Filter { get; init; } = new();
    public string SortBy { get; init; } = SortChoices.Default;
    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public IReadOnlyList<StudyExecution> Rows => Page.Rows;
    public int CurrentPage => Page.Page;
    public int PageSize => Page.PageSize;
    public long TotalRows => Page.TotalRows;
    public int TotalPages => Page.TotalPages;
    public bool HasPrev => CurrentPage > 1;
    public bool HasNext => CurrentPage < TotalPages;
    public long FirstRowOnPage => TotalRows == 0 ? 0 : (long)(CurrentPage - 1) * PageSize + 1;
    public long LastRowOnPage => Math.Min(FirstRowOnPage + Rows.Count - 1, TotalRows);

    public static class StatusChoices
    {
        public static readonly IReadOnlyList<string> All = new[]
        {
            StudyExecution.StatusRunning,
            StudyExecution.StatusSuccess,
            StudyExecution.StatusLlmFailed,
            StudyExecution.StatusParseEmpty,
            StudyExecution.StatusParseInvalidJson,
            StudyExecution.StatusPersistFailed,
            StudyExecution.StatusFailed,
            StudyExecution.StatusCancelled
        };
    }

    public static class PageSizeChoices
    {
        public const int Default = 10;
        public static readonly IReadOnlyList<int> All = new[] { 10, 20, 50, 100, 500, 1000 };
    }

    public static class SortChoices
    {
        public const string Default = "started_at_desc";

        public static readonly IReadOnlyList<(string Value, string Label)> All = new[]
        {
            ("started_at_desc", "Started (newest first)"),
            ("started_at_asc", "Started (oldest first)"),
            ("finished_at_desc", "Finished (newest first)"),
            ("nct_id_asc", "NCT ID (A→Z)"),
            ("status_asc", "Status (A→Z)"),
            ("duration_desc", "Duration (longest first)"),
            ("parsed_desc", "Parsed (most first)"),
            ("persisted_desc", "Persisted (most first)"),
            ("prompt_tokens_desc", "Prompt tokens (most first)"),
            ("completion_tokens_desc", "Completion tokens (most first)")
        };
    }
}

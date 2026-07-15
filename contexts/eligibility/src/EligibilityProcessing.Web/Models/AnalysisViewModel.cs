using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backing model for <c>/Home/Analysis</c>. Three result slots:
///   • <see cref="Study"/>              — high-level ID card (null if no NCT_ID
///                                        was searched, or the trial wasn't
///                                        found in the source DB).
///   • <see cref="SourceEligibility"/>  — full ctgov.eligibilities row.
///   • <see cref="PipelineRows"/>       — public.eligibility records this
///                                        pipeline produced for the same trial.
/// </summary>
public sealed class AnalysisViewModel
{
    /// <summary>The NCT_ID the user submitted (empty for the initial empty form).</summary>
    public string NctId { get; init; } = "";

    public StudyDetails? Study { get; init; }
    public SourceEligibilityDetails? SourceEligibility { get; init; }
    public IReadOnlyList<EligibilityRow> PipelineRows { get; init; } = Array.Empty<EligibilityRow>();
    public IReadOnlyList<StudyExecution> History { get; init; } = Array.Empty<StudyExecution>();

    /// <summary>When the study card / eligibility detail was served from the
    /// persisted snapshot (public.eligibility_study_detail), the capture time;
    /// null when the data was fetched live from AACT.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; init; }

    /// <summary>True when the displayed study detail came from the persisted
    /// snapshot rather than a live AACT query.</summary>
    public bool FromSnapshot => SnapshotCapturedAt is not null;

    /// <summary>True when the user submitted an NCT_ID but neither AACT nor the
    /// pipeline output had anything for it. Distinct from "no search yet".</summary>
    public bool NotFound { get; init; }

    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasSearched => !string.IsNullOrWhiteSpace(NctId);
}

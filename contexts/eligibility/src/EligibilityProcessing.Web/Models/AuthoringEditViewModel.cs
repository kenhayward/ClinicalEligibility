using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backs the <c>/Authoring/Edit/{id}</c> editor — the Study Setup form (and,
/// from Milestone 2, the Analysis section). Holds the full authored study
/// aggregate loaded from the gateway.
/// </summary>
public sealed class AuthoringEditViewModel
{
    public AuthoringStudyAggregate? Aggregate { get; init; }

    public IReadOnlyList<AuthoringStudySummary> Studies { get; init; } =
        Array.Empty<AuthoringStudySummary>();

    public bool NotFound { get; init; }

    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public AuthoringStudy Study => Aggregate?.Study ?? new AuthoringStudy();

    public AuthoringEligibility Eligibility =>
        Aggregate?.Eligibility ?? new AuthoringEligibility();

    public IReadOnlyList<AuthoringCriterion> Criteria =>
        Aggregate?.Criteria ?? Array.Empty<AuthoringCriterion>();

    /// <summary>A success/info banner carried across a post-redirect-get.</summary>
    public string? StatusMessage { get; init; }
}

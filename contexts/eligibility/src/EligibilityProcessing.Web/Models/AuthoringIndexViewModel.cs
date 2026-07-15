using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backs the <c>/Authoring</c> landing page — the list of authored studies
/// (authoring specification §3.1.1).
/// </summary>
public sealed class AuthoringIndexViewModel
{
    public IReadOnlyList<AuthoringStudySummary> Studies { get; init; } =
        Array.Empty<AuthoringStudySummary>();

    public string? ErrorMessage { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

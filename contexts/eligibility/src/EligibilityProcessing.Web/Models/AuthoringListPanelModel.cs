using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// View-model for the shared <c>Views/Authoring/_StudyListPanel.cshtml</c>
/// partial — the left-hand study list rendered on both <c>/Authoring</c> and
/// <c>/Authoring/Edit/{id}</c>. <see cref="SelectedId"/> highlights the active
/// row when the Edit view is open; null on the Index landing view.
/// </summary>
public sealed class AuthoringListPanelModel
{
    public IReadOnlyList<AuthoringStudySummary> Studies { get; init; } =
        Array.Empty<AuthoringStudySummary>();

    public Guid? SelectedId { get; init; }
}

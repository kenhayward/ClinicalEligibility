using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web;

/// <summary>
/// Work item written by the <c>/trigger</c> endpoint and drained by
/// <see cref="BatchRunner"/>. The endpoint returns 202 immediately with
/// <see cref="RunId"/> and <see cref="StartedAt"/> populated; the
/// orchestrator picks the same RunId up when it begins executing.
///
/// <see cref="RerunNctIds"/> is non-empty when the request comes from the
/// dashboard's "Run Trial" button (one entry) or the History tab's "Rerun
/// selection" button (many entries). When non-empty the BatchRunner builds
/// a re-run-mode RunConfiguration so the orchestrator processes the listed
/// trials instead of selecting a batch — one run_id regardless of how many
/// trials are in the list.
///
/// <see cref="Direction"/> is used only for the select-a-batch path
/// (RerunNctIds empty). Forward = earliest unprocessed first (default);
/// Recent = most-recent unprocessed first.
/// </summary>
public sealed record RunRequest(
    Guid RunId,
    int StudyCount,
    DateTimeOffset StartedAt,
    IReadOnlyList<string>? RerunNctIds = null,
    TrialSelectionDirection Direction = TrialSelectionDirection.Forward);

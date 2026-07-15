using EligibilityProcessing.Core;
using Microsoft.AspNetCore.SignalR;

namespace EligibilityProcessing.Web;

/// <summary>
/// IPipelineHooks implementation that broadcasts pipeline events through
/// <see cref="RunProgressHub"/>. Registered in Web's Program.cs to replace
/// the NullPipelineHooks default that Hosting installs.
///
/// IHubContext is thread-safe (SignalR guarantees safe concurrent SendAsync),
/// which is required because the orchestrator's parallel per-trial body
/// fires TrialStarted/TrialCompleted from multiple threads at once.
/// </summary>
public sealed class SignalRPipelineHooks : IPipelineHooks
{
    private readonly IHubContext<RunProgressHub> _hub;

    public SignalRPipelineHooks(IHubContext<RunProgressHub> hub)
    {
        _hub = hub;
    }

    public Task OnBatchStartedAsync(Guid runId, int studyCount, CancellationToken cancellationToken)
        => _hub.Clients.All.SendAsync(
            "BatchStarted",
            new { runId, studyCount, at = DateTimeOffset.UtcNow },
            cancellationToken);

    public Task OnTrialStartedAsync(Guid runId, string nctId, CancellationToken cancellationToken)
        => _hub.Clients.All.SendAsync(
            "TrialStarted",
            new { runId, nctId, at = DateTimeOffset.UtcNow },
            cancellationToken);

    public Task OnTrialCompletedAsync(
        Guid runId, string nctId, int rowCount, bool succeeded, CancellationToken cancellationToken)
        => _hub.Clients.All.SendAsync(
            "TrialCompleted",
            new { runId, nctId, rowCount, succeeded, at = DateTimeOffset.UtcNow },
            cancellationToken);

    public Task OnBatchCompletedAsync(BatchResult result, CancellationToken cancellationToken)
        => _hub.Clients.All.SendAsync(
            "BatchCompleted",
            new
            {
                runId = result.Metrics.RunId,
                status = result.Metrics.Status,
                rowsPersisted = result.Metrics.RowsPersisted,
                studiesProcessed = result.Metrics.StudiesProcessed,
                resolutionRate = result.Metrics.ResolutionRate,
                failedNctIds = result.FailedNctIds,
                at = DateTimeOffset.UtcNow
            },
            cancellationToken);

    public Task OnBatchCancelledAsync(Guid runId, CancellationToken cancellationToken)
        => _hub.Clients.All.SendAsync(
            "BatchCancelled",
            new { runId, at = DateTimeOffset.UtcNow },
            cancellationToken);
}

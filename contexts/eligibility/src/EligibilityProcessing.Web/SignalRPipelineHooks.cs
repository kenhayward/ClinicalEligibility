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
///
/// It also drops the cached corpus aggregate when a batch completes. That is
/// not purely a broadcasting concern, but this is the one place that knows the
/// corpus just changed, and it has to be the SERVER that knows: leaving it to
/// clients to ask for a cache bypass would mean the cache is only correct for
/// whoever happened to be watching, and someone opening the dashboard a moment
/// later would read stale figures for the rest of the TTL.
/// </summary>
public sealed class SignalRPipelineHooks : IPipelineHooks
{
    private readonly IHubContext<RunProgressHub> _hub;
    private readonly ICorpusReadCache _corpusReads;

    public SignalRPipelineHooks(IHubContext<RunProgressHub> hub, ICorpusReadCache corpusReads)
    {
        _hub = hub;
        _corpusReads = corpusReads;
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
    {
        // Order matters: invalidate BEFORE announcing. The dashboard re-reads the
        // figures when it sees this event, so announcing first opens a window
        // where that read repopulates the cache with the pre-run numbers and pins
        // them there for the rest of the TTL - the exact staleness this is here
        // to prevent.
        _corpusReads.InvalidateDashboardMetrics();

        return _hub.Clients.All.SendAsync(
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
    }

    public Task OnBatchCancelledAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Cancelled is not "nothing happened": every trial that finished before
        // the cancel landed has already persisted its rows, so the corpus moved
        // and the cache is stale exactly as it would be after a clean run.
        _corpusReads.InvalidateDashboardMetrics();

        return _hub.Clients.All.SendAsync(
            "BatchCancelled",
            new { runId, at = DateTimeOffset.UtcNow },
            cancellationToken);
    }
}

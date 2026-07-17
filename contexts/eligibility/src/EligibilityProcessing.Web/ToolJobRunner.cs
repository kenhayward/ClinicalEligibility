using System.Threading.Channels;
using EligibilityProcessing.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EligibilityProcessing.Web;

/// <summary>
/// Background worker that drains <see cref="ToolJobRequest"/> items and runs the
/// shared Core tool jobs (<see cref="IUmlsNormalizeJob"/> / <see cref="IStudyEmbeddingJob"/>)
/// off the request thread - so they survive the browser closing, exactly like
/// <see cref="BatchRunner"/> does for the extraction pipeline. Each job gets its own
/// DI scope (the scoped UMLS cache).
///
/// Mutual exclusion is the shared <see cref="RunGate"/>: the POST endpoint acquires
/// it before enqueuing, so a tool job can't start while the main pipeline or the
/// other tool is running, and vice versa. Live metrics flow two ways: into
/// <see cref="ToolJobState"/> (so a freshly loaded Tools tab renders the current
/// job) and out over <see cref="RunProgressHub"/> as ToolJob* events.
/// </summary>
internal sealed class ToolJobRunner : BackgroundService
{
    private readonly Channel<ToolJobRequest> _channel;
    private readonly RunGate _gate;
    private readonly ToolJobState _state;
    private readonly IServiceProvider _services;
    private readonly IHubContext<RunProgressHub> _hub;
    private readonly ILogger<ToolJobRunner> _logger;

    public ToolJobRunner(
        Channel<ToolJobRequest> channel,
        RunGate gate,
        ToolJobState state,
        IServiceProvider services,
        IHubContext<RunProgressHub> hub,
        ILogger<ToolJobRunner> logger)
    {
        _channel = channel;
        _gate = gate;
        _state = state;
        _services = services;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ToolJobRunner started; awaiting tool-job requests");
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var runToken = _gate.CurrentToken;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runToken);
            var kindName = ToolJobState.KindName(request.Kind);
            _state.Begin(request.JobId, request.Kind, Describe(request));
            await Send("ToolJobStarted",
                new { jobId = request.JobId, kind = kindName, at = DateTimeOffset.UtcNow }).ConfigureAwait(false);

            var status = "completed";
            try
            {
                using var scope = _services.CreateScope();
                var progress = new SyncProgress<ToolJobSnapshot>(snapshot =>
                {
                    _state.Update(snapshot);
                    // Fire-and-forget: progress ticks are advisory; the authoritative
                    // state is in ToolJobState (read on page load / reconnect).
                    _ = _hub.Clients.All.SendAsync("ToolJobProgress", Payload(request.JobId, snapshot));
                });

                if (request.Kind == ToolJobKind.NormalizeUmls)
                {
                    var job = scope.ServiceProvider.GetRequiredService<IUmlsNormalizeJob>();
                    await job.RunAsync(request.Normalize!, progress, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    var job = scope.ServiceProvider.GetRequiredService<IStudyEmbeddingJob>();
                    await job.RunAsync(request.Embed!, progress, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _gate.Release();
                _state.Finish("cancelled");
                throw;
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                status = "cancelled";
                _logger.LogInformation("Tool job {JobId} ({Kind}) cancelled by user", request.JobId, kindName);
            }
            catch (Exception ex)
            {
                status = "failed";
                _logger.LogError(ex, "Tool job {JobId} ({Kind}) failed", request.JobId, kindName);
            }
            finally
            {
                _gate.Release();
            }

            _state.Finish(status);

            // Tool jobs move the dashboard's corpus figures, so drop the cached
            // aggregate before announcing - same ordering reason as the pipeline
            // hooks: the dashboard re-reads on this event, and announcing first
            // would let that read pin the pre-job numbers for the rest of the TTL.
            // embed-studies changes studies-without-embeddings; normalize-umls
            // changes the resolution rate. Without this the Tools tab and the
            // Dashboard disagree until the TTL expires, which reads as one of
            // them being broken.
            _services.GetRequiredService<ICorpusReadCache>().InvalidateDashboardMetrics();

            var view = _state.Current;
            await Send("ToolJobCompleted", new
            {
                jobId = request.JobId,
                kind = kindName,
                status,
                total = view?.Total ?? 0,
                processed = view?.Processed ?? 0,
                elapsedSeconds = view?.ElapsedSeconds ?? 0,
                metrics = view?.Metrics.Select(m => new { label = m.Label, value = m.Value }),
                at = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);
        }
    }

    private static object Payload(Guid jobId, ToolJobSnapshot s) => new
    {
        jobId,
        kind = ToolJobState.KindName(s.Kind),
        status = "running",     // progress ticks are always mid-run; the client keys
                                // the live elapsed / ETA / Cancel UI off this.
        total = s.Total,
        processed = s.Processed,
        elapsedSeconds = s.Elapsed.TotalSeconds,
        metrics = s.Metrics.Select(m => new { label = m.Label, value = m.Value }),
        at = DateTimeOffset.UtcNow
    };

    private async Task Send(string method, object payload)
    {
        try
        {
            await _hub.Clients.All.SendAsync(method, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR {Method} broadcast failed", method);
        }
    }

    private static string Describe(ToolJobRequest r)
    {
        if (r.Kind == ToolJobKind.NormalizeUmls && r.Normalize is { } n)
        {
            return $"count {n.Count}, concurrency {n.Concurrency}"
                 + (n.DryRun ? ", dry-run" : "")
                 + (n.Force ? ", force" : "");
        }
        if (r.Embed is { } e)
        {
            return $"concurrency {e.Concurrency}"
                 + (string.IsNullOrEmpty(e.Model) ? "" : $", model {e.Model}");
        }
        return "";
    }

    /// <summary>Synchronous IProgress so snapshots are handled inline on the job's
    /// pump thread (no SynchronizationContext reordering), keeping ToolJobState's
    /// latest-wins value consistent.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onReport;
        public SyncProgress(Action<T> onReport) => _onReport = onReport;
        public void Report(T value) => _onReport(value);
    }
}

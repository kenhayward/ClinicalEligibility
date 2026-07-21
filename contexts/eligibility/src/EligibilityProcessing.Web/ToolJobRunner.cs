using System.Threading.Channels;
using EligibilityProcessing.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EligibilityProcessing.Web;

/// <summary>
/// Background worker that drains <see cref="ToolJobRequest"/> items and runs the
/// shared Core tool jobs (<see cref="IUmlsNormalizeJob"/> / <see cref="IStudyEmbeddingJob"/> /
/// <see cref="IConditionNormalizeJob"/>) off the request thread - so they survive the browser closing, exactly like
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

            var status = "completed";
            try
            {
                // Begin/Describe run inside the try (rather than before it, as
                // originally) so that an unhandled ToolJobKind - which Describe's
                // switch below now throws on, matching the dispatch switch - fails
                // this one job gracefully via the catch block below instead of
                // taking down the whole background service.
                _state.Begin(request.JobId, request.Kind, Describe(request));
                await Send("ToolJobStarted",
                    new { jobId = request.JobId, kind = kindName, at = DateTimeOffset.UtcNow }).ConfigureAwait(false);

                using var scope = _services.CreateScope();
                var progress = new SyncProgress<ToolJobSnapshot>(snapshot =>
                {
                    _state.Update(snapshot);
                    // Fire-and-forget: progress ticks are advisory; the authoritative
                    // state is in ToolJobState (read on page load / reconnect).
                    _ = _hub.Clients.All.SendAsync("ToolJobProgress", Payload(request.JobId, snapshot));
                });

                // Explicit arm per ToolJobKind, not if/else-if with a catch-all
                // else: the catch-all else used to handle any unrecognized kind as
                // EmbedStudies and force-unwrap request.Embed!, which had already
                // caused one null-dereference incident when a request didn't carry
                // an Embed payload. A fourth kind now fails loudly here instead of
                // silently mis-dispatching to the wrong job.
                switch (request.Kind)
                {
                    case ToolJobKind.NormalizeUmls:
                    {
                        var job = scope.ServiceProvider.GetRequiredService<IUmlsNormalizeJob>();
                        await job.RunAsync(request.Normalize!, progress, linked.Token).ConfigureAwait(false);
                        break;
                    }
                    case ToolJobKind.NormalizeConditions:
                    {
                        var job = scope.ServiceProvider.GetRequiredService<IConditionNormalizeJob>();
                        await job.RunAsync(request.Conditions!, progress, linked.Token).ConfigureAwait(false);
                        break;
                    }
                    case ToolJobKind.EmbedStudies:
                    {
                        var job = scope.ServiceProvider.GetRequiredService<IStudyEmbeddingJob>();
                        await job.RunAsync(request.Embed!, progress, linked.Token).ConfigureAwait(false);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unhandled tool job kind: {request.Kind}");
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
        // Same treatment as the dispatch switch in ExecuteAsync: an explicit arm
        // per ToolJobKind with a throwing default, so a fourth kind added without
        // updating this method fails loudly (caught by ExecuteAsync's catch-all,
        // see the comment there) instead of silently falling through to "".
        switch (r.Kind)
        {
            case ToolJobKind.NormalizeUmls when r.Normalize is { } n:
                return $"count {n.Count}, concurrency {n.Concurrency}"
                     + (n.DryRun ? ", dry-run" : "")
                     + (n.Force ? ", force" : "");
            case ToolJobKind.NormalizeConditions when r.Conditions is { } c:
                return (c.Count > 0 ? $"count {c.Count}" : "all pending")
                     + $", concurrency {c.Concurrency}"
                     + (c.DryRun ? ", dry-run" : "")
                     + (c.Force ? ", force" : "");
            case ToolJobKind.EmbedStudies when r.Embed is { } e:
                return $"concurrency {e.Concurrency}"
                     + (string.IsNullOrEmpty(e.Model) ? "" : $", model {e.Model}");
            default:
                throw new InvalidOperationException($"Unhandled tool job kind: {r.Kind}");
        }
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

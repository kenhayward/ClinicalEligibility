using System.Threading.Channels;
using EligibilityProcessing.Core;
using EligibilityProcessing.Data;   // PostgresRunLock (cross-process batch lock)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EligibilityProcessing.Web;

/// <summary>
/// Background worker that drains <see cref="RunRequest"/> items from the work
/// channel and runs the orchestrator. Each run gets its own DI scope so the
/// scoped <c>UmlsCache</c> lives only for the duration of that run.
///
/// Hosted inside the Web project so the orchestrator runs in the same process
/// as the SignalR hub — that's what lets <see cref="SignalRPipelineHooks"/>
/// broadcast live events to dashboard clients (the cross-process gap the
/// standalone Webhook host suffered from).
///
/// Cancellation: the orchestrator is invoked with a token that links the host
/// shutdown token (<c>stoppingToken</c>) with the per-run token held by
/// <see cref="RunGate"/>. The dashboard's Cancel button calls
/// <see cref="RunGate.Cancel"/>, which fires the per-run token; we distinguish
/// user-cancel from host shutdown by inspecting which side of the link fired,
/// and on user-cancel we broadcast a synthetic <c>BatchCancelled</c> event
/// (the orchestrator's normal <c>BatchCompleted</c> path is skipped on OCE).
///
/// Catches every exception around <c>ExecuteAsync</c> — the orchestrator
/// already converts catastrophic failures into a "failed" BatchResult, but
/// belt-and-suspenders catch here means a defect in the orchestrator can't
/// tear down the BackgroundService. <see cref="RunGate.Release"/> is in
/// <c>finally</c> so the gate never gets wedged.
/// </summary>
internal sealed class BatchRunner : BackgroundService
{
    private readonly Channel<RunRequest> _channel;
    private readonly RunGate _gate;
    private readonly IServiceProvider _services;
    private readonly IPipelineHooks _hooks;
    private readonly ILogger<BatchRunner> _logger;

    public BatchRunner(
        Channel<RunRequest> channel,
        RunGate gate,
        IServiceProvider services,
        IPipelineHooks hooks,
        ILogger<BatchRunner> logger)
    {
        _channel = channel;
        _gate = gate;
        _services = services;
        _hooks = hooks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BatchRunner started; awaiting trigger requests");
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var runToken = _gate.CurrentToken;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runToken);
            try
            {
                using var scope = _services.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<PipelineOrchestrator>();
                RunConfiguration config;
                if (request.RerunNctIds is { Count: > 0 } ids)
                {
                    // Re-run path. StudyCount is informational; the
                    // orchestrator's IsRerun branch ignores it and fetches the
                    // specific list. One or many trials, same code path.
                    config = new RunConfiguration(
                        studyCount: ids.Count,
                        triggerSource: "rerun",
                        rerunNctIds: ids);
                    _logger.LogInformation(
                        "BatchRunner picked up RE-RUN {RunId} for {Count} trial(s)",
                        request.RunId, ids.Count);
                }
                else
                {
                    var triggerSource = request.Direction == TrialSelectionDirection.Recent
                        ? "recent"
                        : "webhook";
                    config = new RunConfiguration(
                        request.StudyCount,
                        triggerSource,
                        rerunNctIds: null,
                        direction: request.Direction);
                    _logger.LogInformation(
                        "BatchRunner picked up run {RunId} (StudyCount={Count}, Direction={Direction})",
                        request.RunId, request.StudyCount, request.Direction);
                }
                // RunGate got us this far, but it only serialises THIS process. Take the
                // database-backed lock before touching any trial so a CLI batch running
                // against the same database cannot proceed in parallel - both would
                // select overlapping trials and fight over the model-server slots.
                var runLock = scope.ServiceProvider.GetRequiredService<PostgresRunLock>();
                if (!await runLock.TryAcquireAsync(linked.Token).ConfigureAwait(false))
                {
                    // Another process owns the pipeline. The trigger already returned 202
                    // and the dashboard is showing a run as started, so close that loop
                    // explicitly rather than going quiet.
                    _logger.LogWarning(
                        "Run {RunId} not started: another process (a CLI batch, or another host) holds the run lock.",
                        request.RunId);
                    try
                    {
                        await _hooks.OnBatchCancelledAsync(request.RunId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception hookEx)
                    {
                        _logger.LogWarning(hookEx, "OnBatchCancelled hook failed for run {RunId}", request.RunId);
                    }
                    continue;   // the finally below still releases the in-process gate
                }

                try
                {
                    await orchestrator.ExecuteAsync(config, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    // Release even on cancellation/failure. Process death would end the
                    // session and drop the lock anyway, so this can never wedge.
                    await runLock.ReleaseAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                _logger.LogInformation("Run {RunId} cancelled by user", request.RunId);
                try
                {
                    await _hooks.OnBatchCancelledAsync(request.RunId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnBatchCancelled hook failed for run {RunId}", request.RunId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception executing batch {RunId}", request.RunId);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

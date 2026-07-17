using System.Diagnostics;
using System.Threading.Channels;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EligibilityProcessing.Web.Controllers;

[Authorize]
public class HomeController : Controller
{
    private const int RecentRunsLimit = 50;

    /// <summary>Runs behind the dashboard sparklines ("last 7 runs"). Small on
    /// purpose - it is a shape-at-a-glance, and a longer window would flatten the
    /// recent movement it exists to show.</summary>
    private const int SparklineRunCount = 7;

    private readonly IPostgresGateway _gateway;
    private readonly ICorpusReadCache _corpusReads;
    private readonly IAuditWriter _audit;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        IPostgresGateway gateway,
        ICorpusReadCache corpusReads,
        IAuditWriter audit,
        ILogger<HomeController> logger)
    {
        _gateway = gateway;
        _corpusReads = corpusReads;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IActionResult> Index([FromServices] RunGate gate, CancellationToken cancellationToken)
    {
        try
        {
            // The corpus aggregate is NOT read here any more - the page fetches it
            // from GET /Home/Metrics so it can paint immediately and show a
            // skeleton over the ~700ms read, refresh itself when a run finishes,
            // and give Reload something to do that isn't a page reload.
            //
            // This read stays server-side, and stays live/uncached, for three
            // reasons: it is cheap; it is what makes a just-finished run appear
            // immediately; and it is what decides whether the trigger buttons
            // paint disabled. Resolved client-side, they would flash enabled and a
            // fast operator would already have clicked. It is also the read that
            // keeps this action's error path real - see the catch below.
            var recent = await _gateway.GetRecentRunsAsync(1, cancellationToken);

            return View(new DashboardViewModel
            {
                MostRecentRun = recent.Count > 0 ? recent[0] : null,
                BusyActivity = gate.CurrentActivity
            });
        }
        catch (Exception ex)
        {
            // The dashboard must stay usable even when Postgres is down; show
            // the error inline instead of 500ing. Spec section 6.4 mandates
            // graceful tolerance for backend hiccups.
            _logger.LogWarning(ex, "Failed to render dashboard");
            return View(new DashboardViewModel { ErrorMessage = ex.Message, BusyActivity = gate.CurrentActivity });
        }
    }

    public async Task<IActionResult> Runs(CancellationToken cancellationToken)
    {
        try
        {
            var runs = await _gateway.GetRecentRunsAsync(RecentRunsLimit, cancellationToken);
            return View(new RunsViewModel { Runs = runs });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load runs history");
            return View(new RunsViewModel { ErrorMessage = ex.Message });
        }
    }

    private const int ResultsPageSize = 20;
    private const int ResultsDropdownThreshold = 100;

    /// <summary>
    /// Read-only browser for <c>public.eligibility</c>. Filterable by NCT_ID,
    /// criterion, domain, concept, concept code, and semantic type. Paginated
    /// 20 rows per page with a Prev/Next pager driven by the gateway's
    /// COUNT(*) OVER() total.
    /// </summary>
    public async Task<IActionResult> Results(
        CancellationToken cancellationToken,
        string? nctId = null,
        string? criterion = null,
        string? domain = null,
        string? concept = null,
        string? conceptCode = null,
        string? semanticType = null,
        string? sortBy = null,
        int page = 1)
    {
        try
        {
            // Cached (see ICorpusReadCache). The dropdown lists are corpus-wide
            // distinct values; the paged result set below is NOT cached, since it
            // is keyed by the user's filter and must always be live.
            var options = await _corpusReads.GetEligibilityFilterOptionsAsync(
                ResultsDropdownThreshold, cancellationToken);

            var filter = new EligibilityFilter(
                nctId: nctId, criterion: criterion, domain: domain,
                concept: concept, conceptCode: conceptCode, semanticType: semanticType);

            var effectiveSort = string.IsNullOrWhiteSpace(sortBy)
                ? ResultsViewModel.SortChoices.Default
                : sortBy;

            var pageResult = await _gateway.SearchEligibilityAsync(
                filter, effectiveSort, page, ResultsPageSize, cancellationToken);

            return View(new ResultsViewModel
            {
                Page = pageResult,
                Filter = filter,
                Options = options,
                SortBy = effectiveSort
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load eligibility results");
            return View(new ResultsViewModel { ErrorMessage = ex.Message });
        }
    }

    /// <summary>
    /// Dashboard-side trigger: same plumbing as <c>POST /trigger</c> but gated
    /// by ASP.NET anti-forgery (same-origin form post from the dashboard)
    /// rather than the shared secret. Trust model matches the rest of the
    /// dashboard — anyone with network access to it can already see runs and
    /// trigger them from the buttons. The shared-secret <c>/trigger</c>
    /// endpoint remains for cross-network webhook callers.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Trigger(
        [FromServices] RunGate gate,
        [FromServices] Channel<RunRequest> channel,
        [FromServices] IOptions<WebhookOptions> options,
        int? count)
    {
        var runId = Guid.NewGuid();
        if (!gate.TryAcquire(runId))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId });
        }

        var studyCount = count.GetValueOrDefault(options.Value.DefaultStudyCount);
        if (studyCount <= 0)
        {
            gate.Release();
            return BadRequest(new { error = "count must be a positive integer" });
        }

        var startedAt = DateTimeOffset.UtcNow;
        if (!channel.Writer.TryWrite(new RunRequest(runId, studyCount, startedAt)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("create", "eligibility_run", runId.ToString(), $"trigger forward count={studyCount}", HttpContext.RequestAborted);
        return Accepted(new { run_id = runId, started_at = startedAt, study_count = studyCount });
    }

    /// <summary>
    /// Dashboard-side trigger for the Recent direction: same plumbing as
    /// <see cref="Trigger"/> but the work-channel request carries
    /// <see cref="TrialSelectionDirection.Recent"/>. The orchestrator then
    /// selects the most-recent unprocessed trials (DESC by nct_id, anti-joined
    /// against the already-attempted set). No default count — caller must
    /// supply an explicit number to make the "process N recent" semantics
    /// unambiguous.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriggerRecent(
        [FromServices] RunGate gate,
        [FromServices] Channel<RunRequest> channel,
        int? count)
    {
        if (!count.HasValue || count.Value <= 0)
        {
            return BadRequest(new { error = "count must be a positive integer" });
        }

        var runId = Guid.NewGuid();
        if (!gate.TryAcquire(runId))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId });
        }

        var startedAt = DateTimeOffset.UtcNow;
        if (!channel.Writer.TryWrite(new RunRequest(
                runId, count.Value, startedAt,
                RerunNctIds: null,
                Direction: TrialSelectionDirection.Recent)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("create", "eligibility_run", runId.ToString(), $"trigger recent count={count.Value}", HttpContext.RequestAborted);
        return Accepted(new { run_id = runId, started_at = startedAt, study_count = count.Value, direction = "recent" });
    }

    /// <summary>
    /// Dashboard-side single-trial re-run. Same plumbing as
    /// <see cref="Trigger"/> but the work-channel request carries a
    /// non-empty RerunNctId, which the orchestrator picks up and processes
    /// in re-run mode (bypasses watermark + batch select). Used to retry
    /// trials that landed in parse_invalid_json / parse_empty / etc.
    /// without needing to reset the whole watermark.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rerun(
        [FromServices] RunGate gate,
        [FromServices] Channel<RunRequest> channel,
        string? nctId)
    {
        var trimmed = nctId?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            return BadRequest(new { error = "nctId is required" });
        }

        var runId = Guid.NewGuid();
        if (!gate.TryAcquire(runId))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId });
        }

        var startedAt = DateTimeOffset.UtcNow;
        if (!channel.Writer.TryWrite(new RunRequest(runId, StudyCount: 1, startedAt,
                                                    RerunNctIds: new[] { trimmed })))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("update", "eligibility_study", trimmed, $"rerun (run {runId})", HttpContext.RequestAborted);
        return Accepted(new { run_id = runId, started_at = startedAt, nct_id = trimmed });
    }

    /// <summary>
    /// Dashboard-side multi-trial re-run. Same plumbing as <see cref="Rerun"/>
    /// but the work-channel request carries the full selected-NCT-ID list, so
    /// the orchestrator processes the batch under a single run_id. Used by the
    /// Studies tab's "Rerun selection" button when selection mode is on.
    /// Empty / blank IDs are dropped; duplicates collapse via the
    /// case-insensitive Distinct() inside RunConfiguration.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RerunBatch(
        [FromServices] RunGate gate,
        [FromServices] Channel<RunRequest> channel,
        string[]? nctIds)
    {
        var trimmed = (nctIds ?? Array.Empty<string>())
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (trimmed.Length == 0)
        {
            return BadRequest(new { error = "nctIds is required" });
        }

        var runId = Guid.NewGuid();
        if (!gate.TryAcquire(runId))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId });
        }

        var startedAt = DateTimeOffset.UtcNow;
        if (!channel.Writer.TryWrite(new RunRequest(runId, StudyCount: trimmed.Length, startedAt,
                                                    RerunNctIds: trimmed)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // entity_id is indexed (ix_audit_log_entity); a comma-joined NCT list for a
        // large selection (e.g. 500 trials) overflows the btree row-size limit. Keep
        // entity_id short (the run id) and carry the full NCT list in the unindexed
        // detail column instead.
        await _audit.WriteAsync("update", "eligibility_study", runId.ToString(), $"rerun batch ({trimmed.Length}) run {runId}: {string.Join(",", trimmed)}", HttpContext.RequestAborted);
        return Accepted(new { run_id = runId, started_at = startedAt, nct_ids = trimmed });
    }

    /// <summary>
    /// Dashboard-side cancel: signals the per-run CTS held by
    /// <see cref="RunGate"/>. <see cref="BatchRunner"/> picks up the
    /// cancellation, releases the gate, and broadcasts
    /// <c>OnBatchCancelledAsync</c> so the dashboard can render a terminal
    /// "cancelled" event for the run.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel([FromServices] RunGate gate)
    {
        var runId = gate.CurrentRunId;
        if (!gate.Cancel())
        {
            return NotFound(new { error = "no run in progress" });
        }
        await _audit.WriteAsync("update", "eligibility_run", runId?.ToString(), "cancel", HttpContext.RequestAborted);
        return Ok(new { cancelled_run_id = runId });
    }

    // ===================== Tools tab (maintenance jobs) =====================

    /// <summary>
    /// Tools tab: web versions of the CLI maintenance commands. Shows the live
    /// "remaining work" counts and lets an operator run normalize-umls / embed-studies
    /// as background jobs that survive a browser close. The jobs share the pipeline's
    /// <see cref="RunGate"/>, so they are mutually exclusive with the main pipeline and
    /// each other. Read-tolerant like the other tabs.
    /// </summary>
    public async Task<IActionResult> Tools(
        [FromServices] RunGate gate,
        [FromServices] ToolJobState toolState,
        [FromServices] IConfiguration config,
        [FromServices] IOptions<OrchestratorOptions> orchestratorOptions,
        CancellationToken cancellationToken)
    {
        var model = config["Embedding:Model"] ?? "";
        var cap = Math.Max(1, orchestratorOptions.Value.LlmConcurrencyCap);
        try
        {
            var normalizeRemaining = await _gateway.CountConceptsToNormalizeAsync(false, cancellationToken);
            var embedRemaining = await _gateway.CountStudiesToEmbedAsync(model, cancellationToken);
            var supersededCount = await _gateway.CountSupersededStudiesAsync(cancellationToken);
            return View(new ToolsViewModel
            {
                NormalizeRemaining = normalizeRemaining,
                EmbedRemaining = embedRemaining,
                SupersededCount = supersededCount,
                EmbeddingModel = model,
                DefaultConcurrency = cap,
                BusyActivity = gate.CurrentActivity,
                Current = toolState.Current
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Tools tab");
            return View(new ToolsViewModel
            {
                ErrorMessage = ex.Message,
                EmbeddingModel = model,
                DefaultConcurrency = cap,
                BusyActivity = gate.CurrentActivity,
                Current = toolState.Current
            });
        }
    }

    /// <summary>Current (or most recently finished) tool job as JSON, so a freshly
    /// loaded or reconnected Tools tab can re-sync the running job's live metrics
    /// without waiting for the next SignalR tick. Null when none has run.</summary>
    [HttpGet]
    public IActionResult ToolState([FromServices] ToolJobState toolState)
        => Json(toolState.Current);

    /// <summary>
    /// The dashboard's corpus figures as JSON, so the page can render its numbers
    /// client-side: that is what lets it show a skeleton while the ~700ms aggregate
    /// resolves, refresh itself when a run or tool job finishes, and give the Reload
    /// button something to do that isn't a full page reload.
    /// <para>
    /// <paramref name="fresh"/> drops the cached aggregate first. Without it, Reload
    /// inside the cache TTL (default 60s) returns the identical numbers - the UI would
    /// flash a loading state and change nothing, which reads as broken rather than as
    /// cached. It is a user-triggerable uncached whole-corpus read, so callers should
    /// debounce it; the TTL still throttles the follow-ups, because this invalidates
    /// and then repopulates rather than reading around the cache.
    /// </para>
    /// <para>
    /// Read-tolerant, like <see cref="ToolCounts"/>: returns <c>{ error }</c> with a 200
    /// rather than a 500, so the page can render the message inline and stay usable while
    /// the DB recovers (spec section 6.4). Authorization is the controller's default
    /// [Authorize] - these are the same corpus figures the dashboard already shows, so no
    /// tighter policy than seeing the dashboard itself.
    /// </para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Metrics(bool fresh, CancellationToken cancellationToken)
    {
        try
        {
            if (fresh)
            {
                _corpusReads.InvalidateDashboardMetrics();
            }

            var metrics = await _corpusReads.GetDashboardMetricsAsync(cancellationToken);
            // Uncached and cheap, unlike the aggregate above - and the series has to be
            // live or a just-finished run would be missing from its own sparkline.
            var runs = await _gateway.GetRecentRunsAsync(SparklineRunCount, cancellationToken);

            return Json(DashboardMetricsPayload.From(metrics, runs));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read dashboard metrics");
            return Json(new { error = ex.Message });
        }
    }

    /// <summary>The current "remaining work" counts as JSON, so the Tools tab can
    /// refresh the card numbers after a job (or a pipeline batch) finishes without a
    /// full page reload. Read-tolerant - returns an empty object on a backend hiccup.</summary>
    [HttpGet]
    public async Task<IActionResult> ToolCounts(
        [FromServices] IConfiguration config,
        CancellationToken cancellationToken)
    {
        try
        {
            var model = config["Embedding:Model"] ?? "";
            var normalizeRemaining = await _gateway.CountConceptsToNormalizeAsync(false, cancellationToken);
            var embedRemaining = await _gateway.CountStudiesToEmbedAsync(model, cancellationToken);
            var supersededCount = await _gateway.CountSupersededStudiesAsync(cancellationToken);
            return Json(new { normalizeRemaining, embedRemaining, supersededCount });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tool counts");
            return Json(new { });
        }
    }

    /// <summary>
    /// The EXACT remaining-trials figure, on demand. Deliberately not on any page load:
    /// the filtered source count takes ~26 seconds against Duke's hosted AACT (no
    /// suitable index, and the account is read-only), which is exactly why the dashboard
    /// shows a cheap unfiltered approximation instead.
    /// <para>
    /// Not cached: it is user-initiated, rarely used, and the whole point is that it is
    /// current when you ask. Read-only, so no RunGate - it can safely run alongside a
    /// batch, and it holds one source connection for the duration.
    /// </para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExactRemaining(CancellationToken cancellationToken)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var selectable = await _gateway.CountSelectableSourceTrialsAsync(cancellationToken);
            var attempted = (await _gateway.GetAttemptedNctIdsAsync(cancellationToken)).Count;
            sw.Stop();

            if (selectable is null)
            {
                return Json(new { available = false, reason = "No reachable AACT source is configured." });
            }

            // Same subtraction the dashboard does, but from the FILTERED total, so the
            // ~0.29% of never-selectable trials are gone. A residual drift remains for
            // trials attempted but no longer selectable - see DashboardMetrics.
            var remaining = Math.Max(0L, selectable.Value - attempted);
            _logger.LogInformation(
                "Exact remaining-trials computed in {ElapsedMs} ms: {Selectable} selectable - {Attempted} attempted = {Remaining}",
                sw.ElapsedMilliseconds, selectable.Value, attempted, remaining);

            return Json(new
            {
                available = true,
                selectable = selectable.Value,
                attempted,
                remaining,
                elapsedMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exact remaining-trials count failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// DESTRUCTIVE. Deletes every superseded eligibility_study attempt row, keeping
    /// the latest attempt per NCT_ID. Runs INLINE rather than as a background job:
    /// it is a single DELETE with no LLM calls, so there is no progress to stream
    /// and the request can just wait for it.
    /// <para>
    /// It still takes the shared <see cref="RunGate"/>, for two reasons: a pipeline
    /// batch writes eligibility_study rows as it goes, so deleting mid-run would
    /// race the very rows being superseded; and holding the gate makes this
    /// mutually exclusive with the other tools, matching how every other write on
    /// this tab behaves. Released in a finally so a failed DELETE cannot wedge the
    /// gate and block all future runs.
    /// </para>
    /// <para>
    /// Progression is unaffected - one row per NCT_ID always survives, so the
    /// attempted-set anti-join is unchanged. See DeleteSupersededStudiesAsync.
    /// </para>
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSuperseded(
        [FromServices] RunGate gate,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, "remove-superseded"))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });
        }

        try
        {
            var deleted = await _gateway.DeleteSupersededStudiesAsync(cancellationToken);
            await _audit.WriteAsync("delete", "eligibility_study", null,
                $"removed {deleted} superseded attempt rows (latest attempt per NCT_ID kept)",
                HttpContext.RequestAborted);
            return Json(new { ok = true, deleted });
        }
        catch (Exception ex)
        {
            // Surface the reason inline: the Tools tab renders it next to the card
            // rather than the user getting a blank 500 after confirming a delete.
            _logger.LogWarning(ex, "Failed to remove superseded study rows");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
        finally
        {
            gate.Release();
        }
    }

    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNormalizeUmls(
        [FromServices] RunGate gate,
        [FromServices] Channel<ToolJobRequest> channel,
        [FromServices] IOptions<OrchestratorOptions> orchestratorOptions,
        int? count, int? concurrency, bool dryRun = false, bool force = false)
    {
        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, "normalize-umls"))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });
        }

        var cap = Math.Max(1, orchestratorOptions.Value.LlmConcurrencyCap);
        var options = new NormalizeUmlsOptions
        {
            Count = count is > 0 ? count.Value : 50,
            Concurrency = concurrency is > 0 ? concurrency.Value : cap,
            DryRun = dryRun,
            Force = force
        };
        if (!channel.Writer.TryWrite(new ToolJobRequest(jobId, ToolJobKind.NormalizeUmls, options, null)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("create", "tool_job", jobId.ToString(),
            $"normalize-umls count={options.Count} concurrency={options.Concurrency}"
                + (dryRun ? " dry-run" : "") + (force ? " force" : ""),
            HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = "normalize-umls" });
    }

    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunEmbedStudies(
        [FromServices] RunGate gate,
        [FromServices] Channel<ToolJobRequest> channel,
        [FromServices] IConfiguration config,
        [FromServices] IOptions<OrchestratorOptions> orchestratorOptions,
        int? concurrency)
    {
        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, "embed-studies"))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });
        }

        var cap = Math.Max(1, orchestratorOptions.Value.LlmConcurrencyCap);
        var options = new EmbedStudiesOptions
        {
            Concurrency = concurrency is > 0 ? concurrency.Value : cap,
            Model = config["Embedding:Model"] ?? ""
        };
        if (!channel.Writer.TryWrite(new ToolJobRequest(jobId, ToolJobKind.EmbedStudies, null, options)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("create", "tool_job", jobId.ToString(),
            $"embed-studies concurrency={options.Concurrency}", HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = "embed-studies" });
    }

    /// <summary>Cancel the in-flight tool job (fires the shared gate's per-run token,
    /// which the ToolJobRunner observes). Same gate the dashboard's Cancel uses.</summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelTool([FromServices] RunGate gate)
    {
        var runId = gate.CurrentRunId;
        var activity = gate.CurrentActivity;
        if (!gate.Cancel())
        {
            return NotFound(new { error = "no job in progress" });
        }
        await _audit.WriteAsync("update", "tool_job", runId?.ToString(), $"cancel {activity}", HttpContext.RequestAborted);
        return Ok(new { cancelled_job_id = runId });
    }

    /// <summary>
    /// Per-trial audit browser from <c>public.eligibility_study</c>. Filterable
    /// by NCT ID, status, and run ID; paged like Results. Same "first visit
    /// vs. explicit interaction" trick used by Results — the empty form on
    /// first visit shows all studies newest-first. <paramref name="pageSize"/>
    /// is constrained to the StudiesViewModel.PageSizeChoices whitelist;
    /// out-of-set values silently fall back to the default to prevent a
    /// query-string mash from breaking the page.
    /// </summary>
    public async Task<IActionResult> History(
        CancellationToken cancellationToken,
        string? nctId = null,
        string? status = null,
        Guid? runId = null,
        string? sortBy = null,
        int page = 1,
        int pageSize = StudiesViewModel.PageSizeChoices.Default,
        bool hideSuperseded = true)
    {
        try
        {
            var filter = new StudyFilter(nctId: nctId, status: status, runId: runId,
                                         hideSuperseded: hideSuperseded);
            var effectiveSort = string.IsNullOrWhiteSpace(sortBy)
                ? StudiesViewModel.SortChoices.Default
                : sortBy;
            var effectivePageSize = StudiesViewModel.PageSizeChoices.All.Contains(pageSize)
                ? pageSize
                : StudiesViewModel.PageSizeChoices.Default;

            var pageResult = await _gateway.GetStudiesAsync(
                filter, effectiveSort, page, effectivePageSize, cancellationToken);

            return View(new StudiesViewModel
            {
                Page = pageResult,
                Filter = filter,
                SortBy = effectiveSort
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load studies");
            return View(new StudiesViewModel { ErrorMessage = ex.Message });
        }
    }

    /// <summary>
    /// Back-compat redirect: the tab/URL was renamed from "Studies" to "History"
    /// to match the nav label. Permanently redirect the old /Home/Studies URL,
    /// preserving any query string (filters, paging), so existing bookmarks and
    /// deep links keep working.
    /// </summary>
    [HttpGet]
    public IActionResult Studies() =>
        RedirectPermanent("/Home/History" + Request.QueryString.ToString());

    /// <summary>
    /// Deletes a single eligibility_study audit row. Used by the per-row
    /// "Delete" link on the History tab so operators can clean up the audit
    /// hit-list after triaging failed runs. Filters and pagination state on
    /// the redirect query string preserve the user's view.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteStudy(
        CancellationToken cancellationToken,
        Guid runId,
        string nctId,
        string? filterNctId = null,
        string? filterStatus = null,
        Guid? filterRunId = null,
        string? sortBy = null,
        int page = 1,
        int pageSize = StudiesViewModel.PageSizeChoices.Default,
        bool hideSuperseded = true)
    {
        var trimmedNctId = nctId?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmedNctId) || runId == Guid.Empty)
        {
            return BadRequest(new { error = "runId and nctId are required" });
        }

        try
        {
            await _gateway.DeleteStudyAsync(runId, trimmedNctId, cancellationToken);
            await _audit.WriteAsync("delete", "eligibility_study", $"{runId}/{trimmedNctId}", null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete study {RunId}/{NctId}", runId, trimmedNctId);
        }

        return RedirectToAction(nameof(History), new
        {
            nctId = filterNctId,
            status = filterStatus,
            runId = filterRunId,
            sortBy,
            page,
            pageSize,
            hideSuperseded
        });
    }

    /// <summary>
    /// One-trial analysis view: pulls study metadata + raw eligibility from
    /// the source DB and pairs it with the pipeline's extracted rows from
    /// <c>public.eligibility</c>. Accessed via the Analysis nav tab (form
    /// input) or by clicking an NCT_ID in the Results table.
    ///
    /// Renders a results-bearing view only when <paramref name="nctId"/> is
    /// supplied; otherwise renders the empty form. Missing trials render as
    /// "not found" rather than 404 because pipeline output may exist even
    /// when the source row has been purged from AACT — and vice versa.
    /// </summary>
    public async Task<IActionResult> Analysis(
        CancellationToken cancellationToken,
        string? nctId = null)
    {
        var normalised = nctId?.Trim() ?? "";
        if (string.IsNullOrEmpty(normalised))
        {
            return View(new AnalysisViewModel());
        }

        try
        {
            // Prefer the persisted snapshot (public.eligibility_study_detail);
            // fall back to live AACT only when the trial has not been
            // snapshotted yet. This keeps the Analysis tab working when the
            // source database is unreachable.
            var snapshot = await _gateway.GetStudySnapshotAsync(normalised, cancellationToken);
            var study = snapshot?.Details
                ?? await _gateway.GetStudyDetailsAsync(normalised, cancellationToken);
            var source = snapshot?.Eligibility
                ?? await _gateway.GetSourceEligibilityAsync(normalised, cancellationToken);

            // Pull every pipeline row for this trial in a single page. There is
            // no fixed per-trial entry cap (spec section 2.4.2) — the row count
            // is bounded by the LLM token budget — so we request the gateway's
            // maximum page size (200, the SearchEligibilityAsync clamp ceiling),
            // which comfortably exceeds any realistic per-trial row count.
            var rowsPage = await _gateway.SearchEligibilityAsync(
                new EligibilityFilter(nctId: normalised),
                sortBy: "criterion_asc",
                page: 1,
                pageSize: 200,
                cancellationToken);

            // Per-trial audit history — every run that touched this NCT_ID.
            var history = await _gateway.GetStudyHistoryAsync(normalised, cancellationToken);

            return View(new AnalysisViewModel
            {
                NctId = normalised,
                Study = study,
                SourceEligibility = source,
                PipelineRows = rowsPage.Rows,
                History = history,
                SnapshotCapturedAt = snapshot?.CapturedAt,
                NotFound = study is null && source is null && rowsPage.Rows.Count == 0 && history.Count == 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load analysis for {NctId}", normalised);
            return View(new AnalysisViewModel { NctId = normalised, ErrorMessage = ex.Message });
        }
    }

    /// <summary>
    /// JSON endpoint backing the Analysis-tab Search modal. Each non-empty
    /// query parameter contributes a case-insensitive substring filter; the
    /// gateway returns the matching rows from <c>public.eligibility_study_detail</c>
    /// (the persisted AACT snapshot the Analysis tab already renders).
    ///
    /// Capped at 100 results to keep the modal responsive — the message in
    /// the UI nudges the user to add more filters when the cap is hit.
    /// All filter fields empty → empty result list (the modal won't issue
    /// an unconditional whole-table dump).
    /// </summary>
    public async Task<IActionResult> SearchStudies(
        CancellationToken cancellationToken,
        string? nctId = null,
        string? briefTitle = null,
        string? officialTitle = null,
        string? overallStatus = null,
        string? phase = null,
        string? studyType = null,
        string? source = null,
        string? briefSummary = null,
        string? condition = null,
        string? gender = null,
        string? healthyVolunteers = null)
    {
        const int Limit = 100;
        try
        {
            var filter = new StudySearchFilter(
                nctId: nctId,
                briefTitle: briefTitle,
                officialTitle: officialTitle,
                overallStatus: overallStatus,
                phase: phase,
                studyType: studyType,
                source: source,
                briefSummary: briefSummary,
                condition: condition,
                gender: gender,
                healthyVolunteers: healthyVolunteers);

            if (filter.IsEmpty)
            {
                return Json(new
                {
                    results = Array.Empty<object>(),
                    truncated = false,
                    empty_filter = true
                });
            }

            // Limit + 1 so we can flag the result list as truncated without a
            // second COUNT round-trip; trim the extra row before returning.
            var rows = await _gateway.SearchStudyDetailsAsync(filter, Limit + 1, cancellationToken);
            var truncated = rows.Count > Limit;
            var trimmed = truncated ? rows.Take(Limit) : rows;

            return Json(new
            {
                results = trimmed.Select(r => new
                {
                    nct_id = r.NctId,
                    brief_title = r.BriefTitle,
                    overall_status = r.OverallStatus,
                    phase = r.Phase,
                    study_type = r.StudyType,
                    source = r.Source,
                    conditions = r.Conditions
                }),
                truncated,
                empty_filter = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search studies");
            return Json(new { error = ex.Message });
        }
    }

    /// <summary>
    /// JSON endpoint backing the Analysis-tab "Find Similar" modal. Ranks
    /// processed studies by cosine similarity to <paramref name="nctId"/>'s
    /// own topic embedding in <c>public.eligibility_study_embedding</c>; the
    /// source trial is excluded. <paramref name="matchPhase"/> and
    /// <paramref name="matchStudyType"/> apply same-phase / same-type filters
    /// driven by the source's own snapshot values.
    ///
    /// When the source trial has no embedding yet, returns
    /// <c>{ no_embedding: true }</c> so the modal can render a "run the
    /// embed-studies backfill" hint instead of an empty result list.
    /// </summary>
    public async Task<IActionResult> SimilarTrials(
        CancellationToken cancellationToken,
        string nctId,
        bool matchPhase = false,
        bool matchStudyType = false,
        int topN = 50)
    {
        var trimmed = nctId?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            return BadRequest(new { error = "nctId is required" });
        }

        try
        {
            var effectiveTopN = Math.Clamp(topN, 1, 200);
            var similar = await _gateway.FindSimilarTrialsToAsync(
                trimmed, effectiveTopN, matchPhase, matchStudyType, cancellationToken);

            if (similar is null)
            {
                return Json(new { no_embedding = true, nct_id = trimmed });
            }

            return Json(new
            {
                no_embedding = false,
                nct_id = trimmed,
                studies = similar.Select(x => new
                {
                    nct_id = x.NctId,
                    brief_title = x.BriefTitle,
                    phase = x.Phase,
                    study_type = x.StudyType,
                    overall_status = x.OverallStatus,
                    brief_summary = x.BriefSummary,
                    similarity = Math.Round(x.Similarity, 4)
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Find Similar failed for {NctId}", trimmed);
            return Json(new { error = ex.Message });
        }
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

using System.Threading.Channels;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Seeding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// Owner-only "Create database seed" feature. Runs pg_dump (in a background
/// <see cref="SeedJobRunner"/>) to produce a loader-compatible seed of the six seed
/// tables, then serves it for download so the owner can publish it as a new Release
/// asset. Modeled on the Tools-tab job actions, but Owner-only (not PipelineOps) and
/// with its own single-slot job. Shares the <see cref="RunGate"/> so it is mutually
/// exclusive with the extraction pipeline and the maintenance tools.
/// </summary>
[Authorize(Policy = "OwnerOnly")]
[Route("Seed")]
public class SeedController : Controller
{
    private const string Activity = "create-seed";

    private readonly SeedJobState _state;
    private readonly IAuditWriter _audit;
    private readonly ILogger<SeedController> _logger;

    public SeedController(SeedJobState state, IAuditWriter audit, ILogger<SeedController> logger)
    {
        _state = state;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Start a seed dump. 409 if the pipeline or another job holds the gate.</summary>
    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(
        [FromServices] RunGate gate,
        [FromServices] Channel<SeedJobRequest> channel)
    {
        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, Activity))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });
        }

        if (!channel.Writer.TryWrite(new SeedJobRequest(jobId)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("create", "database_seed", jobId.ToString(), "create database seed", HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = Activity });
    }

    /// <summary>Current (or most recently finished) seed job as JSON, polled by the
    /// modal while a job runs. Null when none has run since the host started.</summary>
    [HttpGet("State")]
    public IActionResult State() => Json(_state.Current);

    /// <summary>Cancel the in-flight seed dump. Only cancels a seed job - it will not
    /// touch a pipeline batch or a maintenance tool that happens to hold the gate.</summary>
    [HttpPost("Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel([FromServices] RunGate gate)
    {
        if (!string.Equals(gate.CurrentActivity, Activity, StringComparison.Ordinal))
        {
            return NotFound(new { error = "no seed job in progress" });
        }
        var runId = gate.CurrentRunId;
        if (!gate.Cancel())
        {
            return NotFound(new { error = "no seed job in progress" });
        }
        await _audit.WriteAsync("update", "database_seed", runId?.ToString(), "cancel database seed", HttpContext.RequestAborted);
        return Ok(new { cancelled_job_id = runId });
    }

    /// <summary>Stream the most recently produced seed archive as an attachment.
    /// 404 when there is nothing to download (none produced yet, or the last run
    /// failed/was cancelled).</summary>
    [HttpGet("Download")]
    public async Task<IActionResult> Download()
    {
        var download = _state.Download;
        if (download is null || !System.IO.File.Exists(download.Value.Path))
        {
            return NotFound(new { error = "No seed is available to download. Create one first." });
        }

        await _audit.WriteAsync("export", "database_seed", null, download.Value.FileName, HttpContext.RequestAborted);
        return PhysicalFile(download.Value.Path, "application/octet-stream", download.Value.FileName);
    }
}

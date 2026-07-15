using System.Threading.Channels;
using EligibilityProcessing.Core;   // IPostgresGateway
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Embeddings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// Owner-only embeddings export/import. Runs pg_dump / pg_restore of the single
/// <c>eligibility_study_embedding</c> corpus index in a background
/// <see cref="EmbeddingsJobRunner"/>, so an owner can publish the similarity index as
/// a release asset and others can import it to light up the Authoring Analysis tab's
/// Find Similar. Separate activity from the seed dump, but shares the
/// <see cref="RunGate"/> so it is mutually exclusive with the pipeline, the seed job,
/// and the maintenance tools. Import CLEARS the existing index first.
/// </summary>
[Authorize(Policy = "OwnerOnly")]
[Route("Embeddings")]
public class EmbeddingsController : Controller
{
    private const string ExportActivity = "export-embeddings";
    private const string ImportActivity = "import-embeddings";

    // Generous per-request cap for the upload import (a full-corpus archive can be ~1GB).
    private const long MaxUploadBytes = 5L * 1024 * 1024 * 1024; // 5 GB

    private readonly EmbeddingsJobState _state;
    private readonly IAuditWriter _audit;
    private readonly ILogger<EmbeddingsController> _logger;

    public EmbeddingsController(EmbeddingsJobState state, IAuditWriter audit, ILogger<EmbeddingsController> logger)
    {
        _state = state;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Current embeddings-index stats (row count + per-model breakdown), so the
    /// modal can show what is there before an export/import. Degrades to an "unavailable"
    /// payload when the DB can't be reached rather than 500ing the modal open.</summary>
    [HttpGet("Stats")]
    public async Task<IActionResult> Stats([FromServices] IPostgresGateway gateway)
    {
        try
        {
            var stats = await gateway.GetEmbeddingStatsAsync(HttpContext.RequestAborted);
            return Json(new
            {
                totalRows = stats.TotalRows,
                models = stats.Models.Select(m => new { model = m.Model, count = m.Count })
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read embedding stats");
            return Json(new { unavailable = true, error = ex.Message });
        }
    }

    /// <summary>Start an embeddings export. 409 if the pipeline or another job holds the gate.</summary>
    [HttpPost("Export")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Export(
        [FromServices] RunGate gate,
        [FromServices] Channel<EmbeddingsJobRequest> channel)
    {
        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, ExportActivity))
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });

        if (!channel.Writer.TryWrite(new EmbeddingsJobRequest(jobId, EmbeddingsJobKind.Export, null, null)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("export", "embeddings", jobId.ToString(), "export embeddings", HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = "export" });
    }

    /// <summary>Import an embeddings archive from a URL (release asset). The runner
    /// downloads it, clears the existing index, then restores. 409 if busy.</summary>
    [HttpPost("ImportUrl")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportUrl(
        [FromForm] string? url,
        [FromServices] RunGate gate,
        [FromServices] Channel<EmbeddingsJobRequest> channel)
    {
        var trimmed = url?.Trim() ?? "";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { error = "Enter a valid http(s) URL to an embeddings archive." });
        }

        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, ImportActivity))
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });

        if (!channel.Writer.TryWrite(new EmbeddingsJobRequest(jobId, EmbeddingsJobKind.Import, null, trimmed)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("import", "embeddings", jobId.ToString(), "import embeddings from url", HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = "import" });
    }

    /// <summary>Import an embeddings archive uploaded from the browser. Clears the
    /// existing index, then restores. 409 if busy.</summary>
    [HttpPost("ImportUpload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> ImportUpload(
        IFormFile? file,
        [FromServices] RunGate gate,
        [FromServices] Channel<EmbeddingsJobRequest> channel)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose an embeddings archive (.dump) to upload." });

        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, ImportActivity))
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });

        string stagedPath;
        try
        {
            Directory.CreateDirectory(EmbeddingsJobRunner.ImportDirectory);
            stagedPath = Path.Combine(EmbeddingsJobRunner.ImportDirectory, $"upload-{jobId:N}.dump");
            await using var dst = System.IO.File.Create(stagedPath);
            await file.CopyToAsync(dst, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            gate.Release();
            _logger.LogError(ex, "Failed to stage uploaded embeddings archive");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Could not stage the upload: " + ex.Message });
        }

        if (!channel.Writer.TryWrite(new EmbeddingsJobRequest(jobId, EmbeddingsJobKind.Import, stagedPath, null)))
        {
            gate.Release();
            TryDelete(stagedPath);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("import", "embeddings", jobId.ToString(), $"import embeddings from upload ({file.Length} bytes)", HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = "import" });
    }

    /// <summary>Current (or most recently finished) embeddings job as JSON, polled by
    /// the modal. Null when none has run since host start.</summary>
    [HttpGet("State")]
    public IActionResult State() => Json(_state.Current);

    /// <summary>Cancel the in-flight embeddings job. Only cancels an embeddings job - it
    /// will not touch a pipeline batch, the seed job, or a maintenance tool.</summary>
    [HttpPost("Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel([FromServices] RunGate gate)
    {
        var activity = gate.CurrentActivity;
        if (activity != ExportActivity && activity != ImportActivity)
            return NotFound(new { error = "no embeddings job in progress" });

        var runId = gate.CurrentRunId;
        if (!gate.Cancel())
            return NotFound(new { error = "no embeddings job in progress" });

        await _audit.WriteAsync("update", "embeddings", runId?.ToString(), "cancel embeddings job", HttpContext.RequestAborted);
        return Ok(new { cancelled_job_id = runId });
    }

    /// <summary>Stream the most recently produced export archive. 404 when there is
    /// nothing to download (none produced yet, last run was an import, or it failed).</summary>
    [HttpGet("Download")]
    public async Task<IActionResult> Download()
    {
        var download = _state.Download;
        if (download is null || !System.IO.File.Exists(download.Value.Path))
            return NotFound(new { error = "No embeddings archive is available to download. Export one first." });

        await _audit.WriteAsync("export", "embeddings", null, download.Value.FileName, HttpContext.RequestAborted);
        return PhysicalFile(download.Value.Path, "application/octet-stream", download.Value.FileName);
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* best effort */ }
    }
}

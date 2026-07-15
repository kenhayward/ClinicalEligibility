using System.Diagnostics;
using System.Threading.Channels;
using EligibilityProcessing.Core;   // IPostgresGateway, EmbeddingStats
using EligibilityProcessing.Data;   // PostgresOptions
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EligibilityProcessing.Web.Embeddings;

/// <summary>
/// Background worker for the owner-only embeddings export/import jobs, off the request
/// thread so they survive the browser closing - like <c>SeedJobRunner</c>. Export runs
/// pg_dump on the single embeddings table; import optionally downloads a release-asset
/// URL, clears the existing index (TRUNCATE via the gateway), then pg_restore's the
/// archive. Progress phases go into <see cref="EmbeddingsJobState"/>, which the modal
/// polls. Mutual exclusion is the shared <see cref="RunGate"/>, acquired by the
/// controller before enqueuing.
/// </summary>
internal sealed class EmbeddingsJobRunner : BackgroundService
{
    // Both are installed on PATH in the web image (postgresql18-client).
    private const string PgDumpExecutable = "pg_dump";
    private const string PgRestoreExecutable = "pg_restore";
    private const int StderrTailLines = 25;

    private readonly Channel<EmbeddingsJobRequest> _channel;
    private readonly RunGate _gate;
    private readonly EmbeddingsJobState _state;
    private readonly IOptions<PostgresOptions> _postgres;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EmbeddingsJobRunner> _logger;

    public EmbeddingsJobRunner(
        Channel<EmbeddingsJobRequest> channel,
        RunGate gate,
        EmbeddingsJobState state,
        IOptions<PostgresOptions> postgres,
        IServiceScopeFactory scopes,
        IHttpClientFactory httpFactory,
        ILogger<EmbeddingsJobRunner> logger)
    {
        _channel = channel;
        _gate = gate;
        _state = state;
        _postgres = postgres;
        _scopes = scopes;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Named HttpClient used for URL imports (configured with no timeout in Program.cs).</summary>
    internal const string HttpClientName = "embeddings-import";

    /// <summary>Where produced export archives are written; only the latest is kept.</summary>
    internal static string ExportDirectory => Path.Combine(Path.GetTempPath(), "eligibility-embeddings");

    /// <summary>Where uploaded / downloaded import archives are staged before restore.</summary>
    internal static string ImportDirectory => Path.Combine(Path.GetTempPath(), "eligibility-embeddings-in");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmbeddingsJobRunner started; awaiting export/import requests");
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var runToken = _gate.CurrentToken;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runToken);
            if (request.Kind == EmbeddingsJobKind.Export) _state.BeginExport(request.JobId);
            else _state.BeginImport(request.JobId);

            string status;
            string? error = null;
            try
            {
                (status, error) = request.Kind == EmbeddingsJobKind.Export
                    ? await RunExportAsync(linked.Token, runToken).ConfigureAwait(false)
                    : await RunImportAsync(request, linked.Token, runToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _state.Fail("cancelled", "host shutting down");
                _gate.Release();
                throw;
            }
            catch (Exception ex)
            {
                status = "failed";
                error = FriendlyError(ex);
                _logger.LogError(ex, "Embeddings {Kind} job {JobId} failed", request.Kind, request.JobId);
            }
            finally
            {
                _gate.Release();
            }

            if (status != "completed") _state.Fail(status, error);
        }
    }

    // ===== export =====

    private async Task<(string Status, string? Error)> RunExportAsync(CancellationToken linked, CancellationToken runToken)
    {
        var conn = _postgres.Value.ConnectionStringOutput;
        if (string.IsNullOrWhiteSpace(conn))
            return ("failed", "Output database connection string is not configured (Postgres:ConnectionStringOutput).");

        var stats = await StatsAsync(linked).ConfigureAwait(false);
        if (stats.TotalRows == 0)
            return ("failed", "No embeddings to export. Build the corpus index first (Tools -> embed-studies).");

        var models = stats.Models.Select(m => m.Model).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        _state.SetPhase($"exporting {stats.TotalRows:N0} embeddings");

        Directory.CreateDirectory(ExportDirectory);
        CleanDirectory(ExportDirectory, "*.dump");
        var fileName = EmbeddingsDump.SuggestedFileName(models, stats.TotalRows, DateTime.UtcNow);
        var filePath = Path.Combine(ExportDirectory, fileName);
        var invocation = EmbeddingsDump.BuildExportInvocation(conn, filePath);

        var (exit, tail, cancelled) = await RunPgAsync(PgDumpExecutable, invocation, linked, runToken).ConfigureAwait(false);
        if (cancelled) { TryDelete(filePath); return ("cancelled", null); }

        if (exit == 0 && File.Exists(filePath))
        {
            var size = new FileInfo(filePath).Length;
            _state.SucceedExport(fileName, size, filePath, stats.TotalRows, FormatModels(stats));
            _logger.LogInformation("Embeddings export completed: {File} ({Size} bytes, {Rows} rows)", fileName, size, stats.TotalRows);
            return ("completed", null);
        }

        TryDelete(filePath);
        return ("failed", tail);
    }

    // ===== import =====

    private async Task<(string Status, string? Error)> RunImportAsync(
        EmbeddingsJobRequest req, CancellationToken linked, CancellationToken runToken)
    {
        var conn = _postgres.Value.ConnectionStringOutput;
        if (string.IsNullOrWhiteSpace(conn))
            return ("failed", "Output database connection string is not configured (Postgres:ConnectionStringOutput).");

        string inputFile;
        if (!string.IsNullOrWhiteSpace(req.SourceUrl))
        {
            _state.SetPhase("downloading");
            Directory.CreateDirectory(ImportDirectory);
            inputFile = Path.Combine(ImportDirectory, $"download-{req.JobId:N}.dump");
            try
            {
                await DownloadAsync(req.SourceUrl!, inputFile, linked).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                TryDelete(inputFile);
                return ("cancelled", null);
            }
            catch (Exception ex)
            {
                TryDelete(inputFile);
                return ("failed", "Download failed: " + ex.Message);
            }
        }
        else if (!string.IsNullOrWhiteSpace(req.InputFilePath))
        {
            inputFile = req.InputFilePath!;
        }
        else
        {
            return ("failed", "No import source: neither an uploaded file nor a URL was provided.");
        }

        try
        {
            if (!File.Exists(inputFile))
                return ("failed", "The import archive is missing (upload or download did not complete).");

            _state.SetPhase("clearing existing embeddings");
            long cleared;
            using (var scope = _scopes.CreateScope())
            {
                cleared = await scope.ServiceProvider.GetRequiredService<IPostgresGateway>()
                    .ClearStudyEmbeddingsAsync(linked).ConfigureAwait(false);
            }

            _state.SetPhase("importing");
            var invocation = EmbeddingsDump.BuildRestoreInvocation(conn, inputFile);
            var (exit, tail, cancelled) = await RunPgAsync(PgRestoreExecutable, invocation, linked, runToken).ConfigureAwait(false);
            if (cancelled) return ("cancelled", null);
            if (exit != 0) return ("failed", "pg_restore failed: " + tail);

            var after = await StatsAsync(linked).ConfigureAwait(false);
            _state.SucceedImport(after.TotalRows, cleared, FormatModels(after));
            _logger.LogInformation("Embeddings import completed: cleared {Cleared}, now {Rows} rows", cleared, after.TotalRows);
            return ("completed", null);
        }
        finally
        {
            // Staged import file (uploaded or downloaded) is single-use.
            TryDelete(inputFile);
        }
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    // ===== helpers =====

    private async Task<EmbeddingStats> StatsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPostgresGateway>()
            .GetEmbeddingStatsAsync(ct).ConfigureAwait(false);
    }

    private static string? FormatModels(EmbeddingStats stats)
    {
        if (stats.Models.Count == 0) return null;
        return string.Join(", ", stats.Models.Select(m =>
            (string.IsNullOrWhiteSpace(m.Model) ? "(unnamed)" : m.Model) + $" ({m.Count:N0})"));
    }

    private async Task<(int ExitCode, string Tail, bool Cancelled)> RunPgAsync(
        string executable, EmbeddingsDump.PgInvocation invocation,
        CancellationToken linkedToken, CancellationToken runToken)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in invocation.Arguments) psi.ArgumentList.Add(arg);
        if (invocation.Password is not null) psi.Environment["PGPASSWORD"] = invocation.Password;

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stdout so the pipe can never fill and deadlock the child.
        var stdoutDrain = process.StandardOutput.ReadToEndAsync();

        var tail = new Queue<string>(StderrTailLines);
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(linkedToken).ConfigureAwait(false)) is not null)
            {
                if (tail.Count >= StderrTailLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            KillQuietly(process);
            return (-1, string.Join(" | ", tail), true);
        }
        finally
        {
            await stdoutDrain.ConfigureAwait(false);
        }

        var detail = tail.Count > 0 ? string.Join(" | ", tail) : $"{executable} exited with code {process.ExitCode}";
        return (process.ExitCode, detail, false);
    }

    private void CleanDirectory(string dir, string pattern)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, pattern)) TryDelete(f);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean {Dir}", dir);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception w when w.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase)
            || w.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            => "pg_dump/pg_restore was not found. The web image must include the postgresql client (postgresql18-client).",
        _ => ex.Message
    };
}

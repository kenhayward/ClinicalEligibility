using System.Diagnostics;
using System.Threading.Channels;
using EligibilityProcessing.Data;   // PostgresOptions
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EligibilityProcessing.Web.Seeding;

/// <summary>
/// Background worker that drains <see cref="SeedJobRequest"/> items and runs pg_dump
/// to produce a loader-compatible seed archive, off the request thread so it survives
/// the browser closing - exactly like <c>ToolJobRunner</c> / <c>BatchRunner</c>.
/// Progress (which table pg_dump is copying) is scraped from pg_dump --verbose stderr
/// into <see cref="SeedJobState"/>, which the modal polls. Mutual exclusion is the
/// shared <see cref="RunGate"/>, acquired by the controller before enqueuing.
/// </summary>
internal sealed class SeedJobRunner : BackgroundService
{
    // pg_dump is installed on PATH in the web image (postgresql18-client).
    private const string PgDumpExecutable = "pg_dump";
    private const int StderrTailLines = 25;

    private readonly Channel<SeedJobRequest> _channel;
    private readonly RunGate _gate;
    private readonly SeedJobState _state;
    private readonly IOptions<PostgresOptions> _postgres;
    private readonly ILogger<SeedJobRunner> _logger;

    public SeedJobRunner(
        Channel<SeedJobRequest> channel,
        RunGate gate,
        SeedJobState state,
        IOptions<PostgresOptions> postgres,
        ILogger<SeedJobRunner> logger)
    {
        _channel = channel;
        _gate = gate;
        _state = state;
        _postgres = postgres;
        _logger = logger;
    }

    /// <summary>Where produced seed archives are written. A dedicated subdir of the
    /// system temp dir; only the latest file is kept.</summary>
    internal static string SeedDirectory => Path.Combine(Path.GetTempPath(), "eligibility-seed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SeedJobRunner started; awaiting seed-dump requests");
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var runToken = _gate.CurrentToken;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runToken);
            _state.Begin(request.JobId);

            string status;
            string? error = null;
            try
            {
                (status, error) = await RunDumpAsync(request.JobId, linked.Token, runToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown: release the gate and surface as cancelled, then stop.
                _state.Fail("cancelled", "host shutting down");
                _gate.Release();
                throw;
            }
            catch (Exception ex)
            {
                status = "failed";
                error = FriendlyError(ex);
                _logger.LogError(ex, "Seed-dump job {JobId} failed", request.JobId);
            }
            finally
            {
                _gate.Release();
            }

            if (status != "completed")
            {
                _state.Fail(status, error);
            }
        }
    }

    /// <returns>(status, error) where status is "completed" | "cancelled" | "failed".
    /// On success the state is already marked completed with the file details.</returns>
    private async Task<(string Status, string? Error)> RunDumpAsync(
        Guid jobId, CancellationToken linkedToken, CancellationToken runToken)
    {
        var connectionString = _postgres.Value.ConnectionStringOutput;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return ("failed", "Output database connection string is not configured (Postgres:ConnectionStringOutput).");
        }

        Directory.CreateDirectory(SeedDirectory);
        CleanPreviousSeeds();

        var fileName = $"seed-{DateTime.UtcNow:yyyyMMdd-HHmmss}.dump";
        var filePath = Path.Combine(SeedDirectory, fileName);
        var invocation = SeedDump.BuildInvocation(connectionString, filePath);

        var psi = new ProcessStartInfo(PgDumpExecutable)
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

        // Drain stdout (empty when -f writes to a file, but read it so the pipe
        // can never fill and deadlock the child).
        var stdoutDrain = process.StandardOutput.ReadToEndAsync();

        var tail = new Queue<string>(StderrTailLines);
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(linkedToken).ConfigureAwait(false)) is not null)
            {
                var table = SeedDump.TryParseDumpingTable(line);
                if (table is not null) _state.AdvanceTable(StripSchema(table));

                if (tail.Count >= StderrTailLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            KillQuietly(process);
            TryDelete(filePath);
            _logger.LogInformation("Seed-dump job {JobId} cancelled by user", jobId);
            return ("cancelled", null);
        }
        finally
        {
            await stdoutDrain.ConfigureAwait(false);
        }

        if (process.ExitCode == 0 && File.Exists(filePath))
        {
            var size = new FileInfo(filePath).Length;
            _state.Succeed(fileName, size, filePath);
            _logger.LogInformation("Seed-dump job {JobId} completed: {File} ({Size} bytes)", jobId, fileName, size);
            return ("completed", null);
        }

        TryDelete(filePath);
        var detail = tail.Count > 0 ? string.Join(" | ", tail) : $"pg_dump exited with code {process.ExitCode}";
        return ("failed", detail);
    }

    private static string StripSchema(string qualified) =>
        qualified.StartsWith("public.", StringComparison.Ordinal) ? qualified["public.".Length..] : qualified;

    /// <summary>Keep only the newest seed - the previous one is superseded and would
    /// otherwise leak in the ephemeral temp dir.</summary>
    private void CleanPreviousSeeds()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(SeedDirectory, "*.dump"))
                TryDelete(f);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean previous seed files in {Dir}", SeedDirectory);
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
            => "pg_dump was not found. The web image must include the postgresql client (postgresql18-client).",
        _ => ex.Message
    };
}

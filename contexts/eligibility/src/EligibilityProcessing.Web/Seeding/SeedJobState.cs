namespace EligibilityProcessing.Web.Seeding;

/// <summary>
/// Process-wide, thread-safe holder for the single in-flight (or most recently
/// finished) seed-dump job. Mirrors <c>ToolJobState</c>: the job runs in a
/// background service, so this survives the browser closing and lets the modal
/// re-sync live progress via <c>GET /Seed/State</c>. Only one runs at a time (the
/// shared <c>RunGate</c>), so a single slot is enough. In-memory only - a host
/// restart loses it (and the produced file, which lives in the container's temp
/// dir, with it).
/// </summary>
public sealed class SeedJobState
{
    private readonly object _lock = new();

    private Guid _jobId;
    private string _status = "idle";
    private int _tablesDumped;
    private string? _currentTable;
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset? _endedAtUtc;
    private string? _fileName;
    private long? _fileSizeBytes;
    private string? _filePath;   // never serialized; used by the Download endpoint
    private string? _error;
    private bool _hasRun;

    /// <summary>Snapshot for JSON (GET /Seed/State) - elapsed is computed live so the
    /// timer keeps ticking between the sparse per-table progress updates. Null until
    /// a job has run since host start.</summary>
    public SeedJobView? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_hasRun) return null;
                var end = _endedAtUtc ?? DateTimeOffset.UtcNow;
                return new SeedJobView
                {
                    JobId = _jobId,
                    Status = _status,
                    TotalTables = SeedDump.SeedTables.Count,
                    TablesDumped = _tablesDumped,
                    CurrentTable = _currentTable,
                    ElapsedSeconds = Math.Max(0, (end - _startedAtUtc).TotalSeconds),
                    FileName = _fileName,
                    FileSizeBytes = _fileSizeBytes,
                    Downloadable = _status == "completed" && _filePath is not null,
                    Error = _error,
                    StartedAtUtc = _startedAtUtc,
                    EndedAtUtc = _endedAtUtc
                };
            }
        }
    }

    /// <summary>The completed file to stream, or null if there is nothing to
    /// download (no job has completed since host start, or the last one failed).</summary>
    public (string Path, string FileName)? Download
    {
        get
        {
            lock (_lock)
            {
                return _status == "completed" && _filePath is not null && _fileName is not null
                    ? (_filePath, _fileName)
                    : null;
            }
        }
    }

    public void Begin(Guid jobId)
    {
        lock (_lock)
        {
            _hasRun = true;
            _jobId = jobId;
            _status = "running";
            _tablesDumped = 0;
            _currentTable = null;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _endedAtUtc = null;
            _fileName = null;
            _fileSizeBytes = null;
            _filePath = null;
            _error = null;
        }
    }

    /// <summary>pg_dump has started copying <paramref name="table"/>'s data; count it.</summary>
    public void AdvanceTable(string table)
    {
        lock (_lock)
        {
            if (_status != "running") return;
            _currentTable = table;
            // Cap at the known table count - pg_dump may log lines we don't expect.
            _tablesDumped = Math.Min(_tablesDumped + 1, SeedDump.SeedTables.Count);
        }
    }

    public void Succeed(string fileName, long fileSizeBytes, string filePath)
    {
        lock (_lock)
        {
            _status = "completed";
            _tablesDumped = SeedDump.SeedTables.Count;
            _currentTable = null;
            _fileName = fileName;
            _fileSizeBytes = fileSizeBytes;
            _filePath = filePath;
            _endedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary><paramref name="status"/> is "failed" or "cancelled".</summary>
    public void Fail(string status, string? error)
    {
        lock (_lock)
        {
            _status = status;
            _currentTable = null;
            _fileName = null;
            _fileSizeBytes = null;
            _filePath = null;
            _error = error;
            _endedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>Serializable view of a seed-dump job. Property names camel-case into
/// JSON (GET /Seed/State), which the modal polls while a job runs.</summary>
public sealed record SeedJobView
{
    public Guid JobId { get; init; }
    public required string Status { get; init; }   // running | completed | cancelled | failed
    public int TotalTables { get; init; }
    public int TablesDumped { get; init; }
    public string? CurrentTable { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? FileName { get; init; }
    public long? FileSizeBytes { get; init; }
    public bool Downloadable { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    // NB: the absolute file path is deliberately NOT part of this view - it never
    // crosses the wire. The Download endpoint reads it from SeedJobState.Download.
}

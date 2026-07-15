namespace EligibilityProcessing.Web.Embeddings;

/// <summary>
/// Process-wide, thread-safe holder for the single in-flight (or most recently
/// finished) embeddings job - export or import. Mirrors <c>SeedJobState</c>: the job
/// runs in a background service, so this survives the browser closing and lets the
/// modal re-sync live via <c>GET /Embeddings/State</c>. Only one runs at a time (the
/// shared <c>RunGate</c>), so a single slot is enough. In-memory only.
/// </summary>
public sealed class EmbeddingsJobState
{
    private readonly object _lock = new();

    private bool _hasRun;
    private Guid _jobId;
    private string _kind = "export";
    private string _status = "idle";
    private string? _phase;
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset? _endedAtUtc;

    // Export result (also the download source).
    private string? _fileName;
    private long? _fileSizeBytes;
    private string? _filePath;   // never serialized; used by the Download endpoint

    // Shared result reporting.
    private long? _rowCount;     // export: rows dumped; import: rows present after restore
    private long? _rowsCleared;  // import: how many existing rows were cleared first
    private string? _models;     // comma-joined distinct model names
    private string? _error;

    /// <summary>Snapshot for JSON (GET /Embeddings/State). Elapsed ticks live between
    /// the coarse phase updates. Null until a job has run since host start.</summary>
    public EmbeddingsJobView? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_hasRun) return null;
                var end = _endedAtUtc ?? DateTimeOffset.UtcNow;
                return new EmbeddingsJobView
                {
                    JobId = _jobId,
                    Kind = _kind,
                    Status = _status,
                    Phase = _phase,
                    ElapsedSeconds = Math.Max(0, (end - _startedAtUtc).TotalSeconds),
                    FileName = _fileName,
                    FileSizeBytes = _fileSizeBytes,
                    RowCount = _rowCount,
                    RowsCleared = _rowsCleared,
                    Models = _models,
                    Downloadable = _status == "completed" && _kind == "export" && _filePath is not null,
                    Error = _error
                };
            }
        }
    }

    /// <summary>The completed export file to stream, or null (import jobs, or nothing
    /// produced yet / last run failed).</summary>
    public (string Path, string FileName)? Download
    {
        get
        {
            lock (_lock)
            {
                return _status == "completed" && _kind == "export" && _filePath is not null && _fileName is not null
                    ? (_filePath, _fileName)
                    : null;
            }
        }
    }

    public void BeginExport(Guid jobId) => Begin(jobId, "export", "exporting");

    public void BeginImport(Guid jobId) => Begin(jobId, "import", "preparing");

    private void Begin(Guid jobId, string kind, string phase)
    {
        lock (_lock)
        {
            _hasRun = true;
            _jobId = jobId;
            _kind = kind;
            _status = "running";
            _phase = phase;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _endedAtUtc = null;
            _fileName = null;
            _fileSizeBytes = null;
            _filePath = null;
            _rowCount = null;
            _rowsCleared = null;
            _models = null;
            _error = null;
        }
    }

    /// <summary>Update the human-readable phase (downloading / clearing existing / importing / ...).</summary>
    public void SetPhase(string phase)
    {
        lock (_lock)
        {
            if (_status == "running") _phase = phase;
        }
    }

    public void SucceedExport(string fileName, long fileSizeBytes, string filePath, long rowCount, string? models)
    {
        lock (_lock)
        {
            _status = "completed";
            _phase = null;
            _fileName = fileName;
            _fileSizeBytes = fileSizeBytes;
            _filePath = filePath;
            _rowCount = rowCount;
            _models = models;
            _endedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SucceedImport(long rowsImported, long rowsCleared, string? models)
    {
        lock (_lock)
        {
            _status = "completed";
            _phase = null;
            _rowCount = rowsImported;
            _rowsCleared = rowsCleared;
            _models = models;
            _endedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary><paramref name="status"/> is "failed" or "cancelled".</summary>
    public void Fail(string status, string? error)
    {
        lock (_lock)
        {
            _status = status;
            _phase = null;
            _fileName = null;
            _fileSizeBytes = null;
            _filePath = null;
            _error = error;
            _endedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>Serializable view of an embeddings job (GET /Embeddings/State), polled by
/// the modal while a job runs. Property names camel-case into JSON.</summary>
public sealed record EmbeddingsJobView
{
    public Guid JobId { get; init; }
    public required string Kind { get; init; }     // export | import
    public required string Status { get; init; }   // running | completed | cancelled | failed
    public string? Phase { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? FileName { get; init; }
    public long? FileSizeBytes { get; init; }
    public long? RowCount { get; init; }
    public long? RowsCleared { get; init; }
    public string? Models { get; init; }
    public bool Downloadable { get; init; }
    public string? Error { get; init; }
    // NB: the absolute file path is deliberately NOT on the view - it never crosses
    // the wire. The Download endpoint reads it from EmbeddingsJobState.Download.
}

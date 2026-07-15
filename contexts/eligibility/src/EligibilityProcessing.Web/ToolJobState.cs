using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web;

/// <summary>
/// Process-wide, thread-safe holder for the single in-flight (or most recently
/// finished) maintenance tool job. Because the job runs in a <see cref="ToolJobRunner"/>
/// background service - not on a request thread - this state survives the browser
/// closing and lets a freshly loaded Tools tab render the running job's live
/// metrics (read via <c>GET /Home/ToolState</c>), with SignalR pushing updates
/// thereafter. Only one job runs at a time (the shared <see cref="RunGate"/>), so a
/// single slot is enough.
///
/// Like the rest of the dashboard's run state, this is in-memory: a host restart
/// loses it (the in-process job dies with the host anyway).
/// </summary>
public sealed class ToolJobState
{
    private readonly object _lock = new();
    private ToolJobView? _current;

    /// <summary>The running job, or the last one that finished, or null if none has
    /// run since the host started.</summary>
    public ToolJobView? Current
    {
        get { lock (_lock) return _current; }
    }

    public void Begin(Guid jobId, ToolJobKind kind, string options)
    {
        lock (_lock)
        {
            _current = new ToolJobView
            {
                JobId = jobId,
                Kind = KindName(kind),
                Status = "running",
                Options = options,
                Total = 0,
                Processed = 0,
                ElapsedSeconds = 0,
                Metrics = Array.Empty<ToolMetricView>(),
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = null
            };
        }
    }

    public void Update(ToolJobSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_current is null) return;
            _current = _current with
            {
                Total = snapshot.Total,
                Processed = snapshot.Processed,
                ElapsedSeconds = snapshot.Elapsed.TotalSeconds,
                Metrics = snapshot.Metrics
                    .Select(m => new ToolMetricView(m.Label, m.Value))
                    .ToArray()
            };
        }
    }

    public void Finish(string status)
    {
        lock (_lock)
        {
            if (_current is null) return;
            _current = _current with { Status = status, EndedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    public static string KindName(ToolJobKind kind) => kind switch
    {
        ToolJobKind.NormalizeUmls => "normalize-umls",
        ToolJobKind.EmbedStudies => "embed-studies",
        _ => kind.ToString()
    };
}

/// <summary>Serializable view of a tool job (JSON for <c>GET /Home/ToolState</c> and
/// the model for the Tools view). Property names camel-case into JSON to match the
/// SignalR event payloads the same page consumes.</summary>
public sealed record ToolJobView
{
    public Guid JobId { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }   // running | completed | cancelled | failed
    public required string Options { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public double ElapsedSeconds { get; init; }
    public IReadOnlyList<ToolMetricView> Metrics { get; init; } = Array.Empty<ToolMetricView>();
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
}

public sealed record ToolMetricView(string Label, long Value);

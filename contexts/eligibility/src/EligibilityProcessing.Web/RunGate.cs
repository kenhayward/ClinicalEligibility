namespace EligibilityProcessing.Web;

/// <summary>
/// Singleton lock guaranteeing one in-flight batch at a time. Acquired by the
/// <c>/trigger</c> endpoint (and the dashboard's Trigger button) before writing
/// to the work channel; released by <see cref="BatchRunner"/> when the
/// orchestrator completes.
///
/// A second trigger arriving while a run is queued or executing sees
/// <see cref="CurrentRunId"/> set and gets <c>409 Conflict</c> with the
/// running RunId in the response body.
///
/// Each acquired run also gets a <see cref="CancellationTokenSource"/> that
/// <see cref="BatchRunner"/> links into the orchestrator's cancellation token.
/// The dashboard's Cancel button calls <see cref="Cancel"/>; BatchRunner
/// detects the user-cancel (vs. host shutdown) and broadcasts
/// <c>OnBatchCancelledAsync</c> so the live feed closes the loop.
/// </summary>
public sealed class RunGate : IDisposable
{
    private readonly object _lock = new();
    private Guid? _currentRunId;
    private string? _activity;
    private CancellationTokenSource? _cts;

    public Guid? CurrentRunId
    {
        get { lock (_lock) return _currentRunId; }
    }

    /// <summary>
    /// What the in-flight run is doing - "batch" for the extraction pipeline, or a
    /// tool job kind ("normalize-umls" / "embed-studies"). Null when idle. Lets the
    /// dashboard and the Tools tab label the single shared lock so the user can see
    /// *why* a trigger is blocked. The gate is one exclusivity domain across the
    /// main pipeline and every tool, so only one of them runs at a time.
    /// </summary>
    public string? CurrentActivity
    {
        get { lock (_lock) return _activity; }
    }

    /// <summary>True when any run (batch or tool job) holds the lock.</summary>
    public bool IsBusy
    {
        get { lock (_lock) return _currentRunId.HasValue; }
    }

    /// <summary>
    /// Token that fires when <see cref="Cancel"/> is called for the current
    /// run. Returns <see cref="CancellationToken.None"/> when no run is active.
    /// </summary>
    public CancellationToken CurrentToken
    {
        get { lock (_lock) return _cts?.Token ?? CancellationToken.None; }
    }

    public bool TryAcquire(Guid runId, string activity = "batch")
    {
        lock (_lock)
        {
            if (_currentRunId.HasValue) return false;
            _currentRunId = runId;
            _activity = activity;
            _cts = new CancellationTokenSource();
            return true;
        }
    }

    /// <summary>
    /// Signal the current run to cancel. Returns <c>false</c> if no run is
    /// active or the run was already cancelled.
    /// </summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_cts is null || _cts.IsCancellationRequested) return false;
            _cts.Cancel();
            return true;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _currentRunId = null;
            _activity = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}

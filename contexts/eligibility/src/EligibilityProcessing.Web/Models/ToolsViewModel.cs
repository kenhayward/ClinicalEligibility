namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Backs the Tools tab (<c>/Home/Tools</c>): the live "remaining work" counts for
/// each maintenance tool, the defaults for its options, the shared-lock activity
/// (so the UI can disable Run while anything is in flight), and the current/last
/// tool job so a freshly loaded tab renders an in-progress job. Any field tolerates
/// a backend hiccup - the view shows an inline error rather than 500ing.
/// </summary>
public sealed class ToolsViewModel
{
    public int NormalizeRemaining { get; init; }
    public int EmbedRemaining { get; init; }
    public string EmbeddingModel { get; init; } = "";
    public int DefaultConcurrency { get; init; } = 1;

    /// <summary>What currently holds the shared RunGate ("batch" / "normalize-umls"
    /// / "embed-studies"), or null when idle. Drives the initial disabled state.</summary>
    public string? BusyActivity { get; init; }

    /// <summary>The running (or most recently finished) tool job, or null.</summary>
    public ToolJobView? Current { get; init; }

    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

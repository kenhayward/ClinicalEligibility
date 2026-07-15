namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Form-post input for the Owner-only Runtime Parameters modal. Numeric fields are
/// strings so the controller can validate and return a friendly error rather than
/// a model-binding 400 on bad input. <c>ApiKey</c> is intentionally absent — it is
/// a secret and never round-trips through this panel.
/// </summary>
public sealed class RuntimeParametersForm
{
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public string? Temperature { get; set; }              // parsed as double, 0–2
    public string? MaxTokens { get; set; }                // parsed as int, > 0
    public bool EnableReasoning { get; set; }             // master on/off for reasoning_effort
    public string? ReasoningEffort { get; set; }          // "" / low / medium / high
    public bool EnableReasoningEscalation { get; set; }
    public string? EscalateReasoningEffort { get; set; }  // "" / low / medium / high
    public string? ConcurrencyCap { get; set; }           // parsed as int, 1–256; applies next run
    public bool NormalizeEnableReasoning { get; set; }    // master on/off for the normalize call's reasoning_effort
}

using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web;

/// <summary>
/// Work item written by the Tools-tab POST actions and drained by
/// <see cref="ToolJobRunner"/>. Exactly one of <see cref="Normalize"/> /
/// <see cref="Embed"/> is set, matching <see cref="Kind"/>. The endpoint acquires
/// the shared <see cref="RunGate"/> (so a tool job is mutually exclusive with the
/// main pipeline and the other tool) before writing, and returns 202 immediately;
/// the runner picks the same <see cref="JobId"/> up when it begins.
/// </summary>
public sealed record ToolJobRequest(
    Guid JobId,
    ToolJobKind Kind,
    NormalizeUmlsOptions? Normalize,
    EmbedStudiesOptions? Embed);

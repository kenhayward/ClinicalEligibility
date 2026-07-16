using System.Globalization;
using EligibilityProcessing.Core;
using EligibilityProcessing.Data;
using EligibilityProcessing.Llm;
using EligibilityProcessing.Umls;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// Owner-only "Runtime Parameters" panel. Reads and mutates the live
/// <see cref="LlmOptions"/> singleton so the LLM tuning knobs (reasoning effort,
/// token budget, model, etc.) can be changed without a redeploy.
/// <para>
/// The mutation works because <c>IOptions&lt;LlmOptions&gt;</c> is a singleton:
/// <c>LlmClient</c> holds a reference to this same <see cref="IOptions{T}.Value"/>
/// instance and reads its properties live on every call, so a change here takes
/// effect on the next extraction. Nothing is persisted, so a restart re-binds the
/// saved configuration (appsettings/.env) — changes are transient by design. Do NOT
/// switch to IOptionsMonitor: a config reload would clobber the in-memory edit.
/// </para>
/// <para>
/// Thread-safety: writing the singleton's scalar fields while a batch is mid-flight
/// is non-atomic and a concurrent call may pick up a change mid-batch. That is
/// acceptable for this manual, single-operator tuning panel; no locking is added.
/// </para>
/// </summary>
[Authorize(Policy = "OwnerOnly")]
[Route("RuntimeParameters")]
public class RuntimeParametersController : Controller
{
    // Reasoning-effort values the LLM endpoint accepts. "" omits the field.
    private static readonly string[] EffortValues = { "", "low", "medium", "high" };

    private readonly IOptions<LlmOptions> _llm;   // singleton — .Value is the live instance
    private readonly IOptions<LlmNormalizeOptions> _normalize;
    private readonly IOptions<EmbeddingOptions> _embedding;
    private readonly IOptions<OrchestratorOptions> _orchestrator;
    private readonly IOptions<UmlsOptions> _umls;
    private readonly IOptions<PostgresOptions> _postgres;
    private readonly IAuditWriter _audit;
    private readonly ILogger<RuntimeParametersController> _logger;

    public RuntimeParametersController(
        IOptions<LlmOptions> llm,
        IOptions<LlmNormalizeOptions> normalize,
        IOptions<EmbeddingOptions> embedding,
        IOptions<OrchestratorOptions> orchestrator,
        IOptions<UmlsOptions> umls,
        IOptions<PostgresOptions> postgres,
        IAuditWriter audit,
        ILogger<RuntimeParametersController> logger)
    {
        _llm = llm;
        _normalize = normalize;
        _embedding = embedding;
        _orchestrator = orchestrator;
        _umls = umls;
        _postgres = postgres;
        _audit = audit;
        _logger = logger;
    }

    // The normalize / embedding clients fall back to the extraction endpoint when
    // their own BaseUrl is unset, and the normalize client also falls back to the
    // extraction model. Show the effective value, flagging inherited ones.
    private static string ResolveInherited(string? specific, string fallback) =>
        string.IsNullOrWhiteSpace(specific) ? $"{fallback} (inherited)" : specific.Trim();

    // The embedding model has no fallback (EmbeddingClient sends it verbatim), so a
    // blank value means embeddings won't work — surface that rather than implying
    // inheritance like the endpoints do.
    private static string ShowOrUnset(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not set)" : value.Trim();

    // Mirrors the composition-root switch EXACTLY (no trim): "postgres" → the
    // local store, anything else → the REST API. Shows the resolver that's truly
    // in effect, so a typo/whitespace that reverts to REST is visible.
    private static string EffectiveUmlsBackend(string? backend) =>
        string.Equals(backend ?? "", "postgres", StringComparison.OrdinalIgnoreCase)
            ? "postgres (local umls.* store)"
            : "rest (UTS REST API)";

    /// <summary>
    /// Host/port/database of a Postgres connection string, for answering "is the
    /// source a local AACT copy or a remote one?" at a glance.
    /// <para>
    /// SECURITY: the connection string carries credentials, so this deliberately
    /// projects ONLY Host, Port, and Database. Username and Password are never
    /// read, so they cannot leak into the response even if the parse succeeds.
    /// A malformed string is reported as "(unparseable)" rather than echoed back,
    /// since echoing it would put the password on screen.
    /// </para>
    /// </summary>
    internal static string DescribeConnection(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "(not set)";
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(b.Host)) return "(no host)";
            var db = string.IsNullOrWhiteSpace(b.Database) ? "?" : b.Database;
            return $"{b.Host}:{b.Port}/{db}";
        }
        catch (Exception)
        {
            // Never echo the raw string - it contains the password.
            return "(unparseable)";
        }
    }

    /// <summary>Current live values. Re-read on every call so the modal reflects
    /// the latest in-memory state. The secret ApiKey is never serialized.</summary>
    [HttpGet("Current")]
    public IActionResult Current()
    {
        var o = _llm.Value;
        return Json(new
        {
            editable = new
            {
                model = o.Model,
                baseUrl = o.BaseUrl,
                temperature = o.Temperature,
                maxTokens = o.MaxTokens,
                enableReasoning = o.EnableReasoning,
                reasoningEffort = o.ReasoningEffort,
                enableReasoningEscalation = o.EnableReasoningEscalation,
                escalateReasoningEffort = o.EscalateReasoningEffort,
                // The pipeline's actual parallelism (Pipeline:LlmConcurrencyCap →
                // Parallel.ForEachAsync MaxDegreeOfParallelism). NOT Llm:ConcurrencyCap,
                // which is vestigial. Read fresh by the scoped orchestrator at the
                // start of each run, so an edit here applies to the next run.
                concurrencyCap = _orchestrator.Value.LlmConcurrencyCap,
                // Independent master switch for the Authoring normalize call.
                normalizeEnableReasoning = _normalize.Value.EnableReasoning
            },
            // Display-only: these are baked into the resilience pipeline / read at
            // startup, so a change would need a restart to take effect.
            readOnly = new
            {
                timeoutSeconds = o.TimeoutSeconds,
                retryCount = o.RetryCount,
                retryDelaySeconds = o.RetryDelaySeconds,
                normalizeMaxTokens = o.NormalizeMaxTokens,
                normalizeEndpoint = ResolveInherited(_normalize.Value.BaseUrl, o.BaseUrl),
                normalizeModel = ResolveInherited(_normalize.Value.Model, o.Model),
                embeddingEndpoint = ResolveInherited(_embedding.Value.BaseUrl, o.BaseUrl),
                embeddingModel = ShowOrUnset(_embedding.Value.Model),
                // The UMLS resolver actually in effect — computed with the SAME
                // condition as the composition-root switch (no trim), so a stray
                // space in Umls:Backend that silently reverts to REST shows here as
                // "rest". Lets an operator confirm "am I really on postgres?".
                umlsBackend = EffectiveUmlsBackend(_umls.Value.Backend),
                // Which Postgres the trial selection actually reads from, so an
                // operator can tell a local AACT copy from a remote one at a
                // glance. Host/port/database only - see DescribeConnection; the
                // credentials in the connection string are never projected.
                sourceDatabase = DescribeConnection(_postgres.Value.ConnectionStringSource),
                outputDatabase = DescribeConnection(_postgres.Value.ConnectionStringOutput)
            }
            // ApiKey deliberately omitted (secret).
        });
    }

    /// <summary>Validate and apply editable values to the live singleton.
    /// Rejects bad input rather than clamping so operator mistakes surface.</summary>
    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(RuntimeParametersForm form, CancellationToken cancellationToken)
    {
        if (form is null || string.IsNullOrWhiteSpace(form.Model))
            return BadRequest(new { error = "Model is required." });

        if (string.IsNullOrWhiteSpace(form.BaseUrl) ||
            !Uri.TryCreate(form.BaseUrl.Trim(), UriKind.Absolute, out _))
            return BadRequest(new { error = "Base URL must be an absolute URL (e.g. http://localhost:8080/v1)." });

        if (!double.TryParse(form.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ||
            temperature < 0 || temperature > 2)
            return BadRequest(new { error = "Temperature must be a number between 0 and 2." });

        if (!int.TryParse(form.MaxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens) ||
            maxTokens <= 0)
            return BadRequest(new { error = "Max tokens must be a positive integer." });

        var effort = (form.ReasoningEffort ?? "").Trim().ToLowerInvariant();
        var escalate = (form.EscalateReasoningEffort ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(EffortValues, effort) < 0 || Array.IndexOf(EffortValues, escalate) < 0)
            return BadRequest(new { error = "Reasoning effort must be empty, low, medium, or high." });

        // Upper bound is a guardrail against a fat-fingered value that would spawn a
        // runaway number of parallel trials; 256 is far above any real slot count.
        if (!int.TryParse(form.ConcurrencyCap, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrencyCap) ||
            concurrencyCap < 1 || concurrencyCap > 256)
            return BadRequest(new { error = "Concurrency cap must be an integer between 1 and 256." });

        var o = _llm.Value;   // mutate the live singleton
        o.Model = form.Model.Trim();
        o.BaseUrl = form.BaseUrl.Trim();
        o.Temperature = temperature;
        o.MaxTokens = maxTokens;
        o.EnableReasoning = form.EnableReasoning;
        o.ReasoningEffort = effort;
        o.EnableReasoningEscalation = form.EnableReasoningEscalation;
        o.EscalateReasoningEffort = escalate;

        // The scoped orchestrator reads this at the start of each run, so the new
        // value applies to the next run — not any run currently in flight.
        _orchestrator.Value.LlmConcurrencyCap = concurrencyCap;

        // Normalize call's reasoning toggle (independent of the extraction one).
        // The normalizer is constructed per call from this singleton, so the next
        // normalize request picks it up.
        _normalize.Value.EnableReasoning = form.NormalizeEnableReasoning;

        var detail = $"model={o.Model}; baseUrl={o.BaseUrl}; temperature={o.Temperature.ToString(CultureInfo.InvariantCulture)}; " +
                     $"maxTokens={o.MaxTokens}; enableReasoning={o.EnableReasoning}; reasoningEffort={o.ReasoningEffort}; " +
                     $"enableReasoningEscalation={o.EnableReasoningEscalation}; escalateReasoningEffort={o.EscalateReasoningEffort}; " +
                     $"concurrencyCap={concurrencyCap}; normalizeEnableReasoning={_normalize.Value.EnableReasoning}";
        _logger.LogInformation("Runtime LLM parameters updated (transient, reverts on restart): {Detail}", detail);
        await _audit.WriteAsync("update", "runtime_parameters", null, detail, cancellationToken);

        return Json(new { ok = true });
    }
}

using System.Globalization;
using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Models;

/// <summary>
/// JSON shape behind <c>GET /Home/Metrics</c>, which the dashboard fetches
/// client-side so it can show a real skeleton while the ~700ms corpus aggregate
/// resolves, refresh itself when a run finishes, and give the Reload button
/// something to do that isn't a page reload.
/// <para>
/// Every figure ships as a raw number AND a preformatted display string. The
/// numbers are for arithmetic the client genuinely has to do (sparkline
/// geometry, donut/pie angles); the strings are so that number formatting stays
/// in one place. Formatting client-side instead would mean re-implementing every
/// N0/P1/N2 in JS and would silently swap the SERVER's culture for the VIEWER's
/// browser locale - the separators would start following whoever is looking.
/// </para>
/// </summary>
public sealed class DashboardMetricsPayload
{
    /// <summary>
    /// Prompt/completion pricing, USD per 1M tokens. Carried over verbatim from
    /// the Razor view this endpoint replaced, hardcoded exactly as it was.
    /// <para>
    /// Worth knowing what this figure is and is not: inference runs on
    /// self-hosted GPUs behind the HAProxy pool, so nobody is billed per token.
    /// These are commercial API list rates, which makes the total a notional
    /// "what this corpus would have cost to buy" rather than money spent.
    /// </para>
    /// </summary>
    public const decimal PromptUsdPerMillion = 5m;
    public const decimal CompletionUsdPerMillion = 25m;

    /// <summary>
    /// The failure buckets, sourced from the persisted status constants rather
    /// than a literal list, so a new status cannot be added to the pipeline and
    /// silently go unreported here.
    /// <para>
    /// Deliberately excludes <c>parse_empty</c> (a valid terminal state - the
    /// LLM answered and there was genuinely nothing to extract) and the
    /// embeddings backlog. Both are "needs attention", neither is a failure, and
    /// folding them in would have made parse_empty the largest slice of a
    /// failure chart. They travel as their own fields below.
    /// </para>
    /// <para>
    /// <c>interrupted</c> IS here: the host died mid-trial, so nothing is known
    /// about the outcome, and the failure surface is the only place those trials
    /// are visible at all. It is not a failure OF the trial, which is why
    /// History badges it amber - keep that distinction when colouring it.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> FailureStatuses = new[]
    {
        StudyExecution.StatusLlmFailed,
        StudyExecution.StatusParseInvalidJson,
        StudyExecution.StatusPersistFailed,
        StudyExecution.StatusFailed,
        StudyExecution.StatusInterrupted
    };

    public long EligibilityRowCount { get; init; }
    public string EligibilityRowCountText { get; init; } = "";

    public long StudiesSuccessful { get; init; }
    public string StudiesSuccessfulText { get; init; } = "";

    public long StudiesFailedLatest { get; init; }
    public string StudiesFailedLatestText { get; init; } = "";

    public long StudiesAttempted { get; init; }
    public string StudiesAttemptedText { get; init; } = "";

    public long ParseEmpty { get; init; }
    public string ParseEmptyText { get; init; } = "";

    public long StudiesWithoutEmbeddings { get; init; }
    public string StudiesWithoutEmbeddingsText { get; init; } = "";

    /// <summary>0..1. The CORPUS-WIDE rate (non-null concept_code over all
    /// eligibility rows) - not the most recent run's. A per-run rate lives on
    /// each <see cref="RunPoint"/>; do not compare the two on one axis.</summary>
    public double ResolutionRate { get; init; }
    public string ResolutionRateText { get; init; } = "";

    /// <summary>Null when no AACT source is reachable - which includes the
    /// seeded quickstart, where there is no ctgov schema at all. Null means
    /// "unknown", NOT zero: render nothing rather than a confident 0.</summary>
    public long? SourceTrialTotal { get; init; }
    public string? SourceTrialTotalText { get; init; }

    /// <summary>Approximate, and null on the same terms as
    /// <see cref="SourceTrialTotal"/>. Derived from an UNFILTERED source count,
    /// so it overstates by the never-selectable trials and bottoms out near
    /// ~1,700 rather than 0. The exact figure is on-demand via
    /// <c>/Home/ExactRemaining</c> (~26s), which is why it is not here.</summary>
    public long? TrialsRemaining { get; init; }
    public string? TrialsRemainingText { get; init; }

    public IReadOnlyList<FailureBucket> Failures { get; init; } = Array.Empty<FailureBucket>();
    public TokenUsage Tokens { get; init; } = new();

    /// <summary>Most recent first. Feeds the sparklines.</summary>
    public IReadOnlyList<RunPoint> Runs { get; init; } = Array.Empty<RunPoint>();

    public static DashboardMetricsPayload From(DashboardMetrics m, IReadOnlyList<RunMetrics> runs)
    {
        var promptCost = (decimal)m.PromptTokens / 1_000_000m * PromptUsdPerMillion;
        var completionCost = (decimal)m.CompletionTokens / 1_000_000m * CompletionUsdPerMillion;

        return new DashboardMetricsPayload
        {
            EligibilityRowCount = m.EligibilityRowCount,
            EligibilityRowCountText = N0(m.EligibilityRowCount),
            StudiesSuccessful = m.StudiesSuccessful,
            StudiesSuccessfulText = N0(m.StudiesSuccessful),
            StudiesFailedLatest = m.StudiesFailedLatest,
            StudiesFailedLatestText = N0(m.StudiesFailedLatest),
            StudiesAttempted = m.StudiesAttempted,
            StudiesAttemptedText = N0(m.StudiesAttempted),
            ParseEmpty = m.ParseEmpty,
            ParseEmptyText = N0(m.ParseEmpty),
            StudiesWithoutEmbeddings = m.StudiesWithoutEmbeddings,
            StudiesWithoutEmbeddingsText = N0(m.StudiesWithoutEmbeddings),
            ResolutionRate = m.ResolutionRate,
            ResolutionRateText = m.ResolutionRate.ToString("P1"),
            SourceTrialTotal = m.SourceTrialTotal,
            SourceTrialTotalText = m.SourceTrialTotal.HasValue ? N0(m.SourceTrialTotal.Value) : null,
            TrialsRemaining = m.TrialsRemaining,
            TrialsRemainingText = m.TrialsRemaining.HasValue ? N0(m.TrialsRemaining.Value) : null,

            // Every bucket, including the zeros: the client decides what to hide.
            // Emitting only non-zero buckets here would make "no llm_failed at
            // all" and "llm_failed not reported" the same payload.
            Failures = FailureStatuses
                .Select(s =>
                {
                    var count = m.FailuresByStatus.TryGetValue(s, out var c) ? c : 0L;
                    return new FailureBucket { Status = s, Count = count, CountText = N0(count) };
                })
                .ToList(),

            Tokens = new TokenUsage
            {
                Prompt = m.PromptTokens,
                PromptText = N0(m.PromptTokens),
                PromptCostText = Usd(promptCost),
                Completion = m.CompletionTokens,
                CompletionText = N0(m.CompletionTokens),
                CompletionCostText = Usd(completionCost),
                Total = m.TokensUsed,
                TotalText = N0(m.TokensUsed),
                TotalCostText = Usd(promptCost + completionCost)
            },

            Runs = runs.Select(r => new RunPoint
            {
                RunId = r.RunId,
                StartedAt = r.StartedAt,
                Status = r.Status,
                StudiesProcessed = r.StudiesProcessed,
                RowsPersisted = r.RowsPersisted,
                ResolutionRate = r.ResolutionRate
            }).ToList()
        };
    }

    private static string N0(long value) => value.ToString("N0");

    // "$" + N2, matching the view this replaced. Not C2: that would bring the
    // server culture's own currency symbol along, and these are US list prices.
    private static string Usd(decimal value) =>
        "$" + value.ToString("N2", CultureInfo.CurrentCulture);

    public sealed class FailureBucket
    {
        public string Status { get; init; } = "";
        public long Count { get; init; }
        public string CountText { get; init; } = "";
    }

    public sealed class TokenUsage
    {
        public long Prompt { get; init; }
        public string PromptText { get; init; } = "";
        public string PromptCostText { get; init; } = "";
        public long Completion { get; init; }
        public string CompletionText { get; init; } = "";
        public string CompletionCostText { get; init; } = "";
        public long Total { get; init; }
        public string TotalText { get; init; } = "";
        public string TotalCostText { get; init; } = "";
    }

    /// <summary>One past run, for the sparklines. <see cref="ResolutionRate"/>
    /// here is PER RUN and is a different measure from the payload's
    /// corpus-wide one.</summary>
    public sealed class RunPoint
    {
        public Guid RunId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public string Status { get; init; } = "";
        public int StudiesProcessed { get; init; }
        public int RowsPersisted { get; init; }
        public double ResolutionRate { get; init; }
    }
}

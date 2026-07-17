using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Models;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// The shape behind GET /Home/Metrics. Tested directly rather than over HTTP:
/// the web test harness points at a placeholder connection string, so every
/// request through the controller lands on the read-tolerant error path and
/// never exercises the formatting or the arithmetic at all.
/// </summary>
public class DashboardMetricsPayloadTests
{
    private static DashboardMetrics Metrics(
        long promptTokens = 0,
        long completionTokens = 0,
        double resolutionRate = 0,
        IReadOnlyDictionary<string, long>? failures = null,
        long studiesAttempted = 0,
        long? sourceTrialTotal = null) =>
        new(
            eligibilityRowCount: 4_032_035,
            studiesSuccessful: 287_670,
            studiesFailedLatest: 1_192,
            resolutionRate: resolutionRate,
            promptTokens: promptTokens,
            completionTokens: completionTokens,
            failuresByStatus: failures ?? new Dictionary<string, long>(),
            studiesWithoutEmbeddings: 6,
            parseEmpty: 803,
            studiesAttempted: studiesAttempted,
            sourceTrialTotal: sourceTrialTotal);

    private static RunMetrics Run(double resolutionRate, int rowsPersisted) =>
        new(
            runId: Guid.NewGuid(),
            startedAt: DateTimeOffset.UtcNow,
            endedAt: DateTimeOffset.UtcNow,
            triggerSource: "form",
            studyCount: 10,
            studiesProcessed: 10,
            rowsPersisted: rowsPersisted,
            resolutionRate: resolutionRate,
            status: "success",
            errorSummary: "");

    // ===== token cost =====
    // Carried over from the Razor view verbatim: $5/1M prompt, $25/1M completion,
    // decimal so the cents don't drift, "$" + N2 rather than C2 (these are US
    // list prices, not the server culture's currency).

    [Fact]
    public void Token_costs_use_the_documented_per_million_rates()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(promptTokens: 418_200_000, completionTokens: 583_700_000),
            Array.Empty<RunMetrics>());

        Assert.Equal("$2,091.00", payload.Tokens.PromptCostText);      // 418.2 * 5
        Assert.Equal("$14,592.50", payload.Tokens.CompletionCostText); // 583.7 * 25
        Assert.Equal("$16,683.50", payload.Tokens.TotalCostText);
    }

    [Fact]
    public void Token_totals_are_the_sum_and_are_thousands_separated()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(promptTokens: 418_200_000, completionTokens: 583_700_000),
            Array.Empty<RunMetrics>());

        Assert.Equal(1_001_900_000, payload.Tokens.Total);
        Assert.Equal("1,001,900,000", payload.Tokens.TotalText);
        Assert.Equal("418,200,000", payload.Tokens.PromptText);
    }

    // Divide-by-nothing rather than divide-by-zero, but a fresh corpus is the
    // state a brand-new deployment is in, so it must not render as blank.
    [Fact]
    public void Zero_tokens_cost_nothing_rather_than_producing_an_empty_string()
    {
        var payload = DashboardMetricsPayload.From(Metrics(), Array.Empty<RunMetrics>());

        Assert.Equal(0, payload.Tokens.Total);
        Assert.Equal("0", payload.Tokens.TotalText);
        Assert.Equal("$0.00", payload.Tokens.TotalCostText);
    }

    // ===== failure buckets =====

    // Zero-count buckets ship too: otherwise "no llm_failed at all" and
    // "llm_failed wasn't reported" would be the same payload, and the client
    // could not tell which it was looking at.
    [Fact]
    public void Every_failure_status_is_reported_including_the_zeros()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(failures: new Dictionary<string, long> { ["llm_failed"] = 128 }),
            Array.Empty<RunMetrics>());

        Assert.Equal(5, payload.Failures.Count);
        Assert.Equal(
            new[] { "llm_failed", "parse_invalid_json", "persist_failed", "failed", "interrupted" },
            payload.Failures.Select(f => f.Status));
        Assert.Equal(128, payload.Failures.Single(f => f.Status == "llm_failed").Count);
        Assert.Equal(0, payload.Failures.Single(f => f.Status == "interrupted").Count);
    }

    // parse_empty is a VALID terminal state (the model answered; there was
    // nothing to extract) and the embeddings backlog is not a failure at all.
    // The handoff design put both in the failures donut, where parse_empty would
    // have been 803 of 1,192 - two thirds of a "failures" chart that isn't one.
    [Fact]
    public void Parse_empty_and_the_embedding_backlog_are_not_failure_buckets()
    {
        var payload = DashboardMetricsPayload.From(Metrics(), Array.Empty<RunMetrics>());

        Assert.DoesNotContain(payload.Failures, f => f.Status == "parse_empty");
        Assert.Equal(803, payload.ParseEmpty);
        Assert.Equal(6, payload.StudiesWithoutEmbeddings);
    }

    // ===== nullable source counts =====

    // Null when no AACT source is reachable - which includes the seeded
    // quickstart, where there is no ctgov schema. It must survive as JSON null:
    // coercing it to 0 would render "0 trials remaining" on a corpus that has
    // barely started, which is the most misleading number the page could show.
    [Fact]
    public void Trials_remaining_is_null_when_no_source_is_reachable()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(sourceTrialTotal: null), Array.Empty<RunMetrics>());

        Assert.Null(payload.SourceTrialTotal);
        Assert.Null(payload.SourceTrialTotalText);
        Assert.Null(payload.TrialsRemaining);
        Assert.Null(payload.TrialsRemainingText);
    }

    [Fact]
    public void Trials_remaining_is_source_total_minus_attempted_when_available()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(sourceTrialTotal: 593_000, studiesAttempted: 288_719),
            Array.Empty<RunMetrics>());

        Assert.Equal(304_281, payload.TrialsRemaining);
        Assert.Equal("304,281", payload.TrialsRemainingText);
    }

    // ===== resolution rate =====

    // "90.2 %" with a space is InvariantCulture's percent pattern, and invariant
    // is what production actually renders: the runtime image is aspnet:8.0-alpine,
    // which ships no ICU and sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true.
    // A Windows dev box would say "90.2%" - the dev box is the outlier here, not
    // the server.
    [Fact]
    public void Resolution_rate_ships_raw_and_preformatted()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(resolutionRate: 0.902), Array.Empty<RunMetrics>());

        Assert.Equal(0.902, payload.ResolutionRate);
        Assert.Equal("90.2 %", payload.ResolutionRateText);
    }

    // The whole point of formatting server-side is that one corpus renders one
    // way. That only holds if the culture is pinned rather than ambient - and
    // ambient differs between an Alpine container (invariant), a Linux CI runner
    // (invariant) and a Windows dev box (not). Forcing a culture whose
    // separators are the OPPOSITE of invariant's proves the pin: de-DE would
    // otherwise give "4.032.035" and "90,2 %".
    [Fact]
    public void Formatting_does_not_follow_the_ambient_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var payload = DashboardMetricsPayload.From(
                Metrics(promptTokens: 418_200_000, resolutionRate: 0.902),
                Array.Empty<RunMetrics>());

            Assert.Equal("4,032,035", payload.EligibilityRowCountText);
            Assert.Equal("90.2 %", payload.ResolutionRateText);
            Assert.Equal("$2,091.00", payload.Tokens.PromptCostText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ===== sparkline series =====

    [Fact]
    public void Runs_carry_the_per_run_series_in_the_order_given()
    {
        var payload = DashboardMetricsPayload.From(
            Metrics(),
            new[] { Run(0.895, 138), Run(0.881, 402) });

        Assert.Equal(new[] { 0.895, 0.881 }, payload.Runs.Select(r => r.ResolutionRate));
        Assert.Equal(new[] { 138, 402 }, payload.Runs.Select(r => r.RowsPersisted));
    }

    [Fact]
    public void No_runs_yields_an_empty_series_rather_than_null()
    {
        var payload = DashboardMetricsPayload.From(Metrics(), Array.Empty<RunMetrics>());

        Assert.NotNull(payload.Runs);
        Assert.Empty(payload.Runs);
    }
}

using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Controllers;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Direct, no-HTTP-host tests for AnalyticsController.Concept. WebTests.cs
/// covers Index/Trend against the shared WebApplicationFactory, whose
/// Postgres connection string is deliberately unreachable (127.0.0.1:1) so
/// every gateway call there throws and exercises the "backend unavailable"
/// catch path. That harness cannot reach the Concept action's not-found
/// branch, because a real query against that placeholder connection always
/// throws before a clean "no row" result could ever come back. This class
/// substitutes a fake IAnalyticsGateway instead, so the not-found branch
/// (GetConceptSummaryAsync returning Nothing for an unrecognised CUI) can be
/// exercised directly, proving the controller renders a clean not-found view
/// rather than letting an exception or a blank page through - a user can
/// type anything into the ?code= query string.
/// </summary>
public class AnalyticsControllerTests
{
    private sealed class FakeAnalyticsGateway : IAnalyticsGateway
    {
        public ConceptSummary? SummaryToReturn { get; set; }
        public IReadOnlyList<ConceptSummary> NameSearchResultsToReturn { get; set; } = Array.Empty<ConceptSummary>();

        public Task<CohortProfile> GetCohortProfileAsync(AnalyticsCohort cohort, CancellationToken cancellationToken) =>
            Task.FromResult(new CohortProfile(0, Array.Empty<ConceptCount>()));

        public Task<IReadOnlyList<ConceptCount>> GetCorpusProfileAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConceptCount>>(Array.Empty<ConceptCount>());

        public Task<int> GetCorpusTrialCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetCohortDefiningCodesAsync(AnalyticsCohort cohort, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyDictionary<string, string>> GetPrefNamesAsync(IReadOnlyList<string> conceptCodes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyList<TrendPoint>> GetTrendAsync(string conceptCode, int currentYear, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrendPoint>>(Array.Empty<TrendPoint>());

        public Task<ConceptSummary> GetConceptSummaryAsync(string conceptCode, CancellationToken cancellationToken) =>
            Task.FromResult(SummaryToReturn!);

        public Task<IReadOnlyList<ConceptSummary>> SearchConceptsAsync(string term, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(NameSearchResultsToReturn);
    }

    // None of this class's methods are reached by the Concept action - a call
    // into any of them would mean the action started touching the wrong
    // dependency, so each one throws rather than silently returning a
    // plausible-looking default.
    private sealed class UnusedCorpusReadCache : ICorpusReadCache
    {
        public Task<DashboardMetrics> GetDashboardMetricsAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Concept action must not read the dashboard metrics cache.");

        public void InvalidateDashboardMetrics() =>
            throw new InvalidOperationException("Concept action must not touch the dashboard metrics cache.");

        public Task<EligibilityFilterOptions> GetEligibilityFilterOptionsAsync(int maxDropdownSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Concept action must not read the Results filter-options cache.");

        public Task<CorpusConceptProfile> GetCorpusConceptProfileAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Concept action must not read the corpus concept-profile cache.");
    }

    private static AnalyticsController MakeController(
        ConceptSummary? summaryToReturn = null,
        IReadOnlyList<ConceptSummary>? nameSearchResultsToReturn = null) =>
        new(new FakeAnalyticsGateway
            {
                SummaryToReturn = summaryToReturn,
                NameSearchResultsToReturn = nameSearchResultsToReturn ?? Array.Empty<ConceptSummary>()
            },
            new UnusedCorpusReadCache(),
            NullLogger<AnalyticsController>.Instance);

    [Fact]
    public async Task Concept_renders_the_not_found_state_when_the_gateway_returns_nothing_for_an_unknown_cui()
    {
        var controller = MakeController(summaryToReturn: null);

        // CUI-shaped (^C\d{7}$) so the controller takes the direct CUI-lookup
        // path rather than treating this as a name search.
        var result = await controller.Concept(CancellationToken.None, code: "C9999999");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AnalyticsConceptViewModel>(view.Model);
        Assert.False(model.HasError);
        Assert.False(model.IsNameSearch);
        Assert.True(model.NotFound);
    }

    [Fact]
    public async Task Concept_renders_the_summary_when_the_gateway_finds_the_concept()
    {
        var summary = new ConceptSummary(
            conceptCode: "C0000001",
            prefName: "Diabetes Mellitus",
            rootSource: "SNOMEDCT_US",
            semanticTypes: "Disease or Syndrome",
            ancestorCount: 2,
            descendantCount: 5,
            trials: 10,
            corpusTrials: 100,
            byPhase: Array.Empty<ConceptCount>(),
            exampleCriteria: Array.Empty<string>());
        var controller = MakeController(summaryToReturn: summary);

        var result = await controller.Concept(CancellationToken.None, code: "C0000001");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AnalyticsConceptViewModel>(view.Model);
        Assert.False(model.NotFound);
        Assert.False(model.HasError);
        Assert.False(model.IsNameSearch);
        Assert.Equal("Diabetes Mellitus", model.Summary?.PrefName);
    }

    [Fact]
    public async Task Concept_renders_the_empty_form_when_no_code_is_supplied()
    {
        var controller = MakeController(summaryToReturn: null);

        var result = await controller.Concept(CancellationToken.None, code: null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AnalyticsConceptViewModel>(view.Model);
        Assert.False(model.HasSearched);
        Assert.False(model.NotFound);
    }

    // --- Fix 6 (2026-07 whole-branch review): lookup by name ---

    [Fact]
    public async Task Concept_treats_a_value_that_does_not_look_like_a_cui_as_a_name_search()
    {
        var matches = new[]
        {
            new ConceptSummary("C0011849", "Diabetes Mellitus", "", "", 0, 0, trials: 42, corpusTrials: 0,
                byPhase: Array.Empty<ConceptCount>(), exampleCriteria: Array.Empty<string>())
        };
        var controller = MakeController(nameSearchResultsToReturn: matches);

        var result = await controller.Concept(CancellationToken.None, code: "diabetes");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AnalyticsConceptViewModel>(view.Model);
        Assert.False(model.HasError);
        Assert.True(model.IsNameSearch);
        // A name search never falls into the CUI not-found state, even
        // though no exact-CUI summary was ever looked up for it.
        Assert.False(model.NotFound);
        var match = Assert.Single(model.NameSearchResults);
        Assert.Equal("C0011849", match.ConceptCode);
    }

    [Fact]
    public async Task Concept_name_search_with_no_matches_still_renders_cleanly()
    {
        var controller = MakeController(nameSearchResultsToReturn: Array.Empty<ConceptSummary>());

        var result = await controller.Concept(CancellationToken.None, code: "not a real concept");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AnalyticsConceptViewModel>(view.Model);
        Assert.False(model.HasError);
        Assert.True(model.IsNameSearch);
        Assert.Empty(model.NameSearchResults);
        Assert.False(model.NotFound);
    }
}

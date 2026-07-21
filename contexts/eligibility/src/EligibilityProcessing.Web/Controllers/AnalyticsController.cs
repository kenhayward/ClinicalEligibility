using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// The Analytics area (design spec "2026-07-21-analytics-tab"). Read-only
/// queries over the processed corpus: what is distinctive about a cohort of
/// trials (this file's Index/lift action), how a concept's prevalence moves
/// over time, and a single-concept lookup. Trend and Concept land in later
/// tasks; only Index (the lift view) is implemented here.
/// </summary>
[Authorize]
public class AnalyticsController : Controller
{
    private readonly IAnalyticsGateway _analytics;
    private readonly ICorpusReadCache _corpusReads;
    private readonly ILogger<AnalyticsController> _logger;

    public AnalyticsController(
        IAnalyticsGateway analytics,
        ICorpusReadCache corpusReads,
        ILogger<AnalyticsController> logger)
    {
        _analytics = analytics;
        _corpusReads = corpusReads;
        _logger = logger;
    }

    /// <summary>
    /// The Analytics landing page: distinctiveness (lift) of one cohort against
    /// the whole corpus. Renders a results-bearing view only when
    /// <paramref name="value"/> is supplied; otherwise renders the empty form -
    /// a cohort with no value is not a request to compute anything (mirrors
    /// <c>HomeController.Analysis</c>).
    /// </summary>
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken,
        string? kind = null,
        string? value = null,
        bool includeDescendants = false,
        int? minSupport = null)
    {
        var cohortKind = ParseCohortKind(kind);
        var trimmedValue = value?.Trim() ?? "";
        var minimumSupport = minSupport.GetValueOrDefault(LiftCalculator.DefaultMinimumSupport);

        if (trimmedValue.Length == 0)
        {
            return View(new AnalyticsLiftViewModel
            {
                Kind = cohortKind.ToString(),
                IncludeDescendants = includeDescendants,
                MinimumSupport = minimumSupport
            });
        }

        try
        {
            var cohort = new AnalyticsCohort(cohortKind, trimmedValue, includeDescendants);

            var cohortSize = await _analytics.GetCohortSizeAsync(cohort, cancellationToken);
            var cohortProfile = await _analytics.GetCohortProfileAsync(cohort, cancellationToken);
            var definingCodes = await _analytics.GetCohortDefiningCodesAsync(cohort, cancellationToken);

            // The corpus baseline MUST come from the cache, never a direct
            // gateway call - it is corpus-wide and identical for every request,
            // so ICorpusReadCache is the whole point of memoising it (~2s query).
            var corpusProfile = await _corpusReads.GetCorpusConceptProfileAsync(cancellationToken);

            // LiftCalculator.Build only ever looks up a name for a code that
            // appears in cohortCounts, so only those codes need resolving.
            var cohortCodes = cohortProfile
                .Select(c => c.ConceptCode)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var prefNames = await _analytics.GetPrefNamesAsync(cohortCodes, cancellationToken);

            var rows = LiftCalculator.Build(
                cohortCounts: cohortProfile,
                corpusCounts: corpusProfile.Counts,
                cohortSize: cohortSize,
                corpusSize: corpusProfile.TrialCount,
                prefNames: prefNames,
                definingCodes: new HashSet<string>(definingCodes, StringComparer.Ordinal),
                minimumSupport: minimumSupport);

            return View(new AnalyticsLiftViewModel
            {
                Kind = cohortKind.ToString(),
                Value = trimmedValue,
                IncludeDescendants = includeDescendants,
                MinimumSupport = minimumSupport,
                Rows = rows,
                CohortSize = cohortSize,
                CorpusSize = corpusProfile.TrialCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute analytics lift for {Kind}={Value}", cohortKind, trimmedValue);
            return View(new AnalyticsLiftViewModel
            {
                Kind = cohortKind.ToString(),
                Value = trimmedValue,
                IncludeDescendants = includeDescendants,
                MinimumSupport = minimumSupport,
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// Parses the cohort-kind query parameter defensively: a missing or
    /// unrecognised value defaults to <see cref="AnalyticsCohortKind.Concept"/>
    /// rather than throwing - the form's own dropdown only ever posts a valid
    /// name, but a hand-edited or stale query string should not 500.
    /// </summary>
    private static AnalyticsCohortKind ParseCohortKind(string? kind) =>
        Enum.TryParse<AnalyticsCohortKind>(kind, ignoreCase: true, out var parsed)
            ? parsed
            : AnalyticsCohortKind.Concept;
}

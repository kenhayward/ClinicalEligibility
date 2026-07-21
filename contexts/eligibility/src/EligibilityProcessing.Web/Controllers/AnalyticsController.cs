using System.Text.RegularExpressions;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Export;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// The Analytics area (design spec "2026-07-21-analytics-tab"). Read-only
/// queries over the processed corpus: what is distinctive about a cohort of
/// trials (Index/lift action), how a concept's prevalence moves over time
/// (Trend), and a single-concept lookup (Concept) - everything known about
/// one CUI, reachable from both the Results and Analysis views.
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
            var result = await ComputeLiftAsync(cohortKind, trimmedValue, includeDescendants, minimumSupport, cancellationToken);

            return View(new AnalyticsLiftViewModel
            {
                Kind = cohortKind.ToString(),
                Value = trimmedValue,
                IncludeDescendants = includeDescendants,
                MinimumSupport = minimumSupport,
                Rows = result.Rows,
                CohortSize = result.CohortSize,
                CorpusSize = result.CorpusSize
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
    /// Exports the current lift view as CSV - the same cohort/profile/lift
    /// computation as <see cref="Index"/> (via <see cref="ComputeLiftAsync"/>),
    /// so the file always matches what was on screen. Unlike the view action,
    /// a missing value or a failure returns an HTTP error rather than an
    /// inline-error view - there is no page for a file download to render
    /// into (same convention as <c>AuthoringController.ExportCriteria</c>).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportLift(
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
            return BadRequest(new { error = "A cohort value is required to export." });
        }

        try
        {
            var result = await ComputeLiftAsync(cohortKind, trimmedValue, includeDescendants, minimumSupport, cancellationToken);

            var csv = AnalyticsLiftCsv.Build(result.Rows);
            var name = $"Analytics_Lift_{LiftFileNamePart(cohortKind, trimmedValue)}.csv";
            return ExportResults.CsvFile(csv, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to export analytics lift for {Kind}={Value}", cohortKind, trimmedValue);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// The cohort/profile/lift computation shared by <see cref="Index"/> and
    /// <see cref="ExportLift"/> - extracted so the export always runs the
    /// same query path as the view rather than a second hand-maintained copy.
    /// </summary>
    private async Task<(IReadOnlyList<ConceptLiftRow> Rows, int CohortSize, int CorpusSize)> ComputeLiftAsync(
        AnalyticsCohortKind cohortKind,
        string trimmedValue,
        bool includeDescendants,
        int minimumSupport,
        CancellationToken cancellationToken)
    {
        var cohort = new AnalyticsCohort(cohortKind, trimmedValue, includeDescendants);

        // A single call now returns both the cohort size and its per-concept
        // profile - the two used to be separate gateway calls that each
        // re-ran the full cohort-defining SQL (measured ~1,225ms apiece, so
        // ~2.4s combined against a 2s warm budget). See
        // AnalyticsGateway.GetCohortProfileAsync.
        var cohortProfile = await _analytics.GetCohortProfileAsync(cohort, cancellationToken);
        var definingCodes = await _analytics.GetCohortDefiningCodesAsync(cohort, cancellationToken);

        // The corpus baseline MUST come from the cache, never a direct
        // gateway call - it is corpus-wide and identical for every request,
        // so ICorpusReadCache is the whole point of memoising it (~2s query).
        var corpusProfile = await _corpusReads.GetCorpusConceptProfileAsync(cancellationToken);

        // LiftCalculator.Build drops everything below minimumSupport anyway,
        // so resolving preferred names for codes that will never survive the
        // floor is wasted work - a large cohort can carry tens of thousands
        // of below-floor concepts. Filter here, before the name lookup;
        // LiftCalculator itself keeps applying the floor unconditionally so
        // it stays correct when called directly (e.g. from a test) with an
        // unfiltered code list.
        var cohortCodesAboveSupport = cohortProfile.Concepts
            .Where(c => c.Trials >= minimumSupport && !string.IsNullOrEmpty(c.ConceptCode))
            .Select(c => c.ConceptCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var prefNames = await _analytics.GetPrefNamesAsync(cohortCodesAboveSupport, cancellationToken);

        var rows = LiftCalculator.Build(
            cohortCounts: cohortProfile.Concepts,
            corpusCounts: corpusProfile.Counts,
            cohortSize: cohortProfile.Size,
            corpusSize: corpusProfile.TrialCount,
            prefNames: prefNames,
            definingCodes: new HashSet<string>(definingCodes, StringComparer.Ordinal),
            minimumSupport: minimumSupport);

        return (rows, cohortProfile.Size, corpusProfile.TrialCount);
    }

    // Filename-safe slug for the export download name: the cohort kind plus a
    // sanitised value (concept code, phase, or year). Falls back to just the
    // kind when the value has no letters or digits at all.
    private static string LiftFileNamePart(AnalyticsCohortKind kind, string value)
    {
        var slug = new string((value ?? "").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(slug) ? kind.ToString() : $"{kind}_{slug}";
    }

    /// <summary>
    /// The trend view: how up to five concepts' prevalence has moved year on
    /// year, each as a percentage of that year's processed studies. Renders
    /// the empty form when no codes are supplied, mirroring Index above - a
    /// query with nothing to plot is not a request to compute anything.
    /// </summary>
    public async Task<IActionResult> Trend(
        CancellationToken cancellationToken,
        string? codes = null)
    {
        // DateTime.Now is read here, at the controller boundary, and passed
        // into the gateway as a plain int - GetTrendAsync itself never reads
        // the clock, so a test can pin currentYear and assert IsPartial
        // deterministically.
        var currentYear = DateTime.Now.Year;
        var codeList = ParseCodes(codes);

        if (codeList.Count == 0)
        {
            return View(new AnalyticsTrendViewModel
            {
                CodesInput = codes ?? "",
                CurrentYear = currentYear
            });
        }

        try
        {
            var prefNames = await _analytics.GetPrefNamesAsync(codeList, cancellationToken);

            var series = new List<TrendSeries>();
            foreach (var code in codeList)
            {
                var points = await _analytics.GetTrendAsync(code, currentYear, cancellationToken);
                series.Add(new TrendSeries
                {
                    ConceptCode = code,
                    PrefName = prefNames.TryGetValue(code, out var name) && !string.IsNullOrEmpty(name) ? name : code,
                    Points = points
                });
            }

            return View(new AnalyticsTrendViewModel
            {
                CodesInput = codes ?? "",
                CurrentYear = currentYear,
                Series = series
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute analytics trend for {Codes}", codes);
            return View(new AnalyticsTrendViewModel
            {
                CodesInput = codes ?? "",
                CurrentYear = currentYear,
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// Matches this corpus's CUIs (umls.concept.cui, always "C" + 7 digits).
    /// A submitted value that does not match is treated as a name search
    /// instead of a CUI lookup - see <see cref="Concept"/>.
    /// </summary>
    private static readonly Regex CuiPattern = new("^C[0-9]{7}$", RegexOptions.Compiled);

    /// <summary>Cap on name-search results - a pick-list, not a paged search page.</summary>
    private const int NameSearchLimit = 25;

    /// <summary>
    /// The concept lookup view: everything known about one CUI, reachable by
    /// typing a CUI or a name (the spec's own wording). Renders the empty
    /// form when no code is supplied (mirrors Index/Trend above); when the
    /// submitted value does not look like a CUI, it is searched by name via
    /// <see cref="IAnalyticsGateway.SearchConceptsAsync"/> and rendered as a
    /// pick-list; otherwise it renders the clean not-found state when the
    /// gateway returns Nothing for an unrecognised CUI - never an exception,
    /// never a blank page, since a user can type anything into the URL - and
    /// the inline "backend unavailable" warning if the gateway itself throws.
    /// </summary>
    public async Task<IActionResult> Concept(
        CancellationToken cancellationToken,
        string? code = null)
    {
        var trimmedCode = code?.Trim() ?? "";

        if (trimmedCode.Length == 0)
        {
            return View(new AnalyticsConceptViewModel());
        }

        var looksLikeCui = CuiPattern.IsMatch(trimmedCode);

        try
        {
            if (!looksLikeCui)
            {
                var matches = await _analytics.SearchConceptsAsync(trimmedCode, NameSearchLimit, cancellationToken);
                return View(new AnalyticsConceptViewModel
                {
                    Code = trimmedCode,
                    IsNameSearch = true,
                    NameSearchResults = matches
                });
            }

            var summary = await _analytics.GetConceptSummaryAsync(trimmedCode, cancellationToken);
            return View(new AnalyticsConceptViewModel
            {
                Code = trimmedCode,
                Summary = summary
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to look up analytics concept {Code}", trimmedCode);
            return View(new AnalyticsConceptViewModel
            {
                Code = trimmedCode,
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

    /// <summary>
    /// Splits, trims, de-duplicates and caps the comma-separated codes input
    /// at five - a readability limit on the line chart, not a technical one.
    /// </summary>
    private static IReadOnlyList<string> ParseCodes(string? codes) =>
        (codes ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();
}

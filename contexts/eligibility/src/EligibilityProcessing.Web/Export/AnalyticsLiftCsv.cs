using System.Globalization;
using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Export;

/// <summary>
/// Builds the CSV for the Analytics distinctiveness (lift) view: one row per
/// <see cref="ConceptLiftRow"/>, in the order given (already sorted by
/// ExcessPp descending by <see cref="LiftCalculator.Build"/> - not re-sorted
/// here). Pure (no I/O) so it's unit-testable without a database.
///
/// The raw cohort/corpus trial counts are included alongside the derived
/// percentages, excess-pp, and lift - deliberately, so a CSV someone shares
/// can be checked independently rather than being a list of unverifiable
/// ratios.
/// </summary>
public static class AnalyticsLiftCsv
{
    private static readonly string[] Headers =
    {
        "concept_code", "pref_name", "cohort_trials", "corpus_trials",
        "pct_cohort", "pct_corpus", "excess_pp", "lift", "defines_cohort"
    };

    public static string Build(IReadOnlyList<ConceptLiftRow> rows)
    {
        rows ??= Array.Empty<ConceptLiftRow>();

        var csvRows = rows.Select(r => (IReadOnlyList<string>)new[]
        {
            r.ConceptCode,
            r.PrefName,
            r.CohortTrials.ToString(CultureInfo.InvariantCulture),
            r.CorpusTrials.ToString(CultureInfo.InvariantCulture),
            r.PctCohort.ToString(CultureInfo.InvariantCulture),
            r.PctCorpus.ToString(CultureInfo.InvariantCulture),
            r.ExcessPp.ToString(CultureInfo.InvariantCulture),
            r.Lift.ToString(CultureInfo.InvariantCulture),
            r.DefinesCohort ? "true" : "false"
        });

        return CsvWriter.Build(Headers, csvRows);
    }
}

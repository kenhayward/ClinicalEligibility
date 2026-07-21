using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Export;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

public class AnalyticsLiftCsvTests
{
    [Fact]
    public void Build_emits_header_and_one_row_per_input_with_raw_counts()
    {
        var rows = new List<ConceptLiftRow>
        {
            new(
                conceptCode: "C0011849",
                prefName: "Diabetes Mellitus",
                cohortTrials: 42,
                corpusTrials: 100,
                pctCohort: 84.0,
                pctCorpus: 10.0,
                excessPp: 74.0,
                lift: 8.4,
                definesCohort: true),
            new(
                conceptCode: "C0004238",
                prefName: "Atrial Fibrillation",
                cohortTrials: 5,
                corpusTrials: 20,
                pctCohort: 10.0,
                pctCorpus: 2.0,
                excessPp: 8.0,
                lift: 5.0,
                definesCohort: false)
        };

        var csv = AnalyticsLiftCsv.Build(rows);
        var lines = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        Assert.Equal(3, lines.Length); // header + two rows
        Assert.Equal(
            "concept_code,pref_name,cohort_trials,corpus_trials,pct_cohort,pct_corpus,excess_pp,lift,defines_cohort",
            lines[0]);

        Assert.Equal("C0011849,Diabetes Mellitus,42,100,84,10,74,8.4,true", lines[1]);
        Assert.Equal("C0004238,Atrial Fibrillation,5,20,10,2,8,5,false", lines[2]);
    }

    [Fact]
    public void Build_emits_header_only_when_no_rows()
    {
        var csv = AnalyticsLiftCsv.Build(new List<ConceptLiftRow>());
        Assert.Single(csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
    }
}

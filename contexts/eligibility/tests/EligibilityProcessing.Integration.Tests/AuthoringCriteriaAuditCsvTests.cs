using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Export;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

public class AuthoringCriteriaAuditCsvTests
{
    private static AuthoringStudy Study(Guid id) =>
        new() { AuthoringStudyId = id, StudyId = "PROTO-001", Label = "Diabetes study" };

    private static AuthoringCriterion Criterion(Guid studyId, Guid critId, params AuthoringCriterionSource[] sources) =>
        new()
        {
            AuthoringCriterionId = critId,
            AuthoringStudyId = studyId,
            Ordinal = 0,
            Criterion = "Inclusion",
            NormalizedText = "Age >= 18",
            Concept = "Adult",
            ConceptCode = "C0001675",
            SemanticType = "Age Group",
            Domain = "Demographics",
            SourceNote = "from cluster",
            CreatedAt = new DateTimeOffset(2026, 5, 1, 9, 30, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero),
            Sources = sources.ToList()
        };

    private static AuthoringCriterionSource Source(string nct, string code, string text) =>
        new()
        {
            AuthoringCriterionSourceId = Guid.NewGuid(),
            NctId = nct,
            Criterion = "Inclusion",
            Domain = "Demographics",
            Concept = "Adult",
            ConceptCode = code,
            SemanticType = "Age Group",
            Qualifier = "",
            TimeWindow = "",
            OriginalText = text,
            MatchScore = 0.875m,
            CreatedAt = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero)
        };

    private static string[] Lines(string csv) =>
        csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    // The first two columns (Audit Row Id, Origin) never contain commas, so a
    // naive split is safe for reading them.
    private static string RowKey(string line) => line.Split(',')[0];
    private static string Origin(string line) => line.Split(',')[1];

    [Fact]
    public void Header_lists_audit_key_origin_study_criterion_and_source_columns()
    {
        var csv = AuthoringCriteriaAuditCsv.Build(Study(Guid.NewGuid()), new List<AuthoringCriterion>());
        var header = Lines(csv)[0];

        Assert.StartsWith("Audit Row Id,Origin,Study ID,Study UUID,Study Label,Criterion Id,", header);
        Assert.Contains("Source Note,Manual Reason,", header);
        Assert.Contains("Source Id,Source Eligibility Id,Source NCT Id,", header);
        Assert.Contains("Source Original Text,Source Match Score,Source Created At (UTC)", header);
    }

    [Fact]
    public void Manual_criterion_reasoning_appears_in_its_row()
    {
        var studyId = Guid.NewGuid();
        var c = Criterion(studyId, Guid.NewGuid());
        c.SourceNote = "";                                  // typed from scratch — no provenance
        c.ManualReason = "Added per protocol amendment 3";

        var lines = Lines(AuthoringCriteriaAuditCsv.Build(Study(studyId), new[] { c }));

        Assert.Equal(2, lines.Length); // header + one row
        Assert.Equal("Manually entered", Origin(lines[1]));
        Assert.Contains("Added per protocol amendment 3", lines[1]);
        // The manual reason sits in the criterion block, so the 13 trailing
        // source columns remain blank.
        var sourceFields = lines[1].Split(',')[^13..];
        Assert.All(sourceFields, f => Assert.Equal("", f));
    }

    [Fact]
    public void Criterion_with_multiple_sources_emits_one_row_each_marked_normalised()
    {
        var studyId = Guid.NewGuid();
        var critId = Guid.NewGuid();
        var c = Criterion(studyId, critId,
            Source("NCT00000001", "C0001675", "Adults aged 18 and over"),
            Source("NCT00000002", "C0001675", "18 years or older"));

        var lines = Lines(AuthoringCriteriaAuditCsv.Build(Study(studyId), new[] { c }));

        Assert.Equal(3, lines.Length); // header + 2 source rows
        Assert.All(lines.Skip(1), l => Assert.Equal("Normalised", Origin(l)));
        Assert.Contains(lines, l => l.Contains("NCT00000001"));
        Assert.Contains(lines, l => l.Contains("NCT00000002"));
        // Criterion + study fields are present on every denormalised row.
        Assert.All(lines.Skip(1), l => Assert.Contains("PROTO-001", l));
        Assert.All(lines.Skip(1), l => Assert.Contains(critId.ToString(), l));
        Assert.All(lines.Skip(1), l => Assert.Contains("0.875", l)); // source match score
    }

    [Fact]
    public void Criterion_with_no_sources_but_a_source_note_is_normalised_with_blank_source_columns()
    {
        // Came from a cluster (has a Source Note) but no per-source lineage was
        // captured — still Normalised, not Manually entered.
        var studyId = Guid.NewGuid();
        var c = Criterion(studyId, Guid.NewGuid()); // SourceNote = "from cluster", no sources

        var lines = Lines(AuthoringCriteriaAuditCsv.Build(Study(studyId), new[] { c }));

        Assert.Equal(2, lines.Length); // header + one row
        Assert.Equal("Normalised", Origin(lines[1]));
        Assert.Contains("Age >= 18", lines[1]);
        // The 13 trailing source columns are all blank. No field in this row is
        // quoted, so a naive split is safe.
        var sourceFields = lines[1].Split(',')[^13..];
        Assert.All(sourceFields, f => Assert.Equal("", f));
    }

    [Fact]
    public void Truly_manual_criterion_with_no_sources_and_no_source_note_is_manually_entered()
    {
        var studyId = Guid.NewGuid();
        var c = Criterion(studyId, Guid.NewGuid());
        c.SourceNote = ""; // typed by an author from scratch — no provenance

        var lines = Lines(AuthoringCriteriaAuditCsv.Build(Study(studyId), new[] { c }));

        Assert.Equal(2, lines.Length); // header + one row
        Assert.Equal("Manually entered", Origin(lines[1]));
        var sourceFields = lines[1].Split(',')[^13..];
        Assert.All(sourceFields, f => Assert.Equal("", f));
    }

    [Fact]
    public void Row_key_is_stable_across_builds_and_independent_of_volatile_source_id()
    {
        var studyId = Guid.NewGuid();
        var critId = Guid.NewGuid();

        // Same logical source content, but a different surrogate source id each
        // build (mirrors the delete+reinsert-with-new-guid save behaviour).
        var build1 = AuthoringCriteriaAuditCsv.Build(Study(studyId),
            new[] { Criterion(studyId, critId, Source("NCT00000001", "C0001675", "Adults aged 18 and over")) });
        var build2 = AuthoringCriteriaAuditCsv.Build(Study(studyId),
            new[] { Criterion(studyId, critId, Source("NCT00000001", "C0001675", "Adults aged 18 and over")) });

        var key1 = RowKey(Lines(build1)[1]);
        var key2 = RowKey(Lines(build2)[1]);

        Assert.Equal(key1, key2);
        // And the two builds carried different Source Id values, proving the key
        // does not depend on the volatile surrogate id.
        Assert.NotEqual(Lines(build1)[1], Lines(build2)[1]);
    }

    [Fact]
    public void Row_key_changes_when_source_content_changes()
    {
        var studyId = Guid.NewGuid();
        var critId = Guid.NewGuid();

        var keyA = RowKey(Lines(AuthoringCriteriaAuditCsv.Build(Study(studyId),
            new[] { Criterion(studyId, critId, Source("NCT00000001", "C0001675", "Adults aged 18 and over")) }))[1]);
        var keyB = RowKey(Lines(AuthoringCriteriaAuditCsv.Build(Study(studyId),
            new[] { Criterion(studyId, critId, Source("NCT00000099", "C0001675", "Adults aged 18 and over")) }))[1]);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Fields_needing_escaping_round_trip_through_csv_writer()
    {
        var studyId = Guid.NewGuid();
        var c = Criterion(studyId, Guid.NewGuid(),
            Source("NCT00000001", "C0001675", "Adults, aged \"18\"\nand over"));

        var csv = AuthoringCriteriaAuditCsv.Build(Study(studyId), new[] { c });

        // The embedded comma/quote/newline force a quoted field with doubled quotes.
        Assert.Contains("\"Adults, aged \"\"18\"\"\nand over\"", csv);
    }
}

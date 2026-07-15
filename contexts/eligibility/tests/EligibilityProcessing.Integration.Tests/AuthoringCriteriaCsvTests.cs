using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Export;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

public class AuthoringCriteriaCsvTests
{
    [Fact]
    public void Build_includes_study_id_label_and_criterion_columns()
    {
        var studyId = Guid.NewGuid();
        var critId = Guid.NewGuid();
        var study = new AuthoringStudy { AuthoringStudyId = studyId, StudyId = "PROTO-001", Label = "Diabetes, Phase 2" };
        var criteria = new List<AuthoringCriterion>
        {
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
                UpdatedAt = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero)
            }
        };

        var csv = AuthoringCriteriaCsv.Build(study, criteria);
        var lines = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        Assert.Equal(2, lines.Length); // header + one row
        Assert.StartsWith("Study ID,Study UUID,Study Label,Criterion Id,Ordinal,Criterion,Normalized Text,", lines[0]);
        Assert.StartsWith("PROTO-001,", lines[1]); // user-facing Study ID is the first column
        Assert.Contains(studyId.ToString(), lines[1]);
        Assert.Contains(critId.ToString(), lines[1]);
        Assert.Contains("\"Diabetes, Phase 2\"", lines[1]); // comma in label forces quoting
        Assert.Contains("C0001675", lines[1]);
        Assert.Contains("2026-05-01 09:30:00", lines[1]); // created-at formatted as UTC
    }

    [Fact]
    public void Build_emits_header_only_when_no_criteria()
    {
        var study = new AuthoringStudy { AuthoringStudyId = Guid.NewGuid(), Label = "x" };
        var csv = AuthoringCriteriaCsv.Build(study, new List<AuthoringCriterion>());
        Assert.Single(csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
    }
}

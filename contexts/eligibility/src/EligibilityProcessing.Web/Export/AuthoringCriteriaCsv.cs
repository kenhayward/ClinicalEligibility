using System.Globalization;
using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Export;

/// <summary>
/// Builds the CSV for an authored study's eligibility-criteria export: every
/// authoring_criterion column for the study, prefixed with the study's id and
/// label. Pure (no I/O) so it's unit-testable without a database.
/// </summary>
public static class AuthoringCriteriaCsv
{
    private static readonly string[] Headers =
    {
        "Study ID", "Study UUID", "Study Label",
        "Criterion Id", "Ordinal", "Criterion", "Normalized Text",
        "Concept", "Concept Code", "Semantic Type", "Domain", "Source Note",
        "Created At (UTC)", "Updated At (UTC)", "Created By", "Last Updated By"
    };

    public static string Build(AuthoringStudy study, IReadOnlyList<AuthoringCriterion> criteria)
    {
        ArgumentNullException.ThrowIfNull(study);
        criteria ??= Array.Empty<AuthoringCriterion>();

        var rows = criteria.Select(c => (IReadOnlyList<string>)new[]
        {
            study.StudyId,
            study.AuthoringStudyId.ToString(),
            study.Label,
            c.AuthoringCriterionId.ToString(),
            c.Ordinal.ToString(CultureInfo.InvariantCulture),
            c.Criterion,
            c.NormalizedText,
            c.Concept,
            c.ConceptCode,
            c.SemanticType,
            c.Domain,
            c.SourceNote,
            Timestamp(c.CreatedAt),
            Timestamp(c.UpdatedAt),
            c.CreatedBy?.ToString() ?? "",
            c.LastUpdatedBy?.ToString() ?? ""
        });

        return CsvWriter.Build(Headers, rows);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

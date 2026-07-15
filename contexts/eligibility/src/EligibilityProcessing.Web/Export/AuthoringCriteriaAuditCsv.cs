using System.Globalization;
using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Export;

/// <summary>
/// Builds the <c>{StudyId}_Eligibility_Audit.csv</c> export for an authored study: a fully
/// denormalised join of every eligibility criterion (all fields) with each
/// source record it was normalised from (all fields), one row per
/// criterion–source pair. Destined for an audit table in the main data fabric.
/// Pure (no I/O) so it's unit-testable without a database.
///
/// Each row carries a persistent <c>Audit Row Id</c> — a deterministic UUIDv5
/// (see <see cref="DeterministicId"/>) derived from the criterion's persistent
/// <c>AuthoringCriterionId</c> plus the source's stable content (NCT id +
/// concept code + original text). It is intentionally NOT built from
/// <c>AuthoringCriterionSource.AuthoringCriterionSourceId</c>, which is
/// regenerated on every save (delete+reinsert) and so is not stable; that
/// volatile id is still emitted in the <c>Source Id</c> column for completeness.
/// A stable key lets the target table upsert idempotently across re-exports.
///
/// A criterion is flagged <c>Origin = "Normalised"</c> when it carries lineage —
/// either persisted source rows, or a provenance Source Note (criteria normalised
/// from a cluster before per-source lineage was captured have the note but no
/// source rows). Only a criterion with neither is <c>"Manually entered"</c>, so
/// the target can filter the two apart. Criteria with no source rows still emit a
/// single row with the source columns blank.
/// </summary>
public static class AuthoringCriteriaAuditCsv
{
    // Fixed namespace for this feature's deterministic row keys. Changing it
    // would change every Audit Row Id, so it must stay constant.
    private static readonly Guid AuditNamespace = new("7a6f3d2c-8b1e-4c5a-9f0d-2e4b6c8a1d33");

    private const string OriginNormalised = "Normalised";
    private const string OriginManual = "Manually entered";

    private static readonly string[] Headers =
    {
        "Audit Row Id", "Origin",
        "Study ID", "Study UUID", "Study Label",
        "Criterion Id", "Ordinal", "Criterion", "Normalized Text",
        "Concept", "Concept Code", "Semantic Type", "Domain", "Source Note", "Manual Reason",
        "Criterion Created At (UTC)", "Criterion Updated At (UTC)",
        "Criterion Created By", "Criterion Last Updated By",
        "Source Id", "Source Eligibility Id", "Source NCT Id", "Source Criterion",
        "Source Domain", "Source Concept", "Source Concept Code", "Source Semantic Type",
        "Source Qualifier", "Source Time Window", "Source Original Text",
        "Source Match Score", "Source Created At (UTC)"
    };

    public static string Build(AuthoringStudy study, IReadOnlyList<AuthoringCriterion> criteria)
    {
        ArgumentNullException.ThrowIfNull(study);
        criteria ??= Array.Empty<AuthoringCriterion>();

        var rows = new List<IReadOnlyList<string>>();
        foreach (var c in criteria)
        {
            var sources = c.Sources ?? new List<AuthoringCriterionSource>();

            // A criterion counts as Normalised when it carries lineage —
            // either persisted source rows, or (for criteria normalised from a
            // cluster before per-source lineage was captured) a provenance
            // Source Note. Only a criterion with neither is "Manually entered".
            var origin = sources.Count > 0 || !string.IsNullOrWhiteSpace(c.SourceNote)
                ? OriginNormalised
                : OriginManual;

            if (sources.Count == 0)
            {
                // No per-source detail — one row, blank source columns.
                rows.Add(Row(study, c, source: null, origin));
            }
            else
            {
                foreach (var src in sources)
                {
                    rows.Add(Row(study, c, src, origin));
                }
            }
        }

        return CsvWriter.Build(Headers, rows);
    }

    private static IReadOnlyList<string> Row(
        AuthoringStudy study, AuthoringCriterion c, AuthoringCriterionSource? source, string origin)
    {
        return new[]
        {
            RowKey(c, source),
            origin,
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
            c.ManualReason,
            Timestamp(c.CreatedAt),
            Timestamp(c.UpdatedAt),
            c.CreatedBy?.ToString() ?? "",
            c.LastUpdatedBy?.ToString() ?? "",
            source?.AuthoringCriterionSourceId.ToString() ?? "",
            source?.EligibilityId?.ToString(CultureInfo.InvariantCulture) ?? "",
            source?.NctId ?? "",
            source?.Criterion ?? "",
            source?.Domain ?? "",
            source?.Concept ?? "",
            source?.ConceptCode ?? "",
            source?.SemanticType ?? "",
            source?.Qualifier ?? "",
            source?.TimeWindow ?? "",
            source?.OriginalText ?? "",
            source is null ? "" : source.MatchScore.ToString(CultureInfo.InvariantCulture),
            source is null ? "" : Timestamp(source.CreatedAt)
        };
    }

    // Persistent per-row key. Derived from the criterion's stable id plus the
    // source's natural content — never the source's surrogate id (volatile).
    // For a no-source row the name is "{criterionId}|", giving a stable
    // per-criterion key. Two sources under one criterion with identical
    // (NctId, ConceptCode, OriginalText) collide — a genuine duplicate.
    private static string RowKey(AuthoringCriterion c, AuthoringCriterionSource? source)
    {
        var natural = source is null
            ? ""
            : $"{source.NctId?.Trim()}|{source.ConceptCode?.Trim()}|{source.OriginalText?.Trim()}";
        var name = $"{c.AuthoringCriterionId:D}|{natural}";
        return DeterministicId.Create(AuditNamespace, name).ToString();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

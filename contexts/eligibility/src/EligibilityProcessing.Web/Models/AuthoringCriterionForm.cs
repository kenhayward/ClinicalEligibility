namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Form-post input model for one entry in the authored eligibility-criteria
/// list. The SaveCriteria action binds a <c>List&lt;AuthoringCriterionForm&gt;</c>
/// via indexed names (<c>criteria[0].NormalizedText</c>, …).
/// </summary>
public sealed class AuthoringCriterionForm
{
    /// <summary>
    /// The existing authoring_criterion_id, round-tripped through a hidden input
    /// so the upsert save preserves a row's identity (and therefore its
    /// created_at / created_by). Null/empty for a newly-added entry.
    /// </summary>
    public Guid? Id { get; set; }

    public string? Criterion { get; set; }
    public string? NormalizedText { get; set; }
    public string? Concept { get; set; }
    public string? ConceptCode { get; set; }
    public string? SemanticType { get; set; }
    public string? Domain { get; set; }
    public string? SourceNote { get; set; }

    /// <summary>
    /// Free-text rationale an author gives when adding a criterion manually
    /// (rather than copying it from an Analysis cluster). Round-trips through a
    /// hidden input; shown in the criterion's expansion area in place of the
    /// source-record list.
    /// </summary>
    public string? ManualReason { get; set; }

    /// <summary>
    /// JSON array of the source records this criterion was normalized from
    /// (lineage). Round-trips through a hidden input so the replace-all
    /// SaveCriteria save preserves mappings. Shape: see
    /// <see cref="AuthoringCriterionSourceForm"/>.
    /// </summary>
    public string? SourcesJson { get; set; }
}

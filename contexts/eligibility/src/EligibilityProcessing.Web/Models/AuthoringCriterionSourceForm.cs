namespace EligibilityProcessing.Web.Models;

/// <summary>
/// One source record behind an authored criterion, as carried in the
/// SaveCriteria form's <see cref="AuthoringCriterionForm.SourcesJson"/> payload
/// and emitted by the Normalize endpoint. Property names are the camelCase JSON
/// keys shared between the server render, the client Add flow, and this
/// deserialization step.
/// </summary>
public sealed class AuthoringCriterionSourceForm
{
    public long? EligibilityId { get; set; }
    public string? NctId { get; set; }
    public string? Criterion { get; set; }
    public string? Domain { get; set; }
    public string? Concept { get; set; }
    public string? ConceptCode { get; set; }
    public string? SemanticType { get; set; }
    public string? Qualifier { get; set; }
    public string? TimeWindow { get; set; }
    public string? OriginalText { get; set; }
    public decimal? MatchScore { get; set; }
}

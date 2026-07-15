namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Form-post input model for the high-level eligibility data form. Adult /
/// Child / OlderAdult are tri-state — "" (unknown), "true", or "false".
/// </summary>
public sealed class AuthoringEligibilityForm
{
    public Guid Id { get; set; }
    public string? Criteria { get; set; }
    public string? Gender { get; set; }
    public string? MinimumAge { get; set; }
    public string? MaximumAge { get; set; }
    public string? HealthyVolunteers { get; set; }
    public string? SamplingMethod { get; set; }
    public string? Population { get; set; }
    public string? Adult { get; set; }
    public string? Child { get; set; }
    public string? OlderAdult { get; set; }
}

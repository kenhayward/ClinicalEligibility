namespace EligibilityProcessing.Web;

/// <summary>
/// Configuration for the trigger surface (<c>POST /trigger</c>) hosted in the
/// Web app. Spec section 2.7. The config key prefix is still <c>Webhook:</c>
/// for backward compatibility with existing .env / appsettings.json files —
/// the host name changed, the section name did not.
/// </summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Shared secret expected on the <c>X-Eligibility-Token</c> header. When
    /// unset, <c>/trigger</c> rejects every request — there is no implicit
    /// "no auth" path. Source from secret storage per spec section 6.5.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// Spec section 2.1: the webhook trigger mode hard-codes <c>StudyCount = 500</c>.
    /// Configurable in case a deploy wants a different production cadence.
    /// </summary>
    public int DefaultStudyCount { get; set; } = 500;

    /// <summary>
    /// Rate-limit permits per window for <c>POST /trigger</c>. Spec says
    /// "1 req / 60s" so the production default is 1; integration tests bump it
    /// to a value that does not interfere with multi-test runs against a shared
    /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>.
    /// </summary>
    public int RateLimitPermits { get; set; } = 1;

    /// <summary>Rate-limit window in seconds. Defaults to 60 per spec.</summary>
    public int RateLimitWindowSeconds { get; set; } = 60;
}

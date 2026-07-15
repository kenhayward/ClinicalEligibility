namespace EligibilityProcessing.Web.Auth;

/// <summary>
/// Bound from the "Auth" configuration section. Google ClientId/ClientSecret come
/// from user-secrets / .env (not checked-in JSON), like the SMTP credentials.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Sliding cookie lifetime in hours.</summary>
    public int CookieExpiryHours { get; set; } = 8;

    public GoogleAuthOptions Google { get; set; } = new();

    public sealed class GoogleAuthOptions
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";

        /// <summary>True once both Google credentials are present.</summary>
        public bool Enabled =>
            !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    }
}

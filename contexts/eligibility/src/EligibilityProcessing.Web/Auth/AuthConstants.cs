namespace EligibilityProcessing.Web.Auth;

public static class AuthConstants
{
    /// <summary>Transient cookie scheme the Google handler signs into before account mapping.</summary>
    public const string ExternalScheme = "External";

    /// <summary>Claim type carrying the user's avatar URL (Google picture).</summary>
    public const string PictureClaim = "picture";
}

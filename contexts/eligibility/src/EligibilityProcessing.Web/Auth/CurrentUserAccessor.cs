using System.Security.Claims;

namespace EligibilityProcessing.Web.Auth;

/// <summary>The acting user for the current request, read from the auth cookie claims.</summary>
public interface ICurrentUserAccessor
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string UserName { get; }
    string Email { get; }

    /// <summary>A human-readable label for audit rows (userid, falling back to email).</summary>
    string AuditLabel { get; }
}

public sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _http;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor http) => _http = http;

    private ClaimsPrincipal? User => _http.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string UserName => User?.FindFirstValue(ClaimTypes.Name) ?? "";

    public string Email => User?.FindFirstValue(ClaimTypes.Email) ?? "";

    public string AuditLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(UserName)) return UserName;
            if (!string.IsNullOrEmpty(Email)) return Email;
            return "(anonymous)";
        }
    }
}

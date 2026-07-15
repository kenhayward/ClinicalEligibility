using EligibilityProcessing.Core;

namespace EligibilityProcessing.Web.Auth;

/// <summary>
/// Writes audit rows for manual create/update/delete actions and logins. All
/// writes are best-effort — a failure is logged but never propagated, so auditing
/// can't break a user action.
/// </summary>
public interface IAuditWriter
{
    /// <summary>Audits an action attributed to the current request's user.</summary>
    Task WriteAsync(string action, string entityType, string? entityId, string? detail, CancellationToken cancellationToken);

    /// <summary>
    /// Audits an action with an explicit actor. Used for login events, where the
    /// principal has just been issued and isn't yet on HttpContext.User, and for
    /// denied logins (no user id at all).
    /// </summary>
    Task WriteAsync(Guid? userId, string userLabel, string action, string entityType, string? entityId, string? detail, CancellationToken cancellationToken);
}

public sealed class AuditWriter : IAuditWriter
{
    private readonly IPostgresGateway _gateway;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ILogger<AuditWriter> _logger;

    public AuditWriter(IPostgresGateway gateway, ICurrentUserAccessor currentUser, ILogger<AuditWriter> logger)
    {
        _gateway = gateway;
        _currentUser = currentUser;
        _logger = logger;
    }

    public Task WriteAsync(string action, string entityType, string? entityId, string? detail, CancellationToken cancellationToken) =>
        WriteAsync(_currentUser.UserId, _currentUser.AuditLabel, action, entityType, entityId, detail, cancellationToken);

    public async Task WriteAsync(Guid? userId, string userLabel, string action, string entityType, string? entityId, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            await _gateway.InsertAuditAsync(new AuditEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                UserId = userId,
                UserLabel = string.IsNullOrEmpty(userLabel) ? "(unknown)" : userLabel,
                Action = action,
                EntityType = entityType,
                EntityId = entityId ?? "",
                Detail = detail ?? ""
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write audit row action={Action} entity={EntityType}/{EntityId}", action, entityType, entityId);
        }
    }
}

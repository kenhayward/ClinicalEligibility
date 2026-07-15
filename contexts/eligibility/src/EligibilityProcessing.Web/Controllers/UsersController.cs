using System.Globalization;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// Manage Accounts (admins only): list/add/change-role/delete users. JSON
/// endpoints backing the modal. Enforces the protected-Owner rule — the last
/// Owner can't be demoted or deleted, and only an Owner may touch another Owner.
/// </summary>
[Authorize(Policy = "ManageUsers")]
[Route("Users")]
public class UsersController : Controller
{
    private readonly IPostgresGateway _gateway;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IAuditWriter _audit;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IPostgresGateway gateway,
        IPasswordHasher passwordHasher,
        ICurrentUserAccessor currentUser,
        IAuditWriter audit,
        ILogger<UsersController> logger)
    {
        _gateway = gateway;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
        _audit = audit;
        _logger = logger;
    }

    private bool ActingUserIsOwner => User.IsInRole(Roles.Owner);

    [HttpGet("List")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var users = await _gateway.ListUsersAsync(cancellationToken);
        return Json(new
        {
            users = users.Select(u => new
            {
                userId = u.UserId,
                userName = u.UserName,
                email = u.Email,
                displayName = u.DisplayName,
                role = Roles.ToRoleName(u.Role),
                isActive = u.IsActive,
                hasPassword = !string.IsNullOrEmpty(u.PasswordHash),
                hasGoogle = !string.IsNullOrEmpty(u.GoogleSubject),
                pictureUrl = u.PictureUrl,
                lastLoginAt = u.LastLoginAt
            })
        });
    }

    // NOTE: bind everything [FromQuery]. "action" is a reserved MVC route key, so
    // without an explicit query-string binding the parameter would be filled from
    // RouteData ("AuditLog", the action name) instead of ?action=, making the
    // filter always "WHERE action = 'AuditLog'" — which matches nothing.
    [HttpGet("AuditLog")]
    public async Task<IActionResult> AuditLog(
        [FromQuery] string? user,
        [FromQuery(Name = "action")] string? actionFilter,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var filter = new AuditLogFilter
        {
            UserSearch = user?.Trim() ?? "",
            Action = actionFilter?.Trim() ?? "",
            FromUtc = from,
            ToUtc = to
        };

        try
        {
            var result = await _gateway.GetAuditLogAsync(
                filter, page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize, cancellationToken);
            return Json(new
            {
                rows = result.Rows.Select(r => new
                {
                    occurredAt = r.OccurredAt,
                    user = r.UserLabel,
                    action = r.Action,
                    entityType = r.EntityType,
                    entityId = r.EntityId,
                    detail = r.Detail
                }),
                page = result.Page,
                pageSize = result.PageSize,
                totalRows = result.TotalRows,
                totalPages = result.TotalPages
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load audit log");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    // Exports ALL rows matching the filter (not just the current page) as CSV.
    // Same [FromQuery] binding caveat as AuditLog re: the reserved "action" key.
    [HttpGet("AuditLog/Export")]
    public async Task<IActionResult> AuditLogExport(
        [FromQuery] string? user,
        [FromQuery(Name = "action")] string? actionFilter,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var filter = new AuditLogFilter
        {
            UserSearch = user?.Trim() ?? "",
            Action = actionFilter?.Trim() ?? "",
            FromUtc = from,
            ToUtc = to
        };

        try
        {
            var rows = await _gateway.GetAuditLogForExportAsync(filter, cancellationToken);
            var headers = new[] { "Time (UTC)", "User", "Action", "Entity Type", "Entity Id", "Detail" };
            var csv = CsvWriter.Build(headers, rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.UserLabel,
                r.Action,
                r.EntityType,
                r.EntityId,
                r.Detail
            }));
            var name = $"audit-trail-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
            await _audit.WriteAsync("export", "audit_log", null, $"{rows.Count} rows", cancellationToken);
            return ExportResults.CsvFile(csv, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to export audit log");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? userName, string? email, string? displayName, string? role, string? password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { error = "User name and email are required." });
        }
        Role parsedRole = default;
        if (!Roles.TryParseRole(role, ref parsedRole))
        {
            return BadRequest(new { error = "Unknown role." });
        }
        if (parsedRole == Role.Owner && !ActingUserIsOwner)
        {
            return Forbid();
        }

        var user = new AppUser
        {
            UserId = Guid.NewGuid(),
            UserName = userName.Trim(),
            Email = email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            Role = parsedRole,
            PasswordHash = string.IsNullOrWhiteSpace(password) ? "" : _passwordHasher.Hash(password),
            IsActive = true
        };

        try
        {
            await _gateway.CreateUserAsync(user, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create user {UserName}", userName);
            return BadRequest(new { error = "Could not create user (the name or email may already exist)." });
        }

        await _audit.WriteAsync("create", "app_user", user.UserId.ToString(),
            $"{user.UserName} as {Roles.ToRoleName(parsedRole)}", cancellationToken);
        return Json(new { ok = true });
    }

    [HttpPost("ChangeRole")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeRole(Guid userId, string? role, CancellationToken cancellationToken)
    {
        Role newRole = default;
        if (!Roles.TryParseRole(role, ref newRole))
        {
            return BadRequest(new { error = "Unknown role." });
        }

        var target = await _gateway.GetUserAsync(userId, cancellationToken);
        if (target is null)
        {
            return NotFound(new { error = "User not found." });
        }
        if (target.Role == newRole)
        {
            return Json(new { ok = true });
        }

        // Only an Owner may grant or remove the Owner role.
        if ((target.Role == Role.Owner || newRole == Role.Owner) && !ActingUserIsOwner)
        {
            return Forbid();
        }
        // Never demote the last Owner.
        if (target.Role == Role.Owner && newRole != Role.Owner)
        {
            var owners = await _gateway.CountOwnersAsync(cancellationToken);
            if (owners <= 1)
            {
                return BadRequest(new { error = "Cannot demote the last Owner." });
            }
        }

        await _gateway.UpdateUserRoleAsync(userId, newRole, cancellationToken);
        await _audit.WriteAsync("role_change", "app_user", userId.ToString(),
            $"{Roles.ToRoleName(target.Role)} -> {Roles.ToRoleName(newRole)}", cancellationToken);
        return Json(new { ok = true });
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid userId, CancellationToken cancellationToken)
    {
        var target = await _gateway.GetUserAsync(userId, cancellationToken);
        if (target is null)
        {
            return NotFound(new { error = "User not found." });
        }
        if (_currentUser.UserId == userId)
        {
            return BadRequest(new { error = "You cannot delete your own account." });
        }
        if (target.Role == Role.Owner)
        {
            if (!ActingUserIsOwner)
            {
                return Forbid();
            }
            var owners = await _gateway.CountOwnersAsync(cancellationToken);
            if (owners <= 1)
            {
                return BadRequest(new { error = "Cannot delete the last Owner." });
            }
        }

        await _gateway.DeleteUserAsync(userId, cancellationToken);
        await _audit.WriteAsync("delete", "app_user", userId.ToString(), target.UserName, cancellationToken);
        return Json(new { ok = true });
    }
}

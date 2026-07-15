using System.Security.Claims;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// Sign-in surface: password login, Google OAuth (with account linking by email),
/// first-run owner bootstrap, and sign-out. Anonymous by default so an
/// unauthenticated user can actually reach the login page.
/// </summary>
[AllowAnonymous]
public class AccountController : Controller
{
    private const int MinPasswordLength = 8;

    private readonly IPostgresGateway _gateway;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditWriter _audit;
    private readonly IAccessRequestNotifier _accessRequestNotifier;
    private readonly IOptions<AuthOptions> _authOptions;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IPostgresGateway gateway,
        IPasswordHasher passwordHasher,
        IAuditWriter audit,
        IAccessRequestNotifier accessRequestNotifier,
        IOptions<AuthOptions> authOptions,
        ILogger<AccountController> logger)
    {
        _gateway = gateway;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _accessRequestNotifier = accessRequestNotifier;
        _authOptions = authOptions;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken cancellationToken)
    {
        // Bootstrap only when we can confirm the table is genuinely empty. If the
        // count can't be read (DB down), fail toward showing the login form rather
        // than the open bootstrap page.
        int userCount;
        try
        {
            userCount = await _gateway.CountUsersAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count users for login bootstrap check");
            userCount = -1;
        }

        if (userCount == 0)
        {
            return RedirectToAction(nameof(Bootstrap));
        }

        return View(new LoginViewModel
        {
            ReturnUrl = returnUrl,
            GoogleEnabled = _authOptions.Value.Google.Enabled
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (await _gateway.CountUsersAsync(cancellationToken) == 0)
        {
            return RedirectToAction(nameof(Bootstrap));
        }

        model.GoogleEnabled = _authOptions.Value.Google.Enabled;

        var user = await _gateway.GetUserByUserNameAsync(model.UserName ?? "", cancellationToken);
        if (user is null || !user.IsActive ||
            !_passwordHasher.Verify(model.Password ?? "", user.PasswordHash))
        {
            await _audit.WriteAsync(null, string.IsNullOrWhiteSpace(model.UserName) ? "(unknown)" : model.UserName!,
                "login_denied", "session", null, "invalid credentials", cancellationToken);
            model.Password = "";
            model.ErrorMessage = "Invalid user name or password.";
            return View(model);
        }

        await SignInUserAsync(user, cancellationToken);
        return LocalRedirectOrHome(model.ReturnUrl);
    }

    [HttpGet]
    public IActionResult ExternalLogin(string? returnUrl)
    {
        if (!_authOptions.Value.Google.Enabled)
        {
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        var redirectUrl = Url.Action(nameof(GoogleCallback), "Account", new { returnUrl });
        var props = new AuthenticationProperties { RedirectUri = redirectUrl };
        return Challenge(props, GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet]
    public async Task<IActionResult> GoogleCallback(string? returnUrl, CancellationToken cancellationToken)
    {
        var result = await HttpContext.AuthenticateAsync(AuthConstants.ExternalScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return RedirectToAction(nameof(Login));
        }

        var external = result.Principal;
        var subject = external.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var email = external.FindFirstValue(ClaimTypes.Email) ?? "";
        var name = external.FindFirstValue(ClaimTypes.Name) ?? "";
        var picture = external.FindFirstValue("picture")
            ?? external.FindFirstValue("urn:google:picture") ?? "";

        // 1) Already-linked Google identity.
        AppUser? user = string.IsNullOrEmpty(subject)
            ? null
            : await _gateway.GetUserByGoogleSubjectAsync(subject, cancellationToken);

        // 2) Not linked yet — match by email and link.
        if (user is null && !string.IsNullOrEmpty(email))
        {
            var byEmail = await _gateway.GetUserByEmailAsync(email, cancellationToken);
            if (byEmail is { IsActive: true })
            {
                await _gateway.LinkGoogleSubjectAsync(byEmail.UserId, subject, picture, cancellationToken);
                byEmail.GoogleSubject = subject;
                if (!string.IsNullOrEmpty(picture)) byEmail.PictureUrl = picture;
                user = byEmail;
            }
        }

        // Drop the transient external cookie regardless of outcome.
        await HttpContext.SignOutAsync(AuthConstants.ExternalScheme);

        // 3) Unknown / inactive — deny, audit, and email an admin.
        if (user is null || !user.IsActive)
        {
            await _audit.WriteAsync(null, string.IsNullOrEmpty(email) ? "(unknown google user)" : email,
                "login_denied", "session", null, "no account for Google login", cancellationToken);
            await _accessRequestNotifier.SendAccessRequestAsync(name, email, cancellationToken);
            return View("Denied", new DeniedViewModel { Email = email, Name = name });
        }

        await SignInUserAsync(user, cancellationToken);
        return LocalRedirectOrHome(returnUrl);
    }

    [HttpGet]
    public async Task<IActionResult> Bootstrap(CancellationToken cancellationToken)
    {
        if (await _gateway.CountUsersAsync(cancellationToken) > 0)
        {
            return RedirectToAction(nameof(Login));
        }

        return View(new BootstrapViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bootstrap(BootstrapViewModel model, CancellationToken cancellationToken)
    {
        // Re-check server-side: once any user exists, bootstrap is closed for good
        // (no permanent backdoor — the GET check alone is not enough).
        if (await _gateway.CountUsersAsync(cancellationToken) > 0)
        {
            return RedirectToAction(nameof(Login));
        }

        var error = ValidateBootstrap(model);
        if (error is not null)
        {
            model.Password = "";
            model.ConfirmPassword = "";
            model.ErrorMessage = error;
            return View(model);
        }

        var user = new AppUser
        {
            UserId = Guid.NewGuid(),
            UserName = model.UserName!.Trim(),
            Email = model.Email!.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.UserName!.Trim() : model.DisplayName!.Trim(),
            Role = Role.Owner,
            PasswordHash = _passwordHasher.Hash(model.Password!),
            IsActive = true
        };

        try
        {
            await _gateway.CreateUserAsync(user, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create initial owner account");
            model.Password = "";
            model.ConfirmPassword = "";
            model.ErrorMessage = "Could not create the owner account: " + ex.Message;
            return View(model);
        }

        await _audit.WriteAsync(user.UserId, user.UserName, "bootstrap", "app_user", user.UserId.ToString(),
            "initial owner", cancellationToken);
        await SignInUserAsync(user, cancellationToken);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult Denied() => View(new DeniedViewModel());

    // ===== helpers =====

    private async Task SignInUserAsync(AppUser user, CancellationToken cancellationToken)
    {
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, AuthClaims.Build(user));
        await _gateway.RecordLoginAsync(user.UserId, DateTimeOffset.UtcNow, cancellationToken);
        await _audit.WriteAsync(user.UserId, user.UserName, "login", "session", user.UserId.ToString(), null, cancellationToken);
    }

    private IActionResult LocalRedirectOrHome(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");

    private static string? ValidateBootstrap(BootstrapViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.UserName) ||
            string.IsNullOrWhiteSpace(model.Email) ||
            string.IsNullOrWhiteSpace(model.Password))
        {
            return "User name, email, and password are required.";
        }
        if (model.Password != model.ConfirmPassword)
        {
            return "Passwords do not match.";
        }
        if (model.Password!.Length < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters.";
        }
        return null;
    }
}

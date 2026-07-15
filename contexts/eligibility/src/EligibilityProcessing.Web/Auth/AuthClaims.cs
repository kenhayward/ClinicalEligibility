using System.Security.Claims;
using EligibilityProcessing.Core;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace EligibilityProcessing.Web.Auth;

/// <summary>Builds the application's cookie principal from an <see cref="AppUser"/>.</summary>
public static class AuthClaims
{
    public static ClaimsPrincipal Build(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.UserName ?? ""),
            new(ClaimTypes.Email, user.Email ?? ""),
            new(ClaimTypes.Role, Roles.ToRoleName(user.Role)),
        };
        if (!string.IsNullOrEmpty(user.PictureUrl))
        {
            claims.Add(new Claim(AuthConstants.PictureClaim, user.PictureUrl));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}

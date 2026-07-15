namespace EligibilityProcessing.Web.Models;

public sealed class LoginViewModel
{
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? ReturnUrl { get; set; }
    public bool GoogleEnabled { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class BootstrapViewModel
{
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class DeniedViewModel
{
    /// <summary>Set when an unrecognised Google account was refused entry.</summary>
    public string? Email { get; set; }
    public string? Name { get; set; }

    public bool NoAccount => !string.IsNullOrEmpty(Email);
}

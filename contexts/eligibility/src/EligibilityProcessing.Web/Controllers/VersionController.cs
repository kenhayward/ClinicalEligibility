using EligibilityProcessing.Web.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// Surfaces the app version (JSON, for the About modal) and the Release Notes page
/// (opened in its own tab). Both are anonymous - the version is not sensitive and
/// the page should render even before sign-in. Data comes from the embedded
/// version.json single source of truth via <see cref="AppVersion"/>.
/// </summary>
[AllowAnonymous]
public class VersionController : Controller
{
    // GET /Version - the current version + schema version (mirrors the authoring API shape).
    [HttpGet("/Version")]
    public IActionResult Info() => Json(AppVersion.GetInfo());

    // GET /ReleaseNotes - the changelog page (Enhancements vs Bug fixes per version).
    [HttpGet("/ReleaseNotes")]
    public IActionResult ReleaseNotes() => View(AppVersion.GetReleaseNotes());
}

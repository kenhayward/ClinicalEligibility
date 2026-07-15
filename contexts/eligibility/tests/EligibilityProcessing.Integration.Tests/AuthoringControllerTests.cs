using System.Net;
using EligibilityProcessing.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// The reinstated Authoring area's authorization surface. Write actions are gated
/// by the "AuthorWrite" policy (Owner / Administrator / Author) - a Viewer is
/// rejected; read actions (list / export) are open to any authenticated user; and
/// the Authoring nav link is present in the dashboard chrome. The gateway points
/// at an unreachable Postgres, so read actions catch the failure and render inline
/// (200) rather than 500 - the graceful-degradation path the dashboard follows.
/// </summary>
public class AuthoringControllerTests
{
    private static readonly Dictionary<string, string?> PlaceholderConfig = new()
    {
        ["Postgres:ConnectionStringSource"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Postgres:ConnectionStringOutput"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Umls:ApiKey"] = "test",
        ["Llm:ApiKey"] = "test",
        ["Llm:BaseUrl"] = "http://localhost:8080/v1"
    };

    // The five state-changing endpoints, all [Authorize(Policy = "AuthorWrite")].
    public static IEnumerable<object[]> WriteEndpoints => new[]
    {
        new object[] { "/Authoring/Create" },
        new object[] { "/Authoring/SaveStudy" },
        new object[] { "/Authoring/SaveEligibility" },
        new object[] { "/Authoring/Delete" },
        new object[] { "/Authoring/SaveCriteria" }
    };

    // ===== write gate: AuthorWrite (Owner / Administrator / Author), not Viewer =====

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public async Task Viewer_is_forbidden_on_write_endpoints(string url)
    {
        // Authorization runs before anti-forgery, so a Viewer gets 403 even though
        // this POST carries no token.
        Assert.Equal(HttpStatusCode.Forbidden, await Post(url, "Viewer"));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Administrator")]
    [InlineData("Author")]
    public async Task AuthorWrite_roles_pass_the_write_gate(string role)
    {
        // An allowed role clears authorization; the missing anti-forgery token then
        // yields 400 (not 403). Proving "not Forbidden" is what asserts the gate
        // admits the role - crucially distinguishing AuthorWrite from OwnerOnly.
        Assert.NotEqual(HttpStatusCode.Forbidden, await Post("/Authoring/Create", role));
    }

    // ===== read actions: any authenticated user, including Viewer =====

    [Fact]
    public async Task Viewer_can_open_the_authoring_index()
    {
        // Index catches the (unreachable) gateway and renders inline, so a Viewer
        // still gets 200 - read access is not gated by AuthorWrite.
        Assert.Equal(HttpStatusCode.OK, await Get("/Authoring", "Viewer"));
    }

    [Fact]
    public async Task Viewer_is_not_forbidden_on_criteria_export()
    {
        // Export is a read action (no AuthorWrite gate). With no backend it may
        // 500/404, but it must never be 403 for a Viewer.
        Assert.NotEqual(HttpStatusCode.Forbidden,
            await Get("/Authoring/ExportCriteria?id=" + Guid.NewGuid(), "Viewer"));
    }

    // ===== dashboard integration: nav tab + master-detail shell =====

    [Fact]
    public async Task Dashboard_shows_the_authoring_nav_tab()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Author");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">Authoring</a>", body);                 // a normal in-dashboard nav tab
        Assert.Matches("href=\"/Authoring(/Index)?\"", body);    // routed to the Authoring controller
        Assert.DoesNotContain("&#x2197;", body);                 // no longer an "opens in new tab" button
    }

    [Fact]
    public async Task Authoring_index_renders_master_detail_shell_inside_the_dashboard()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Authoring");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Author");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The collapsible master-detail: shell wrapper, study-list sidebar, and
        // the collapse toggle that frees full page width.
        Assert.Contains("id=\"authoring-shell\"", body);
        Assert.Contains("id=\"study-list-panel\"", body);
        Assert.Contains("id=\"aside-toggle\"", body);
        Assert.Contains(">Studies<", body);
        // Rendered inside the dashboard layout (nav chrome present), not the old
        // standalone _AuthoringLayout page.
        Assert.Contains(">Dashboard</a>", body);
    }

    // ===== helpers =====

    private static async Task<HttpStatusCode> Post(string url, string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> Get(string url, string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private class AnonFactory : WebApplicationFactory<WebMarker>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(PlaceholderConfig));
        }
    }

    private sealed class AuthedFactory : AnonFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }
    }
}

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
/// Authentication + role-authorization behavior for the Web host. Uses two
/// factories: one with no auth (real cookie scheme) to verify the unauthenticated
/// redirect + anonymous endpoints, and one with the <see cref="TestAuthHandler"/>
/// so role gating can be exercised via the X-Test-Role header. None of these
/// tests need a database — middleware-level authorization runs before controllers,
/// and the dashboard renders its chrome even when the gateway is unavailable.
/// </summary>
public class AuthTests
{
    private static readonly Dictionary<string, string?> PlaceholderConfig = new()
    {
        ["Postgres:ConnectionStringSource"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Postgres:ConnectionStringOutput"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Umls:ApiKey"] = "test",
        ["Llm:ApiKey"] = "test",
        ["Llm:BaseUrl"] = "http://localhost:8080/v1"
    };

    // ===== anonymous (real cookie scheme) =====

    [Fact]
    public async Task Protected_page_redirects_unauthenticated_user_to_login()
    {
        using var factory = new AnonFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        using var factory = new AnonFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The dashboard's figures moved behind a JSON endpoint. It is read-tolerant
    // by design - it answers 200 with an { error } body rather than 500ing - so
    // pin that this tolerance does NOT extend to serving corpus counts to an
    // anonymous caller.
    [Fact]
    public async Task Metrics_endpoint_rejects_unauthenticated_callers()
    {
        using var factory = new AnonFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Home/Metrics");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Login_page_is_reachable_unauthenticated()
    {
        using var factory = new AnonFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign in", body);
    }

    // ===== role gating (test auth scheme) =====

    [Fact]
    public async Task Author_cannot_trigger_pipeline()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/Home/Trigger");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Author");
        // XHR header makes the forbid return 403 rather than redirecting to the
        // Access Denied page. Authorization runs before the antiforgery filter,
        // so a forbidden role gets 403 even without a token.
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_dashboard_but_sees_no_pipeline_controls()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Viewer");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Trigger Earliest", body);
        Assert.DoesNotContain("Manage Accounts", body);
        // ...but the toolbar is not left empty. A header row with nothing in it
        // reads as broken; "Read-only" says the controls are absent by design.
        Assert.Contains("Read-only", body);
    }

    [Fact]
    public async Task Owner_sees_pipeline_controls_and_manage_accounts()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Trigger Earliest", body);
        Assert.Contains("Manage Accounts", body);
    }

    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Audit_trail_endpoint_requires_manage_users(string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Users/AuditLog");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Audit_export_endpoint_requires_manage_users(string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Users/AuditLog/Export");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Author_does_not_see_manage_accounts()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Author");

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Manage Accounts", body);
    }

    // The Resolve action edits run history, so it sits behind the same
    // PipelineOps policy as every other run control. Authorization runs before
    // the antiforgery filter, so a forbidden role gets 403 without a token.
    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Resolve_run_requires_pipeline_ops(string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/Home/ResolveRun");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

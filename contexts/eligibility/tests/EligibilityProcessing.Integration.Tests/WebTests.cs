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
/// In-process smoke tests for the Web dashboard host. The factory points the
/// pipeline at placeholder Postgres connection strings; the controller catches
/// the gateway exception and renders the page with an inline "backend
/// unavailable" warning, so tests still get a 200 OK and can assert on the
/// expected layout chrome.
///
/// This is the graceful-degradation path the dashboard MUST follow per spec
/// section 6.4 — verifying it works in tests catches regressions where a
/// future change accidentally lets a gateway exception escape to an error page.
/// </summary>
public class WebTests : IClassFixture<WebTests.Factory>
{
    private readonly Factory _factory;

    public WebTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Index_returns_200_with_dashboard_chrome()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Dashboard", body);
        Assert.Contains("Eligibility Processing", body);
    }

    [Fact]
    public async Task Index_renders_inline_error_when_gateway_unavailable()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Placeholder connection strings + no real Postgres = gateway throws,
        // controller catches and surfaces inline.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Backend unavailable", body);
    }

    [Fact]
    public async Task Runs_returns_200_with_runs_chrome()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Runs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Runs", body);
    }

    [Fact]
    public async Task Runs_renders_inline_error_when_gateway_unavailable()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Runs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Backend unavailable", body);
    }

    [Fact]
    public async Task Nav_contains_links_to_dashboard_and_runs()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(">Dashboard<", body);
        Assert.Contains(">Runs<", body);
    }

    [Fact]
    public async Task History_returns_200_with_history_chrome()
    {
        // The per-trial audit browser was renamed from /Home/Studies to
        // /Home/History to match the nav label.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/History");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("History", body);
    }

    [Fact]
    public async Task History_nav_link_points_at_history_action()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        // Nav label is "History" and it links to the renamed /Home/History route.
        Assert.Contains("href=\"/Home/History\"", body);
        Assert.DoesNotContain("href=\"/Home/Studies\"", body);
    }

    [Fact]
    public async Task Old_studies_url_redirects_permanently_to_history()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var response = await client.GetAsync("/Home/Studies");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/Home/History", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Old_studies_url_redirect_preserves_query_string()
    {
        // Existing bookmarks / deep links carry filter + paging state; the 301
        // must keep it so they land on the same filtered view.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var response = await client.GetAsync("/Home/Studies?status=llm_failed&page=2");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/Home/History?status=llm_failed&page=2", response.Headers.Location!.ToString());
    }


    [Fact]
    public async Task Analysis_returns_200_with_form_chrome()
    {
        // No nctId: the controller returns the empty form without touching the
        // gateway, so this stays a clean 200 even with no backend.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Analysis");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Analysis", body);
        Assert.Contains("Analyse", body);
    }

    [Fact]
    public async Task Analysis_with_nctId_renders_inline_error_when_gateway_unavailable()
    {
        // With an nctId the controller queries the gateway — first the new
        // snapshot lookup, then the fallbacks. All hit placeholder Postgres and
        // throw; the controller must catch and surface inline rather than 500.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Analysis?nctId=NCT00000001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Backend unavailable", body);
    }

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/DoesNotExist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Index_embeds_signalr_client_and_activity_log()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        // The view section pulls signalr.min.js and starts a connection to /hubs/progress.
        Assert.Contains("signalr.min.js", body);
        Assert.Contains("/hubs/progress", body);
        Assert.Contains("activity-log", body);
    }

    [Fact]
    public async Task Hubs_progress_endpoint_is_mapped()
    {
        // A bare GET to a SignalR hub returns 400 (not 404) because the server
        // recognises the path but the request isn't a valid SignalR negotiation.
        // 404 would mean the hub wasn't mapped at all.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/hubs/progress");
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<WebMarker>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Postgres:ConnectionStringSource"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
                    ["Postgres:ConnectionStringOutput"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
                    ["Umls:ApiKey"] = "test",
                    ["Llm:ApiKey"] = "test",
                    ["Llm:BaseUrl"] = "http://localhost:8080/v1"
                });
            });
            // Authenticate every request (default Owner; override per-request via
            // the X-Test-Role header) so these page tests bypass cookie/Google
            // sign-in. The app's FallbackPolicy requires an authenticated user.
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }
    }
}

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

    // The Resolve modal renders from the PipelineOps check alone, independent of
    // whether the run table loaded - so these two assert the authorization gate
    // and that the Razor actually emits the modal (and its antiforgery token),
    // which a compile-only check would not catch.
    [Fact]
    public async Task Runs_renders_the_resolve_modal_for_pipeline_ops()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Home/Runs");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("resolveRunModal", body);
        Assert.Contains("__RequestVerificationToken", body);
    }

    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Runs_hides_the_resolve_modal_from_non_pipeline_ops(string role)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Home/Runs");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("resolveRunModal", body);
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

    // The nav rail is icon-only, so every item's label is a .visually-hidden
    // span - that span IS the accessible name. Without it the whole navigation
    // is unusable to a screen reader and unnameable to anything but sight, so
    // pin all seven rather than trusting an icon to speak for itself.
    [Theory]
    [InlineData("Dashboard", "/")]          // default route collapses Home/Index to "/"
    [InlineData("Runs", "/Home/Runs")]
    [InlineData("History", "/Home/History")]
    [InlineData("Analysis", "/Home/Analysis")]
    [InlineData("Results", "/Home/Results")]
    [InlineData("Tools", "/Home/Tools")]
    [InlineData("Authoring", "/Authoring")]
    public async Task Nav_rail_item_has_an_accessible_label_and_route(string label, string href)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        // Matched loosely: _Layout has a .cshtml.css companion, so CSS isolation
        // stamps every element in it with a scope attribute, and it lands BEFORE
        // the class - <span b-0npz3m0mho class="visually-hidden">.
        Assert.Matches($"<span[^>]*class=\"visually-hidden\"[^>]*>{label}</span>", body);
        Assert.Contains($"href=\"{href}\"", body);
    }

    // The theme picker is three-state (Light / Dark / Auto) and lives in the
    // user menu, which the rail redesign relocated. "auto" is the default and
    // the only mode that tracks the OS, so losing it is a silent regression a
    // purely visual review would not catch.
    [Fact]
    public async Task Theme_picker_offers_light_dark_and_auto()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-bs-theme-value=\"light\"", body);
        Assert.Contains("data-bs-theme-value=\"dark\"", body);
        Assert.Contains("data-bs-theme-value=\"auto\"", body);
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


    // ===== GET /Home/Metrics =====
    // The dashboard fetches its corpus figures from here so it can show a real
    // skeleton, refresh on run completion, and give Reload something to do.

    // Read-tolerant like ToolCounts: a 500 here would leave the dashboard
    // permanently skeletal on a backend hiccup, when it should render the
    // message inline and stay usable (spec section 6.4). The placeholder
    // connection string in this harness means the gateway always throws, which
    // is exactly the path under test.
    [Fact]
    public async Task Metrics_returns_200_with_an_inline_error_when_gateway_unavailable()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType!.ToString());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", body);
    }

    [Fact]
    public async Task Metrics_accepts_the_fresh_cache_bypass_flag()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Metrics?fresh=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    public async Task Analytics_index_returns_200_with_empty_form_chrome()
    {
        // No value: AnalyticsController.Index returns the empty form without
        // touching IAnalyticsGateway or ICorpusReadCache, so this stays a clean
        // 200 even with the factory's unreachable Postgres connection strings -
        // same shape as Analysis_returns_200_with_form_chrome above.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Analytics/Index");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Analytics", body);
        Assert.Contains("Choose a cohort", body);
    }

    [Fact]
    public async Task Analytics_trend_returns_200_with_empty_form_chrome()
    {
        // No codes: AnalyticsController.Trend returns the empty form without
        // touching IAnalyticsGateway, so this stays a clean 200 even with the
        // factory's unreachable Postgres connection strings.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Analytics/Trend");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Analytics - Trend", body);
        Assert.Contains("Enter one or more concept codes", body);
    }

    [Fact]
    public async Task Analytics_trend_renders_inline_error_when_gateway_unavailable()
    {
        // With codes supplied, the controller queries the gateway - it hits
        // placeholder Postgres and throws; the controller must catch and
        // surface inline rather than 500, same shape as Index above.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Analytics/Trend?codes=C0011849");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Backend unavailable", body);
    }

    [Fact]
    public async Task Analytics_concept_returns_200_with_empty_form_chrome()
    {
        // No code: AnalyticsController.Concept returns the empty form without
        // touching IAnalyticsGateway, so this stays a clean 200 even with the
        // factory's unreachable Postgres connection strings - same shape as
        // Analytics_index_returns_200_with_empty_form_chrome above. This is
        // also the only automated coverage that actually renders
        // Concept.cshtml through Razor - the controller-level tests never
        // execute the view.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Analytics/Concept");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Analytics - Concept", body);
        Assert.Contains("Concept code (CUI)", body);
    }

    [Fact]
    public async Task Analytics_concept_renders_inline_error_when_gateway_unavailable()
    {
        // With a code supplied, the controller queries the gateway - it hits
        // placeholder Postgres and throws; the controller must catch and
        // surface inline rather than 500, same shape as Trend above.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Analytics/Concept?code=C0011849");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Backend unavailable", body);
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

    // Auto-refresh controls. The timer logic itself has no automated coverage
    // (there is no JS test harness in this repo), so these assert the contract
    // the script depends on: the elements exist, the default is 30s, and the
    // controls are NOT behind the pipeline-ops gate.
    [Fact]
    public async Task Dashboard_renders_auto_refresh_controls()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("auto-refresh-toggle", body);
        Assert.Contains("auto-refresh-interval", body);
        Assert.Contains("auto-refresh-state", body);
    }

    [Fact]
    public async Task Auto_refresh_offers_four_intervals_defaulting_to_30_seconds()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("value=\"10000\"", body);
        Assert.Contains("value=\"30000\"", body);
        Assert.Contains("value=\"60000\"", body);
        Assert.Contains("value=\"300000\"", body);
        // The 30s option is the one carrying `selected`.
        Assert.Contains("value=\"30000\" selected", body);
    }

    // Watching a run is a read. The Reload button is deliberately outside the
    // PipelineOps gate for the same reason - a read-only user watching a batch
    // has more reason to refresh than anyone.
    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Auto_refresh_controls_render_for_read_only_roles(string role)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("auto-refresh-toggle", body);
    }

    // Smoke tests for the app's first multi-value GET filter.
    //
    // WHAT THESE DO NOT COVER: this factory points at an unreachable database
    // (port 1), so Results always takes its error path and the filter form -
    // which lives in the `else` branch of Results.cshtml - never renders. These
    // therefore prove only that the new parameter shapes bind without throwing;
    // they cannot assert the multi-select is emitted, that the pager preserves a
    // selection, or that the legacy name resolves to a TUI. Those are covered by
    // the gateway integration tests (containment semantics) and by manual
    // verification (the UI round-trip). Do not read a pass here as more than it
    // is.
    [Fact]
    public async Task Results_accepts_repeated_semantic_type_parameters()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Results?semanticTypeTuis=T121&semanticTypeTuis=T047");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Results_tolerates_the_legacy_semantic_type_parameter()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Results?semanticType=Pharmacologic+Substance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

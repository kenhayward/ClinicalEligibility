using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EligibilityProcessing.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// In-process tests for the webhook host. Uses <see cref="WebApplicationFactory{TEntryPoint}"/>
/// to spin up the real app pipeline (auth, rate limiting, channel, background worker)
/// while overriding configuration so we never hit a real database / LLM / UMLS.
///
/// The orchestrator gets resolved in the background and will fail trying to
/// connect to the placeholder Postgres — but that happens *after* the 202 has
/// already been returned, and the orchestrator catches the failure as a
/// "failed" RunMetrics, so it doesn't escape the BackgroundService.
/// </summary>
public class WebhookTests : IClassFixture<WebhookTests.Factory>
{
    private const string TestSecret = "test-secret-123";

    private readonly Factory _factory;

    public WebhookTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_returns_200_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Trigger_without_secret_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/trigger", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trigger_with_wrong_secret_returns_401()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/trigger");
        request.Headers.Add("X-Eligibility-Token", "definitely-not-the-secret");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trigger_with_correct_secret_returns_202_with_run_id()
    {
        await WaitForIdleAsync();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/trigger");
        request.Headers.Add("X-Eligibility-Token", TestSecret);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = body.GetProperty("run_id").GetGuid();
        Assert.NotEqual(Guid.Empty, runId);
        Assert.Equal(500, body.GetProperty("study_count").GetInt32());
        Assert.True(body.GetProperty("started_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Trigger_with_explicit_count_overrides_default()
    {
        await WaitForIdleAsync();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/trigger?count=42");
        request.Headers.Add("X-Eligibility-Token", TestSecret);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(42, body.GetProperty("study_count").GetInt32());
    }

    [Fact]
    public async Task Trigger_with_non_positive_count_returns_400()
    {
        await WaitForIdleAsync();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/trigger?count=0");
        request.Headers.Add("X-Eligibility-Token", TestSecret);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Trigger tests share the factory's RunGate singleton — a previous test
    /// may have left a run still draining (the orchestrator fails fast against
    /// the bogus DB, but it isn't synchronous). Poll until the gate clears so
    /// the next trigger gets 202 instead of 409.
    /// </summary>
    private async Task WaitForIdleAsync(int timeoutSeconds = 10)
    {
        var gate = _factory.Services.GetRequiredService<RunGate>();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (gate.CurrentRunId.HasValue && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        if (gate.CurrentRunId.HasValue)
        {
            throw new TimeoutException(
                $"RunGate did not release within {timeoutSeconds}s; current run = {gate.CurrentRunId}");
        }
    }

    [Fact]
    public async Task GET_trigger_returns_405()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/trigger");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// WebApplicationFactory that injects test config: a known shared secret,
    /// placeholder Postgres connection strings (the orchestrator will fail
    /// fast against these, but the failure happens *after* /trigger has
    /// already returned 202), and an inflated rate-limit window so test
    /// requests don't trip the 1/60s production policy.
    ///
    /// BatchRunner is left enabled — tests rely on it to drain the channel
    /// and release the RunGate after each run completes. See WaitForIdleAsync.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<WebMarker>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Webhook:Secret"] = TestSecret,
                    ["Webhook:DefaultStudyCount"] = "500",
                    ["Webhook:RateLimitPermits"] = "10000",
                    ["Webhook:RateLimitWindowSeconds"] = "1",
                    ["Postgres:ConnectionStringSource"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
                    ["Postgres:ConnectionStringOutput"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
                    ["Umls:ApiKey"] = "test",
                    ["Llm:ApiKey"] = "test",
                    ["Llm:BaseUrl"] = "http://localhost:8080/v1"
                });
            });
            // Authenticate requests so the FallbackPolicy (auth-by-default) doesn't
            // redirect unknown-path / wrong-method requests to the login page —
            // these tests assert the underlying 404 / 405 routing. /trigger itself
            // is anonymous and gated by its own shared-secret check regardless.
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }
    }
}

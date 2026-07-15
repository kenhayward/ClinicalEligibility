using System.Net;
using System.Threading.Channels;
using EligibilityProcessing.Web;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Controllers;
using EligibilityProcessing.Web.Seeding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// The owner-only "Create database seed" surface: every endpoint is Owner-only (an
/// Administrator is rejected, unlike PipelineOps tools), the menu item is Owner-only,
/// enqueue acquires the shared gate and rejects a second concurrent run, and Download
/// 404s when nothing has been produced.
/// </summary>
public class SeedControllerTests
{
    private static readonly Dictionary<string, string?> PlaceholderConfig = new()
    {
        ["Postgres:ConnectionStringSource"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Postgres:ConnectionStringOutput"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Umls:ApiKey"] = "test",
        ["Llm:ApiKey"] = "test",
        ["Llm:BaseUrl"] = "http://localhost:8080/v1"
    };

    // ===== auth gate: Owner-only across every endpoint =====

    [Theory]
    [InlineData("Administrator")]   // crucial: proves Owner-only, not PipelineOps
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Non_owner_is_forbidden_on_state(string role)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(HttpMethod.Get, "/Seed/State", role));
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Non_owner_is_forbidden_on_run(string role)
    {
        // Authorization runs before anti-forgery, so a forbidden role gets 403
        // even though this POST carries no token.
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(HttpMethod.Post, "/Seed/Run", role));
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Non_owner_is_forbidden_on_download(string role)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(HttpMethod.Get, "/Seed/Download", role));
    }

    // ===== menu visibility =====

    [Fact]
    public async Task Owner_sees_create_seed_menu_item()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Create database seed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Administrator_does_not_see_create_seed_menu_item()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Administrator");

        var response = await client.SendAsync(request);

        Assert.DoesNotContain("Create database seed", await response.Content.ReadAsStringAsync());
    }

    // ===== controller logic (direct, no host, no pg_dump) =====

    [Fact]
    public async Task Run_acquires_the_gate_enqueues_and_returns_202()
    {
        var controller = NewController(out var audit);
        var gate = new RunGate();
        var channel = NewChannel();

        var result = await controller.Run(gate, channel);

        Assert.IsType<AcceptedResult>(result);
        Assert.True(gate.IsBusy);
        Assert.Equal("create-seed", gate.CurrentActivity);
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(1, audit.Writes);
    }

    [Fact]
    public async Task Run_returns_409_when_the_gate_is_already_held()
    {
        var controller = NewController(out _);
        var gate = new RunGate();
        var channel = NewChannel();

        await controller.Run(gate, channel);            // first acquires, not released
        var second = await controller.Run(gate, channel);

        Assert.IsType<ConflictObjectResult>(second);
    }

    [Fact]
    public async Task Download_returns_404_when_no_seed_has_been_produced()
    {
        var controller = NewController(out _);

        Assert.IsType<NotFoundObjectResult>(await controller.Download());
    }

    [Fact]
    public void State_is_null_until_a_job_runs()
    {
        var controller = NewController(out _);
        var json = Assert.IsType<JsonResult>(controller.State());
        Assert.Null(json.Value);
    }

    // ===== helpers =====

    private static SeedController NewController(out FakeAuditWriter audit)
    {
        audit = new FakeAuditWriter();
        return new SeedController(new SeedJobState(), audit, NullLogger<SeedController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static Channel<SeedJobRequest> NewChannel() =>
        Channel.CreateBounded<SeedJobRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private async Task<HttpStatusCode> GetStatus(HttpMethod method, string url, string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class FakeAuditWriter : IAuditWriter
    {
        public int Writes { get; private set; }

        public Task WriteAsync(string action, string entityType, string? entityId, string? detail, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public Task WriteAsync(Guid? userId, string userLabel, string action, string entityType, string? entityId, string? detail, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
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

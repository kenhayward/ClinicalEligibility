using System.Net;
using System.Text;
using System.Threading.Channels;
using EligibilityProcessing.Web;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Controllers;
using EligibilityProcessing.Web.Embeddings;
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
/// The owner-only embeddings export/import surface: every endpoint is Owner-only (an
/// Administrator is rejected, unlike PipelineOps tools), export/import acquire the
/// shared gate and reject a second concurrent run, a bad import URL is rejected before
/// the gate is taken, an upload with no file is rejected, and Download 404s when
/// nothing has been produced.
/// </summary>
public class EmbeddingsControllerTests
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
    [InlineData(HttpMethod2.Get, "/Embeddings/State")]
    [InlineData(HttpMethod2.Get, "/Embeddings/Stats")]
    [InlineData(HttpMethod2.Get, "/Embeddings/Download")]
    [InlineData(HttpMethod2.Post, "/Embeddings/Export")]
    [InlineData(HttpMethod2.Post, "/Embeddings/ImportUrl")]
    [InlineData(HttpMethod2.Post, "/Embeddings/ImportUpload")]
    [InlineData(HttpMethod2.Post, "/Embeddings/Cancel")]
    public async Task Administrator_is_forbidden_on_every_endpoint(HttpMethod2 method, string url)
    {
        // Administrator (not Owner) proves Owner-only, not PipelineOps. Authorization
        // runs before anti-forgery, so the POSTs get 403 despite carrying no token.
        var http = method == HttpMethod2.Get ? HttpMethod.Get : HttpMethod.Post;
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(http, url, "Administrator"));
    }

    public enum HttpMethod2 { Get, Post }

    // ===== menu: the modal (with the Embeddings tab) renders for the Owner =====

    [Fact]
    public async Task Owner_modal_has_the_embeddings_tab()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");

        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("id=\"emb-pane\"", body);       // the embeddings tab pane
        Assert.Contains("Database seed &amp; embeddings", body);  // the renamed menu/modal title
    }

    // ===== controller logic (direct, no host, no pg tools) =====

    [Fact]
    public async Task Export_acquires_the_gate_enqueues_and_returns_202()
    {
        var controller = NewController(out var audit);
        var gate = new RunGate();
        var channel = NewChannel();

        var result = await controller.Export(gate, channel);

        Assert.IsType<AcceptedResult>(result);
        Assert.True(gate.IsBusy);
        Assert.Equal("export-embeddings", gate.CurrentActivity);
        Assert.True(channel.Reader.TryRead(out var req));
        Assert.Equal(EmbeddingsJobKind.Export, req!.Kind);
        Assert.Equal(1, audit.Writes);
    }

    [Fact]
    public async Task Export_returns_409_when_the_gate_is_already_held()
    {
        var controller = NewController(out _);
        var gate = new RunGate();
        var channel = NewChannel();

        await controller.Export(gate, channel);
        Assert.IsType<ConflictObjectResult>(await controller.Export(gate, channel));
    }

    [Fact]
    public async Task ImportUrl_enqueues_an_import_with_the_url()
    {
        var controller = NewController(out _);
        var gate = new RunGate();
        var channel = NewChannel();

        var result = await controller.ImportUrl("https://example.com/embeddings.dump", gate, channel);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal("import-embeddings", gate.CurrentActivity);
        Assert.True(channel.Reader.TryRead(out var req));
        Assert.Equal(EmbeddingsJobKind.Import, req!.Kind);
        Assert.Equal("https://example.com/embeddings.dump", req.SourceUrl);
        Assert.Null(req.InputFilePath);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/x.dump")]
    [InlineData("")]
    public async Task ImportUrl_rejects_a_bad_url_without_taking_the_gate(string url)
    {
        var controller = NewController(out _);
        var gate = new RunGate();
        var channel = NewChannel();

        Assert.IsType<BadRequestObjectResult>(await controller.ImportUrl(url, gate, channel));
        Assert.False(gate.IsBusy);  // gate untouched, so another job can still run
    }

    [Fact]
    public async Task ImportUpload_stages_the_file_and_enqueues_an_import()
    {
        var controller = NewController(out _);
        var gate = new RunGate();
        var channel = NewChannel();
        var file = FakeUpload("embeddings.dump", "PGDMP fake archive bytes");

        var result = await controller.ImportUpload(file, gate, channel);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal("import-embeddings", gate.CurrentActivity);
        Assert.True(channel.Reader.TryRead(out var req));
        Assert.Equal(EmbeddingsJobKind.Import, req!.Kind);
        Assert.NotNull(req.InputFilePath);
        Assert.True(File.Exists(req.InputFilePath!));
        Assert.Null(req.SourceUrl);

        TryDelete(req.InputFilePath!);  // clean up the staged temp file
    }

    [Fact]
    public async Task ImportUpload_rejects_a_missing_file()
    {
        var controller = NewController(out _);
        var gate = new RunGate();
        var channel = NewChannel();

        Assert.IsType<BadRequestObjectResult>(await controller.ImportUpload(null, gate, channel));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task Download_returns_404_when_no_export_has_been_produced()
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

    private static EmbeddingsController NewController(out FakeAuditWriter audit)
    {
        audit = new FakeAuditWriter();
        return new EmbeddingsController(new EmbeddingsJobState(), audit, NullLogger<EmbeddingsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static Channel<EmbeddingsJobRequest> NewChannel() =>
        Channel.CreateBounded<EmbeddingsJobRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private static IFormFile FakeUpload(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream"
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

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

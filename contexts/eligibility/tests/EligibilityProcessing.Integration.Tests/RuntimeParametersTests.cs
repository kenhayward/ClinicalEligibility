using System.Linq;
using System.Net;
using EligibilityProcessing.Core;
using EligibilityProcessing.Data;
using EligibilityProcessing.Llm;
using EligibilityProcessing.Umls;
using EligibilityProcessing.Web;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Controllers;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Owner-only Runtime Parameters panel: the auth gate (Owner only — Administrator
/// is rejected, unlike ManageUsers), the secret is never exposed, the menu item is
/// Owner-only, and the controller's validation + live-singleton mutation logic.
/// </summary>
public class RuntimeParametersTests
{
    private static readonly Dictionary<string, string?> PlaceholderConfig = new()
    {
        ["Postgres:ConnectionStringSource"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Postgres:ConnectionStringOutput"] = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
        ["Umls:ApiKey"] = "test",
        ["Llm:ApiKey"] = "test",
        ["Llm:BaseUrl"] = "http://localhost:8080/v1"
    };

    // ===== auth gate (test auth scheme) =====

    [Fact]
    public async Task Owner_can_read_current_parameters_without_exposing_apikey()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/RuntimeParameters/Current");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("model", body);
        Assert.Contains("reasoningEffort", body);
        // The normalize + embedding endpoints and models are surfaced read-only.
        Assert.Contains("normalizeEndpoint", body);
        Assert.Contains("normalizeModel", body);
        Assert.Contains("embeddingEndpoint", body);
        Assert.Contains("embeddingModel", body);
        // The secret must never be serialized.
        Assert.DoesNotContain("apiKey", body, StringComparison.OrdinalIgnoreCase);
    }

    // ===== read-only normalize / embedding model surfacing (direct) =====

    [Fact]
    public void Current_shows_normalize_model_inherited_when_unset_and_override_when_set()
    {
        var llm = Options.Create(new LlmOptions { Model = "gpt-oss-20b", BaseUrl = "http://main/v1" });

        // Unset normalize model inherits the extraction model, flagged.
        var inherited = ReadOnlyValues(NewController(
            llm, new LlmNormalizeOptions(), new EmbeddingOptions { Model = "bge-large" }));
        Assert.Equal("gpt-oss-20b (inherited)", inherited["normalizeModel"]);

        // Explicit normalize model is shown verbatim.
        var overridden = ReadOnlyValues(NewController(
            llm, new LlmNormalizeOptions { Model = "tiny-3b" }, new EmbeddingOptions { Model = "bge-large" }));
        Assert.Equal("tiny-3b", overridden["normalizeModel"]);
    }

    [Fact]
    public void Current_shows_embedding_model_verbatim_or_not_set_without_inheriting()
    {
        var llm = Options.Create(new LlmOptions { Model = "gpt-oss-20b", BaseUrl = "http://main/v1" });

        var withModel = ReadOnlyValues(NewController(
            llm, new LlmNormalizeOptions(), new EmbeddingOptions { Model = "bge-large" }));
        Assert.Equal("bge-large", withModel["embeddingModel"]);

        // No fallback: a blank embedding model surfaces as "(not set)", not the LLM model.
        var blank = ReadOnlyValues(NewController(
            llm, new LlmNormalizeOptions(), new EmbeddingOptions { Model = "" }));
        Assert.Equal("(not set)", blank["embeddingModel"]);
    }

    [Fact]
    public void Current_concurrency_cap_reflects_the_pipeline_cap_not_the_vestigial_llm_cap()
    {
        // Llm.ConcurrencyCap is unused; the panel must show the real pipeline cap
        // (Pipeline:LlmConcurrencyCap → Parallel.ForEachAsync MaxDegreeOfParallelism).
        // It is now an editable field rather than read-only.
        var llm = Options.Create(new LlmOptions { ConcurrencyCap = 99 });
        var values = EditableValues(NewController(
            llm, new LlmNormalizeOptions(), new EmbeddingOptions(),
            new OrchestratorOptions { LlmConcurrencyCap = 12 }));

        Assert.Equal("12", values["concurrencyCap"]);
    }

    [Theory]
    [InlineData("Administrator")]   // crucial: proves Owner-only, not ManageUsers
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Non_owner_cannot_read_current_parameters(string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/RuntimeParameters/Current");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Non_owner_cannot_save_parameters(string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/RuntimeParameters/Save");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ===== menu visibility =====

    [Fact]
    public async Task Owner_sees_runtime_parameters_menu_item()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Runtime Parameters", body);
    }

    [Fact]
    public async Task Administrator_does_not_see_runtime_parameters_menu_item()
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Administrator");

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Runtime Parameters", body);
    }

    // ===== controller logic (direct, no host) =====

    [Fact]
    public async Task Save_mutates_the_live_options_singleton()
    {
        var opts = Options.Create(new LlmOptions());
        // Hold the orchestrator instance so we can assert the cap is applied to it.
        var orchestrator = new OrchestratorOptions { LlmConcurrencyCap = 4 };
        var audit = new FakeAuditWriter();
        var controller = new RuntimeParametersController(
            opts,
            Options.Create(new LlmNormalizeOptions()),
            Options.Create(new EmbeddingOptions()),
            Options.Create(orchestrator),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            audit,
            NullLogger<RuntimeParametersController>.Instance);

        var result = await controller.Save(new RuntimeParametersForm
        {
            Model = "gpt-oss-20b",
            BaseUrl = "http://localhost:1234/v1",
            Temperature = "0.7",
            MaxTokens = "20000",
            EnableReasoning = true,
            ReasoningEffort = "high",
            EnableReasoningEscalation = true,
            EscalateReasoningEffort = "",
            ConcurrencyCap = "16",
            NormalizeEnableReasoning = true
        }, CancellationToken.None);

        Assert.IsType<JsonResult>(result);
        Assert.Equal("gpt-oss-20b", opts.Value.Model);
        Assert.Equal("http://localhost:1234/v1", opts.Value.BaseUrl);
        Assert.Equal(0.7, opts.Value.Temperature);
        Assert.Equal(20000, opts.Value.MaxTokens);
        Assert.True(opts.Value.EnableReasoning);
        Assert.Equal("high", opts.Value.ReasoningEffort);
        Assert.True(opts.Value.EnableReasoningEscalation);
        Assert.Equal("", opts.Value.EscalateReasoningEffort);
        // The cap was applied to the live orchestrator options (picked up next run).
        Assert.Equal(16, orchestrator.LlmConcurrencyCap);
        Assert.Equal(1, audit.Writes);
    }

    [Fact]
    public async Task Save_can_disable_reasoning_for_a_non_reasoning_model()
    {
        var opts = Options.Create(new LlmOptions { EnableReasoning = true });
        var controller = NewController(opts, new FakeAuditWriter());

        var result = await controller.Save(new RuntimeParametersForm
        {
            Model = "instruct-model",
            BaseUrl = "http://localhost:1234/v1",
            Temperature = "0.3",
            MaxTokens = "8000",
            EnableReasoning = false,
            ReasoningEffort = "low",
            EscalateReasoningEffort = "",
            ConcurrencyCap = "8"
        }, CancellationToken.None);

        Assert.IsType<JsonResult>(result);
        Assert.False(opts.Value.EnableReasoning);
    }

    [Fact]
    public void Current_surfaces_enable_reasoning_as_an_editable_value()
    {
        var opts = Options.Create(new LlmOptions { EnableReasoning = false });
        var values = EditableValues(new RuntimeParametersController(
            opts,
            Options.Create(new LlmNormalizeOptions()),
            Options.Create(new EmbeddingOptions()),
            Options.Create(new OrchestratorOptions()),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            new FakeAuditWriter(),
            NullLogger<RuntimeParametersController>.Instance));

        Assert.Equal("False", values["enableReasoning"]);
    }

    [Fact]
    public async Task Save_can_toggle_normalize_reasoning_independently()
    {
        var normalize = new LlmNormalizeOptions { EnableReasoning = true };
        var controller = new RuntimeParametersController(
            Options.Create(new LlmOptions()),
            Options.Create(normalize),
            Options.Create(new EmbeddingOptions()),
            Options.Create(new OrchestratorOptions()),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            new FakeAuditWriter(),
            NullLogger<RuntimeParametersController>.Instance);

        var result = await controller.Save(new RuntimeParametersForm
        {
            Model = "gpt-oss-20b",
            BaseUrl = "http://localhost:1234/v1",
            Temperature = "0.3",
            MaxTokens = "8000",
            EnableReasoning = true,                 // extraction stays on...
            ReasoningEffort = "low",
            EscalateReasoningEffort = "",
            ConcurrencyCap = "8",
            NormalizeEnableReasoning = false         // ...normalize turned off
        }, CancellationToken.None);

        Assert.IsType<JsonResult>(result);
        Assert.False(normalize.EnableReasoning);
    }

    [Fact]
    public void Current_surfaces_normalize_enable_reasoning_as_an_editable_value()
    {
        var values = EditableValues(new RuntimeParametersController(
            Options.Create(new LlmOptions()),
            Options.Create(new LlmNormalizeOptions { EnableReasoning = false }),
            Options.Create(new EmbeddingOptions()),
            Options.Create(new OrchestratorOptions()),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            new FakeAuditWriter(),
            NullLogger<RuntimeParametersController>.Instance));

        Assert.Equal("False", values["normalizeEnableReasoning"]);
    }

    [Theory]
    [InlineData("postgres", "postgres")]
    [InlineData("rest", "rest")]
    [InlineData("", "rest")]
    [InlineData("postgres ", "rest")]   // trailing space reverts to REST — shown faithfully
    public void Current_surfaces_the_effective_umls_backend(string configured, string expectedSubstring)
    {
        var values = ReadOnlyValues(new RuntimeParametersController(
            Options.Create(new LlmOptions()),
            Options.Create(new LlmNormalizeOptions()),
            Options.Create(new EmbeddingOptions()),
            Options.Create(new OrchestratorOptions()),
            Options.Create(new UmlsOptions { Backend = configured }),
            Options.Create(new PostgresOptions()),
            new FakeAuditWriter(),
            NullLogger<RuntimeParametersController>.Instance));

        Assert.Contains(expectedSubstring, values["umlsBackend"]);
    }

    [Theory]
    [InlineData("0")]       // below minimum
    [InlineData("257")]     // above the guardrail max
    [InlineData("abc")]     // not an integer
    [InlineData("")]        // missing
    public async Task Save_rejects_out_of_range_concurrency_cap_without_mutating(string cap)
    {
        var orchestrator = new OrchestratorOptions { LlmConcurrencyCap = 8 };
        var controller = new RuntimeParametersController(
            Options.Create(new LlmOptions()),
            Options.Create(new LlmNormalizeOptions()),
            Options.Create(new EmbeddingOptions()),
            Options.Create(orchestrator),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            new FakeAuditWriter(),
            NullLogger<RuntimeParametersController>.Instance);

        var result = await controller.Save(new RuntimeParametersForm
        {
            Model = "m",
            BaseUrl = "http://x/v1",
            Temperature = "0.3",
            MaxTokens = "100",
            ReasoningEffort = "low",
            EscalateReasoningEffort = "",
            ConcurrencyCap = cap
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Concurrency cap", bad.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(8, orchestrator.LlmConcurrencyCap);   // unchanged
    }

    [Theory]
    [InlineData("9", "100", "low", "http://x/v1", "Temperature")]      // temp out of range
    [InlineData("0.5", "0", "low", "http://x/v1", "Max tokens")]        // non-positive tokens
    [InlineData("0.5", "100", "extreme", "http://x/v1", "Reasoning")]   // bad effort
    [InlineData("0.5", "100", "low", "not-a-url", "Base URL")]          // bad base url
    public async Task Save_rejects_invalid_input_without_mutating(
        string temperature, string maxTokens, string effort, string baseUrl, string expectedErrorFragment)
    {
        var original = new LlmOptions();
        var opts = Options.Create(new LlmOptions
        {
            Model = original.Model,
            Temperature = original.Temperature,
            MaxTokens = original.MaxTokens
        });
        var audit = new FakeAuditWriter();
        var controller = NewController(opts, audit);

        var result = await controller.Save(new RuntimeParametersForm
        {
            Model = "m",
            BaseUrl = baseUrl,
            Temperature = temperature,
            MaxTokens = maxTokens,
            ReasoningEffort = effort,
            EnableReasoningEscalation = false,
            EscalateReasoningEffort = ""
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(expectedErrorFragment, bad.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
        // Nothing changed, and no audit row written.
        Assert.Equal(original.Temperature, opts.Value.Temperature);
        Assert.Equal(original.MaxTokens, opts.Value.MaxTokens);
        Assert.Equal(0, audit.Writes);
    }

    [Fact]
    public async Task Save_requires_a_model()
    {
        var opts = Options.Create(new LlmOptions());
        var controller = NewController(opts, new FakeAuditWriter());

        var result = await controller.Save(new RuntimeParametersForm
        {
            Model = "  ",
            BaseUrl = "http://x/v1",
            Temperature = "0.3",
            MaxTokens = "100",
            ReasoningEffort = "low",
            EscalateReasoningEffort = "medium"
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Model", bad.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ===== helpers =====

    private static RuntimeParametersController NewController(IOptions<LlmOptions> opts, IAuditWriter audit) =>
        new(opts,
            Options.Create(new LlmNormalizeOptions()),
            Options.Create(new EmbeddingOptions()),
            Options.Create(new OrchestratorOptions()),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            audit,
            NullLogger<RuntimeParametersController>.Instance);

    private static RuntimeParametersController NewController(
        IOptions<LlmOptions> llm, LlmNormalizeOptions normalize, EmbeddingOptions embedding,
        OrchestratorOptions? orchestrator = null) =>
        new(llm,
            Options.Create(normalize),
            Options.Create(embedding),
            Options.Create(orchestrator ?? new OrchestratorOptions()),
            Options.Create(new UmlsOptions()),
            Options.Create(new PostgresOptions()),
            new FakeAuditWriter(),
            NullLogger<RuntimeParametersController>.Instance);

    // Pulls a named anonymous sub-object ("readOnly" / "editable") out of
    // Current()'s JsonResult into a string map. Anonymous-type properties are
    // public, so cross-assembly reflection reads them fine even though the type
    // itself is internal.
    private static Dictionary<string, string> SectionValues(RuntimeParametersController controller, string section)
    {
        var json = Assert.IsType<JsonResult>(controller.Current());
        var value = json.Value!;
        var sub = value.GetType().GetProperty(section)!.GetValue(value)!;
        return sub.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(sub)?.ToString() ?? "");
    }

    private static Dictionary<string, string> ReadOnlyValues(RuntimeParametersController controller) =>
        SectionValues(controller, "readOnly");

    private static Dictionary<string, string> EditableValues(RuntimeParametersController controller) =>
        SectionValues(controller, "editable");

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

    // ===== DescribeConnection: the source/output database display =====
    //
    // This projects a connection string onto the panel, and connection strings
    // carry passwords. Every test here is really the same test: the password must
    // not come out the other side.

    // Hosts here are RFC 5737 / RFC 2606 documentation examples on purpose - this
    // is a public repository, so test fixtures must never carry a real
    // deployment's addresses.
    [Fact]
    public void DescribeConnection_shows_host_port_database_only()
    {
        var actual = RuntimeParametersController.DescribeConnection(
            "Host=192.0.2.10;Port=5432;Database=clinical;Username=postgres;Password=hunter2");

        Assert.Equal("192.0.2.10:5432/clinical", actual);
    }

    [Fact]
    public void DescribeConnection_never_leaks_credentials()
    {
        var actual = RuntimeParametersController.DescribeConnection(
            "Host=aact-db.example.org;Port=5432;Database=aact;Username=secretuser;Password=sup3rs3cret!");

        Assert.DoesNotContain("sup3rs3cret", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretuser", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", actual, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("aact-db.example.org:5432/aact", actual);
    }

    // A malformed string must NOT be echoed back - echoing is exactly how a
    // password would reach the screen.
    [Fact]
    public void DescribeConnection_does_not_echo_an_unparseable_string()
    {
        var actual = RuntimeParametersController.DescribeConnection(
            "this is not a connection string Password=leakme");

        Assert.DoesNotContain("leakme", actual, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("(unparseable)", actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DescribeConnection_reports_unset_for_blank(string? value)
    {
        Assert.Equal("(not set)", RuntimeParametersController.DescribeConnection(value));
    }

    [Fact]
    public void DescribeConnection_defaults_the_port_when_omitted()
    {
        // Npgsql defaults Port to 5432, so the display stays complete.
        Assert.Equal("localhost:5432/clinical",
            RuntimeParametersController.DescribeConnection("Host=localhost;Database=clinical"));
    }

    [Fact]
    public void DescribeConnection_handles_a_missing_database()
    {
        Assert.Equal("localhost:5432/?",
            RuntimeParametersController.DescribeConnection("Host=localhost"));
    }

    // The whole point of surfacing this: telling a local AACT copy from the
    // remote one at a glance.
    [Fact]
    public void DescribeConnection_distinguishes_local_from_remote_source()
    {
        var local = RuntimeParametersController.DescribeConnection(
            "Host=192.0.2.10;Database=clinical;Username=u;Password=p");
        var remote = RuntimeParametersController.DescribeConnection(
            "Host=aact-db.example.org;Database=aact;Username=u;Password=p");

        Assert.NotEqual(local, remote);
        Assert.StartsWith("192.0.2.10:", local);
        Assert.StartsWith("aact-db.example.org:", remote);
    }
}

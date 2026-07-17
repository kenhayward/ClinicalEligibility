using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using EligibilityProcessing.Data;
using EligibilityProcessing.Web;
using EligibilityProcessing.Web.Versioning;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Version + Release Notes surface for the eligibility dashboard. Both are
/// anonymous and read only the embedded version.json (no DB), so they return 200
/// against the placeholder connection strings the other web tests use.
/// </summary>
public class VersionWebTests : IClassFixture<WebTests.Factory>
{
    private readonly WebTests.Factory _factory;

    public VersionWebTests(WebTests.Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Version_endpoint_reports_0_1_21_with_schema_count()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var info = await response.Content.ReadFromJsonAsync<VersionDto>();
        Assert.NotNull(info);
        Assert.Equal("0.1.27", info!.Version);
        Assert.Equal("2026-07-17", info.ReleaseDate);
        Assert.Equal(PostgresGateway.MigrationNames.Count, info.SchemaVersion);
    }

    [Fact]
    public async Task ReleaseNotes_page_renders_the_version_and_enhancements()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/ReleaseNotes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Release notes", body);
        Assert.Contains("0.1.27", body);
        Assert.Contains("Enhancements", body);
    }

    [Fact]
    public async Task Footer_shows_version_and_release_notes_link()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("v0.1.27", body);
        Assert.Contains("/ReleaseNotes", body);
    }

    [Fact]
    public async Task About_modal_includes_aact_and_umls_acknowledgements()
    {
        // The About modal partial is rendered into the authenticated dashboard.
        var client = _factory.CreateClient();
        var body = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("Clinical Trials Transformation Initiative", body);
        Assert.Contains("ctti-clinicaltrials.org/citation-policy", body);
        Assert.Contains("UMLS Metathesaurus", body);
        Assert.Contains("license_agreement.html", body);
        // The eligibility pipeline does not use PubMed - no PubMed disclaimer here.
        Assert.DoesNotContain("PubMed", body);
    }

    [Fact]
    public void AppVersion_GetInfo_formats_version_and_passes_schema_version_through()
    {
        var info = AppVersion.GetInfo(schemaVersion: 7);
        Assert.Equal("0.1.27", info.Version);
        Assert.Equal(7, info.SchemaVersion);
        Assert.Equal("Eligibility Pipeline", info.App);
    }

    [Fact]
    public void Assembly_informational_version_matches_version_json()
    {
        var info = AppVersion.GetInfo(0);
        var expected = $"{info.Version} ({info.ReleaseDate})";

        var actual = typeof(WebMarker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.Equal(expected, actual);
    }

    private sealed record VersionDto(
        string App, string Version, int Major, int Minor, int Build, string ReleaseDate, int SchemaVersion);
}

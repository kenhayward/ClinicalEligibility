using System.Text.Json;
using System.Text.Json.Serialization;
using EligibilityProcessing.Data;

namespace EligibilityProcessing.Web.Versioning;

/// <summary>The current version payload surfaced by GET /Version.</summary>
public sealed record VersionInfo(
    string App,
    string Version,
    int Major,
    int Minor,
    int Build,
    string ReleaseDate,
    int SchemaVersion);

/// <summary>One entry in the changelog surfaced on the Release Notes page.</summary>
public sealed record ReleaseNote(
    string Version,
    string ReleaseDate,
    IReadOnlyList<string> Enhancements,
    IReadOnlyList<string> Fixes);

/// <summary>
/// Reads the embedded <c>version.json</c> (the single source of truth shared with
/// the build stamp and the docker deploy scripts) and reports the current version
/// + the changelog. The schema version is the count of applied eligibility
/// migrations, so a deployment can confirm its schema matches the declared minor.
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<VersionFile> File = new(Load);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The current version + schema version.</summary>
    public static VersionInfo GetInfo() => GetInfo(PostgresGateway.MigrationNames.Count);

    /// <summary>Overload that takes the schema version (used by tests).</summary>
    public static VersionInfo GetInfo(int schemaVersion)
    {
        var c = File.Value.Current;
        return new VersionInfo(
            File.Value.App,
            $"{c.Major}.{c.Minor}.{c.Build}",
            c.Major, c.Minor, c.Build,
            c.ReleaseDate,
            schemaVersion);
    }

    /// <summary>The full changelog, newest first (as authored in version.json).</summary>
    public static IReadOnlyList<ReleaseNote> GetReleaseNotes() =>
        File.Value.Releases
            .Select(r => new ReleaseNote(
                r.Version,
                r.ReleaseDate,
                r.Enhancements ?? new List<string>(),
                r.Fixes ?? new List<string>()))
            .ToList();

    private static VersionFile Load()
    {
        var assembly = typeof(AppVersion).Assembly;
        using var stream = assembly.GetManifestResourceStream("version.json")
            ?? throw new InvalidOperationException(
                "Embedded resource 'version.json' was not found. Confirm it is registered as an " +
                "<EmbeddedResource> with LogicalName=\"version.json\" in EligibilityProcessing.Web.csproj.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<VersionFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("version.json could not be parsed.");
    }

    // The on-disk shape of version.json.
    private sealed class VersionFile
    {
        public string App { get; set; } = "";
        public CurrentVersion Current { get; set; } = new();
        public List<ReleaseEntry> Releases { get; set; } = new();
    }

    private sealed class CurrentVersion
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Build { get; set; }
        [JsonPropertyName("releaseDate")] public string ReleaseDate { get; set; } = "";
    }

    private sealed class ReleaseEntry
    {
        public string Version { get; set; } = "";
        [JsonPropertyName("releaseDate")] public string ReleaseDate { get; set; } = "";
        public List<string>? Enhancements { get; set; }
        public List<string>? Fixes { get; set; }
    }
}

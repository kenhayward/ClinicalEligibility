using EligibilityProcessing.Web.Embeddings;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Pure-logic tests for the embeddings export/import command lines: the pg_dump
/// (export) and pg_restore (import) invocations are built correctly, the import is
/// restricted to the one table (so a hostile archive can't touch anything else), the
/// password never lands on the command line, and the export filename carries the model.
/// </summary>
public class EmbeddingsDumpTests
{
    private const string Conn =
        "Host=db;Port=5433;Database=clinical;Username=postgres;Password=s3cr3t;Maximum Pool Size=20";

    [Fact]
    public void EmbeddingsTable_is_the_corpus_index()
    {
        Assert.Equal("eligibility_study_embedding", EmbeddingsDump.EmbeddingsTable);
    }

    [Fact]
    public void BuildExportInvocation_dumps_only_the_embeddings_table_data_only()
    {
        var inv = EmbeddingsDump.BuildExportInvocation(Conn, "/out/embeddings.dump");
        var args = inv.Arguments;

        Assert.Contains("--format=custom", args);
        Assert.Contains("--data-only", args);
        Assert.Contains("--no-owner", args);
        Assert.Contains("--no-privileges", args);

        AssertFollowedBy(args, "-h", "db");
        AssertFollowedBy(args, "-p", "5433");
        AssertFollowedBy(args, "-U", "postgres");
        AssertFollowedBy(args, "-d", "clinical");
        AssertFollowedBy(args, "-f", "/out/embeddings.dump");

        // Exactly the one embeddings table, schema-qualified.
        Assert.Single(args.Where(a => a == "-t"));
        Assert.Contains("public.eligibility_study_embedding", args);

        Assert.DoesNotContain("s3cr3t", args);
        Assert.Equal("s3cr3t", inv.Password);
    }

    [Fact]
    public void BuildRestoreInvocation_is_data_only_and_restricted_to_the_embeddings_table()
    {
        var inv = EmbeddingsDump.BuildRestoreInvocation(Conn, "/in/embeddings.dump");
        var args = inv.Arguments;

        Assert.Contains("--data-only", args);
        Assert.Contains("--disable-triggers", args);
        Assert.Contains("--no-owner", args);
        Assert.Contains("--no-privileges", args);
        Assert.Contains("--exit-on-error", args);

        // -t restriction is the safety boundary: a crafted archive can only write here.
        AssertFollowedBy(args, "-t", "eligibility_study_embedding");
        AssertFollowedBy(args, "-d", "clinical");

        // The archive path is the final positional argument (pg_restore reads it there).
        Assert.Equal("/in/embeddings.dump", args[^1]);

        Assert.DoesNotContain("s3cr3t", args);
        Assert.Equal("s3cr3t", inv.Password);
    }

    [Theory]
    [InlineData("gpt-embed-v2", 100, "embeddings-gpt-embed-v2-100.dump")]
    [InlineData("text/embed:3", 42, "embeddings-text-embed-3-42.dump")]  // unsafe chars slugged
    public void SuggestedFileName_names_a_single_model_export_with_the_model(string model, long rows, string expected)
    {
        var name = EmbeddingsDump.SuggestedFileName(new[] { model }, rows, new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void SuggestedFileName_falls_back_to_a_timestamp_for_zero_or_mixed_models()
    {
        var utc = new DateTime(2026, 7, 15, 13, 5, 9, DateTimeKind.Utc);

        var none = EmbeddingsDump.SuggestedFileName(Array.Empty<string>(), 0, utc);
        Assert.Equal("embeddings-20260715-130509.dump", none);

        var mixed = EmbeddingsDump.SuggestedFileName(new[] { "a", "b" }, 5, utc);
        Assert.Equal("embeddings-20260715-130509.dump", mixed);
    }

    [Theory]
    [InlineData("a/b c", "a-b-c")]
    [InlineData("Nomic-Embed.v1", "Nomic-Embed.v1")]
    [InlineData("", "model")]
    [InlineData("///", "model")]
    public void SanitizeModel_keeps_safe_chars_and_never_empty(string model, string expected)
    {
        Assert.Equal(expected, EmbeddingsDump.SanitizeModel(model));
    }

    private static void AssertFollowedBy(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && args[i + 1] == value) return;
        Assert.Fail($"Expected argument '{flag} {value}' in: {string.Join(' ', args)}");
    }
}

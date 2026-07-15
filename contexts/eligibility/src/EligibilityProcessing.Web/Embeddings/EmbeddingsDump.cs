using System.Globalization;
using Npgsql;

namespace EligibilityProcessing.Web.Embeddings;

/// <summary>
/// Pure helpers for the owner-only embeddings export/import feature: the single
/// table involved, how to build the pg_dump (export) and pg_restore (import)
/// command lines, and how to name the produced archive. Kept free of I/O so it is
/// unit-testable; the process launch lives in <see cref="EmbeddingsJobRunner"/>.
///
/// Export mirrors the seed dump format (custom, data-only) but for the single
/// <c>eligibility_study_embedding</c> table. Import restores it with
/// <c>pg_restore --data-only -t eligibility_study_embedding</c> - the table
/// restriction means a crafted archive cannot write to any other table.
/// </summary>
public static class EmbeddingsDump
{
    /// <summary>The one table the embeddings archive carries - the corpus similarity index.</summary>
    public const string EmbeddingsTable = "eligibility_study_embedding";

    /// <summary>A pg_dump/pg_restore command line: the argument list (passed via
    /// ProcessStartInfo.ArgumentList, so no shell quoting) plus the password handed
    /// out-of-band via PGPASSWORD (never on the command line / process list).</summary>
    public sealed record PgInvocation(IReadOnlyList<string> Arguments, string? Password);

    /// <summary>
    /// pg_dump of the embeddings table -> a custom-format, data-only archive at
    /// <paramref name="outputFilePath"/>. Same format as the seed dump, so it is a
    /// standard Postgres archive that pg_restore (or the quickstart tooling) reads.
    /// </summary>
    public static PgInvocation BuildExportInvocation(string outputConnectionString, string outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputConnectionString))
            throw new ArgumentException("Output connection string is required.", nameof(outputConnectionString));
        if (string.IsNullOrWhiteSpace(outputFilePath))
            throw new ArgumentException("Output file path is required.", nameof(outputFilePath));

        var b = new NpgsqlConnectionStringBuilder(outputConnectionString);
        var args = new List<string>
        {
            "--format=custom",
            "--data-only",
            "--no-owner",
            "--no-privileges",
            "--verbose"
        };
        AddConnection(args, b);
        args.Add("-t");
        args.Add($"public.{EmbeddingsTable}");
        args.Add("-f");
        args.Add(outputFilePath);
        return new PgInvocation(args, b.Password);
    }

    /// <summary>
    /// pg_restore of an embeddings archive into the output DB, data-only and
    /// restricted to <see cref="EmbeddingsTable"/> (so a hostile archive can only
    /// touch that table). The caller TRUNCATEs the table first, so this is a clean
    /// replace. The archive path is the final positional argument.
    /// </summary>
    public static PgInvocation BuildRestoreInvocation(string outputConnectionString, string inputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputConnectionString))
            throw new ArgumentException("Output connection string is required.", nameof(outputConnectionString));
        if (string.IsNullOrWhiteSpace(inputFilePath))
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));

        var b = new NpgsqlConnectionStringBuilder(outputConnectionString);
        var args = new List<string>
        {
            "--data-only",
            "--disable-triggers",
            "--no-owner",
            "--no-privileges",
            "--exit-on-error",
            "--verbose",
            "-t",
            EmbeddingsTable
        };
        AddConnection(args, b);   // -d selects the DB pg_restore connects to and restores into
        args.Add(inputFilePath);  // positional: the archive to restore
        return new PgInvocation(args, b.Password);
    }

    private static void AddConnection(List<string> args, NpgsqlConnectionStringBuilder b)
    {
        if (!string.IsNullOrWhiteSpace(b.Host)) { args.Add("-h"); args.Add(b.Host); }
        args.Add("-p"); args.Add(b.Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(b.Username)) { args.Add("-U"); args.Add(b.Username); }
        if (!string.IsNullOrWhiteSpace(b.Database)) { args.Add("-d"); args.Add(b.Database); }
    }

    /// <summary>
    /// Names the export so the embedding model is visible in the filename (the model
    /// is what makes an imported set comparable for Find Similar). A single-model
    /// index becomes <c>embeddings-{model}-{rows}.dump</c>; zero or mixed models fall
    /// back to a timestamp so the name is still unique.
    /// </summary>
    public static string SuggestedFileName(IReadOnlyList<string> models, long rowCount, DateTime utcNow)
    {
        var named = (models ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();
        if (named.Count == 1)
        {
            return $"embeddings-{SanitizeModel(named[0])}-{rowCount}.dump";
        }
        return $"embeddings-{utcNow:yyyyMMdd-HHmmss}.dump";
    }

    /// <summary>Filename-safe slug of a model name (letters/digits/./- kept, else '-').</summary>
    public static string SanitizeModel(string model)
    {
        var slug = new string((model ?? "")
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrEmpty(slug) ? "model" : slug;
    }
}

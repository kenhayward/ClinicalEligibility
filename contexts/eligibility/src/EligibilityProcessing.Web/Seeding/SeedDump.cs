using System.Globalization;
using Npgsql;

namespace EligibilityProcessing.Web.Seeding;

/// <summary>
/// Pure helpers for the owner-only "Create database seed" feature: which tables the
/// seed contains, how to build the pg_dump command line, and how to read pg_dump's
/// --verbose progress. Kept free of I/O so it is unit-testable; the actual process
/// launch lives in <see cref="SeedJobRunner"/>.
///
/// The output MUST be byte-for-byte loader-compatible with deploy/quickstart:
/// make-seed.sh runs `pg_dump --format=custom --data-only --no-owner --no-privileges
/// -t public.&lt;table&gt; ...` and load-seed.sh restores it with
/// `pg_restore --data-only`. This builder mirrors that invocation exactly.
/// </summary>
public static class SeedDump
{
    /// <summary>
    /// The six seed tables. This SET (not the order - pg_dump emits data in
    /// dependency order) is what makes the dump loader-compatible. Must stay in
    /// sync with deploy/quickstart/make-seed.sh and load-seed.sh.
    /// </summary>
    public static readonly IReadOnlyList<string> SeedTables = new[]
    {
        "eligibility",
        "eligibility_study",
        "eligibility_study_detail",
        "eligibility_run",
        "eligibility_failed",
        "eligibility_umls_retry"
    };

    /// <summary>A pg_dump command line: the argument list (passed via
    /// ProcessStartInfo.ArgumentList, so no shell quoting) plus the password to
    /// hand pg_dump out-of-band as the PGPASSWORD environment variable (never on
    /// the command line, where it would show up in the process list).</summary>
    public sealed record PgDumpInvocation(IReadOnlyList<string> Arguments, string? Password);

    /// <summary>
    /// Build the pg_dump invocation for the seed, from the output-database Npgsql
    /// connection string, writing the custom-format archive to <paramref name="outputFilePath"/>.
    /// Npgsql-only keywords in the connection string (e.g. "Maximum Pool Size") are
    /// ignored - only host/port/db/user/password are used.
    /// </summary>
    public static PgDumpInvocation BuildInvocation(string outputConnectionString, string outputFilePath)
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
        if (!string.IsNullOrWhiteSpace(b.Host)) { args.Add("-h"); args.Add(b.Host); }
        args.Add("-p"); args.Add(b.Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(b.Username)) { args.Add("-U"); args.Add(b.Username); }
        if (!string.IsNullOrWhiteSpace(b.Database)) { args.Add("-d"); args.Add(b.Database); }
        foreach (var table in SeedTables)
        {
            args.Add("-t");
            args.Add($"public.{table}");
        }
        args.Add("-f");
        args.Add(outputFilePath);

        return new PgDumpInvocation(args, b.Password);
    }

    /// <summary>
    /// pg_dump --verbose logs one line per table as it copies its data, e.g.
    /// <c>pg_dump: dumping contents of table "public.eligibility"</c>. Returns the
    /// quoted qualified table name when the line is such a marker, otherwise null.
    /// Tolerant of any prefix (log level / timestamp) before the marker.
    /// </summary>
    public static string? TryParseDumpingTable(string? stderrLine)
    {
        if (string.IsNullOrEmpty(stderrLine)) return null;
        const string marker = "dumping contents of table \"";
        var i = stderrLine.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = stderrLine.IndexOf('"', start);
        if (end < 0) return null;
        return stderrLine.Substring(start, end - start);
    }
}

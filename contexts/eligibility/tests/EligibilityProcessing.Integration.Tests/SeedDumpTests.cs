using EligibilityProcessing.Web.Seeding;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Pure-logic tests for the owner-only seed dump: the pg_dump command line is built
/// to match deploy/quickstart/make-seed.sh (so output is loader-compatible), the
/// password is kept off the command line, verbose progress parsing works, and the
/// in-memory job state transitions correctly.
/// </summary>
public class SeedDumpTests
{
    private const string Conn =
        "Host=db;Port=5433;Database=clinical;Username=postgres;Password=s3cr3t;Maximum Pool Size=20";

    [Fact]
    public void SeedTables_are_exactly_the_six_loader_tables()
    {
        Assert.Equal(new[]
        {
            "eligibility",
            "eligibility_study",
            "eligibility_study_detail",
            "eligibility_run",
            "eligibility_failed",
            "eligibility_umls_retry"
        }, SeedDump.SeedTables);
    }

    [Fact]
    public void BuildInvocation_mirrors_make_seed_sh_and_keeps_password_off_the_command_line()
    {
        var inv = SeedDump.BuildInvocation(Conn, "/out/seed.dump");
        var args = inv.Arguments;

        // Format flags identical to make-seed.sh (custom, data-only, no owner/privileges).
        Assert.Contains("--format=custom", args);
        Assert.Contains("--data-only", args);
        Assert.Contains("--no-owner", args);
        Assert.Contains("--no-privileges", args);
        Assert.Contains("--verbose", args);

        // Connection params parsed from the Npgsql string; the pool-size keyword is ignored.
        AssertFollowedBy(args, "-h", "db");
        AssertFollowedBy(args, "-p", "5433");
        AssertFollowedBy(args, "-U", "postgres");
        AssertFollowedBy(args, "-d", "clinical");
        AssertFollowedBy(args, "-f", "/out/seed.dump");

        // Exactly the six tables, each schema-qualified.
        Assert.Equal(6, args.Count(a => a == "-t"));
        foreach (var t in SeedDump.SeedTables)
            Assert.Contains($"public.{t}", args);

        // The password is NEVER an argument (it would show in the process list);
        // it is returned to be passed via PGPASSWORD.
        Assert.DoesNotContain("s3cr3t", args);
        Assert.Equal("s3cr3t", inv.Password);
    }

    [Theory]
    [InlineData("pg_dump: dumping contents of table \"public.eligibility\"", "public.eligibility")]
    [InlineData("2026-07-15 pg_dump: dumping contents of table \"public.eligibility_study\"", "public.eligibility_study")]
    [InlineData("pg_dump: reading user-defined tables", null)]
    [InlineData("pg_dump: saving encoding = UTF8", null)]
    [InlineData("", null)]
    public void TryParseDumpingTable_extracts_the_table_only_from_data_copy_lines(string line, string? expected)
    {
        Assert.Equal(expected, SeedDump.TryParseDumpingTable(line));
    }

    [Fact]
    public void SeedJobState_transitions_from_running_to_completed_with_download()
    {
        var state = new SeedJobState();
        Assert.Null(state.Current);
        Assert.Null(state.Download);

        var id = Guid.NewGuid();
        state.Begin(id);
        Assert.Equal("running", state.Current!.Status);
        Assert.Equal(6, state.Current!.TotalTables);

        state.AdvanceTable("eligibility");
        state.AdvanceTable("eligibility_study");
        Assert.Equal(2, state.Current!.TablesDumped);
        Assert.Equal("eligibility_study", state.Current!.CurrentTable);

        // Never exceeds the known table count even if pg_dump logs extra lines.
        for (var i = 0; i < 10; i++) state.AdvanceTable("noise");
        Assert.Equal(6, state.Current!.TablesDumped);

        state.Succeed("seed-1.dump", 4242, "/tmp/seed-1.dump");
        Assert.Equal("completed", state.Current!.Status);
        Assert.True(state.Current!.Downloadable);
        Assert.Equal(4242, state.Current!.FileSizeBytes);
        Assert.Equal(("/tmp/seed-1.dump", "seed-1.dump"), state.Download);
    }

    [Fact]
    public void SeedJobState_failure_clears_the_download_and_records_the_error()
    {
        var state = new SeedJobState();
        state.Begin(Guid.NewGuid());
        state.Fail("failed", "pg_dump exited with code 1");

        Assert.Equal("failed", state.Current!.Status);
        Assert.False(state.Current!.Downloadable);
        Assert.Null(state.Download);
        Assert.Equal("pg_dump exited with code 1", state.Current!.Error);
    }

    private static void AssertFollowedBy(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && args[i + 1] == value) return;
        Assert.Fail($"Expected argument '{flag} {value}' in: {string.Join(' ', args)}");
    }
}

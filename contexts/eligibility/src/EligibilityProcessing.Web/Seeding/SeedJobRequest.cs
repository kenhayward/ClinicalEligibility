namespace EligibilityProcessing.Web.Seeding;

/// <summary>
/// A queued "create database seed" request. Carries only the job id; the work
/// (which tables, where they come from) is fixed by <see cref="SeedDump"/> and the
/// output-database connection string, so there is nothing to parameterize.
/// </summary>
public sealed record SeedJobRequest(Guid JobId);

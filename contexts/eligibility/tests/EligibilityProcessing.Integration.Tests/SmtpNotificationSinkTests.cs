using System.Collections.Concurrent;
using EligibilityProcessing.Core;
using EligibilityProcessing.Hosting;
using EligibilityProcessing.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Tests for the SMTP notification sink + its registration in the Hosting
/// composition root. The MailKit transport itself is unit-testable through
/// the ISmtpEmailSender seam — no real SMTP server needed for unit verification.
///
/// Verifies:
///   1. SmtpNotificationSink builds the spec §2.10.1 fields into the body.
///   2. From / To / Subject are populated from SmtpNotificationOptions.
///   3. Error-path message includes failed NCT IDs + error summary.
///   4. CompositionRoot registers the SMTP sink when Notifications:Smtp:Host is
///      set, and falls back to NullNotificationSink when it is empty.
///   5. Send failures inside the sink are swallowed (logged not thrown).
/// </summary>
public class SmtpNotificationSinkTests
{
    // ============ message body content ============

    [Fact]
    public void Completion_body_contains_all_spec_metrics()
    {
        var sender = new FakeSmtpEmailSender();
        var sink = MakeSink(sender);
        var result = MakeResult(status: "success", studiesProcessed: 50, rowsPersisted: 374);

        var message = sink.BuildCompletionMessage(result);
        var body = ((TextPart)message.Body).Text;

        Assert.Contains("Status:", body);
        Assert.Contains("success", body);
        Assert.Contains("Studies processed:", body);
        Assert.Contains("50 / 50", body);
        Assert.Contains("Total criteria rows:", body);
        Assert.Contains("374", body);
        Assert.Contains("Avg criteria / study:", body);
        Assert.Contains("7.5", body); // 374 / 50 = 7.48 → "7.5"
        Assert.Contains("Resolution rate:", body);
        Assert.Contains("Workflow runtime:", body);
        Assert.Contains("Avg runtime / study:", body);
        Assert.Contains("Run ID:", body);
        Assert.Contains("Trigger:", body);
    }

    [Fact]
    public void Completion_subject_summarises_run()
    {
        var message = MakeSink(new FakeSmtpEmailSender())
            .BuildCompletionMessage(MakeResult(status: "success", studiesProcessed: 50, rowsPersisted: 374));
        Assert.Contains("[Eligibility]", message.Subject);
        Assert.Contains("success", message.Subject);
        Assert.Contains("50", message.Subject);
        Assert.Contains("374", message.Subject);
    }

    [Fact]
    public void Completion_message_uses_From_and_To_from_options()
    {
        var sink = MakeSink(new FakeSmtpEmailSender(), opts: new SmtpNotificationOptions
        {
            Host = "smtp.example.com",
            FromAddress = "pipeline@example.com",
            FromName = "Pipeline Bot",
            ToAddresses = "ops@example.com, alerts@example.com"
        });
        var message = sink.BuildCompletionMessage(MakeResult());

        Assert.Single(message.From);
        Assert.Equal("pipeline@example.com", ((MailboxAddress)message.From[0]).Address);
        Assert.Equal("Pipeline Bot", ((MailboxAddress)message.From[0]).Name);
        Assert.Equal(2, message.To.Count);
        Assert.Equal("ops@example.com", ((MailboxAddress)message.To[0]).Address);
        Assert.Equal("alerts@example.com", ((MailboxAddress)message.To[1]).Address);
    }

    [Fact]
    public void Completion_body_includes_retrigger_url_when_configured()
    {
        var sink = MakeSink(new FakeSmtpEmailSender(), opts: new SmtpNotificationOptions
        {
            Host = "smtp.example.com",
            FromAddress = "x@y.z",
            ToAddresses = "a@b.c",
            RetriggerUrl = "https://pipeline.example.com/trigger"
        });
        var body = ((TextPart)sink.BuildCompletionMessage(MakeResult()).Body).Text;
        Assert.Contains("Re-trigger:", body);
        Assert.Contains("https://pipeline.example.com/trigger", body);
    }

    [Fact]
    public void Completion_body_lists_failed_nctIds_when_present()
    {
        var sink = MakeSink(new FakeSmtpEmailSender());
        var result = MakeResult(failedNctIds: new[] { "NCT00000001", "NCT00000002" });
        var body = ((TextPart)sink.BuildCompletionMessage(result).Body).Text;
        Assert.Contains("Failed trials (2)", body);
        Assert.Contains("NCT00000001", body);
        Assert.Contains("NCT00000002", body);
    }

    // ============ error path ============

    [Fact]
    public void Error_message_includes_failed_count_in_subject()
    {
        var sink = MakeSink(new FakeSmtpEmailSender());
        var result = MakeResult(
            status: "failed",
            failedNctIds: new[] { "NCT00000001", "NCT00000002", "NCT00000003" });
        var message = sink.BuildErrorMessage(result);
        Assert.Contains("3 failed", message.Subject);
    }

    [Fact]
    public void Error_body_includes_error_summary_when_set()
    {
        var sink = MakeSink(new FakeSmtpEmailSender());
        var result = MakeResult(status: "failed", errorSummary: "Postgres unreachable");
        var body = ((TextPart)sink.BuildErrorMessage(result).Body).Text;
        Assert.Contains("Error summary:", body);
        Assert.Contains("Postgres unreachable", body);
    }

    // ============ send dispatch + failure swallowing ============

    [Fact]
    public async Task SendCompletionAsync_hands_message_to_sender()
    {
        var sender = new FakeSmtpEmailSender();
        var sink = MakeSink(sender);
        await sink.SendCompletionAsync(MakeResult(), CancellationToken.None);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task SendErrorAsync_hands_message_to_sender()
    {
        var sender = new FakeSmtpEmailSender();
        var sink = MakeSink(sender);
        await sink.SendErrorAsync(MakeResult(status: "failed"), CancellationToken.None);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Sender_exception_is_swallowed_not_propagated()
    {
        var sender = new FakeSmtpEmailSender { ThrowOnSend = new InvalidOperationException("smtp dead") };
        var sink = MakeSink(sender);
        // Must not throw — graceful tolerance per spec section 6.4.
        await sink.SendCompletionAsync(MakeResult(), CancellationToken.None);
    }

    [Fact]
    public async Task User_cancellation_is_propagated()
    {
        var sender = new FakeSmtpEmailSender { ThrowOnSend = new OperationCanceledException() };
        var sink = MakeSink(sender);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.SendCompletionAsync(MakeResult(), cts.Token));
    }

    // ============ Hosting registration ============

    [Fact]
    public void Hosting_registers_NullNotificationSink_when_Smtp_Host_unset()
    {
        var provider = BuildProviderWithConfig(new Dictionary<string, string?>
        {
            ["Postgres:ConnectionStringSource"] = "Host=x;Username=u;Password=p;Database=d",
            ["Postgres:ConnectionStringOutput"] = "Host=x;Username=u;Password=p;Database=d"
        });
        var sink = provider.GetRequiredService<INotificationSink>();
        Assert.IsType<NullNotificationSink>(sink);
    }

    [Fact]
    public void Hosting_registers_SmtpNotificationSink_when_Smtp_Host_configured()
    {
        var provider = BuildProviderWithConfig(new Dictionary<string, string?>
        {
            ["Postgres:ConnectionStringSource"] = "Host=x;Username=u;Password=p;Database=d",
            ["Postgres:ConnectionStringOutput"] = "Host=x;Username=u;Password=p;Database=d",
            ["Notifications:Smtp:Host"] = "smtp.example.com",
            ["Notifications:Smtp:FromAddress"] = "pipeline@example.com",
            ["Notifications:Smtp:ToAddresses"] = "ops@example.com"
        });
        var sink = provider.GetRequiredService<INotificationSink>();
        Assert.IsType<SmtpNotificationSink>(sink);
    }

    // ============ helpers ============

    private static SmtpNotificationSink MakeSink(
        FakeSmtpEmailSender sender,
        SmtpNotificationOptions? opts = null)
    {
        var resolved = opts ?? new SmtpNotificationOptions
        {
            Host = "smtp.example.com",
            FromAddress = "pipeline@example.com",
            FromName = "Pipeline Bot",
            ToAddresses = "ops@example.com"
        };
        return new SmtpNotificationSink(sender, Options.Create(resolved));
    }

    private static BatchResult MakeResult(
        string status = "success",
        int studiesProcessed = 50,
        int rowsPersisted = 374,
        string errorSummary = "",
        IReadOnlyList<string>? failedNctIds = null)
    {
        var started = new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero);
        var ended = started.AddMinutes(11);
        var metrics = new RunMetrics(
            runId: Guid.NewGuid(),
            startedAt: started,
            endedAt: ended,
            triggerSource: "webhook",
            studyCount: studiesProcessed,
            studiesProcessed: studiesProcessed,
            rowsPersisted: rowsPersisted,
            resolutionRate: 0.882,
            status: status,
            errorSummary: errorSummary);
        return new BatchResult(metrics, failedNctIds ?? Array.Empty<string>());
    }

    private static IServiceProvider BuildProviderWithConfig(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEligibilityPipeline(configuration);
        return services.BuildServiceProvider();
    }
}

internal sealed class FakeSmtpEmailSender : ISmtpEmailSender
{
    public ConcurrentBag<MimeMessage> Sent { get; } = new();
    public Exception? ThrowOnSend { get; set; }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        if (ThrowOnSend is not null) throw ThrowOnSend;
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

using System.Net;
using System.Text;
using EligibilityProcessing.Core;
using EligibilityProcessing.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// Tests the LLM typed-HttpClient resilience pipeline wired in CompositionRoot.
///
/// The pipeline enforces <c>Llm:TimeoutSeconds</c> as a <em>per-attempt</em>
/// timeout strategy (retry wraps timeout), and leaves HttpClient.Timeout
/// infinite. A slow attempt therefore times out on its own budget and the
/// retry strategy can recover it within the same run — rather than the old
/// behaviour where one outer HttpClient.Timeout bounded all retries together
/// and a single slow attempt cancelled the whole pipeline.
/// </summary>
public class LlmResilienceTests
{
    [Fact]
    public async Task Slow_first_attempt_times_out_per_attempt_and_retry_recovers_within_run()
    {
        var handler = new SlowThenFastHandler(slowAttempts: 1, slowDelay: TimeSpan.FromSeconds(30));
        var client = BuildLlmClient(handler, timeoutSeconds: 1, retryCount: 2);

        var result = await client.CompleteAsync(
            new LlmRequest("NCT00000123", "Inclusion: adult"), CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(handler.CallCount > 1,
            $"expected the slow attempt to be retried; handler was called {handler.CallCount} time(s)");
    }

    [Fact]
    public async Task Every_attempt_slow_exhausts_retries_then_fails_gracefully()
    {
        var handler = new SlowThenFastHandler(slowAttempts: int.MaxValue, slowDelay: TimeSpan.FromSeconds(30));
        var client = BuildLlmClient(handler, timeoutSeconds: 1, retryCount: 2);

        var result = await client.CompleteAsync(new LlmRequest("NCT0", "x"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(3, handler.CallCount); // 1 initial attempt + RetryCount (2)
    }

    private static ILlmClient BuildLlmClient(HttpMessageHandler primaryHandler, int timeoutSeconds, int retryCount)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Postgres:ConnectionStringSource"] = "Host=x;Username=u;Password=p;Database=d",
            ["Postgres:ConnectionStringOutput"] = "Host=x;Username=u;Password=p;Database=d",
            ["Llm:BaseUrl"] = "http://llm.test/v1",
            ["Llm:TimeoutSeconds"] = timeoutSeconds.ToString(),
            ["Llm:RetryCount"] = retryCount.ToString(),
            ["Llm:RetryDelaySeconds"] = "0",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEligibilityPipeline(configuration);
        // Swap the "llamacpp" transport for the stub; the resilience handler stays.
        services.AddHttpClient("llamacpp").ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        return services.BuildServiceProvider().GetRequiredService<ILlmClient>();
    }

    [Fact]
    public async Task Normalizer_with_zero_retry_count_builds_and_does_not_retry()
    {
        // Polly's HttpRetryStrategyOptions validates MaxRetryAttempts >= 1, so a
        // naive AddRetry with RetryCount=0 throws on the first request. The
        // pipeline must skip AddRetry entirely so a "no retries" config (often
        // used with a fast small model) is honored.
        var handler = new CountingFailHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Postgres:ConnectionStringSource"] = "Host=x;Username=u;Password=p;Database=d",
            ["Postgres:ConnectionStringOutput"] = "Host=x;Username=u;Password=p;Database=d",
            ["Llm:BaseUrl"] = "http://llm.test/v1",
            ["LlmNormalize:BaseUrl"] = "http://norm.test/v1",
            ["LlmNormalize:Model"] = "tiny-3b",
            ["LlmNormalize:RetryCount"] = "0",
            ["LlmNormalize:RetryDelaySeconds"] = "0",
            ["LlmNormalize:TimeoutSeconds"] = "5",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEligibilityPipeline(configuration);
        services.AddHttpClient("normalizer").ConfigurePrimaryHttpMessageHandler(() => handler);

        var normalizer = services.BuildServiceProvider().GetRequiredService<ICriteriaNormalizer>();
        var result = await normalizer.NormalizeAsync(new[] { "Adults over 18" }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, handler.CallCount);
    }
}

internal sealed class CountingFailHandler : HttpMessageHandler
{
    private int _calls;
    public int CallCount => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        });
    }
}

/// <summary>
/// Stub transport: the first <c>slowAttempts</c> calls hang past any sane
/// per-attempt timeout (the linked cancellation token from the timeout
/// strategy aborts the delay); later calls return a valid success response.
/// </summary>
internal sealed class SlowThenFastHandler : HttpMessageHandler
{
    private const string SuccessJson =
        "{\"choices\":[{\"message\":{\"content\":\"[]\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}";

    private readonly int _slowAttempts;
    private readonly TimeSpan _slowDelay;
    private int _calls;

    public SlowThenFastHandler(int slowAttempts, TimeSpan slowDelay)
    {
        _slowAttempts = slowAttempts;
        _slowDelay = slowDelay;
    }

    public int CallCount => Volatile.Read(ref _calls);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _calls);
        if (attempt <= _slowAttempts)
        {
            // Cancelled by the per-attempt timeout -> TimeoutRejectedException -> retried.
            await Task.Delay(_slowDelay, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, Encoding.UTF8, "application/json"),
        };
    }
}

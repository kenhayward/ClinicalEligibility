using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// The hooks drop the cached corpus aggregate when a batch reaches a terminal
/// state, so the dashboard's refresh-on-completion reads new figures for
/// everyone rather than only for whoever was watching. These tests pin that,
/// and pin the ORDER - invalidate before announcing - because the client
/// re-reads on the announcement, and a read that beats the invalidation would
/// repopulate the cache with the pre-run numbers and stick them there for the
/// rest of the TTL.
/// </summary>
public class SignalRPipelineHooksTests
{
    [Fact]
    public async Task Batch_completion_invalidates_the_dashboard_cache()
    {
        var (order, hooks) = Build();

        await hooks.OnBatchCompletedAsync(Result(), CancellationToken.None);

        Assert.Equal(new[] { "invalidate", "send:BatchCompleted" }, order);
    }

    [Fact]
    public async Task Batch_cancellation_invalidates_too()
    {
        // Cancelled is not "nothing happened": trials that finished before the
        // cancel already persisted their rows, so the corpus moved.
        var (order, hooks) = Build();

        await hooks.OnBatchCancelledAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new[] { "invalidate", "send:BatchCancelled" }, order);
    }

    [Fact]
    public async Task Per_trial_events_do_not_invalidate()
    {
        // TrialCompleted fires from the parallel per-trial body, potentially
        // thousands of times a run. Invalidating on each would defeat the cache
        // entirely; the batch-level event is the one that matters.
        var (order, hooks) = Build();

        await hooks.OnTrialCompletedAsync(Guid.NewGuid(), "NCT01", 3, true, CancellationToken.None);

        Assert.Equal(new[] { "send:TrialCompleted" }, order);
    }

    private static (List<string> order, SignalRPipelineHooks hooks) Build()
    {
        var order = new List<string>();
        var hub = new RecordingHubContext(order);
        var cache = new SpyCache(order);
        return (order, new SignalRPipelineHooks(hub, cache));
    }

    private static BatchResult Result() =>
        new(
            new RunMetrics(
                runId: Guid.NewGuid(),
                startedAt: DateTimeOffset.UtcNow,
                endedAt: DateTimeOffset.UtcNow,
                triggerSource: "form",
                studyCount: 10,
                studiesProcessed: 10,
                rowsPersisted: 100,
                resolutionRate: 0.9,
                status: "success",
                errorSummary: ""),
            Array.Empty<string>());

    // Records only that invalidation happened, and when relative to the send.
    private sealed class SpyCache : ICorpusReadCache
    {
        private readonly List<string> _order;
        public SpyCache(List<string> order) => _order = order;

        public void InvalidateDashboardMetrics() => _order.Add("invalidate");

        public Task<DashboardMetrics> GetDashboardMetricsAsync(CancellationToken ct)
            => Task.FromResult(DashboardMetrics.Empty);

        public Task<EligibilityFilterOptions> GetEligibilityFilterOptionsAsync(int maxDropdownSize, CancellationToken ct)
            => Task.FromResult(EligibilityFilterOptions.Empty);

        public Task<CorpusConceptProfile> GetCorpusConceptProfileAsync(CancellationToken ct)
            => Task.FromResult(new CorpusConceptProfile(Array.Empty<ConceptCount>(), 0));
    }

    // Minimal IHubContext that records each SendAsync by method name. SendAsync is
    // an extension over SendCoreAsync, so that is the one method to implement.
    private sealed class RecordingHubContext : IHubContext<RunProgressHub>
    {
        private readonly IClientProxy _all;
        public RecordingHubContext(List<string> order) => _all = new RecordingProxy(order);

        public IHubClients Clients => new Hubs(_all);
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class Hubs : IHubClients
        {
            private readonly IClientProxy _all;
            public Hubs(IClientProxy all) => _all = all;
            public IClientProxy All => _all;
            public IClientProxy AllExcept(IReadOnlyList<string> e) => _all;
            public IClientProxy Client(string c) => _all;
            public IClientProxy Clients(IReadOnlyList<string> c) => _all;
            public IClientProxy Group(string g) => _all;
            public IClientProxy Groups(IReadOnlyList<string> g) => _all;
            public IClientProxy GroupExcept(string g, IReadOnlyList<string> e) => _all;
            public IClientProxy User(string u) => _all;
            public IClientProxy Users(IReadOnlyList<string> u) => _all;
        }

        private sealed class RecordingProxy : IClientProxy
        {
            private readonly List<string> _order;
            public RecordingProxy(List<string> order) => _order = order;

            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                _order.Add("send:" + method);
                return Task.CompletedTask;
            }
        }
    }
}

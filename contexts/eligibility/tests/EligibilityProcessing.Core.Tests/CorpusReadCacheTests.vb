Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Caching.Memory
Imports Xunit

' CorpusReadCache is only observable through how often it reaches the gateway,
' so these tests assert on FakeGateway's call counters rather than on returned
' values alone.
Public Class CorpusReadCacheTests

    Private Shared Function NewCache(gateway As FakeGateway, ttl As TimeSpan) As CorpusReadCache
        Return New CorpusReadCache(gateway, New MemoryCache(New MemoryCacheOptions()), ttl)
    End Function

    Private Shared Function NewCache(gateway As FakeGateway) As CorpusReadCache
        Return NewCache(gateway, TimeSpan.FromMinutes(5))
    End Function

    <Fact>
    Public Async Function DashboardMetrics_SecondCallWithinTtl_DoesNotHitGateway() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)

        Dim first = Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Dim second = Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(1, gateway.GetDashboardMetricsCalls)
        Assert.Same(first, second)
    End Function

    ' ===== invalidation =====
    ' Backs the dashboard's Reload button and its refresh-on-run-completed. Both
    ' would otherwise be no-ops inside the TTL: the UI shows a loading state,
    ' re-reads the identical cached numbers, and appears broken.

    <Fact>
    Public Async Function InvalidateDashboardMetrics_ForcesTheNextReadToHitTheGateway() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)

        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Assert.Equal(1, gateway.GetDashboardMetricsCalls)

        cache.InvalidateDashboardMetrics()
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
    End Function

    ' Invalidate-then-read repopulates for everyone, so the TTL keeps throttling
    ' a user leaning on Reload rather than every press reaching Postgres.
    <Fact>
    Public Async Function InvalidateDashboardMetrics_RepopulatesSoLaterReadsAreCachedAgain() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)

        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        cache.InvalidateDashboardMetrics()
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
    End Function

    ' The filter-options entry is keyed separately and is not the dashboard's to
    ' drop - Results would pay ~1150ms to rebuild it for no reason.
    <Fact>
    Public Async Function InvalidateDashboardMetrics_LeavesFilterOptionsCached() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)

        Await cache.GetEligibilityFilterOptionsAsync(50, CancellationToken.None)
        cache.InvalidateDashboardMetrics()
        Await cache.GetEligibilityFilterOptionsAsync(50, CancellationToken.None)

        Assert.Equal(1, gateway.GetFilterOptionsCalls)
    End Function

    ' With caching off every read already hits the gateway, so invalidating is
    ' meaningless - but it must not throw, because the caller cannot see the TTL.
    <Fact>
    Public Async Function InvalidateDashboardMetrics_WhenCachingDisabled_IsAHarmlessNoOp() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway, TimeSpan.Zero)

        cache.InvalidateDashboardMetrics()
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        cache.InvalidateDashboardMetrics()
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
    End Function

    <Fact>
    Public Async Function FilterOptions_SecondCallWithinTtl_DoesNotHitGateway() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)

        Dim first = Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)
        Dim second = Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)

        Assert.Equal(1, gateway.GetFilterOptionsCalls)
        Assert.Same(first, second)
    End Function

    ' maxDropdownSize decides which columns come back populated, so it must be
    ' part of the key - otherwise a second caller with a different threshold
    ' would silently get the first caller's answer.
    <Fact>
    Public Async Function FilterOptions_DifferentMaxDropdownSize_IsCachedSeparately() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)

        Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)
        Await cache.GetEligibilityFilterOptionsAsync(250, CancellationToken.None)
        ' Repeat both: each should now be served from its own entry.
        Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)
        Await cache.GetEligibilityFilterOptionsAsync(250, CancellationToken.None)

        Assert.Equal(2, gateway.GetFilterOptionsCalls)
        Assert.Equal({100, 250}, gateway.FilterOptionsCallArgs.OrderBy(Function(x) x).ToArray())
    End Function

    <Fact>
    Public Async Function CachedValue_IsTheGatewayValue() As Task
        Dim gateway As New FakeGateway()
        Dim expectedMetrics As New DashboardMetrics(
                eligibilityRowCount:=42, studiesSuccessful:=7, studiesFailedLatest:=1,
                resolutionRate:=0.5, promptTokens:=10, completionTokens:=20,
                failuresByStatus:=New Dictionary(Of String, Long) From {{"llm_failed", 1L}},
                studiesWithoutEmbeddings:=3, parseEmpty:=0)
        gateway.MetricsToReturn = expectedMetrics
        Dim cache = NewCache(gateway)

        Dim actual = Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Same(expectedMetrics, actual)
    End Function

    ' A cached failure would pin a transient Postgres error on the dashboard for
    ' the whole TTL. The next request must retry.
    <Fact>
    Public Async Function GatewayFailure_IsNotCached() As Task
        Dim gateway As New FakeGateway()
        gateway.CorpusReadError = New InvalidOperationException("postgres down")
        Dim cache = NewCache(gateway)

        Await Assert.ThrowsAsync(Of InvalidOperationException)(
                Function() cache.GetDashboardMetricsAsync(CancellationToken.None))

        ' Recovery: clear the fault and the very next call must reach the gateway
        ' again rather than replay the failure.
        gateway.CorpusReadError = Nothing
        Dim recovered = Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.NotNull(recovered)
        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
    End Function

    <Fact>
    Public Async Function FilterOptionsFailure_IsNotCached() As Task
        Dim gateway As New FakeGateway()
        gateway.CorpusReadError = New InvalidOperationException("postgres down")
        Dim cache = NewCache(gateway)

        Await Assert.ThrowsAsync(Of InvalidOperationException)(
                Function() cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None))

        gateway.CorpusReadError = Nothing
        Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)

        Assert.Equal(2, gateway.GetFilterOptionsCalls)
    End Function

    ' TTL <= 0 is the documented "disable caching" switch (Web:CorpusCacheTtlSeconds=0).
    <Fact>
    Public Async Function ZeroTtl_DisablesCaching_EveryCallHitsGateway() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway, TimeSpan.Zero)

        Assert.False(cache.IsEnabled)

        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)
        Await cache.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)

        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
        Assert.Equal(2, gateway.GetFilterOptionsCalls)
    End Function

    <Fact>
    Public Async Function NegativeTtl_DisablesCaching() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway, TimeSpan.FromSeconds(-1))

        Assert.False(cache.IsEnabled)

        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
    End Function

    ' Real elapsed time rather than a fake clock: MemoryCache's clock hook has
    ' churned across package versions, and a 60 ms wait is cheap enough to keep
    ' the suite fast.
    <Fact>
    Public Async Function EntryExpires_AfterTtl_RefetchesFromGateway() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway, TimeSpan.FromMilliseconds(30))

        Await cache.GetDashboardMetricsAsync(CancellationToken.None)
        Await Task.Delay(120)
        Await cache.GetDashboardMetricsAsync(CancellationToken.None)

        Assert.Equal(2, gateway.GetDashboardMetricsCalls)
    End Function

    <Fact>
    Public Sub NullDependencies_Throw()
        Dim gateway As New FakeGateway()
        Assert.Throws(Of ArgumentNullException)(
                Function() New CorpusReadCache(Nothing, New MemoryCache(New MemoryCacheOptions()), TimeSpan.FromMinutes(1)))
        Assert.Throws(Of ArgumentNullException)(
                Function() New CorpusReadCache(gateway, Nothing, TimeSpan.FromMinutes(1)))
    End Sub

    <Fact>
    Public Async Function CancellationToken_IsHonoured() As Task
        Dim gateway As New FakeGateway()
        Dim cache = NewCache(gateway)
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                    Function() cache.GetDashboardMetricsAsync(cts.Token))
        End Using
    End Function

End Class

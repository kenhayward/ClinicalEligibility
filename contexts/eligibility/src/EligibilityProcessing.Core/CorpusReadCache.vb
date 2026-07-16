Imports System.Globalization
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Caching.Memory

' IMemoryCache-backed ICorpusReadCache. See the interface for why only these
' two reads are cached.
'
' In-memory rather than distributed (Redis) on purpose: the web host is a
' hard singleton - deploy/eligibility-pipeline/docker-compose.yml pins
' container_name, and the SignalR feed requires the orchestrator in the same
' process as the hub - so there is no second instance to stay coherent with.
' A distributed cache would add a network hop and a failure domain to solve a
' problem this deployment does not have. If the host ever scales out, swap the
' IMemoryCache dependency for IDistributedCache here; nothing else moves.
'
' Two properties worth knowing:
'
'   1. Concurrent misses are not collapsed. Two requests that miss the same key
'      at the same moment both query Postgres and both write the same value.
'      Harmless (the reads are idempotent), and cheaper than the lock needed to
'      prevent it. Same trade-off UmlsCache makes.
'
'   2. Failures are never cached. If the gateway throws, the exception
'      propagates and nothing is stored, so the next request retries. This is
'      the opposite of UmlsCache's empty-on-error behaviour, and deliberately
'      so: the dashboard renders gateway errors inline, and a cached failure
'      would pin that error on screen for the whole TTL.
Public NotInheritable Class CorpusReadCache
    Implements ICorpusReadCache

    ' Chosen to be shorter than a human notices on a dashboard, but long enough
    ' that a burst of page views (or a pager click-through) collapses to one
    ' query. Overridable via config - see Web:CorpusCacheTtlSeconds.
    Public Const DefaultTtlSeconds As Integer = 60

    Private Const MetricsKey As String = "corpus:dashboard-metrics"
    Private Const FilterOptionsKeyPrefix As String = "corpus:filter-options:"

    Private ReadOnly _inner As IPostgresGateway
    Private ReadOnly _cache As IMemoryCache
    Private ReadOnly _ttl As TimeSpan

    ' ttl <= TimeSpan.Zero disables caching entirely: every call goes straight
    ' to the gateway and nothing is stored. Kept as a supported mode so the
    ' behaviour can be switched off from config without a redeploy.
    Public Sub New(inner As IPostgresGateway, cache As IMemoryCache, ttl As TimeSpan)
        If inner Is Nothing Then Throw New ArgumentNullException(NameOf(inner))
        If cache Is Nothing Then Throw New ArgumentNullException(NameOf(cache))
        _inner = inner
        _cache = cache
        _ttl = ttl
    End Sub

    Public ReadOnly Property IsEnabled As Boolean
        Get
            Return _ttl > TimeSpan.Zero
        End Get
    End Property

    Public Async Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics) _
            Implements ICorpusReadCache.GetDashboardMetricsAsync

        If Not IsEnabled Then
            Return Await _inner.GetDashboardMetricsAsync(cancellationToken).ConfigureAwait(False)
        End If

        Dim cached As DashboardMetrics = Nothing
        If _cache.TryGetValue(MetricsKey, cached) Then Return cached

        Dim fresh = Await _inner.GetDashboardMetricsAsync(cancellationToken).ConfigureAwait(False)
        _cache.Set(MetricsKey, fresh, _ttl)
        Return fresh
    End Function

    Public Async Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions) _
            Implements ICorpusReadCache.GetEligibilityFilterOptionsAsync

        If Not IsEnabled Then
            Return Await _inner.GetEligibilityFilterOptionsAsync(
                    maxDropdownSize, cancellationToken).ConfigureAwait(False)
        End If

        ' maxDropdownSize is part of the key: it decides which columns come back
        ' populated, so two different thresholds are two different results.
        Dim key = FilterOptionsKeyPrefix & maxDropdownSize.ToString(CultureInfo.InvariantCulture)

        Dim cached As EligibilityFilterOptions = Nothing
        If _cache.TryGetValue(key, cached) Then Return cached

        Dim fresh = Await _inner.GetEligibilityFilterOptionsAsync(
                maxDropdownSize, cancellationToken).ConfigureAwait(False)
        _cache.Set(key, fresh, _ttl)
        Return fresh
    End Function

End Class

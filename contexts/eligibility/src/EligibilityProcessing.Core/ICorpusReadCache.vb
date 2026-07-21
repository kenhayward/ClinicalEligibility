Imports System.Threading
Imports System.Threading.Tasks

' Read-through cache over the corpus-derived gateway reads that the web UI
' issues on every page view.
'
' Why these specifically (measured against a 3.9M-row public.eligibility /
' 290k-row public.eligibility_study corpus):
'   - GetDashboardMetricsAsync      ~700 ms, on every /Home/Index hit
'   - GetEligibilityFilterOptionsAsync ~1150 ms, on every /Home/Results hit,
'     including every pager click and every filter change
'   - GetCorpusConceptProfileAsync  ~2.0 s, the Analytics tab's lift baseline,
'     on every /Analytics hit
'
' All are aggregates over the whole corpus, so they only change when a
' pipeline run persists new trials - i.e. on the order of minutes, never
' per-request. A short TTL therefore costs nothing a user would notice while
' removing seconds of Postgres work from the hottest pages.
'
' Deliberately NOT a decorator over the whole IPostgresGateway: that interface
' carries ~70 methods, all but a couple of which must stay uncached (writes,
' per-trial reads, and the paged Results query itself, which is keyed by
' filter and must always be live).
Public Interface ICorpusReadCache

    ' Corpus-wide dashboard aggregate. Cached for the configured TTL.
    Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics)

    ' Drops the cached dashboard aggregate so the next read goes to Postgres.
    '
    ' Needed because the TTL and a user's expectations disagree. The dashboard
    ' has an explicit Reload control and re-reads itself when a run finishes;
    ' inside the TTL both would hand back the identical cached numbers, so the
    ' UI would show a loading state, resolve, and change nothing. That reads as
    ' broken rather than as cached.
    '
    ' Invalidate-then-read, not read-around: the caller repopulates the entry for
    ' everyone else, and the TTL still throttles anyone leaning on the button.
    Sub InvalidateDashboardMetrics()

    ' Per-column distinct-value lists for the Results filter dropdowns. Cached
    ' per maxDropdownSize, since that argument changes the result.
    Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions)

    ''' <summary>
    ''' Corpus-wide per-concept trial counts, the lift baseline for the Analytics
    ''' tab. Measured at 2.0s and identical for every request, so it is cached on
    ''' the same TTL as the dashboard aggregate.
    ''' </summary>
    Function GetCorpusConceptProfileAsync(
            cancellationToken As CancellationToken) As Task(Of CorpusConceptProfile)

End Interface

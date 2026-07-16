Imports System.Threading
Imports System.Threading.Tasks

' Read-through cache over the two corpus-derived gateway reads that the web UI
' issues on every page view.
'
' Why these two specifically (measured against a 3.9M-row public.eligibility /
' 290k-row public.eligibility_study corpus):
'   - GetDashboardMetricsAsync      ~700 ms, on every /Home/Index hit
'   - GetEligibilityFilterOptionsAsync ~1150 ms, on every /Home/Results hit,
'     including every pager click and every filter change
'
' Both are aggregates over the whole corpus, so they only change when a
' pipeline run persists new trials - i.e. on the order of minutes, never
' per-request. A short TTL therefore costs nothing a user would notice while
' removing ~1.85 s of Postgres work from the two hottest pages.
'
' Deliberately NOT a decorator over the whole IPostgresGateway: that interface
' carries ~70 methods, all but these two of which must stay uncached (writes,
' per-trial reads, and the paged Results query itself, which is keyed by
' filter and must always be live).
Public Interface ICorpusReadCache

    ' Corpus-wide dashboard aggregate. Cached for the configured TTL.
    Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics)

    ' Per-column distinct-value lists for the Results filter dropdowns. Cached
    ' per maxDropdownSize, since that argument changes the result.
    Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions)

End Interface

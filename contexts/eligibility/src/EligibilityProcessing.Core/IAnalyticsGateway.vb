Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' Read-only analytics queries over the processed corpus. Separate from
''' IPostgresGateway, which owns pipeline persistence and carries ~70 methods -
''' these reads share none of that concern.
''' </summary>
Public Interface IAnalyticsGateway

    ''' <summary>Distinct trials matching the cohort.</summary>
    Function GetCohortSizeAsync(cohort As AnalyticsCohort,
                                cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Per-concept distinct-trial counts within the cohort.</summary>
    Function GetCohortProfileAsync(cohort As AnalyticsCohort,
                                   cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptCount))

    ''' <summary>
    ''' Per-concept distinct-trial counts across the whole corpus - the lift
    ''' baseline. Identical for every request, so callers memoise it.
    ''' </summary>
    Function GetCorpusProfileAsync(cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptCount))

    ''' <summary>Distinct trials with any resolved concept - the baseline denominator.</summary>
    Function GetCorpusTrialCountAsync(cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' The concept codes that define this cohort - its own concept plus, when
    ''' descendants are included, those descendants. Empty for Phase and Year.
    ''' Used to flag tautological rows, not to remove them.
    ''' </summary>
    Function GetCohortDefiningCodesAsync(cohort As AnalyticsCohort,
                                         cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

    ''' <summary>Preferred names for the given CUIs, from umls.concept.</summary>
    Function GetPrefNamesAsync(conceptCodes As IReadOnlyList(Of String),
                               cancellationToken As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, String))

    ''' <summary>Per-year prevalence of one concept as a share of that year's processed studies.</summary>
    Function GetTrendAsync(conceptCode As String,
                           currentYear As Integer,
                           cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of TrendPoint))

    ''' <summary>Everything the lookup view shows, or Nothing when the CUI is unknown.</summary>
    Function GetConceptSummaryAsync(conceptCode As String,
                                    cancellationToken As CancellationToken) As Task(Of ConceptSummary)

    ''' <summary>Concepts whose preferred name matches, most-used first, capped at limit.</summary>
    Function SearchConceptsAsync(term As String, limit As Integer,
                                 cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptSummary))

End Interface

Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions

' In-memory memoising decorator over <see cref="IUmlsClient"/>.
'
' Architecture section 2.4 / spec gap section 9.7. The reference n8n workflow
' re-queries UMLS for every appearance of the same Concept (e.g. "Pregnancy"
' resolves once per inclusion criterion across many trials). For a 500-trial
' batch, this is hundreds to thousands of redundant lookups against an external
' API. Memoising within a single run trades a small amount of memory for a
' large reduction in UMLS round trips.
'
' Scope: one instance per pipeline run. Register Scoped in DI so the cache
' dies with the run (no cross-run staleness, no unbounded growth).
'
' Concurrency: ConcurrentDictionary handles the read/write races. A rare
' duplicate inner fetch is possible when two threads miss the same key
' simultaneously; both writes land on the same value, so this is benign and
' the simpler implementation is preferred over a Lazy(Of Task) pattern.
'
' Error semantics: the inner client swallows UMLS transport failures and
' returns empty results (spec section 2.6.1 / 2.6.3). The cache therefore
' cannot distinguish "no results" from "UMLS was down" — both get cached.
' Within a single ~11-minute run this is acceptable: a transient UMLS outage
' at start-of-run makes that concept unresolved for the whole run, which
' matches the n8n behaviour. The benefit (caching genuinely-not-in-UMLS
' jargon, e.g. trial-specific terms) outweighs the cost.

Public NotInheritable Class UmlsCache
    Implements IUmlsClient

    Private ReadOnly _inner As IUmlsClient
    Private ReadOnly _logger As ILogger(Of UmlsCache)

    ' Lower-cased concept -> candidate list. OrdinalIgnoreCase is defensive
    ' in case a caller bypasses our own lowercasing.
    Private ReadOnly _searchCache As New ConcurrentDictionary(Of String, IReadOnlyList(Of UmlsCandidate))(
            StringComparer.OrdinalIgnoreCase)

    ' CUI (case-sensitive per UMLS convention, format "C" + 7 digits) -> semantic-type names.
    Private ReadOnly _semanticTypesCache As New ConcurrentDictionary(Of String, IReadOnlyList(Of String))(
            StringComparer.Ordinal)

    Public Sub New(inner As IUmlsClient, Optional logger As ILogger(Of UmlsCache) = Nothing)
        If inner Is Nothing Then Throw New ArgumentNullException(NameOf(inner))
        _inner = inner
        _logger = If(logger, CType(NullLogger(Of UmlsCache).Instance, ILogger(Of UmlsCache)))
    End Sub

    Public Async Function SearchAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate)) _
            Implements IUmlsClient.SearchAsync

        If String.IsNullOrWhiteSpace(concept) Then
            Return Array.Empty(Of UmlsCandidate)()
        End If

        Dim key = concept.Trim().ToLowerInvariant()
        Dim cached As IReadOnlyList(Of UmlsCandidate) = Nothing
        If _searchCache.TryGetValue(key, cached) Then
            Return cached
        End If

        Dim fresh = Await _inner.SearchAsync(concept, cancellationToken).ConfigureAwait(False)
        _searchCache.TryAdd(key, fresh)
        Return fresh
    End Function

    Public Async Function GetSemanticTypesAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IUmlsClient.GetSemanticTypesAsync

        If String.IsNullOrWhiteSpace(cui) Then
            Return Array.Empty(Of String)()
        End If

        Dim cached As IReadOnlyList(Of String) = Nothing
        If _semanticTypesCache.TryGetValue(cui, cached) Then
            Return cached
        End If

        Dim fresh = Await _inner.GetSemanticTypesAsync(cui, cancellationToken).ConfigureAwait(False)
        _semanticTypesCache.TryAdd(cui, fresh)
        Return fresh
    End Function

    ''' <summary>Current count of cached concept searches.</summary>
    Public ReadOnly Property SearchCacheSize As Integer
        Get
            Return _searchCache.Count
        End Get
    End Property

    ''' <summary>Current count of cached CUI -> semantic-types entries.</summary>
    Public ReadOnly Property SemanticTypesCacheSize As Integer
        Get
            Return _semanticTypesCache.Count
        End Get
    End Property

    ''' <summary>Empties both caches. Intended for diagnostics / tests / explicit run boundaries.</summary>
    Public Sub Clear()
        _searchCache.Clear()
        _semanticTypesCache.Clear()
    End Sub

End Class

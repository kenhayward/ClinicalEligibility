Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions

' Local-Postgres implementation of IUmlsClient (the umls.* schema, V17). A
' drop-in alternative to the REST UmlsClient behind the same interface and the
' same UmlsCache decorator / UmlsMatchScorer — selected when Umls:Backend =
' "postgres". Delegates the SQL to UmlsMetathesaurusStore and enforces the
' interface contract: never throw (UMLS failures are non-fatal — return empty),
' but DO propagate user cancellation. CandidateLimit / TrigramThreshold are
' passed in by the composition root (read from UmlsOptions) to keep this Data
' project independent of the Umls project's options type.

Public NotInheritable Class PostgresUmlsClient
    Implements IUmlsClient

    Private Shared ReadOnly NonWordRegex As New Regex("\W+", RegexOptions.Compiled)

    Private ReadOnly _store As UmlsMetathesaurusStore
    Private ReadOnly _candidateLimit As Integer
    Private ReadOnly _trigramThreshold As Double
    Private ReadOnly _maxAtomLength As Integer
    Private ReadOnly _minQueryCoverage As Double
    Private ReadOnly _requireQueryCodeMatch As Boolean
    Private ReadOnly _enableTrigramFallback As Boolean
    Private ReadOnly _scorer As UmlsMatchScorer
    Private ReadOnly _logger As ILogger(Of PostgresUmlsClient)

    Public Sub New(
            store As UmlsMetathesaurusStore,
            candidateLimit As Integer,
            trigramThreshold As Double,
            Optional minQueryCoverage As Double = 0.6,
            Optional requireQueryCodeMatch As Boolean = True,
            Optional maxAtomLength As Integer = 80,
            Optional enableTrigramFallback As Boolean = True,
            Optional scorer As UmlsMatchScorer = Nothing,
            Optional logger As ILogger(Of PostgresUmlsClient) = Nothing)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        _store = store
        _candidateLimit = candidateLimit
        _trigramThreshold = trigramThreshold
        _maxAtomLength = maxAtomLength
        _minQueryCoverage = minQueryCoverage
        _requireQueryCodeMatch = requireQueryCodeMatch
        _enableTrigramFallback = enableTrigramFallback
        ' UmlsMatchScorer is stateless; new one up if the caller didn't supply the
        ' DI singleton. Used only for the score-aware trigram gate below — the real
        ' resolution still happens downstream via the same scorer + threshold.
        _scorer = If(scorer, New UmlsMatchScorer())
        _logger = If(logger, CType(NullLogger(Of PostgresUmlsClient).Instance, ILogger(Of PostgresUmlsClient)))
    End Sub

    Public Async Function SearchAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate)) _
            Implements IUmlsClient.SearchAsync

        If String.IsNullOrWhiteSpace(concept) Then Return Array.Empty(Of UmlsCandidate)()
        Try
            ' Fast path: exact + FTS only (no expensive pg_trgm fuzzy scan).
            Dim primary = Await _store.SearchCandidatesAsync(
                    concept, _candidateLimit, _trigramThreshold, _maxAtomLength,
                    includeTrigram:=False, cancellationToken).ConfigureAwait(False)
            Dim guarded = ApplyGuards(concept, primary)

            ' Score-aware trigram gate. The fuzzy arm can only change a resolved/
            ' unresolved outcome when the fast path resolves NOTHING — so fire it
            ' only when no guarded candidate clears the scorer's match threshold
            ' (the exact predicate the downstream PickBestMatch will apply). This
            ' pays the ~250ms fuzzy scan solely on the would-be-unresolved minority.
            If Not _enableTrigramFallback OrElse ResolvesAny(concept, guarded) Then
                Return guarded
            End If

            Dim full = Await _store.SearchCandidatesAsync(
                    concept, _candidateLimit, _trigramThreshold, _maxAtomLength,
                    includeTrigram:=True, cancellationToken).ConfigureAwait(False)
            Return ApplyGuards(concept, full)
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex,
                    "Local UMLS search failed for concept {Concept}; treating as empty result", concept)
            Return Array.Empty(Of UmlsCandidate)()
        End Try
    End Function

    ' Applies both precision guards in order (coverage then discriminative-code), so
    ' the fast and fuzzy paths produce the same candidate set the scorer would see.
    Private Function ApplyGuards(
            concept As String,
            candidates As IReadOnlyList(Of UmlsCandidate)) As IReadOnlyList(Of UmlsCandidate)
        Return ApplyCodeGuard(concept, ApplyCoverageGuard(concept, candidates))
    End Function

    ' True when at least one (already-guarded) candidate clears the scorer's match
    ' threshold for the concept — i.e. the concept will resolve without the fuzzy
    ' arm. Mirrors UmlsMatchScorer.PickBestMatch's accept condition exactly (same
    ' Score, same 0.45 threshold), so the gate predicts the downstream verdict.
    Private Function ResolvesAny(
            concept As String,
            candidates As IReadOnlyList(Of UmlsCandidate)) As Boolean
        If candidates Is Nothing OrElse candidates.Count = 0 Then Return False
        For Each c In candidates
            If c IsNot Nothing AndAlso _scorer.Score(concept, c.Name) >= UmlsMatchScorer.MatchThreshold Then
                Return True
            End If
        Next
        Return False
    End Function

    ' Drops over-generic candidates before the scorer sees them: for a multi-word
    ' query, a candidate must cover at least MinQueryCoverage of the query's
    ' significant tokens. Without this, a generic 1-token atom (e.g. "Examination")
    ' wins the scorer's containment/substring match against a long query and beats
    ' the correct specific concept. Single-token (and fuzzy) lookups are untouched.
    Private Function ApplyCoverageGuard(
            concept As String,
            candidates As IReadOnlyList(Of UmlsCandidate)) As IReadOnlyList(Of UmlsCandidate)

        If _minQueryCoverage <= 0 OrElse candidates Is Nothing OrElse candidates.Count = 0 Then
            Return candidates
        End If
        If UmlsMatchScorer.SignificantTokenCount(concept) < 2 Then Return candidates

        Dim kept = candidates.Where(
                Function(c) c IsNot Nothing AndAlso
                           UmlsMatchScorer.QueryTokenCoverage(concept, c.Name) >= _minQueryCoverage).ToList()
        Return kept
    End Function

    ' Discriminative-token guard (conflict-only): when the query carries
    ' numeric/code tokens (digit-bearing — numbers, isotopes, gene loci, drug
    ' codes), drop a candidate that carries its OWN code(s) none of which match the
    ' query's — a conflicting number. Stops "4 meter" matching "10 meter" and
    ' "1 month" matching "12 month". Candidates with NO code of their own are kept
    ' (they may express the number in words — e.g. "Non-Insulin-Dependent" for
    ' "Type 2"), leaving FTS rank + coverage to judge them. No-op when the query
    ' has no code tokens or the guard is disabled.
    Private Function ApplyCodeGuard(
            concept As String,
            candidates As IReadOnlyList(Of UmlsCandidate)) As IReadOnlyList(Of UmlsCandidate)

        If Not _requireQueryCodeMatch OrElse candidates Is Nothing OrElse candidates.Count = 0 Then
            Return candidates
        End If
        Dim queryCodes = CodeTokens(concept)
        If queryCodes.Count = 0 Then Return candidates

        Return candidates.Where(
                Function(c)
                    If c Is Nothing Then Return False
                    Dim candidateCodes = CodeTokens(c.Name)
                    Return candidateCodes.Count = 0 OrElse candidateCodes.Overlaps(queryCodes)
                End Function).ToList()
    End Function

    ' Digit-bearing tokens (lower-cased): "10", "131i", "17p", "177lu", "100mg".
    Private Shared Function CodeTokens(value As String) As HashSet(Of String)
        Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If String.IsNullOrEmpty(value) Then Return result
        For Each tok In NonWordRegex.Split(value)
            If tok.Length >= 1 AndAlso tok.Any(AddressOf Char.IsDigit) Then result.Add(tok.ToLowerInvariant())
        Next
        Return result
    End Function

    Public Async Function GetSemanticTypeAssignmentsAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) _
            Implements IUmlsClient.GetSemanticTypeAssignmentsAsync

        If String.IsNullOrWhiteSpace(cui) Then Return Array.Empty(Of SemanticTypeAssignment)()
        Try
            Return Await _store.GetSemanticTypeAssignmentsAsync(cui, cancellationToken).ConfigureAwait(False)
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex,
                    "Local UMLS semantic-type lookup failed for cui {Cui}; treating as empty result", cui)
            Return Array.Empty(Of SemanticTypeAssignment)()
        End Try
    End Function

End Class

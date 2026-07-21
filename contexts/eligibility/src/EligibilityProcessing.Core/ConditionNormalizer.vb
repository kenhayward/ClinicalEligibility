Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' Resolves a raw AACT condition string to a UMLS CUI, in three tiers.
'''
''' Tier 1a - an exact umls.atom.str_norm match resolving to exactly one CUI is
'''   accepted directly, WITHOUT the scorer. An atom is a synonym of its concept,
'''   so matching one is matching the concept. This is not an optimisation: the
'''   scorer compares the query to the concept's PREFERRED name, and for the
'''   highest-volume conditions those differ wildly ("stroke" vs "CVA -
'''   Cerebrovascular accident", "covid-19" vs "Disease caused by 2019-nCoV").
'''   Routing tier 1 through the scorer would reject perfect matches. 63% of
'''   corpus condition mentions land here.
'''
''' Tier 1b - an exact match resolving to several CUIs needs a choice, so the
'''   scorer is used as a tie-break only. Accepted regardless of score, because
'''   the string is still an exact atom match; only which concept was in doubt.
'''
''' Tier 2 - no exact atom, so fall back to the existing FTS/trigram search and
'''   PickBestMatch, accepting at >= FuzzyThreshold.
'''
''' See docs/superpowers/specs/2026-07-21-condition-normalizer-design.md.
''' </summary>
Public NotInheritable Class ConditionNormalizer

    ''' <summary>
    ''' Acceptance cutoff for tier 2, stricter than the criteria pipeline's
    ''' UmlsMatchScorer.MatchThreshold (0.45). A wrong condition mapping does not
    ''' announce itself - it silently misfiles trials into the wrong analytic
    ''' slice, where nobody sees it. A wrong criterion match, by contrast, is
    ''' visible next to its text in the Results browser.
    ''' </summary>
    Public Const FuzzyThreshold As Double = 0.6

    Private ReadOnly _store As IConditionConceptStore
    Private ReadOnly _umlsClient As IUmlsClient
    Private ReadOnly _scorer As UmlsMatchScorer

    Public Sub New(store As IConditionConceptStore, umlsClient As IUmlsClient, scorer As UmlsMatchScorer)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        If umlsClient Is Nothing Then Throw New ArgumentNullException(NameOf(umlsClient))
        If scorer Is Nothing Then Throw New ArgumentNullException(NameOf(scorer))
        _store = store
        _umlsClient = umlsClient
        _scorer = scorer
    End Sub

    ''' <summary>Resolve one raw condition string. Never throws for empty input.</summary>
    Public Async Function ResolveAsync(rawForm As String,
                                       cancellationToken As CancellationToken) As Task(Of ConditionResolution)

        Dim norm = ConceptKey.Normalize(rawForm)
        If norm = "" Then Return ConditionResolution.Unresolved

        Dim exactCandidates = Await _store.LookupExactAsync(norm, cancellationToken).ConfigureAwait(False)

        If exactCandidates IsNot Nothing AndAlso exactCandidates.Count = 1 Then
            ' Tier 1a. No scoring - see the class comment.
            Dim only = exactCandidates(0)
            Return New ConditionResolution(
                    conceptCode:=If(only.Ui, ""),
                    umlsName:=If(only.Name, ""),
                    tier:=ConditionMatchTier.Exact,
                    score:=1.0)
        End If

        If exactCandidates IsNot Nothing AndAlso exactCandidates.Count > 1 Then
            ' Tier 1b.
            Dim picked = PickAmbiguous(rawForm, norm, exactCandidates)
            Return New ConditionResolution(
                    conceptCode:=If(picked.Ui, ""),
                    umlsName:=If(picked.Name, ""),
                    tier:=ConditionMatchTier.ExactAmbiguous,
                    score:=1.0)
        End If

        ' Tier 2. Pass the RAW form, not the normalized key: the scorer's acronym
        ' term requires ^[A-Z0-9]{2,6}$ on the raw query, so lowercasing here
        ' would silently disable acronym matching for NSCLC, COPD, HIV and the rest.
        Dim candidates = Await _umlsClient.SearchAsync(rawForm, cancellationToken).ConfigureAwait(False)
        Dim match = _scorer.PickBestMatch(rawForm, candidates)
        If Not match.IsResolved OrElse match.MatchScore < FuzzyThreshold Then
            Return ConditionResolution.Unresolved
        End If

        Return New ConditionResolution(
                conceptCode:=match.ConceptCode,
                umlsName:=match.UmlsName,
                tier:=ConditionMatchTier.Fuzzy,
                score:=match.MatchScore)
    End Function

    ''' <summary>
    ''' Deterministic choice among several exact-match CUIs:
    '''   1. the CUI whose preferred name normalizes to the query;
    '''   2. otherwise the highest scorer value;
    '''   3. otherwise the lexicographically lowest CUI.
    ''' Rule 3 exists so a re-run reproduces the same answer.
    ''' </summary>
    Private Function PickAmbiguous(rawForm As String,
                                   norm As String,
                                   candidates As IReadOnlyList(Of UmlsCandidate)) As UmlsCandidate

        Dim usable = candidates.Where(Function(c) c IsNot Nothing).ToList()

        Dim exactName = usable.
                Where(Function(c) ConceptKey.Normalize(c.Name) = norm).
                OrderBy(Function(c) If(c.Ui, ""), StringComparer.Ordinal).
                FirstOrDefault()
        If exactName IsNot Nothing Then Return exactName

        Return usable.
                OrderByDescending(Function(c) _scorer.Score(rawForm, If(c.Name, ""))).
                ThenBy(Function(c) If(c.Ui, ""), StringComparer.Ordinal).
                First()
    End Function

    ''' <summary>
    ''' Resolve and store every condition string on this study that has no
    ''' dictionary row yet. Returns how many rows were written - 0 in the steady
    ''' state, which costs one indexed query.
    ''' </summary>
    Public Async Function EnsureForStudyAsync(nctId As String,
                                              cancellationToken As CancellationToken) As Task(Of Integer)

        If String.IsNullOrWhiteSpace(nctId) Then Return 0

        Dim unseen = Await _store.GetUnseenConditionsForStudyAsync(nctId, cancellationToken).ConfigureAwait(False)
        If unseen Is Nothing OrElse unseen.Count = 0 Then Return 0

        Dim written = 0
        For Each raw In unseen
            cancellationToken.ThrowIfCancellationRequested()
            Dim norm = ConceptKey.Normalize(raw)
            If norm = "" Then Continue For

            Dim resolution = Await ResolveAsync(raw, cancellationToken).ConfigureAwait(False)
            Await _store.UpsertAsync(New ConditionConceptEntry With {
                    .ConditionNorm = norm,
                    .RawForm = raw,
                    .StudyCount = 0,
                    .ConceptCode = resolution.ConceptCode,
                    .UmlsName = resolution.UmlsName,
                    .MatchTier = resolution.Tier,
                    .MatchScore = resolution.Score
                }, cancellationToken).ConfigureAwait(False)
            written += 1
        Next
        Return written
    End Function

End Class

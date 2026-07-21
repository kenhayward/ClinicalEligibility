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
            ' An empty Cui would otherwise produce match_tier='exact' with a NULL
            ' concept_code - a state no downstream consumer expects (every other
            ' unresolved outcome is tier='unresolved'). Treat it as unresolved.
            If String.IsNullOrEmpty(only.Cui) Then Return ConditionResolution.Unresolved
            Return New ConditionResolution(
                    conceptCode:=only.Cui,
                    umlsName:=If(only.PrefName, ""),
                    tier:=ConditionMatchTier.Exact,
                    score:=1.0)
        End If

        If exactCandidates IsNot Nothing AndAlso exactCandidates.Count > 1 Then
            ' Tier 1b.
            Dim picked = PickAmbiguous(rawForm, norm, exactCandidates)
            ' PickAmbiguous returns Nothing only when every candidate in the list
            ' was itself Nothing - unreachable from ConditionConceptStore today
            ' (its SQL never yields a null ConditionCandidate), but
            ' IConditionConceptStore is a public port any implementation can
            ' satisfy. Fall back to unresolved rather than dereferencing a null
            ' candidate below.
            If picked Is Nothing Then Return ConditionResolution.Unresolved
            ' Same hole as tier 1a above: an empty Cui on the picked candidate
            ' would otherwise produce match_tier='exact_ambiguous' with a NULL
            ' concept_code. Treat it as unresolved for consistency.
            If String.IsNullOrEmpty(picked.Cui) Then Return ConditionResolution.Unresolved
            Return New ConditionResolution(
                    conceptCode:=picked.Cui,
                    umlsName:=If(picked.PrefName, ""),
                    tier:=ConditionMatchTier.ExactAmbiguous,
                    score:=1.0)
        End If

        ' Tier 2. Pass the RAW form, not the normalized key: the scorer's acronym
        ' term requires ^[A-Z0-9]{2,6}$ on the raw query, so lowercasing here
        ' would silently disable acronym matching for NSCLC, COPD, HIV and the rest.
        '
        ' Known behaviour worth knowing about when tier-2 yield looks lower than
        ' expected: when Umls:Backend=postgres, PostgresUmlsClient only runs its
        ' (slower) trigram fallback arm when NOTHING from the fast path clears the
        ' scorer's own 0.45 match threshold. A candidate that scores between 0.45
        ' and this class's stricter 0.60 FuzzyThreshold therefore suppresses the
        ' trigram arm even though it will go on to be rejected here - the trigram
        ' arm might otherwise have surfaced a better candidate that clears 0.60.
        ' This costs some tier-2 resolution yield but never produces a WRONG
        ' answer (the 0.45-0.60 candidate is still correctly rejected).
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
    '''   1. the candidate that can roll up (has at least one row in
    '''      umls.concept_ancestor as a descendant);
    '''   2. otherwise the CUI whose preferred name normalizes to the query;
    '''   3. otherwise the highest scorer value;
    '''   4. otherwise the lexicographically lowest CUI.
    ''' Rule 4 exists so a re-run reproduces the same answer.
    '''
    ''' Rule 1 outranks rule 2 deliberately: a candidate whose preferred name is
    ''' literally the query string "wins" on label prettiness alone, but if it
    ''' has no concept_ancestor entry it can never roll up to a broader concept
    ''' - the entire point of the hierarchy feature. Measured in production on
    ''' "stroke": C5977286 (LOINC "Finding", pref_name "Stroke", no hierarchy)
    ''' used to beat C0038454 (SNOMED "CVA - Cerebrovascular accident", has
    ''' hierarchy) under the old rule 1, silently orphaning every study resolved
    ''' to it from the rollup. Where no candidate has hierarchy, rule 1 does not
    ''' discriminate and this falls through to the old behaviour unchanged.
    ''' </summary>
    Private Function PickAmbiguous(rawForm As String,
                                   norm As String,
                                   candidates As IReadOnlyList(Of ConditionCandidate)) As ConditionCandidate

        Dim usable = candidates.Where(Function(c) c IsNot Nothing).ToList()

        ' FirstOrDefault, not First: if candidates held only Nothing entries,
        ' usable is empty here and First() would throw InvalidOperationException.
        ' Returns Nothing in that case; ResolveAsync treats that as unresolved.
        '
        ' OrderByDescending on a Boolean puts True first (True > False), which is
        ' what rule 1 needs - candidates that can roll up sort ahead of ones that
        ' cannot.
        Return usable.
                OrderByDescending(Function(c) c.HasHierarchy).
                ThenByDescending(Function(c) ConceptKey.Normalize(c.PrefName) = norm).
                ThenByDescending(Function(c) _scorer.Score(rawForm, If(c.PrefName, ""))).
                ThenBy(Function(c) If(c.Cui, ""), StringComparer.Ordinal).
                FirstOrDefault()
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

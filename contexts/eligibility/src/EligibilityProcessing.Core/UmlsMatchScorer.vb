Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.RegularExpressions

' Composite scorer for UMLS candidate matches.
'
' Spec section 2.6.2: composite score = max(levSim, jaccardContainment, acronymContribution).
' Acceptance threshold is 0.45 — below threshold the record is treated as unresolved
' and persists with empty UMLS fields and MatchScore = 0.
'
' All math is pure and deterministic. The class is stateless, so a single instance
' can be shared across the orchestrator.

Public NotInheritable Class UmlsMatchScorer

    Public Const MatchThreshold As Double = 0.45

    ' Spec section 2.6.2 signal 2: stopword set used by tokenisation.
    Private Shared ReadOnly Stopwords As New HashSet(Of String)(
            {"the", "a", "an", "of", "for", "and", "or", "to", "in", "on",
             "with", "by", "at", "from", "is", "are", "as"})

    ' Spec section 2.6.2 signal 3: "raw query (case preserved) matches /^[A-Z0-9]{2,6}$/".
    Private Shared ReadOnly AcronymPattern As New Regex(
            "^[A-Z0-9]{2,6}$", RegexOptions.Compiled)

    ' Spec section 2.6.2 signal 2: "split on non-word chars".
    Private Shared ReadOnly TokenSplitPattern As New Regex(
            "\W+", RegexOptions.Compiled)

    ''' <summary>
    ''' Composite score for one (query, candidate-name) pair. Returns a value
    ''' in [0, 1]; not yet rounded — rounding happens at persistence boundary.
    ''' </summary>
    Public Function Score(query As String, candidateName As String) As Double
        Dim lev = LevenshteinSimilarity(query, candidateName)
        Dim jac = JaccardContainment(query, candidateName)
        Dim acr = AcronymContribution(query, candidateName, lev)
        Return Math.Max(lev, Math.Max(jac, acr))
    End Function

    ''' <summary>
    ''' Scores every candidate, returns the highest-scoring one if it meets the
    ''' 0.45 threshold, otherwise <see cref="UmlsMatch.Unresolved"/>. Score on
    ''' resolved matches is rounded to 3 decimal places per spec section 2.8.1.
    ''' </summary>
    Public Function PickBestMatch(
            query As String,
            candidates As IReadOnlyList(Of UmlsCandidate)) As UmlsMatch
        If candidates Is Nothing OrElse candidates.Count = 0 Then
            Return UmlsMatch.Unresolved
        End If

        Dim bestScore As Double = Double.NegativeInfinity
        Dim best As UmlsCandidate = Nothing

        For Each c In candidates
            If c Is Nothing Then Continue For
            Dim s = Score(query, c.Name)
            If s > bestScore Then
                bestScore = s
                best = c
            End If
        Next

        If best Is Nothing OrElse bestScore < MatchThreshold Then
            Return UmlsMatch.Unresolved
        End If

        Return New UmlsMatch(
                conceptCode:=If(best.Ui, ""),
                umlsName:=If(best.Name, ""),
                matchSource:=If(best.RootSource, ""),
                matchScore:=Math.Round(bestScore, 3, MidpointRounding.AwayFromZero))
    End Function

    ' --- signal 1: Levenshtein similarity ---
    ' levSim(a, b) = 1 - (editDistance(a, b) / max(len(a), len(b)))
    ' Both strings lower-cased and trimmed before comparison.
    Friend Shared Function LevenshteinSimilarity(a As String, b As String) As Double
        Dim aa = If(a, "").Trim().ToLowerInvariant()
        Dim bb = If(b, "").Trim().ToLowerInvariant()
        Dim maxLen = Math.Max(aa.Length, bb.Length)
        If maxLen = 0 Then Return 1.0  ' both empty -> treat as identical
        Dim dist = Fastenshtein.Levenshtein.Distance(aa, bb)
        Return 1.0 - (CDbl(dist) / CDbl(maxLen))
    End Function

    ' --- signal 2: query-token containment ---
    ' Fraction of the QUERY's significant tokens (a) found in the candidate (b).
    ' Denominator is the query token count, NOT min(|a|,|b|): a short query fully
    ' contained in a longer candidate still scores 1.0 (query fully covered), but
    ' a short *candidate* contained in a longer query no longer scores 1.0 — it
    ' scores its coverage of the query. This stops a generic 1-token atom (e.g.
    ' "Examination") from scoring a perfect containment against a multi-word query
    ' (e.g. "12-lead ECG Examination") and beating the correct, specific concept.
    ' (Existing tests only exercise |a| <= |b|, where min(|a|,|b|) = |a|, so this
    ' is behaviour-preserving for them.)
    Friend Shared Function JaccardContainment(a As String, b As String) As Double
        Dim ta = Tokenize(a)
        Dim tb = Tokenize(b)
        If ta.Count = 0 OrElse tb.Count = 0 Then Return 0.0
        Dim intersectionCount As Integer = 0
        For Each t In ta
            If tb.Contains(t) Then intersectionCount += 1
        Next
        Return CDbl(intersectionCount) / CDbl(ta.Count)
    End Function

    ''' <summary>
    ''' Fraction of <paramref name="query"/>'s significant tokens present in
    ''' <paramref name="candidate"/> — the query-coverage signal the Postgres UMLS
    ''' backend uses to drop over-generic candidates before scoring. Public so the
    ''' backend can reuse the exact same tokenisation as the scorer.
    ''' </summary>
    Public Shared Function QueryTokenCoverage(query As String, candidate As String) As Double
        Return JaccardContainment(query, candidate)
    End Function

    ''' <summary>Count of significant tokens (post stopword / &lt;2-char drop).</summary>
    Public Shared Function SignificantTokenCount(value As String) As Integer
        Return Tokenize(value).Count
    End Function

    Friend Shared Function Tokenize(s As String) As HashSet(Of String)
        Dim tokens As New HashSet(Of String)
        If String.IsNullOrEmpty(s) Then Return tokens
        For Each tok In TokenSplitPattern.Split(s.ToLowerInvariant())
            If tok.Length < 2 Then Continue For
            If Stopwords.Contains(tok) Then Continue For
            tokens.Add(tok)
        Next
        Return tokens
    End Function

    ' --- signal 3: acronym contribution = acrBase + 0.3 * levSim (always) ---
    ' acrBase = 0.5 when the raw query matches /^[A-Z0-9]{2,6}$/ AND the candidate
    ' contains the query as a whole word (case-insensitive); otherwise 0.
    ' The 0.3 * levSim term is added unconditionally per the spec formula.
    Friend Shared Function AcronymContribution(
            rawQuery As String,
            candidate As String,
            levSim As Double) As Double
        Dim acrBase As Double = 0
        If Not String.IsNullOrEmpty(rawQuery) AndAlso AcronymPattern.IsMatch(rawQuery) Then
            If candidate IsNot Nothing Then
                Dim wordPattern As New Regex(
                        "\b" & Regex.Escape(rawQuery) & "\b",
                        RegexOptions.IgnoreCase)
                If wordPattern.IsMatch(candidate) Then
                    acrBase = 0.5
                End If
            End If
        End If
        Return acrBase + 0.3 * levSim
    End Function

End Class

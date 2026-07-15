Imports EligibilityProcessing.Core
Imports Xunit

' Tests organised by spec section 2.6.2 signal. Internal helpers are reached
' via InternalsVisibleTo so each signal's math can be asserted directly rather
' than only through the composite.

Public Class UmlsMatchScorerTests

    Private ReadOnly _scorer As New UmlsMatchScorer()

    ' ============ signal 1: Levenshtein similarity ============

    <Fact>
    Public Sub Lev_returns_1_for_identical_strings()
        Assert.Equal(1.0, UmlsMatchScorer.LevenshteinSimilarity("Adult", "Adult"), 5)
    End Sub

    <Fact>
    Public Sub Lev_is_case_insensitive_after_lowercasing()
        Assert.Equal(1.0, UmlsMatchScorer.LevenshteinSimilarity("ADULT", "adult"), 5)
    End Sub

    <Fact>
    Public Sub Lev_trims_whitespace_before_comparing()
        Assert.Equal(1.0, UmlsMatchScorer.LevenshteinSimilarity("  adult  ", "adult"), 5)
    End Sub

    <Fact>
    Public Sub Lev_returns_0_for_completely_disjoint_strings()
        ' "abc" -> "xyz": 3 substitutions, maxLen=3, sim = 1 - 3/3 = 0
        Assert.Equal(0.0, UmlsMatchScorer.LevenshteinSimilarity("abc", "xyz"), 5)
    End Sub

    <Fact>
    Public Sub Lev_returns_1_when_both_strings_are_empty()
        Assert.Equal(1.0, UmlsMatchScorer.LevenshteinSimilarity("", ""), 5)
    End Sub

    <Fact>
    Public Sub Lev_returns_0_when_one_string_is_empty()
        Assert.Equal(0.0, UmlsMatchScorer.LevenshteinSimilarity("", "foo"), 5)
        Assert.Equal(0.0, UmlsMatchScorer.LevenshteinSimilarity("foo", ""), 5)
    End Sub

    <Fact>
    Public Sub Lev_treats_null_as_empty()
        Assert.Equal(1.0, UmlsMatchScorer.LevenshteinSimilarity(Nothing, Nothing), 5)
        Assert.Equal(0.0, UmlsMatchScorer.LevenshteinSimilarity(Nothing, "foo"), 5)
    End Sub

    <Fact>
    Public Sub Lev_known_distance_one_substitution()
        ' "diabetes" -> "diabetis": 1 substitution, maxLen=8, sim = 1 - 1/8 = 0.875
        Assert.Equal(0.875, UmlsMatchScorer.LevenshteinSimilarity("diabetes", "diabetis"), 5)
    End Sub

    ' ============ signal 2: Jaccard containment ============

    <Fact>
    Public Sub Jaccard_short_query_fully_contained_in_longer_candidate_scores_1()
        ' Denominator is the query token count, so a 1-token query fully present in
        ' the candidate scores 1.0 (query fully covered).
        Assert.Equal(1.0, UmlsMatchScorer.JaccardContainment("diabetes", "type 2 diabetes mellitus"), 5)
    End Sub

    <Fact>
    Public Sub Jaccard_long_query_short_generic_candidate_scores_only_its_query_coverage()
        ' The generic-atom trap: a 1-token candidate present in a 4-token query
        ' covers only 1/4 of the query, so it scores 0.25 — NOT a perfect 1.0.
        ' tokens("12-lead ecg examination") = {12, lead, ecg, examination}.
        Assert.Equal(0.25, UmlsMatchScorer.JaccardContainment("12-lead ecg examination", "examination"), 5)
    End Sub

    <Fact>
    Public Sub QueryTokenCoverage_is_the_public_alias_for_query_containment()
        Assert.Equal(0.5, UmlsMatchScorer.QueryTokenCoverage("alpha beta", "beta gamma"), 5)
        Assert.Equal(1.0, UmlsMatchScorer.QueryTokenCoverage("diabetes", "type 2 diabetes mellitus"), 5)
    End Sub

    <Theory>
    <InlineData("diabetes mellitus", 2)>
    <InlineData("the x diabetes mellitus", 2)>
    <InlineData("12-lead ECG examination", 4)>
    <InlineData("", 0)>
    Public Sub SignificantTokenCount_drops_stopwords_and_short_tokens(input As String, expected As Integer)
        Assert.Equal(expected, UmlsMatchScorer.SignificantTokenCount(input))
    End Sub

    <Fact>
    Public Sub Jaccard_no_overlap_returns_0()
        Assert.Equal(0.0, UmlsMatchScorer.JaccardContainment("diabetes", "asthma"), 5)
    End Sub

    <Fact>
    Public Sub Jaccard_returns_0_when_either_token_set_is_empty()
        ' "a" is both a stopword and < 2 chars, so its token set is empty.
        Assert.Equal(0.0, UmlsMatchScorer.JaccardContainment("a", "diabetes"), 5)
        Assert.Equal(0.0, UmlsMatchScorer.JaccardContainment("diabetes", "a"), 5)
    End Sub

    <Fact>
    Public Sub Jaccard_drops_stopwords()
        ' "the diabetes" tokenises to {"diabetes"}; same as "diabetes".
        Assert.Equal(1.0, UmlsMatchScorer.JaccardContainment("the diabetes", "diabetes"), 5)
    End Sub

    <Fact>
    Public Sub Jaccard_drops_tokens_shorter_than_2_chars()
        ' "x diabetes" tokenises to {"diabetes"}; the single-char "x" is dropped.
        Assert.Equal(1.0, UmlsMatchScorer.JaccardContainment("x diabetes", "diabetes"), 5)
    End Sub

    <Fact>
    Public Sub Jaccard_partial_overlap()
        ' tokens("alpha beta") = {alpha, beta}; tokens("beta gamma") = {beta, gamma}
        ' intersection = {beta}, min = 2 -> 0.5
        Assert.Equal(0.5, UmlsMatchScorer.JaccardContainment("alpha beta", "beta gamma"), 5)
    End Sub

    <Fact>
    Public Sub Jaccard_splits_on_non_word_chars()
        ' "type-2:diabetes" and "type 2; diabetes" both tokenise to {"type","diabetes"}
        ' ("2" is < 2 chars, dropped).
        Assert.Equal(1.0, UmlsMatchScorer.JaccardContainment("type-2:diabetes", "type 2; diabetes"), 5)
    End Sub

    ' ============ signal 3: acronym contribution = acrBase + 0.3 * levSim ============

    <Fact>
    Public Sub Acronym_uppercase_query_matched_as_whole_word_gets_half_plus_lev_share()
        Dim lev As Double = 0.8
        Assert.Equal(0.5 + 0.3 * lev, UmlsMatchScorer.AcronymContribution("HER2", "HER2-positive", lev), 5)
    End Sub

    <Fact>
    Public Sub Acronym_lowercase_query_is_not_treated_as_acronym()
        Dim lev As Double = 0.8
        ' "her2" doesn't match the case-sensitive ^[A-Z0-9]{2,6}$ pattern.
        Assert.Equal(0.3 * lev, UmlsMatchScorer.AcronymContribution("her2", "her2-positive", lev), 5)
    End Sub

    <Fact>
    Public Sub Acronym_query_with_1_char_is_below_minimum_length()
        Assert.Equal(0.3 * 0.5, UmlsMatchScorer.AcronymContribution("A", "A patient", 0.5), 5)
    End Sub

    <Fact>
    Public Sub Acronym_query_with_7_chars_exceeds_maximum_length()
        Assert.Equal(0.3 * 0.5, UmlsMatchScorer.AcronymContribution("ABCDEFG", "ABCDEFG molecule", 0.5), 5)
    End Sub

    <Fact>
    Public Sub Acronym_query_not_present_in_candidate_does_not_get_base_bonus()
        Assert.Equal(0.3 * 0.5, UmlsMatchScorer.AcronymContribution("HER2", "Human ABC123 molecule", 0.5), 5)
    End Sub

    <Fact>
    Public Sub Acronym_query_must_appear_as_whole_word_not_substring()
        ' "HER2" embedded inside "HER2A" is not a whole-word match.
        Assert.Equal(0.3 * 0.5, UmlsMatchScorer.AcronymContribution("HER2", "HER2Apositive", 0.5), 5)
    End Sub

    <Fact>
    Public Sub Acronym_whole_word_match_is_case_insensitive_in_candidate()
        Assert.Equal(0.5 + 0.3 * 0.5, UmlsMatchScorer.AcronymContribution("HER2", "her2-positive disease", 0.5), 5)
    End Sub

    <Fact>
    Public Sub Acronym_digit_only_query_in_2_to_6_range_still_qualifies()
        Assert.Equal(0.5 + 0.3 * 0.4, UmlsMatchScorer.AcronymContribution("123", "study 123 cohort", 0.4), 5)
    End Sub

    <Fact>
    Public Sub Acronym_lev_share_is_applied_even_with_no_acronym_match()
        ' Formula adds 0.3 * levSim unconditionally; only acrBase is zero.
        Assert.Equal(0.3 * 0.7, UmlsMatchScorer.AcronymContribution("not-an-acronym", "anything", 0.7), 5)
    End Sub

    <Fact>
    Public Sub Acronym_handles_null_inputs()
        Assert.Equal(0.0, UmlsMatchScorer.AcronymContribution(Nothing, "foo", 0.0), 5)
        Assert.Equal(0.3 * 0.5, UmlsMatchScorer.AcronymContribution("HER2", Nothing, 0.5), 5)
    End Sub

    ' ============ composite Score = max(lev, jac, acr) ============

    <Fact>
    Public Sub Score_returns_max_of_the_three_signals()
        ' Jaccard dominates here: lev is low, jac = 1.0, acr is low.
        Dim s = _scorer.Score("diabetes", "type 2 diabetes mellitus")
        Assert.Equal(1.0, s, 5)
    End Sub

    <Fact>
    Public Sub Score_identical_strings_yields_1()
        Assert.Equal(1.0, _scorer.Score("Adult", "Adult"), 5)
    End Sub

    <Fact>
    Public Sub Score_for_null_query_is_0()
        Assert.Equal(0.0, _scorer.Score(Nothing, "diabetes"), 5)
    End Sub

    ' ============ PickBestMatch — selection + threshold + rounding ============

    <Fact>
    Public Sub PickBestMatch_returns_Unresolved_for_empty_list()
        Dim match = _scorer.PickBestMatch("diabetes", Array.Empty(Of UmlsCandidate)())
        Assert.False(match.IsResolved)
        Assert.Same(UmlsMatch.Unresolved, match)
    End Sub

    <Fact>
    Public Sub PickBestMatch_returns_Unresolved_for_null_list()
        Dim match = _scorer.PickBestMatch("diabetes", Nothing)
        Assert.False(match.IsResolved)
    End Sub

    <Fact>
    Public Sub PickBestMatch_returns_Unresolved_when_all_candidates_below_threshold()
        Dim candidates = New UmlsCandidate() {
            New UmlsCandidate("C001", "completely unrelated", "MSH"),
            New UmlsCandidate("C002", "another mismatch entirely", "SNOMEDCT_US")
        }
        Dim match = _scorer.PickBestMatch("diabetes", candidates)
        Assert.False(match.IsResolved)
        Assert.Equal(0.0, match.MatchScore)
        Assert.Equal("", match.ConceptCode)
    End Sub

    <Fact>
    Public Sub PickBestMatch_returns_resolved_match_when_one_candidate_above_threshold()
        Dim candidates = New UmlsCandidate() {
            New UmlsCandidate("C001", "Diabetes", "MSH")
        }
        Dim match = _scorer.PickBestMatch("diabetes", candidates)
        Assert.True(match.IsResolved)
        Assert.Equal("C001", match.ConceptCode)
        Assert.Equal("Diabetes", match.UmlsName)
        Assert.Equal("MSH", match.MatchSource)
        Assert.Equal(1.0, match.MatchScore, 3)
    End Sub

    <Fact>
    Public Sub PickBestMatch_picks_highest_scoring_candidate()
        ' C001 "Diabetic": lev = 1 - 2/8 = 0.75 (es -> ic), jac = 0, acr = 0.225 -> score 0.75.
        ' C002 "Diabetes": exact match -> score 1.0. C002 must win.
        Dim candidates = New UmlsCandidate() {
            New UmlsCandidate("C001", "Diabetic", "MSH"),
            New UmlsCandidate("C002", "Diabetes", "SNOMEDCT_US"),
            New UmlsCandidate("C003", "Asthma", "MSH")
        }
        Dim match = _scorer.PickBestMatch("diabetes", candidates)
        Assert.True(match.IsResolved)
        Assert.Equal("C002", match.ConceptCode)
        Assert.Equal(1.0, match.MatchScore, 3)
    End Sub

    <Fact>
    Public Sub PickBestMatch_rounds_score_to_3_decimal_places()
        ' "diabetes" vs "diabetis": lev = 1 - 1/8 = 0.875 (already 3dp)
        Dim candidates = New UmlsCandidate() {
            New UmlsCandidate("C001", "Diabetis", "MSH")
        }
        Dim match = _scorer.PickBestMatch("diabetes", candidates)
        Assert.Equal(0.875, match.MatchScore, 3)
    End Sub

    <Fact>
    Public Sub PickBestMatch_skips_null_candidates_in_list()
        Dim candidates = New UmlsCandidate() {
            Nothing,
            New UmlsCandidate("C001", "Diabetes", "MSH"),
            Nothing
        }
        Dim match = _scorer.PickBestMatch("diabetes", candidates)
        Assert.True(match.IsResolved)
        Assert.Equal("C001", match.ConceptCode)
    End Sub

    <Fact>
    Public Sub PickBestMatch_ties_resolve_to_first_candidate()
        ' Both score 1.0; first one wins (>, not >=).
        Dim candidates = New UmlsCandidate() {
            New UmlsCandidate("C001", "Diabetes", "MSH"),
            New UmlsCandidate("C002", "Diabetes", "SNOMEDCT_US")
        }
        Dim match = _scorer.PickBestMatch("diabetes", candidates)
        Assert.Equal("C001", match.ConceptCode)
    End Sub

    <Fact>
    Public Sub PickBestMatch_threshold_is_inclusive()
        ' Construct a candidate whose score lands exactly at the 0.45 boundary.
        ' "abcdefghij" (10 chars) vs "abcde00000" -> 5 substitutions, lev = 1 - 5/10 = 0.5.
        ' That is above 0.45 so should resolve.
        Dim candidates = New UmlsCandidate() {
            New UmlsCandidate("C001", "abcde00000", "MSH")
        }
        Dim match = _scorer.PickBestMatch("abcdefghij", candidates)
        Assert.True(match.IsResolved)
        Assert.Equal(0.5, match.MatchScore, 3)
    End Sub

End Class

Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Cli
Imports EligibilityProcessing.Data
Imports Xunit

' Integration tests for the local UMLS store (umls.* schema, V17) against a real
' Postgres (pgvector image — has pg_trgm). Cover the loader write path and
' PostgresUmlsClient's exact + trigram lookup + semantic types.
Public Class UmlsMetathesaurusIntegrationTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function PostgresUmlsClient_resolves_by_exact_and_fuzzy_with_semantic_types() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Dim atoms As New List(Of AtomRow) From {
            Atom("C0011860", "Diabetes Mellitus, Non-Insulin-Dependent", "MSH", "MH", True),
            Atom("C0011860", "Type 2 Diabetes Mellitus", "SNOMEDCT_US", "SY", False),
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }
        Await store.BulkLoadAtomsAsync(atoms, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0011860", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C0020615", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)

        Dim client As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3)

        ' Exact match on the normalized form.
        Dim exact = Await client.SearchAsync("hypoglycemia", CancellationToken.None)
        Assert.Contains(exact, Function(c) c.Ui = "C0020615")

        ' Fuzzy (typo) match via the pg_trgm arm.
        Dim fuzzy = Await client.SearchAsync("Hypoglycemiaa", CancellationToken.None)
        Assert.Contains(fuzzy, Function(c) c.Ui = "C0020615")

        ' A synonym atom resolves the concept even though it is not the preferred name.
        Dim synonym = Await client.SearchAsync("Type 2 Diabetes Mellitus", CancellationToken.None)
        Dim diabetes = synonym.FirstOrDefault(Function(c) c.Ui = "C0011860")
        Assert.NotNull(diabetes)
        ' The candidate carries the concept's PREFERRED name + source (from umls.concept),
        ' which the is_pref MSH atom won.
        Assert.Equal("Diabetes Mellitus, Non-Insulin-Dependent", diabetes.Name)
        Assert.Equal("MSH", diabetes.RootSource)

        Dim sts = Await client.GetSemanticTypesAsync("C0011860", CancellationToken.None)
        Assert.Contains("Disease or Syndrome", sts)

        ' Unknown concept -> no candidates (and never throws).
        Assert.Empty(Await client.SearchAsync("zzzqqq nonexistent term", CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function LoadUmls_from_rrf_filters_vocab_and_populates_all_tables() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Dim mrconso = WriteTempRrf({
            "C0020615|ENG|P|L|PF|S|Y|A||||SNOMEDCT_US|PT|271327008|Hypoglycemia|0|N||",
            "C0020615|FRE|P|L|PF|S|N|A||||SNOMEDCT_FR|PT|x|Hypoglycemie|0|N||",       ' non-English
            "C0011860|ENG|P|L|PF|S|Y|A||||FOO|PT|x|Diabetes|0|N||"                    ' vocab filtered out
        })
        Dim mrsty = WriteTempRrf({"C0020615|T047|A1|Disease or Syndrome|AT|"})
        Try
            Dim atomCount = Await store.BulkLoadAtomsAsync(
                    UmlsRrfReader.ReadAtoms(mrconso, {"SNOMEDCT_US"}), CancellationToken.None)
            Dim conceptCount = Await store.RebuildConceptTableAsync(CancellationToken.None)
            Dim styCount = Await store.LoadSemanticTypesAsync(
                    UmlsRrfReader.ReadSemanticTypes(mrsty), CancellationToken.None)

            Assert.Equal(1L, atomCount)     ' only the English SNOMEDCT_US atom survives
            Assert.Equal(1L, conceptCount)
            Assert.Equal(1L, styCount)
            Assert.Equal(1L, Await store.CountAsync("umls.atom", CancellationToken.None))
        Finally
            File.Delete(mrconso)
            File.Delete(mrsty)
        End Try
    End Function

    <SkippableFact>
    Public Async Function CoverageGuard_drops_generic_short_atoms_for_multiword_queries() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' A generic 1-token concept and a specific multi-token concept that both
        ' fuzzy-match the multi-word query "12-lead ECG examination".
        Await store.BulkLoadAtomsAsync({
            Atom("C0031809", "Examination", "SNOMEDCT_US", "PT", True),
            Atom("C2215949", "12-lead ECG examination", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)

        ' Guard ON (0.5): the generic "Examination" covers only 1/4 of the query
        ' and is dropped; the specific concept survives.
        Dim guarded As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3, minQueryCoverage:=0.5)
        Dim g = Await guarded.SearchAsync("12-lead ECG examination", CancellationToken.None)
        Assert.DoesNotContain(g, Function(c) c.Ui = "C0031809")
        Assert.Contains(g, Function(c) c.Ui = "C2215949")

        ' Guard OFF (0): the generic atom surfaces again (proving the guard, not the
        ' SQL, is what removed it).
        Dim unguarded As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3, minQueryCoverage:=0.0)
        Dim u = Await unguarded.SearchAsync("12-lead ECG examination", CancellationToken.None)
        Assert.Contains(u, Function(c) c.Ui = "C0031809")

        ' Single-token query is never guarded.
        Dim singleTok = Await guarded.SearchAsync("Examination", CancellationToken.None)
        Assert.Contains(singleTok, Function(c) c.Ui = "C0031809")
    End Function

    <SkippableFact>
    Public Async Function Fts_ranks_complete_match_first_and_code_guard_drops_wrong_number() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' Three near-identical test names differing by the distinctive number.
        Await store.BulkLoadAtomsAsync({
            Atom("C0000010", "10 Meter Walk Test", "SNOMEDCT_US", "PT", True),
            Atom("C0000004", "4 meter walk test", "SNOMEDCT_US", "PT", True),
            Atom("C0000011", "Timed 10 meter walk", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)

        Dim client As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3,
                                             minQueryCoverage:=0.5, requireQueryCodeMatch:=True)
        Dim r = Await client.SearchAsync("10 meter walk test", CancellationToken.None)

        ' Wrong-number candidate dropped by the code guard (4 conflicts with 10).
        Assert.DoesNotContain(r, Function(c) c.Ui = "C0000004")
        ' FTS ranks the complete 4-lexeme match first; the partial (has the right
        ' number) survives.
        Assert.Equal("C0000010", r.First().Ui)
        Assert.Contains(r, Function(c) c.Ui = "C0000011")
    End Function

    <SkippableFact>
    Public Async Function Fuzzy_arms_skip_overlong_and_pipe_panel_atoms() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Dim longSurvey = "During the last 7 days, on how many days did you walk for at least 10 minutes at a time"
        Await store.BulkLoadAtomsAsync({
            Atom("C_GOOD", "Timed 10 meter walk", "SNOMEDCT_US", "PT", True),
            Atom("C_LONG", longSurvey, "LNC", "PT", True),
            Atom("C_PANEL", "Walk test | Donor | Blood or Tissue | observations", "LNC", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)

        ' maxAtomLength 80 excludes the long survey item; the pipe-panel atom is
        ' excluded regardless of length. requireQueryCodeMatch off here so the test
        ' isolates the length/panel filters.
        Dim client As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3,
                                             minQueryCoverage:=0.0, requireQueryCodeMatch:=False, maxAtomLength:=80)
        Dim r = Await client.SearchAsync("10 meter walk", CancellationToken.None)

        Assert.Contains(r, Function(c) c.Ui = "C_GOOD")
        Assert.DoesNotContain(r, Function(c) c.Ui = "C_LONG")
        Assert.DoesNotContain(r, Function(c) c.Ui = "C_PANEL")
    End Function

    <SkippableFact>
    Public Async Function EnableTrigramFallback_gates_the_fuzzy_arm_on_resolution() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)

        ' A typo ("Hypoglycemiaa") has no exact match and no FTS whole-word overlap
        ' (different lexeme), so the fast path resolves nothing — it resolves ONLY
        ' via the pg_trgm fuzzy fallback.

        ' Disabled: the fast path resolves nothing and the fuzzy arm never runs.
        Dim gateOff As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3,
                                              enableTrigramFallback:=False)
        Assert.Empty(Await gateOff.SearchAsync("Hypoglycemiaa", CancellationToken.None))

        ' Enabled (the default): the fast path resolves nothing, so the score-aware
        ' gate fires the trigram arm and the typo resolves.
        Dim gateOn As New PostgresUmlsClient(store, candidateLimit:=15, trigramThreshold:=0.3,
                                             enableTrigramFallback:=True)
        Dim resolved = Await gateOn.SearchAsync("Hypoglycemiaa", CancellationToken.None)
        Assert.Contains(resolved, Function(c) c.Ui = "C0020615")

        ' An exact match resolves on the fast path, so it never needs the fuzzy arm —
        ' it resolves even with the fallback disabled (proving the gate suppresses
        ' only the trigram scan, not exact/FTS).
        Assert.Contains(Await gateOff.SearchAsync("hypoglycemia", CancellationToken.None),
                        Function(c) c.Ui = "C0020615")
    End Function

    ' ============ GetLoadCompletenessAsync ============
    '
    ' The guard that would have caught the May 2026 failure: umls.semantic_type
    ' held 100 rows / 49 CUIs against 1.27M concepts and nothing noticed for two
    ' months. Keys on CUI COVERAGE rather than raw row count - a raw-row rule
    ' would pass if a single CUI had a huge number of semantic types.

    <SkippableFact>
    Public Async Function LoadCompleteness_is_complete_when_every_concept_has_a_semantic_type() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0011860", "Diabetes Mellitus, Non-Insulin-Dependent", "MSH", "MH", True),
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0011860", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C0020615", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(2L, c.ConceptCount)
        Assert.Equal(2L, c.SemanticTypeCuiCount)
        Assert.Equal(2L, c.SemanticTypeRowCount)
        Assert.True(c.IsComplete)
    End Function

    ' Reproduces the production shape: concepts loaded, semantic types almost
    ' entirely absent.
    <SkippableFact>
    Public Async Function LoadCompleteness_is_incomplete_when_semantic_types_are_a_prefix() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0011860", "Diabetes Mellitus, Non-Insulin-Dependent", "MSH", "MH", True),
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True),
            Atom("C0000005", "Thyroxine-Binding Globulin", "MSH", "MH", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)
        ' Only one of the three concepts gets a semantic type.
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0000005", .Tui = "T116", .Sty = "Amino Acid, Peptide, or Protein"}
        }, CancellationToken.None)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(3L, c.ConceptCount)
        Assert.Equal(1L, c.SemanticTypeCuiCount)
        Assert.False(c.IsComplete)
        ' The message must carry both numbers - an operator seeing only "incomplete"
        ' cannot tell a near-miss from a total failure.
        Assert.Contains("3", c.Describe())
        Assert.Contains("1", c.Describe())
    End Function

    <SkippableFact>
    Public Async Function LoadCompleteness_is_incomplete_when_semantic_types_are_empty() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(1L, c.ConceptCount)
        Assert.Equal(0L, c.SemanticTypeCuiCount)
        Assert.False(c.IsComplete)
    End Function

    ' Degenerate case: an empty store is vacuously complete. Without this the CLI
    ' would refuse to run on a fresh database, where 0 of 0 is the correct answer.
    <SkippableFact>
    Public Async Function LoadCompleteness_is_complete_for_an_empty_store() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(0L, c.ConceptCount)
        Assert.True(c.IsComplete)
    End Function

    ' The load reports what it wrote, but the completeness check is what decides
    ' success. This asserts the two agree on a healthy load - the CLI's exit-code
    ' branch is exercised manually, since the CLI entry point needs a full host.
    <SkippableFact>
    Public Async Function LoadCompleteness_agrees_with_a_healthy_full_load() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0011860", "Diabetes Mellitus, Non-Insulin-Dependent", "MSH", "MH", True),
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)
        Dim written = Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0011860", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C0020615", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(2L, written)
        Assert.Equal(written, c.SemanticTypeRowCount)
        Assert.True(c.IsComplete)
    End Function

    ' Semantic types are loaded for EVERY CUI in MRSTY, including ones with no
    ' atom in the curated subset. public.eligibility holds 19,133 such CUIs -
    ' resolved by the REST backend from outside the six curated vocabularies - and
    ' a concept-filtered load would leave ~3% of the corpus permanently unfillable.
    <SkippableFact>
    Public Async Function LoadSemanticTypes_retains_cuis_with_no_atom_in_the_curated_subset() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0020615", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C9999999", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(1L, c.ConceptCount)
        Assert.Equal(2L, c.SemanticTypeCuiCount)   ' C9999999 is kept, not filtered
        ' Coverage is still judged against concepts, so a superset stays complete.
        Assert.True(c.IsComplete)
    End Function

    ' The case a count comparison would wave through. umls.semantic_type is a
    ' superset of umls.concept, so "sty_cuis >= concept_count" can hold while the
    ' concepts themselves are entirely uncovered. Only containment catches this.
    <SkippableFact>
    Public Async Function LoadCompleteness_is_incomplete_when_semantic_types_outnumber_but_miss_the_concepts() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.BulkLoadAtomsAsync({
            Atom("C0020615", "Hypoglycemia", "SNOMEDCT_US", "PT", True)
        }, CancellationToken.None)
        Await store.RebuildConceptTableAsync(CancellationToken.None)
        ' Three semantic-type CUIs against one concept - but none of them IS the
        ' concept, so coverage is zero despite the counts looking healthy.
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C9999997", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C9999998", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C9999999", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(1L, c.ConceptCount)
        Assert.Equal(3L, c.SemanticTypeCuiCount)      ' outnumbers concepts...
        Assert.Equal(1L, c.ConceptsWithoutSemanticType)  ' ...but covers none of them
        Assert.False(c.IsComplete)
    End Function

    ' The load no longer depends on concepts existing first. Guards the ordering
    ' hazard that is the leading explanation for the original May 2026 failure:
    ' under the old concept-filtered INSERT, running this step before the concept
    ' rebuild silently loaded almost nothing.
    <SkippableFact>
    Public Async Function LoadSemanticTypes_does_not_require_concepts_to_exist_first() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' No atoms, no concepts - just semantic types.
        Dim written = Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0020615", .Tui = "T047", .Sty = "Disease or Syndrome"},
            New SemanticTypeRow With {.Cui = "C0011860", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)

        Assert.Equal(2L, written)
    End Function

    Private Shared Function Atom(cui As String, str As String, sab As String, tty As String, pref As Boolean) As AtomRow
        Return New AtomRow With {
            .Cui = cui, .Str = str, .StrNorm = UmlsMetathesaurusStore.NormalizeConcept(str),
            .Sab = sab, .Tty = tty, .IsPref = pref}
    End Function

    Private Shared Function WriteTempRrf(lines As IEnumerable(Of String)) As String
        Dim tempPath = Path.GetTempFileName()
        File.WriteAllLines(tempPath, lines)
        Return tempPath
    End Function

End Class

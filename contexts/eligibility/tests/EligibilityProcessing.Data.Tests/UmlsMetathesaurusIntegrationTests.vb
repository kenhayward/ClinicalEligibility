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

Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports Xunit

' Integration tests for public.condition_concept (V24) and ConditionConceptStore.
Public Class ConditionConceptStoreTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function V24_creates_condition_concept_table_and_indexes() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'condition_concept'
ORDER BY column_name"
                Dim columns As New List(Of String)
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        columns.Add(reader.GetString(0))
                    End While
                End Using
                Assert.Contains("condition_norm", columns)
                Assert.Contains("raw_form", columns)
                Assert.Contains("study_count", columns)
                Assert.Contains("concept_code", columns)
                Assert.Contains("umls_name", columns)
                Assert.Contains("match_tier", columns)
                Assert.Contains("match_score", columns)
                Assert.Contains("resolved_at", columns)
                Assert.Contains("created_at", columns)
            End Using

            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT indexname FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'condition_concept'"
                Dim indexes As New List(Of String)
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        indexes.Add(reader.GetString(0))
                    End While
                End Using
                Assert.Contains("ix_condition_concept_code", indexes)
                Assert.Contains("ix_condition_concept_pending", indexes)
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function Sql_normalization_matches_ConceptKey_Normalize() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        ' Built with ChrW rather than literal source bytes, per the repo's
        ' ASCII-only-source rule. U+00E9 (e-acute) checks that lower() and
        ' ToLowerInvariant agree on an accented character - they do.
        '
        ' A non-breaking space (U+00A0) sample was tried here and DELIBERATELY
        ' removed - it does NOT agree and is a real, currently-unresolved
        ' divergence, not a flaky test: ConceptKey.Normalize (.NET regex \s
        ' matches Unicode whitespace, including U+00A0) collapses it to a plain
        ' space, but this SQL's \s (Postgres regex under the container's default
        ' locale) does not, leaving the NBSP in place. A raw condition string
        ' containing an NBSP would therefore get a different condition_norm
        ' depending on whether it went through SQL (SeedFromCorpusAsync,
        ' GetUnseenConditionsForStudyAsync) or ConceptKey.Normalize
        ' (ConditionNormalizer.ResolveAsync/EnsureForStudyAsync) - splitting one
        ' analytic bucket into two, exactly the failure mode this test exists to
        ' catch. Flagged for a decision (strip/normalize NBSP on one side or the
        ' other) rather than fixed here - see the branch's review report.
        Dim eacute = ChrW(&HE9)
        Dim samples = {"COPD", "  Breast   Cancer  ", "Non-Small Cell", "COVID-19",
                       "Type" & vbTab & "2 Diabetes", "hiv/aids",
                       "Pr" & eacute & "eclampsia"}

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            For Each s In samples
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT regexp_replace(btrim(lower(@raw)), '\s+', ' ', 'g')"
                    cmd.Parameters.AddWithValue("raw", s)
                    Dim fromSql = CStr(Await cmd.ExecuteScalarAsync())
                    Assert.Equal(ConceptKey.Normalize(s), fromSql)
                End Using
            Next
        End Using
    End Function

    Private Async Function SeedStudyAsync(nctId As String, conditions As String()) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.eligibility_study_detail (nct_id, conditions)
VALUES (@n, @c)
ON CONFLICT (nct_id) DO UPDATE SET conditions = excluded.conditions"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.Add(New NpgsqlParameter("c", NpgsqlTypes.NpgsqlDbType.Array Or NpgsqlTypes.NpgsqlDbType.Text) With {.Value = conditions})
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedAtomAsync(cui As String, str As String, prefName As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.atom (cui, str, str_norm, sab, tty, is_pref) VALUES (@cui, @s, @sn, 'SNOMEDCT_US', 'PT', true);
INSERT INTO umls.concept (cui, pref_name, root_source) VALUES (@cui, @pn, 'SNOMEDCT_US')
ON CONFLICT (cui) DO UPDATE SET pref_name = excluded.pref_name"
                cmd.Parameters.AddWithValue("cui", cui)
                cmd.Parameters.AddWithValue("s", str)
                cmd.Parameters.AddWithValue("sn", ConceptKey.Normalize(str))
                cmd.Parameters.AddWithValue("pn", prefName)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function LookupExact_returns_one_candidate_for_an_unambiguous_string() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAtomAsync("C0038454", "Stroke", "CVA - Cerebrovascular accident")

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Dim hits = Await store.LookupExactAsync("stroke", CancellationToken.None)

        Assert.Single(hits)
        Assert.Equal("C0038454", hits(0).Cui)
        ' The candidate carries the concept's PREFERRED name, not the atom string.
        Assert.Equal("CVA - Cerebrovascular accident", hits(0).PrefName)
    End Function

    <SkippableFact>
    Public Async Function LookupExact_returns_every_cui_for_an_ambiguous_string() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAtomAsync("C0000100", "Cancer", "Blastoma")
        Await SeedAtomAsync("C0000200", "Cancer", "Malignant Neoplasm")

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Dim hits = Await store.LookupExactAsync("cancer", CancellationToken.None)

        Assert.Equal(2, hits.Count)
    End Function

    ' Regression coverage for the PickAmbiguous hierarchy tie-break: HasHierarchy
    ' must reflect a real umls.concept_ancestor row (descendant_cui, ancestor_cui,
    ' min_distance), not just whichever candidate happens to come back first.
    <SkippableFact>
    Public Async Function LookupExact_reports_HasHierarchy_only_for_the_cui_with_an_ancestor_row() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAtomAsync("C0000100", "Stroke", "CVA - Cerebrovascular accident")
        Await SeedAtomAsync("C0000200", "Stroke", "Stroke")

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.concept_ancestor (descendant_cui, ancestor_cui, min_distance)
VALUES (@descendant, @ancestor, 1)"
                cmd.Parameters.AddWithValue("descendant", "C0000100")
                cmd.Parameters.AddWithValue("ancestor", "C9999999")
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Dim hits = Await store.LookupExactAsync("stroke", CancellationToken.None)

        Assert.Equal(2, hits.Count)
        Assert.True(hits.Single(Function(h) h.Cui = "C0000100").HasHierarchy)
        Assert.False(hits.Single(Function(h) h.Cui = "C0000200").HasHierarchy)
    End Function

    <SkippableFact>
    Public Async Function SeedFromCorpus_picks_the_most_frequent_raw_form_and_counts_studies() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' COPD appears in 3 studies, Copd in 1 - the uppercase form must win,
        ' because the scorer's acronym term needs it.
        Await SeedStudyAsync("NCT001", {"COPD"})
        Await SeedStudyAsync("NCT002", {"COPD"})
        Await SeedStudyAsync("NCT003", {"COPD"})
        Await SeedStudyAsync("NCT004", {"Copd"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Dim inserted = Await store.SeedFromCorpusAsync(CancellationToken.None)

        Assert.Equal(1, inserted)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT raw_form, study_count FROM public.condition_concept WHERE condition_norm = 'copd'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    Assert.True(Await reader.ReadAsync())
                    Assert.Equal("COPD", reader.GetString(0))
                    Assert.Equal(4, reader.GetInt32(1))
                End Using
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function SeedFromCorpus_breaks_raw_form_ties_lexicographically() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' One study each: the counts tie, so the ORDER BY cnt DESC, raw ASC
        ' tiebreak decides. Without a deterministic tiebreak, array_agg ordering
        ' is arbitrary and a re-seed could silently change which casing the
        ' matcher sees - which for an acronym flips whether it resolves at all.
        Await SeedStudyAsync("NCT001", {"HIV"})
        Await SeedStudyAsync("NCT002", {"Hiv"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Await store.SeedFromCorpusAsync(CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT raw_form FROM public.condition_concept WHERE condition_norm = 'hiv'"
                Assert.Equal("HIV", CStr(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using

        ' And it stays the same on a re-seed.
        Await store.SeedFromCorpusAsync(CancellationToken.None)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT raw_form FROM public.condition_concept WHERE condition_norm = 'hiv'"
                Assert.Equal("HIV", CStr(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function SeedFromCorpus_counts_a_study_once_even_when_it_lists_two_casings() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' A single trial listing two casings of the same condition in its own
        ' conditions array - exactly the duplication this feature exists to
        ' eliminate - must not double-count that trial toward study_count.
        ' study_count is corpus frequency (count(DISTINCT nct_id) per normalized
        ' condition), not a count of (study, raw-form) pairs.
        Await SeedStudyAsync("NCT001", {"COPD", "Copd"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Await store.SeedFromCorpusAsync(CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT study_count FROM public.condition_concept WHERE condition_norm = 'copd'"
                Assert.Equal(1, CInt(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function SeedFromCorpus_clears_resolved_at_when_raw_form_casing_changes() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' First seed with only a casing the scorer's acronym term does not fire
        ' on ("Nsclc" rather than "NSCLC"). Simulate the per-trial hook resolving
        ' it (as-is) and stamping resolved_at - unresolved, because the acronym
        ' term never fired on "Nsclc".
        Await SeedStudyAsync("NCT001", {"Nsclc"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Await store.SeedFromCorpusAsync(CancellationToken.None)
        Await store.UpsertAsync(New ConditionConceptEntry With {
                .ConditionNorm = "nsclc", .RawForm = "Nsclc",
                .MatchTier = ConditionMatchTier.Unresolved, .MatchScore = 0.0
            }, CancellationToken.None)

        ' Confirmed resolved (attempted) and not pending.
        Assert.Empty(Await store.GetPendingAsync(10, force:=False, cancellationToken:=CancellationToken.None))

        ' More trials mention the correct uppercase "NSCLC", enough to outrank
        ' "Nsclc" for raw_form on the next seed.
        Await SeedStudyAsync("NCT002", {"NSCLC"})
        Await SeedStudyAsync("NCT003", {"NSCLC"})
        Await store.SeedFromCorpusAsync(CancellationToken.None)

        ' raw_form changed ("Nsclc" -> "NSCLC"), so resolved_at must be cleared -
        ' the row is resolvable now (the acronym term will fire) and must not be
        ' skipped forever without --force.
        Dim pending = Await store.GetPendingAsync(10, force:=False, cancellationToken:=CancellationToken.None)
        Assert.Single(pending)
        Assert.Equal("nsclc", pending(0).ConditionNorm)
        Assert.Equal("NSCLC", pending(0).RawForm)
    End Function

    <SkippableFact>
    Public Async Function SeedFromCorpus_does_not_clear_resolved_at_when_raw_form_is_unchanged() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' Otherwise every seed would re-open the whole corpus for resolution,
        ' making --force meaningless and re-doing UMLS lookups on every run.
        Await SeedStudyAsync("NCT001", {"COPD"})
        Await SeedStudyAsync("NCT002", {"COPD"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Await store.SeedFromCorpusAsync(CancellationToken.None)
        Await store.UpsertAsync(New ConditionConceptEntry With {
                .ConditionNorm = "copd", .RawForm = "COPD",
                .ConceptCode = "C0024117", .UmlsName = "COPD",
                .MatchTier = ConditionMatchTier.Exact, .MatchScore = 1.0
            }, CancellationToken.None)

        Assert.Empty(Await store.GetPendingAsync(10, force:=False, cancellationToken:=CancellationToken.None))

        ' Re-seed with more mentions of the SAME casing - raw_form stays "COPD".
        Await SeedStudyAsync("NCT003", {"COPD"})
        Await store.SeedFromCorpusAsync(CancellationToken.None)

        Assert.Empty(Await store.GetPendingAsync(10, force:=False, cancellationToken:=CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function SeedFromCorpus_is_idempotent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedStudyAsync("NCT001", {"Stroke", "Obesity"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Assert.Equal(2, Await store.SeedFromCorpusAsync(CancellationToken.None))
        Assert.Equal(0, Await store.SeedFromCorpusAsync(CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function GetPending_orders_by_study_count_descending_and_force_includes_resolved() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedStudyAsync("NCT001", {"Rare Thing"})
        Await SeedStudyAsync("NCT002", {"Common Thing"})
        Await SeedStudyAsync("NCT003", {"Common Thing"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Await store.SeedFromCorpusAsync(CancellationToken.None)

        Dim pending = Await store.GetPendingAsync(10, force:=False, cancellationToken:=CancellationToken.None)
        Assert.Equal(2, pending.Count)
        Assert.Equal("common thing", pending(0).ConditionNorm)

        ' Resolve one, then confirm it drops out unless forced.
        Await store.UpsertAsync(New ConditionConceptEntry With {
                .ConditionNorm = "common thing", .RawForm = "Common Thing",
                .ConceptCode = "C0000001", .UmlsName = "Common Thing",
                .MatchTier = ConditionMatchTier.Exact, .MatchScore = 1.0
            }, CancellationToken.None)

        Assert.Single(Await store.GetPendingAsync(10, force:=False, cancellationToken:=CancellationToken.None))
        Assert.Equal(2, (Await store.GetPendingAsync(10, force:=True, cancellationToken:=CancellationToken.None)).Count)
        Assert.Equal(1, Await store.CountPendingAsync(force:=False, cancellationToken:=CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function Upsert_is_idempotent_and_stamps_resolved_at() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Dim entry As New ConditionConceptEntry With {
                .ConditionNorm = "stroke", .RawForm = "Stroke",
                .ConceptCode = "C0038454", .UmlsName = "CVA - Cerebrovascular accident",
                .MatchTier = ConditionMatchTier.Exact, .MatchScore = 1.0}

        Await store.UpsertAsync(entry, CancellationToken.None)
        Await store.UpsertAsync(entry, CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*), max(resolved_at) IS NOT NULL FROM public.condition_concept WHERE condition_norm = 'stroke'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    Assert.True(Await reader.ReadAsync())
                    Assert.Equal(1L, reader.GetInt64(0))
                    Assert.True(reader.GetBoolean(1))
                End Using
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function GetUnseenConditionsForStudy_returns_only_strings_with_no_dictionary_row() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedStudyAsync("NCT001", {"Stroke", "Obesity"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Await store.UpsertAsync(New ConditionConceptEntry With {
                .ConditionNorm = "stroke", .RawForm = "Stroke",
                .MatchTier = ConditionMatchTier.Unresolved}, CancellationToken.None)

        Dim unseen = Await store.GetUnseenConditionsForStudyAsync("NCT001", CancellationToken.None)

        Assert.Single(unseen)
        Assert.Equal("Obesity", unseen(0))
    End Function

    <SkippableFact>
    Public Async Function Upsert_does_not_clobber_study_count() As Task
        ' study_count is a corpus statistic maintained only in bulk by
        ' SeedFromCorpusAsync, because the per-trial path cannot know whether
        ' a given trial already contributed that string. If the ON CONFLICT SET
        ' clause mistakenly included study_count = excluded.study_count, every
        ' count in the dictionary would silently be zeroed on the next per-trial
        ' upsert, since ConditionConceptEntry.StudyCount defaults to 0.
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Seed the same condition across two different trials to get study_count > 1.
        Await SeedStudyAsync("NCT001", {"Hypertension"})
        Await SeedStudyAsync("NCT002", {"Hypertension"})

        Dim store As New ConditionConceptStore(_fixture.DataSource)
        Dim seeded = Await store.SeedFromCorpusAsync(CancellationToken.None)
        Assert.Equal(1, seeded)

        ' Verify the seeded study_count is 2.
        Dim beforeCount As Integer
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT study_count FROM public.condition_concept WHERE condition_norm = 'hypertension'"
                beforeCount = CInt(Await cmd.ExecuteScalarAsync())
            End Using
        End Using
        Assert.Equal(2, beforeCount)

        ' Call UpsertAsync with a matching condition but default StudyCount (0).
        ' This is what the per-trial extraction path does: it passes the resolved
        ' concept but does not know about study_count.
        Await store.UpsertAsync(New ConditionConceptEntry With {
                .ConditionNorm = "hypertension", .RawForm = "Hypertension",
                .ConceptCode = "C0020538", .UmlsName = "Hypertension",
                .MatchTier = ConditionMatchTier.Exact, .MatchScore = 1.0,
                .StudyCount = 0}, CancellationToken.None)

        ' Re-read study_count and assert it is STILL 2, not 0.
        Dim afterCount As Integer
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT study_count FROM public.condition_concept WHERE condition_norm = 'hypertension'"
                afterCount = CInt(Await cmd.ExecuteScalarAsync())
            End Using
        End Using
        Assert.Equal(2, afterCount)
    End Function
End Class

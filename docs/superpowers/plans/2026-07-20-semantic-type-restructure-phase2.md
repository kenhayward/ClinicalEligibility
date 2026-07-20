# Semantic Type Restructure - Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every resolved row in `public.eligibility` an unambiguous, queryable set of semantic types, and one canonical display string, without changing what the UI shows.

**Architecture:** A new `semantic_type_tuis text[]` column becomes the analytic representation; the existing `semantic_type` text column stays as a display string but is *derived from* the array so the two cannot drift. TUIs are stable across UMLS releases where names are not. The write path stops passing `", "`-joined strings between stages. A batched CLI backfill fills 3,985,113 existing rows.

**Tech Stack:** .NET 8, VB.NET, Npgsql, PostgreSQL 16 (GIN index on `text[]`), xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-07-20-semantic-type-repair-design.md` (phase 2 section)

## Measured baseline (2026-07-20, after phase 1)

These numbers are current and several supersede the spec's predictions. Re-measure before trusting them if time has passed.

| Metric | Value |
|---|---|
| `public.eligibility` rows | 4,439,480 |
| Resolved rows (non-empty `concept_code`) | 3,985,113 |
| **Unresolved rows** (get NULL array) | 454,367 |
| Distinct CUIs in corpus | 132,243 |
| **CUIs with no semantic type available** | **0** (was 19,133 before phase 1's widening) |
| Rows with NULL `semantic_type` to fill | 3,479,090 |
| Legacy rows with a `semantic_type` | 506,023 |
| - already canonical | 494,264 (97.7%) |
| - **would change under canonicalisation** | **11,759 (2.3%)** |
| `umls.semantic_type` | 3,876,942 rows / 3,530,466 CUIs |
| Distinct TUIs / STYs | **132 / 132, perfect bijection** |
| `(cui, tui)` duplicates | 0 |
| NULL or empty `tui` | 0 |

**Consequence worth internalising:** every resolved row needs its array populated, so the backfill touches all 3,985,113 rows regardless. The spec's decision to also rewrite legacy strings therefore costs nothing extra - it is the same UPDATE.

## Corrections to the spec

Record these in the spec as part of Task 6; do not silently diverge.

1. **The PK-change rationale in the spec is wrong.** It says `DISTINCT ON (cui, sty)` discards a conflicting TUI arbitrarily. With a 132/132 bijection nothing is discarded. The change is still worth making, for a different reason: `--semantic-types-only` (phase 1) is *additive*, so if a future UMLS release renames a semantic type, `ON CONFLICT (cui, sty)` would insert a second row for the same `(cui, tui)`. A `(cui, tui)` key makes the additive load idempotent against renames.
2. **The "delete NULL tui rows before applying NOT NULL" step is unnecessary** - there are zero. Keep it as a defensive no-op; it costs one statement and protects against a future partial load.
3. **The spec expects unmapped CUIs to be counted and reported.** That count is now zero. Keep the reporting - it is how a regression would surface - but expect it to read 0.
4. **`concept_normalization` does not need a TUI column.** It already stores `concept_code`; TUIs are derivable from it. This is simpler than the spec implies.

## Global Constraints

- Branch `feat/semantic-type-restructure` exists off `origin/main`. Never commit to `main`.
- **ASCII only** in every authored file. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8.
- **Never write files with PowerShell `Set-Content`/`Out-File`** (adds a BOM). Use Edit/Write.
- **Never pass a multi-line commit message with double quotes through a PowerShell here-string.** Use `git commit -F <file>`.
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`.
- **This phase adds a migration.** Therefore: bump **MINOR** and reset build to 0 (0.1.35 -> 0.2.0), and update `docs/specs/database_schema.md` **in the same change** - that is a project rule, not a nicety.
- **The UI must not change in this phase.** `semantic_type` stays populated and the existing exact-match filter keeps working. Phase 3 changes the UX.
- Bulk statements now run under `Postgres:OutputCommandTimeoutSeconds` (default 600, added in phase 1). The backfill's batches must still be sized to stay well inside it.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Data/Migrations/V22__semantic_type_tuis.sql` | Column, GIN index, PK change, dim table | 1 |
| `docs/specs/database_schema.md` | Schema doc for V22 | 1 |
| `Core/ResolvedRecord.vb` | Carry TUIs; derive display string | 2 |
| `Core/DuplicateConceptMerger.vb` | Merge on TUIs, not the split string | 2 |
| `Core/PipelineOrchestrator.vb` | Remove the one-element-list shim | 3 |
| `Core/UmlsNormalizeJob.vb`, `Cli/Program.vb` | The other two join sites | 3 |
| `Data/PostgresGateway.vb` | Persist both columns | 3 |
| `Cli/Program.vb` + `Data/UmlsMetathesaurusStore.vb` | `backfill-semantic-types` command | 4 |
| `version.json`, spec | Version, spec corrections | 5 |

---

### Task 1: Migration V22

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V22__semantic_type_tuis.sql`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb` (add `V22__semantic_type_tuis` to `MigrationNames`)
- Modify: `docs/specs/database_schema.md`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Produces: `public.eligibility.semantic_type_tuis text[]` (GIN indexed); `umls.semantic_type` PK `(cui, tui)` with `tui NOT NULL`; `umls.semantic_type_dim (tui text PRIMARY KEY, sty text NOT NULL)`.

- [ ] **Step 1: Write the failing test**

Append to `PostgresGatewayIntegrationTests.vb` before the final `End Class`:

```vb
    ' ============ V22 schema ============

    <SkippableFact>
    Public Async Function V22_adds_semantic_type_tuis_and_dim_table() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT (SELECT count(*) FROM information_schema.columns
         WHERE table_schema='public' AND table_name='eligibility'
           AND column_name='semantic_type_tuis' AND data_type='ARRAY')            AS has_tuis_col,
       (SELECT count(*) FROM information_schema.tables
         WHERE table_schema='umls' AND table_name='semantic_type_dim')            AS has_dim,
       (SELECT count(*) FROM pg_indexes
         WHERE schemaname='public' AND indexname='ix_eligibility_semantic_type_tuis') AS has_gin,
       (SELECT count(*) FROM information_schema.columns
         WHERE table_schema='umls' AND table_name='semantic_type'
           AND column_name='tui' AND is_nullable='NO')                            AS tui_not_null"
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.Equal(1, reader.GetInt32(0))
                    Assert.Equal(1, reader.GetInt32(1))
                    Assert.Equal(1, reader.GetInt32(2))
                    Assert.Equal(1, reader.GetInt32(3))
                End Using
            End Using
        End Using
    End Function

    ' The dim table is populated by the migration itself from existing data, so a
    ' vocabulary reload is not required to make it usable.
    <SkippableFact>
    Public Async Function V22_dim_table_is_populated_from_existing_semantic_types() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.semantic_type (cui, tui, sty) VALUES ('C0011860','T047','Disease or Syndrome')
  ON CONFLICT DO NOTHING;
INSERT INTO umls.semantic_type_dim (tui, sty)
SELECT DISTINCT tui, sty FROM umls.semantic_type ON CONFLICT (tui) DO NOTHING;"
                Await cmd.ExecuteNonQueryAsync()
            End Using
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT sty FROM umls.semantic_type_dim WHERE tui = 'T047'"
                Assert.Equal("Disease or Syndrome", CStr(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function
```

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~V22_"
```

Expected: FAIL - the columns and table do not exist (assertions return 0).

- [ ] **Step 3: Write the migration**

Create `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V22__semantic_type_tuis.sql`:

```sql
-- V22: semantic types as data, not as a display string.
--
-- public.eligibility.semantic_type is a ", "-joined string. Several UMLS
-- semantic type names contain commas ("Amino Acid, Peptide, or Protein"), so the
-- value cannot be parsed back into its parts, and the Results filter matches the
-- whole string - under-reporting "Pharmacologic Substance" by 68% (6,389 rows
-- matched of 19,674 that carry the type).
--
-- semantic_type_tuis carries the TUIs. TUIs are stable across UMLS releases;
-- names get reworded. semantic_type stays as the display string, derived from
-- the array, so the two cannot drift.
--
-- Idempotent - ADD COLUMN / CREATE ... IF NOT EXISTS, so re-running
-- EnsureSchemaAsync is safe.

ALTER TABLE public.eligibility
    ADD COLUMN IF NOT EXISTS semantic_type_tuis text[];

-- GIN supports the containment queries phase 3 needs (semantic_type_tuis && ARRAY[...]).
CREATE INDEX IF NOT EXISTS ix_eligibility_semantic_type_tuis
    ON public.eligibility USING gin (semantic_type_tuis);

-- umls.semantic_type: key on (cui, tui) rather than (cui, sty).
--
-- Not because the current data is ambiguous - TUI and STY are a perfect 132/132
-- bijection, so nothing is being discarded today. The reason is that
-- load-umls --semantic-types-only (phase 1) is ADDITIVE: if a future UMLS
-- release renames a semantic type, ON CONFLICT (cui, sty) would insert a second
-- row for the same (cui, tui). Keying on TUI makes the additive load idempotent
-- against renames.
--
-- Defensive no-op today: there are zero NULL TUIs, but a future partial load
-- could introduce some and ALTER ... SET NOT NULL would then fail.
DELETE FROM umls.semantic_type WHERE tui IS NULL OR tui = '';

ALTER TABLE umls.semantic_type ALTER COLUMN tui SET NOT NULL;

ALTER TABLE umls.semantic_type DROP CONSTRAINT IF EXISTS semantic_type_pkey;
ALTER TABLE umls.semantic_type ADD PRIMARY KEY (cui, tui);

-- sty is no longer part of the key, so keep it indexed for the dim rebuild and
-- for any name-based lookup.
CREATE INDEX IF NOT EXISTS ix_umls_semantic_type_sty ON umls.semantic_type (sty);

-- ~132 rows. Lets a TUI resolve to a name without touching the 3.9M-row table.
CREATE TABLE IF NOT EXISTS umls.semantic_type_dim (
    tui text PRIMARY KEY,
    sty text NOT NULL
);

-- Populated by the migration from existing data, so no vocabulary reload is
-- needed to make it usable. The loader refreshes it on future loads.
INSERT INTO umls.semantic_type_dim (tui, sty)
SELECT DISTINCT tui, sty FROM umls.semantic_type
ON CONFLICT (tui) DO NOTHING;
```

- [ ] **Step 4: Register the migration**

In `PostgresGateway.vb`, find `MigrationNames` (the ordered list ending `V21__signing_credentials`) and append `"V22__semantic_type_tuis"`. Follow the exact string format of the existing entries.

Note `VersionWebTests` asserts `SchemaVersion` equals `MigrationNames.Count`, so this changes that count automatically - no test edit needed for it.

- [ ] **Step 5: Run to verify pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~V22_"
```

Expected: 2 tests PASS.

- [ ] **Step 6: Update the schema doc**

In `docs/specs/database_schema.md`: add `semantic_type_tuis text[]` to the `public.eligibility` table section, add the `ix_eligibility_semantic_type_tuis` index, document the new `umls.semantic_type_dim` table, amend `umls.semantic_type`'s primary key to `(cui, tui)` and its `tui` to NOT NULL **in place** (the doc describes the effective post-migration schema, not a changelog), and add V22 to the migration-history table.

- [ ] **Step 7: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V22__semantic_type_tuis.sql contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb docs/specs/database_schema.md contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb
git commit -m "Add V22: semantic_type_tuis array, TUI-keyed semantic types, dim table"
```

---

### Task 2: `ResolvedRecord` carries TUIs

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/ResolvedRecord.vb:35-45`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/DuplicateConceptMerger.vb:39,47,88-96`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ResolvedRecordTests.vb`, `DuplicateConceptMergerTests.vb`

**Interfaces:**
- Produces: `ResolvedRecord.SemanticTypeTuis As IReadOnlyList(Of String)` alongside the existing `SemanticType As String`. New constructor overload taking `semanticTypes As IReadOnlyList(Of SemanticTypeAssignment)`.
- New type `SemanticTypeAssignment` with `Tui As String`, `Sty As String` - so a stage never has to re-derive one from the other.

**Why a pair type rather than two lists:** the display string must be ordered by `sty` while the array is keyed by `tui`. Passing them as separate lists invites them drifting out of alignment; a pair makes that impossible.

- [ ] **Step 1: Write the failing tests**

`ResolvedRecordTests.vb:62-83` currently format-locks the `", "` join. Replace that region with:

```vb
    <Fact>
    Public Sub SemanticType_string_is_sorted_by_name_and_comma_joined()
        Dim r = MakeRecord({
            New SemanticTypeAssignment("T041", "Mental Process"),
            New SemanticTypeAssignment("T047", "Disease or Syndrome")})
        ' Sorted by STY, not by input order - one canonical form corpus-wide.
        Assert.Equal("Disease or Syndrome, Mental Process", r.SemanticType)
    End Sub

    <Fact>
    Public Sub SemanticType_tuis_preserve_every_assignment()
        Dim r = MakeRecord({
            New SemanticTypeAssignment("T041", "Mental Process"),
            New SemanticTypeAssignment("T047", "Disease or Syndrome")})
        Assert.Equal({"T041", "T047"}, r.SemanticTypeTuis.OrderBy(Function(t) t).ToArray())
    End Sub

    ' The case the joined string cannot express: an STY name containing ", ".
    ' Splitting "Amino Acid, Peptide, or Protein, Enzyme" on ", " yields four
    ' fragments, none of which is a real semantic type.
    <Fact>
    Public Sub SemanticType_tuis_survive_names_containing_commas()
        Dim r = MakeRecord({
            New SemanticTypeAssignment("T116", "Amino Acid, Peptide, or Protein"),
            New SemanticTypeAssignment("T126", "Enzyme")})
        Assert.Equal(2, r.SemanticTypeTuis.Count)
        Assert.Contains("T116", r.SemanticTypeTuis)
        Assert.Contains("T126", r.SemanticTypeTuis)
        Assert.Equal("Amino Acid, Peptide, or Protein, Enzyme", r.SemanticType)
    End Sub

    <Fact>
    Public Sub SemanticType_is_empty_when_no_assignments()
        Dim r = MakeRecord(Array.Empty(Of SemanticTypeAssignment)())
        Assert.Equal("", r.SemanticType)
        Assert.Empty(r.SemanticTypeTuis)
    End Sub
```

Add a `MakeRecord` helper to that test class mirroring however the existing tests construct a `ResolvedRecord` (read the top of the file and follow it), taking `IReadOnlyList(Of SemanticTypeAssignment)`.

In `DuplicateConceptMergerTests.vb`, add:

```vb
    ' Merge identity is the TUI set, not the joined string. Two records for the
    ' same CUI whose legacy strings were ordered differently are the same record
    ' and must merge - previously they did not.
    <Fact>
    Public Sub Merges_records_whose_semantic_types_differ_only_in_order()
        Dim a = MakeResolved("NCT1", "C0000005", "criterion", {
            New SemanticTypeAssignment("T109", "Organic Chemical"),
            New SemanticTypeAssignment("T121", "Pharmacologic Substance")})
        Dim b = MakeResolved("NCT1", "C0000005", "criterion", {
            New SemanticTypeAssignment("T121", "Pharmacologic Substance"),
            New SemanticTypeAssignment("T109", "Organic Chemical")})

        Dim merged = DuplicateConceptMerger.Merge({a, b})

        Assert.Single(merged)
    End Sub
```

Follow the existing helper style in that file for `MakeResolved`.

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~ResolvedRecordTests|FullyQualifiedName~DuplicateConceptMerger"
```

Expected: BUILD FAILURE - `SemanticTypeAssignment` undefined.

- [ ] **Step 3: Add the pair type and rework `ResolvedRecord`**

Create the type at the end of `ResolvedRecord.vb`:

```vb
''' <summary>
''' One semantic-type assignment: the stable TUI and its display name.
''' </summary>
''' <remarks>
''' Carried as a pair rather than two parallel lists because the display string
''' is ordered by name while the array is keyed by TUI - separate lists would
''' invite them drifting out of alignment.
''' </remarks>
Public NotInheritable Class SemanticTypeAssignment

    Public Sub New(tui As String, sty As String)
        Me.Tui = If(tui, "")
        Me.Sty = If(sty, "")
    End Sub

    Public ReadOnly Property Tui As String
    Public ReadOnly Property Sty As String

End Class
```

In `ResolvedRecord.vb`, replace the constructor's semantic-type handling (lines 35-37) so it takes `IReadOnlyList(Of SemanticTypeAssignment)` and sets both properties:

```vb
        Dim assignments = If(semanticTypes, CType(Array.Empty(Of SemanticTypeAssignment)(), IReadOnlyList(Of SemanticTypeAssignment)))
        ' Sorted by name so one CUI yields one string corpus-wide. The legacy
        ' REST-era values preserved UMLS API order, which is not alphabetical -
        ' the backfill rewrites them to this form.
        Me.SemanticType = String.Join(", ", assignments.Select(Function(a) a.Sty).OrderBy(Function(s) s, StringComparer.Ordinal))
        Me.SemanticTypeTuis = assignments.Select(Function(a) a.Tui).Distinct(StringComparer.Ordinal).ToArray()
```

and add `Public ReadOnly Property SemanticTypeTuis As IReadOnlyList(Of String)` next to `SemanticType`. Update the comment on `SemanticType` to say it is the derived display form.

- [ ] **Step 4: Rework the merger**

In `DuplicateConceptMerger.vb`, change the merge key at lines 39 and 47 from `SemanticType` to a stable TUI-set key, and replace the string split at lines 88-96:

```vb
        ' Merge identity is the TUI SET. The previous key used the joined display
        ' string, so the same CUI with differently-ordered semantic types counted
        ' as two records. The old code also split that string on ", " with a
        ' comment claiming "semantic-type names don't contain ', ' in practice" -
        ' which is false ("Amino Acid, Peptide, or Protein"). It was safe only
        ' because it re-joined with the same separator; under a real list that
        ' safety is gone.
        Dim tuiKey = String.Join("|", r.SemanticTypeTuis.OrderBy(Function(t) t, StringComparer.Ordinal))
        Dim key = (r.ConceptCode, tuiKey, r.Criterion)
```

and in `MergeGroup`, rebuild assignments from the first record's TUIs paired with its names rather than splitting the string. Since `ResolvedRecord` no longer exposes the pairs, add an internal `Assignments` property to `ResolvedRecord` returning `IReadOnlyList(Of SemanticTypeAssignment)` and use it here.

- [ ] **Step 5: Run to verify pass, then full suite**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~ResolvedRecordTests|FullyQualifiedName~DuplicateConceptMerger"
dotnet test contexts/eligibility/Eligibility.sln
```

The full suite will surface every remaining caller of the old constructor. Fix each to pass assignments; that is the point of this step.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core contexts/eligibility/tests/EligibilityProcessing.Core.Tests
git commit -m "Carry semantic types as TUI assignments through ResolvedRecord and the merger"
```

---

### Task 3: Write path and persistence

**Files:**
- Modify: `Core/PipelineOrchestrator.vb:426,467-469,472`
- Modify: `Core/UmlsNormalizeJob.vb:124`
- Modify: `Cli/Program.vb:651-657`
- Modify: `Data/PostgresGateway.vb:514,526,541` (INSERT) and `:657,663` (retry UPDATE)
- Modify: `Core/IUmlsClient.vb` and its three implementations
- Test: `Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Consumes: `SemanticTypeAssignment`, `ResolvedRecord.SemanticTypeTuis` (Task 2).
- Produces: `IUmlsClient.GetSemanticTypeAssignmentsAsync(cui, ct) As Task(Of IReadOnlyList(Of SemanticTypeAssignment))` replacing `GetSemanticTypesAsync`.

**The orchestrator shim.** `PipelineOrchestrator.vb:467-469` takes the joined string out of `umls.concept_normalization` and wraps it as a one-element list so the constructor re-joins it verbatim. That cannot survive a real list. Replace it by looking up assignments from the cached `concept_code` via `IUmlsClient` - `concept_normalization` already stores the CUI, so **it needs no new column**, which is simpler than the spec implies.

- [ ] **Step 1: Write the failing test**

Append to `PostgresGatewayIntegrationTests.vb`:

```vb
    <SkippableFact>
    Public Async Function PersistEligibility_writes_semantic_type_tuis_and_derived_string() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim rec = MakeResolvedForPersist("NCT00000001", "C0000005", {
            New SemanticTypeAssignment("T116", "Amino Acid, Peptide, or Protein"),
            New SemanticTypeAssignment("T126", "Enzyme")})
        Await _fixture.Gateway.PersistTrialAsync("NCT00000001", {rec}, CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT semantic_type, semantic_type_tuis FROM public.eligibility WHERE nct_id='NCT00000001'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.Equal("Amino Acid, Peptide, or Protein, Enzyme", reader.GetString(0))
                    Dim tuis = CType(reader.GetValue(1), String())
                    Assert.Equal({"T116", "T126"}, tuis.OrderBy(Function(t) t).ToArray())
                End Using
            End Using
        End Using
    End Function
```

Add a `MakeResolvedForPersist` helper following the file's existing construction style, and use whatever the real persist method is called - check `IPostgresGateway` for the per-trial DELETE+INSERT method name rather than assuming `PersistTrialAsync`.

- [ ] **Step 2: Run to verify failure, then implement**

Rename `IUmlsClient.GetSemanticTypesAsync` to `GetSemanticTypeAssignmentsAsync` returning the pair type, and update:

- `UmlsClient.vb:69` (REST) - parse TUI and name from the UTS response. Check `ParseSemanticTypesResponse` at `:154` for the available fields; if the API response carries only names, resolve TUIs via `umls.semantic_type_dim`.
- `UmlsMetathesaurusStore.vb:244` - change to `SELECT tui, sty FROM umls.semantic_type WHERE cui = @cui ORDER BY sty`.
- `UmlsCache.vb:75-90` - cache the pair list.
- The three join sites (`ResolvedRecord` is already done in Task 2; fix `UmlsNormalizeJob.vb:124` and `Cli/Program.vb:654-655` to pass assignments).
- `PipelineOrchestrator.vb:467-469` - delete the one-element shim, look up assignments by the cached CUI.

In `PostgresGateway.vb`, add `semantic_type_tuis` to the INSERT column list (`:514`), placeholder (`:526`) and parameter binding (`:541`) using `NpgsqlDbType.Array Or NpgsqlDbType.Text`. Write `Nothing`/DBNull when the list is empty so unresolved rows get NULL rather than an empty array - `NullIfEmpty` is the existing idiom for the string; mirror its intent.

- [ ] **Step 3: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src contexts/eligibility/tests
git commit -m "Persist semantic_type_tuis and drop the joined-string round trips"
```

---

### Task 4: The backfill command

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb` (backfill SQL)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb` (command + help)
- Test: `Data.Tests/UmlsMetathesaurusIntegrationTests.vb`

**Interfaces:**
- Produces: `UmlsMetathesaurusStore.BackfillSemanticTypesBatchAsync(fromId, toId, ct) As Task(Of Long)`; CLI `backfill-semantic-types [--batch-size N] [--dry-run]`.

**Guard (spec requirement).** The command must refuse to run when `GetLoadCompletenessAsync().IsComplete` is false. Backfilling from an incomplete `umls.semantic_type` writes NULLs across 4M rows and looks like success - the exact silent failure phase 1 exists to prevent.

- [ ] **Step 1: Write the failing tests**

```vb
    <SkippableFact>
    Public Async Function Backfill_fills_tuis_and_canonical_string_for_resolved_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0000005", .Tui = "T116", .Sty = "Amino Acid, Peptide, or Protein"},
            New SemanticTypeRow With {.Cui = "C0000005", .Tui = "T126", .Sty = "Enzyme"}
        }, CancellationToken.None)
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "crit", "concept", conceptCode:="C0000005")

        Dim updated = Await store.BackfillSemanticTypesBatchAsync(0, Long.MaxValue, CancellationToken.None)

        Assert.Equal(1L, updated)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT semantic_type, array_length(semantic_type_tuis,1) FROM public.eligibility WHERE nct_id='NCT00000001'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.Equal("Amino Acid, Peptide, or Protein, Enzyme", reader.GetString(0))
                    Assert.Equal(2, reader.GetInt32(1))
                End Using
            End Using
        End Using
    End Function

    ' Re-running must converge, not double-apply or churn.
    <SkippableFact>
    Public Async Function Backfill_is_idempotent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)
        Await store.LoadSemanticTypesAsync({
            New SemanticTypeRow With {.Cui = "C0020615", .Tui = "T047", .Sty = "Disease or Syndrome"}
        }, CancellationToken.None)
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "crit", "concept", conceptCode:="C0020615")

        Await store.BackfillSemanticTypesBatchAsync(0, Long.MaxValue, CancellationToken.None)
        Dim second = Await store.BackfillSemanticTypesBatchAsync(0, Long.MaxValue, CancellationToken.None)

        ' Second pass finds nothing left to change.
        Assert.Equal(0L, second)
    End Function

    ' Unresolved rows must not gain an empty array - NULL means "no concept",
    ' and phase 3's containment filter would otherwise treat them as a category.
    <SkippableFact>
    Public Async Function Backfill_leaves_unresolved_rows_null() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)
        Await _fixture.InsertEligibilityRowAsync("NCT00000003", "crit", "concept", conceptCode:="")

        Await store.BackfillSemanticTypesBatchAsync(0, Long.MaxValue, CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT semantic_type_tuis IS NULL FROM public.eligibility WHERE nct_id='NCT00000003'"
                Assert.True(CBool(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function
```

- [ ] **Step 2: Implement the batch update**

In `UmlsMetathesaurusStore.vb`:

```vb
    ''' <summary>
    ''' Fills semantic_type_tuis and the derived semantic_type for resolved rows
    ''' in the given id range. Returns rows changed. Re-runnable: the WHERE clause
    ''' excludes rows already at their target value, so a second pass returns 0.
    ''' </summary>
    Public Async Function BackfillSemanticTypesBatchAsync(
            fromId As Long, toId As Long,
            cancellationToken As CancellationToken) As Task(Of Long)

        ' Aggregate per CUI once, then join - not a correlated subquery per row.
        ' Ordered by sty so the string matches ResolvedRecord's canonical form.
        Const Sql As String = "
WITH canon AS (
    SELECT cui,
           array_agg(tui ORDER BY tui)     AS tuis,
           string_agg(sty, ', ' ORDER BY sty) AS sty_text
    FROM umls.semantic_type
    GROUP BY cui
)
UPDATE public.eligibility e
   SET semantic_type_tuis = c.tuis,
       semantic_type      = c.sty_text
  FROM canon c
 WHERE e.concept_code = c.cui
   AND e.id BETWEEN @from_id AND @to_id
   AND e.concept_code IS NOT NULL
   AND e.concept_code <> ''
   AND (e.semantic_type_tuis IS DISTINCT FROM c.tuis
     OR e.semantic_type      IS DISTINCT FROM c.sty_text)"
        ' ... execute, return rows affected
    End Function
```

Note `IS DISTINCT FROM` rather than `<>` - a NULL-safe comparison, so rows with NULL are correctly seen as needing an update.

- [ ] **Step 3: Add the CLI command**

Add `backfill-semantic-types` to the dispatch switch and help text. It must:

- Call `GetLoadCompletenessAsync` first and **abort with exit 4** if incomplete.
- Read `MIN(id)` / `MAX(id)` from `public.eligibility` and walk in batches of `--batch-size` (default 100000, tuned to stay well inside `OutputCommandTimeoutSeconds`).
- Print running progress: batch, rows changed, cumulative.
- Support `--dry-run`, reporting how many rows *would* change without writing.
- On completion, report **rows still unmapped** - resolved rows whose CUI has no `umls.semantic_type` entry. Expected 0; a non-zero value is the regression signal the spec asks for.
- Remind the operator to `VACUUM (ANALYZE) public.eligibility` afterwards. `ix_eligibility_semantic_type` indexes a changed column so these are non-HOT updates across ~4M rows; bloat is expected.

- [ ] **Step 4: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src contexts/eligibility/tests
git commit -m "Add backfill-semantic-types with a completeness guard"
```

---

### Task 5: Version, spec corrections, run the backfill

- [ ] **Step 1: Bump the version - MINOR, not build**

This phase adds a migration, so per the project rule: `0.1.35` -> **`0.2.0`** (minor +1, build reset to 0), `releaseDate` today. Prepend a `releases` entry describing the array column, the canonical string and the backfill. Keep ASCII-only; `releases[0]` must match `current`.

Then update the four version literals and the one `ReleaseDate` assertion in `VersionWebTests.cs` with the Edit tool (not PowerShell - BOM).

- [ ] **Step 2: Correct the spec**

Apply the four corrections listed at the top of this plan to `docs/superpowers/specs/2026-07-20-semantic-type-repair-design.md`, so the spec does not carry a rationale we know to be wrong.

- [ ] **Step 3: Run the backfill against production**

Database should be idle. Backups exist (2026-07-20).

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- backfill-semantic-types --dry-run
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- backfill-semantic-types
```

Expect ~3,985,113 rows changed, 0 unmapped. Then:

```sql
VACUUM (ANALYZE) public.eligibility;
```

Verify:

```sql
-- Expect 0: every resolved row has an array.
SELECT count(*) FROM public.eligibility
 WHERE concept_code IS NOT NULL AND concept_code <> '' AND semantic_type_tuis IS NULL;
-- Expect 0: no unresolved row gained one.
SELECT count(*) FROM public.eligibility
 WHERE (concept_code IS NULL OR concept_code = '') AND semantic_type_tuis IS NOT NULL;
-- Expect 19,674 - the phase 3 target, provable before phase 3 exists.
SELECT count(*) FROM public.eligibility WHERE semantic_type_tuis && ARRAY['T121'];
```

That last query is the payoff: `T121` is Pharmacologic Substance, and the old exact-match filter returned 6,389 for it.

- [ ] **Step 4: Commit, push, PR**

Push and open the PR with `gh pr create --body-file <file>`. The body must state: the corpus impact, that the **UI is deliberately unchanged** in this phase, that **dedup identity changed** (records previously distinct by string order now merge - intended, and it shifts row counts on reprocessing), and the measured backfill result.

---

## Deliberately not in this phase

Phase 3 (UX: containment filter, dim-sourced dropdown, multi-select, Results CSV export) and phase 4 (authoring tables). Until phase 4, authoring rows keep the legacy string format - acceptable because that corpus is small and hand-built, but it must be stated rather than discovered.

## Known risks

**Dedup identity changes.** Merging on the TUI set means records previously distinguished by differently-ordered strings now merge. Intended, but it changes row counts on reprocessing and must not be diagnosed later as data loss.

**~4M non-HOT updates in a 2 GB table.** Bloat is expected; the VACUUM is part of the job.

**The `IUmlsClient` rename touches every implementation and fake.** The full suite is the safety net - expect it to fail loudly and fix each caller rather than adding compatibility shims.

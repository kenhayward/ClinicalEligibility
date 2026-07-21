# Concept Hierarchy Load - Part 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `umls.concept_ancestor` - a SNOMED-derived CUI hierarchy with precomputed transitive closure - so criteria clustering can roll up to broader concepts in Part 2.

**Architecture:** Extend the existing RRF loader to stream `MRREL.RRF`, keep only `SAB='SNOMEDCT_US'` with `REL IN ('PAR','CHD')`, normalise to one `(child, parent)` orientation in a staging table, then compute the transitive closure in Postgres with a recursive CTE bounded by depth. Table shape deliberately mirrors OMOP's `CONCEPT_ANCESTOR` so a later swap to OMOP is a load change, not a rewrite.

**Tech Stack:** .NET 8, VB.NET, Npgsql binary COPY, PostgreSQL 16 recursive CTE, xUnit + Testcontainers (`pgvector/pgvector:pg16`).

**Spec:** `docs/superpowers/specs/2026-07-20-criteria-hierarchy-rollup-design.md` (Part 1 only)

## What Part 1 does NOT do

No clustering change, no UI, no `CriterionCluster` change. Part 2 consumes this
table. Part 1 ends with a loaded, verified hierarchy and nothing using it - which
is deliberate: the orientation risk below is far cheaper to find here than
through a wrong-looking cluster.

## Global Constraints

- Branch `feat/criteria-rollup-design` exists off `origin/main` with the spec committed. Never commit to `main`.
- **ASCII only** in every authored file. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8.
- **Never write files with PowerShell `Set-Content`/`Out-File`** (adds a BOM). Use Edit/Write.
- **Never pass a multi-line commit message with double quotes through a PowerShell here-string.** Use `git commit -F <file>`.
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`.
- **Adds a migration**: bump **MINOR**, reset build to 0 (0.2.1 -> 0.3.0), and update `docs/specs/database_schema.md` **in the same change** - project rule.
- **A new migration `.sql` must be registered in TWO places** or every Postgres test silently skips: `MigrationResourceNames` in `PostgresGateway.vb:32`, and an `<EmbeddedResource>` with explicit `<LogicalName>` in `EligibilityProcessing.Data.vbproj`. Missing the second presents as "Docker likely unavailable" - see the known hazard below.
- Bulk statements run under `Postgres:OutputCommandTimeoutSeconds` (default 600). The closure build must stay inside it or raise it deliberately.

## Known hazard: skipped tests look like passing tests

`PostgresFixture` catches **any** exception during startup and turns it into
`SkipReason` with the message *"Postgres test container could not start (Docker
likely unavailable)"*. A migration that is not registered as an embedded
resource, or that has a SQL error, therefore presents as **the entire Postgres
suite skipping** - which reads as green.

**After every step that touches a migration, confirm the Data.Tests run reports
`Skipped: 0`.** A pass with skips is not a pass.

## Source data

`D:\umls\2025AB\META\MRREL.RRF` - **6,399,435,346 bytes (6.0 GB)**. Pipe-delimited,
columns (0-indexed) confirmed against the file:

| Index | Field | Sample |
|---|---|---|
| 0 | `CUI1` | `C0000005` |
| 3 | `REL` | `RB` |
| 4 | `CUI2` | `C0036775` |
| 10 | `SAB` | `MSH` |

The whole 6 GB is streamed to filter it - `File.ReadLines` keeps memory flat, but
expect a long single-pass read. That cost is unavoidable and one-off per release.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Data/Migrations/V23__concept_hierarchy.sql` | `umls.concept_ancestor` table + index | 1 |
| `Data/EligibilityProcessing.Data.vbproj` | Register the migration resource | 1 |
| `Data/PostgresGateway.vb` | Register in `MigrationResourceNames` | 1 |
| `docs/specs/database_schema.md` | Schema doc | 1 |
| `Cli/UmlsRrfReader.vb` | `ReadRelations` streaming parser | 2 |
| `Data/UmlsMetathesaurusStore.vb` | Edge load + closure build + counts | 3 |
| `Cli/Program.vb` | `--hierarchy-only`, guard, help | 4 |
| `version.json`, `deploy/.../umls-loader.md` | Version, runbook | 5 |

---

### Task 1: Migration V23

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V23__concept_hierarchy.sql`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/EligibilityProcessing.Data.vbproj`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:32` (`MigrationResourceNames`)
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayUnitTests.vb:60` (latest-migration assertion)
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresFixture.vb` (TRUNCATE list)
- Modify: `docs/specs/database_schema.md`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Produces: `umls.concept_ancestor (descendant_cui text, ancestor_cui text, min_distance integer)`, PK `(descendant_cui, ancestor_cui)`, plus index `ix_umls_concept_ancestor_ancestor (ancestor_cui)`.

- [ ] **Step 1: Write the failing test**

Append to `PostgresGatewayIntegrationTests.vb`, before the `MakeResolved` helper:

```vb
    ' ============ V23 schema ============

    <SkippableFact>
    Public Async Function V23_adds_concept_ancestor_table() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT (SELECT count(*) FROM information_schema.tables
         WHERE table_schema='umls' AND table_name='concept_ancestor')            AS has_table,
       (SELECT count(*) FROM information_schema.columns
         WHERE table_schema='umls' AND table_name='concept_ancestor'
           AND column_name='min_distance' AND data_type='integer')               AS has_distance,
       (SELECT count(*) FROM pg_indexes
         WHERE schemaname='umls' AND indexname='ix_umls_concept_ancestor_ancestor') AS has_ancestor_ix"
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Assert.Equal(1L, reader.GetInt64(0))
                    Assert.Equal(1L, reader.GetInt64(1))
                    Assert.Equal(1L, reader.GetInt64(2))
                End Using
            End Using
        End Using
    End Function
```

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~V23_"
```

Expected: FAIL - assertions return 0.

- [ ] **Step 3: Write the migration**

Create `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V23__concept_hierarchy.sql`:

```sql
-- V23: SNOMED-derived concept hierarchy, for rolling criteria clusters up to
-- broader concepts.
--
-- The Authoring Analysis tab clusters criteria on exact concept identity, so
-- "Type 2 Diabetes Mellitus" and "Diabetes Mellitus" fragment into separate
-- clusters. Since the tab drops singleton clusters client-side, fragmentation
-- does not merely add noise - it suppresses signal.
--
-- Deliberately shaped like OMOP's CONCEPT_ANCESTOR (ancestor, descendant,
-- distance). If the OMOP route is adopted later - it covers vocabularies beyond
-- SNOMED, at the cost of an ATHENA download and a CUI-to-OMOP crosswalk - this
-- becomes a load change rather than a rewrite of the rollup SQL and UI.
--
-- Populated by `load-umls --hierarchy-only` from MRREL.RRF, scoped to
-- SAB='SNOMEDCT_US' and REL IN ('PAR','CHD'). Roughly half the corpus's distinct
-- CUIs (66,514 of 132,243, measured 2026-07-20) have SNOMED edges; the rest do
-- not roll up. That partial coverage is expected and surfaced in the UI.
--
-- Idempotent - CREATE ... IF NOT EXISTS, so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS umls.concept_ancestor (
    descendant_cui text    NOT NULL,
    ancestor_cui   text    NOT NULL,
    -- Shortest path length. A DAG gives multiple paths between the same pair;
    -- the minimum is what "roll up at most N levels" is measured against.
    min_distance   integer NOT NULL,
    PRIMARY KEY (descendant_cui, ancestor_cui)
);

-- The PK serves descendant -> ancestors (the rollup direction). This index
-- serves the reverse, ancestor -> descendants, which Part 2 needs to expand a
-- rolled-up cluster back to its member concepts.
CREATE INDEX IF NOT EXISTS ix_umls_concept_ancestor_ancestor
    ON umls.concept_ancestor (ancestor_cui);
```

- [ ] **Step 4: Register the migration in BOTH places**

In `PostgresGateway.vb`, append to `MigrationResourceNames` (currently ending `V22__semantic_type_tuis.sql`):

```vb
            "EligibilityProcessing.Data.Migrations.V23__concept_hierarchy.sql"
```

Remember the trailing comma on the previous line.

In `EligibilityProcessing.Data.vbproj`, after the `V22` entry:

```xml
    <EmbeddedResource Include="Migrations\V23__concept_hierarchy.sql">
      <LogicalName>EligibilityProcessing.Data.Migrations.V23__concept_hierarchy.sql</LogicalName>
    </EmbeddedResource>
```

**Both are required.** Omitting the vbproj entry makes `EnsureSchemaAsync` throw
"Embedded migration resource not found", which the fixture converts into a skip
for the whole Postgres suite.

- [ ] **Step 5: Update the two tests that track schema state**

`PostgresGatewayUnitTests.vb:60` asserts the newest migration name:

```vb
        Assert.Equal("V23__concept_hierarchy", names(names.Count - 1))
```

`PostgresFixture.vb` `ResetAsync` TRUNCATEs every table for test isolation. Add
`umls.concept_ancestor` to that list, next to `umls.semantic_type_dim`.

- [ ] **Step 6: Run to verify pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~V23_"
```

Expected: 1 test PASS. **Check the summary says `Skipped: 0`** - a skip here means the resource registration is wrong, not that Docker is missing.

- [ ] **Step 7: Update the schema doc**

In `docs/specs/database_schema.md`, add a `### umls.concept_ancestor` section
after `umls.semantic_type_dim`, documenting the three columns, the PK, the
`ancestor_cui` index, that it is SNOMED-only and therefore partial, and that the
shape mirrors OMOP `CONCEPT_ANCESTOR`. Add V23 to the migration-history table.

- [ ] **Step 8: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Data contexts/eligibility/tests/EligibilityProcessing.Data.Tests docs/specs/database_schema.md
git commit -m "Add V23: umls.concept_ancestor for SNOMED hierarchy rollup"
```

---

### Task 2: MRREL streaming reader

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Cli/UmlsRrfReader.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsLoaderUnitTests.vb`

**Interfaces:**
- Produces: `ConceptEdgeRow` structure with `ChildCui As String`, `ParentCui As String`; and `UmlsRrfReader.ReadRelations(mrrelPath As String) As IEnumerable(Of ConceptEdgeRow)`.

**Files (additional):**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb` (add `ConceptEdgeRow` at the end, beside `AtomRow` / `SemanticTypeRow`)

`ConceptEdgeRow` is defined **here**, not in Task 3, because this task's tests
reference it - Task 2 must compile and pass on its own. It lives in the Data
project beside the other RRF row types; `UmlsRrfReader` already imports
`EligibilityProcessing.Data` to reach `AtomRow` and `SemanticTypeRow`.

**Orientation - the single most important decision in this plan.**

`MRREL.REL` describes the relationship of the **second** concept to the first. So
for a row `(CUI1, REL='PAR', CUI2)`, `CUI2` is the **parent** of `CUI1`, and the
edge is `child=CUI1, parent=CUI2`. `REL='CHD'` is the inverse: `CUI2` is the
child, so the edge is `child=CUI2, parent=CUI1`.

**Do not trust that reading.** It is asserted here from the UMLS reference manual
and must be proven against real data - Task 3 Step 1 carries the test, and Task 5
verifies it against the production load. An inverted hierarchy does not error; it
rolls concepts up to *more specific* terms and reads as merely odd.

Emitting both `PAR` and `CHD` normalised to one orientation means every edge
appears twice (UMLS stores both directions). Task 3's staging insert dedupes.

- [ ] **Step 1: Write the failing tests**

Append to `UmlsLoaderUnitTests.vb`:

```vb
    ' ============ ReadRelations (MRREL) ============
    '
    ' MRREL.REL describes the relationship of the SECOND concept to the first:
    ' (CUI1, 'PAR', CUI2) means CUI2 is the parent of CUI1. 'CHD' is the inverse.
    ' Both are normalised to a single (child, parent) orientation here.

    <Fact>
    Public Sub ReadRelations_maps_PAR_with_cui2_as_parent()
        ' C0011860 (Type 2 Diabetes) PAR C0011849 (Diabetes Mellitus)
        Dim path = WriteTempRrf({
            "C0011860|A1|SCUI|PAR|C0011849|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Dim rows = UmlsRrfReader.ReadRelations(path).ToList()
            Assert.Single(rows)
            Assert.Equal("C0011860", rows(0).ChildCui)
            Assert.Equal("C0011849", rows(0).ParentCui)
        Finally
            File.Delete(path)
        End Try
    End Sub

    <Fact>
    Public Sub ReadRelations_maps_CHD_as_the_inverse()
        ' (CUI1, 'CHD', CUI2) means CUI2 is the CHILD of CUI1.
        Dim path = WriteTempRrf({
            "C0011849|A1|SCUI|CHD|C0011860|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Dim rows = UmlsRrfReader.ReadRelations(path).ToList()
            Assert.Single(rows)
            Assert.Equal("C0011860", rows(0).ChildCui)
            Assert.Equal("C0011849", rows(0).ParentCui)
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' Only SNOMED edges. Other vocabularies have their own, incompatible
    ' hierarchies; mixing them produces incoherent ancestry and cycles.
    <Fact>
    Public Sub ReadRelations_skips_other_source_vocabularies()
        Dim path = WriteTempRrf({
            "C0000005|A1|SCUI|PAR|C0036775|A2|SCUI||R1||MSH|MSH|||N||",
            "C0011860|A1|SCUI|PAR|C0011849|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Dim rows = UmlsRrfReader.ReadRelations(path).ToList()
            Assert.Single(rows)
            Assert.Equal("C0011860", rows(0).ChildCui)
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' RB/RN (broader/narrower) are not the is-a hierarchy and must not be treated
    ' as one - the very first line of the real MRREL.RRF is an RB row.
    <Fact>
    Public Sub ReadRelations_skips_non_hierarchical_relationships()
        Dim path = WriteTempRrf({
            "C0000005|A13433185|SCUI|RB|C0036775|A7466261|SCUI||R86000559||SNOMEDCT_US|SNOMEDCT_US|||N||",
            "C0000005|A1|SCUI|SY|C0036775|A2|SCUI||R2||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Assert.Empty(UmlsRrfReader.ReadRelations(path).ToList())
        Finally
            File.Delete(path)
        End Try
    End Sub

    ' A concept is not its own parent. Self-edges would make the closure loop.
    <Fact>
    Public Sub ReadRelations_skips_self_relationships()
        Dim path = WriteTempRrf({
            "C0011860|A1|SCUI|PAR|C0011860|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Assert.Empty(UmlsRrfReader.ReadRelations(path).ToList())
        Finally
            File.Delete(path)
        End Try
    End Sub

    <Fact>
    Public Sub ReadRelations_skips_malformed_and_blank_rows()
        Dim path = WriteTempRrf({
            "too|few|fields",
            "|A1|SCUI|PAR|C0011849|A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||",
            "C0011860|A1|SCUI|PAR||A2|SCUI||R1||SNOMEDCT_US|SNOMEDCT_US|||N||"})
        Try
            Assert.Empty(UmlsRrfReader.ReadRelations(path).ToList())
        Finally
            File.Delete(path)
        End Try
    End Sub
```

`WriteTempRrf` already exists in this test project - check
`UmlsMetathesaurusIntegrationTests.vb` for its definition and either reuse or
mirror it locally.

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~ReadRelations"
```

Expected: BUILD FAILURE - `ReadRelations` is not a member.

- [ ] **Step 3: Add the row type**

At the end of
`contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb`,
alongside `AtomRow` and `SemanticTypeRow`:

```vb
''' <summary>One is-a edge for bulk loading the concept hierarchy.
''' ChildCui is always the more specific concept.</summary>
Public Structure ConceptEdgeRow
    Public Property ChildCui As String
    Public Property ParentCui As String
End Structure
```

- [ ] **Step 4: Implement the reader**

In `UmlsRrfReader.vb`, extend the header comment's column map and add:

```vb
    ''' <summary>
    ''' Yields one <see cref="ConceptEdgeRow"/> per SNOMED is-a relationship in
    ''' MRREL, normalised so ChildCui is always the more specific concept.
    ''' </summary>
    ''' <remarks>
    ''' MRREL.REL describes the relationship of the SECOND concept to the first:
    ''' (CUI1, 'PAR', CUI2) means CUI2 is the parent of CUI1; 'CHD' is the
    ''' inverse. UMLS stores both directions, so most edges appear twice - the
    ''' staging insert dedupes.
    '''
    ''' Scoped to SAB='SNOMEDCT_US'. UMLS asserts no cross-source hierarchy, so
    ''' mixing vocabularies produces incoherent ancestry and can introduce cycles.
    ''' RB/RN (broader/narrower) are deliberately excluded - they are not the
    ''' is-a hierarchy, and they are the most common relationship in the file.
    ''' </remarks>
    Public Shared Iterator Function ReadRelations(mrrelPath As String) As IEnumerable(Of ConceptEdgeRow)
        For Each line In File.ReadLines(mrrelPath)
            Dim f = line.Split("|"c)
            If f.Length < 11 Then Continue For
            If Not String.Equals(f(10), "SNOMEDCT_US", StringComparison.Ordinal) Then Continue For

            Dim cui1 = f(0)
            Dim rel = f(3)
            Dim cui2 = f(4)
            If String.IsNullOrWhiteSpace(cui1) OrElse String.IsNullOrWhiteSpace(cui2) Then Continue For
            If String.Equals(cui1, cui2, StringComparison.Ordinal) Then Continue For

            If String.Equals(rel, "PAR", StringComparison.Ordinal) Then
                Yield New ConceptEdgeRow With {.ChildCui = cui1, .ParentCui = cui2}
            ElseIf String.Equals(rel, "CHD", StringComparison.Ordinal) Then
                Yield New ConceptEdgeRow With {.ChildCui = cui2, .ParentCui = cui1}
            End If
        Next
    End Function
```

Note the MRREL column map for the header comment:
`0=CUI1 3=REL 4=CUI2 10=SAB`.

- [ ] **Step 5: Run to verify pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~ReadRelations"
```

Expected: 6 tests PASS. (These need no database, so they must not skip.)

- [ ] **Step 6: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Cli/UmlsRrfReader.vb contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsLoaderUnitTests.vb
git commit -m "Add MRREL streaming reader for SNOMED is-a edges"
```

---

### Task 3: Edge load and closure build

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsMetathesaurusIntegrationTests.vb`

**Interfaces:**
- Consumes: `ConceptEdgeRow` with `ChildCui As String`, `ParentCui As String` (defined in Task 2).
- Produces:
  - `UmlsMetathesaurusStore.LoadConceptHierarchyAsync(rows, maxDepth, ct) As Task(Of Long)` - returns closure row count
  - `UmlsMetathesaurusStore.GetHierarchyStatsAsync(ct) As Task(Of UmlsHierarchyStats)` with `ClosureRows As Long`, `DistinctDescendants As Long`, `MaxDistance As Integer`, `Describe() As String`

- [ ] **Step 1: Write the failing tests**

Append to `UmlsMetathesaurusIntegrationTests.vb`, before the `Atom` helper:

```vb
    ' ============ LoadConceptHierarchyAsync ============

    Private Shared Function Edge(child As String, parent As String) As ConceptEdgeRow
        Return New ConceptEdgeRow With {.ChildCui = child, .ParentCui = parent}
    End Function

    Private Async Function AncestorDistanceAsync(descendant As String, ancestor As String) As Task(Of Integer)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COALESCE(min_distance, -1) FROM umls.concept_ancestor
                                    WHERE descendant_cui = @d AND ancestor_cui = @a"
                cmd.Parameters.AddWithValue("d", descendant)
                cmd.Parameters.AddWithValue("a", ancestor)
                Dim v = Await cmd.ExecuteScalarAsync()
                Return If(v Is Nothing, -1, Convert.ToInt32(v))
            End Using
        End Using
    End Function

    ' THE ORIENTATION TEST. An inverted hierarchy does not error - it rolls
    ' concepts up to MORE SPECIFIC terms and reads as merely odd. This asserts a
    ' real, checkable fact: Type 2 Diabetes is a kind of Diabetes Mellitus, not
    ' the other way round.
    <SkippableFact>
    Public Async Function LoadConceptHierarchy_puts_the_specific_concept_under_the_general_one() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' C0011860 = Type 2 Diabetes Mellitus, C0011849 = Diabetes Mellitus
        Await store.LoadConceptHierarchyAsync({Edge("C0011860", "C0011849")}, 5, CancellationToken.None)

        Assert.Equal(1, Await AncestorDistanceAsync("C0011860", "C0011849"))
        Assert.Equal(-1, Await AncestorDistanceAsync("C0011849", "C0011860"))
    End Function

    <SkippableFact>
    Public Async Function LoadConceptHierarchy_computes_transitive_closure() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' A -> B -> C -> D (each child under its parent)
        Await store.LoadConceptHierarchyAsync({
            Edge("C0000001", "C0000002"),
            Edge("C0000002", "C0000003"),
            Edge("C0000003", "C0000004")}, 5, CancellationToken.None)

        Assert.Equal(1, Await AncestorDistanceAsync("C0000001", "C0000002"))
        Assert.Equal(2, Await AncestorDistanceAsync("C0000001", "C0000003"))
        Assert.Equal(3, Await AncestorDistanceAsync("C0000001", "C0000004"))
    End Function

    ' A DAG gives several paths between the same pair. "Roll up at most N levels"
    ' is measured against the SHORTEST, so the minimum is what gets stored.
    <SkippableFact>
    Public Async Function LoadConceptHierarchy_keeps_the_minimum_distance() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' A reaches D directly (1) and via B, C (3).
        Await store.LoadConceptHierarchyAsync({
            Edge("C0000001", "C0000004"),
            Edge("C0000001", "C0000002"),
            Edge("C0000002", "C0000003"),
            Edge("C0000003", "C0000004")}, 5, CancellationToken.None)

        Assert.Equal(1, Await AncestorDistanceAsync("C0000001", "C0000004"))
    End Function

    <SkippableFact>
    Public Async Function LoadConceptHierarchy_stops_at_max_depth() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        ' A six-link chain, loaded with maxDepth 5.
        Await store.LoadConceptHierarchyAsync({
            Edge("C0000001", "C0000002"), Edge("C0000002", "C0000003"),
            Edge("C0000003", "C0000004"), Edge("C0000004", "C0000005"),
            Edge("C0000005", "C0000006"), Edge("C0000006", "C0000007")}, 5, CancellationToken.None)

        Assert.Equal(5, Await AncestorDistanceAsync("C0000001", "C0000006"))
        Assert.Equal(-1, Await AncestorDistanceAsync("C0000001", "C0000007"))
    End Function

    ' UMLS stores each edge twice (PAR and its CHD inverse), so the reader emits
    ' duplicates by design. Loading must converge, not fail on the PK.
    <SkippableFact>
    Public Async Function LoadConceptHierarchy_tolerates_duplicate_edges() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Dim rows = Await store.LoadConceptHierarchyAsync({
            Edge("C0011860", "C0011849"),
            Edge("C0011860", "C0011849")}, 5, CancellationToken.None)

        Assert.Equal(1L, rows)
    End Function

    ' A full reload must replace, not accumulate - otherwise a retired edge from
    ' last year's release survives forever.
    <SkippableFact>
    Public Async Function LoadConceptHierarchy_replaces_previous_contents() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.LoadConceptHierarchyAsync({Edge("C0000001", "C0000002")}, 5, CancellationToken.None)
        Await store.LoadConceptHierarchyAsync({Edge("C0000003", "C0000004")}, 5, CancellationToken.None)

        Assert.Equal(-1, Await AncestorDistanceAsync("C0000001", "C0000002"))
        Assert.Equal(1, Await AncestorDistanceAsync("C0000003", "C0000004"))
    End Function

    <SkippableFact>
    Public Async Function HierarchyStats_report_rows_descendants_and_depth() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Await store.LoadConceptHierarchyAsync({
            Edge("C0000001", "C0000002"),
            Edge("C0000002", "C0000003")}, 5, CancellationToken.None)

        Dim stats = Await store.GetHierarchyStatsAsync(CancellationToken.None)

        Assert.Equal(3L, stats.ClosureRows)          ' 1->2, 2->3, 1->3
        Assert.Equal(2L, stats.DistinctDescendants)  ' C0000001, C0000002
        Assert.Equal(2, stats.MaxDistance)
        Assert.Contains("3", stats.Describe())
    End Function
```

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~LoadConceptHierarchy|FullyQualifiedName~HierarchyStats"
```

Expected: BUILD FAILURE - `ConceptEdgeRow` and `LoadConceptHierarchyAsync` undefined.

- [ ] **Step 3: Implement the loader**

(`ConceptEdgeRow` already exists - Task 2 added it.)

Add to `UmlsMetathesaurusStore.vb`, after `LoadSemanticTypesAsync`:

```vb
    ''' <summary>
    ''' Replaces umls.concept_ancestor from the given is-a edges, computing the
    ''' transitive closure up to <paramref name="maxDepth"/> levels. Returns the
    ''' final row count.
    ''' </summary>
    ''' <remarks>
    ''' Closure is computed IN POSTGRES with a recursive CTE, not in .NET: the
    ''' edge set is large and the intermediate result larger, so materialising it
    ''' client-side would be pointless traffic.
    '''
    ''' Precomputed rather than walked at query time because Part 2 clusters
    ''' interactively over the criteria of up to 200 studies - a recursive walk
    ''' per cluster would be felt. This is also OMOP's own design, which is what
    ''' keeps a later swap to CONCEPT_ANCESTOR cheap.
    '''
    ''' TRUNCATE, not upsert: a vocabulary release retires edges, and an additive
    ''' load would keep them forever.
    ''' </remarks>
    Public Async Function LoadConceptHierarchyAsync(
            rows As IEnumerable(Of ConceptEdgeRow),
            maxDepth As Integer,
            cancellationToken As CancellationToken) As Task(Of Long)

        If maxDepth < 1 Then Throw New ArgumentOutOfRangeException(NameOf(maxDepth), "maxDepth must be >= 1")

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DROP TABLE IF EXISTS pg_temp.mrrel_stage;
CREATE TEMP TABLE mrrel_stage (child_cui text, parent_cui text)"
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using

            Using writer = Await conn.BeginBinaryImportAsync(
                    "COPY pg_temp.mrrel_stage (child_cui, parent_cui) FROM STDIN (FORMAT BINARY)",
                    cancellationToken).ConfigureAwait(False)
                For Each r In rows
                    cancellationToken.ThrowIfCancellationRequested()
                    Await writer.StartRowAsync(cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.ChildCui, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.ParentCui, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                Next
                Await writer.CompleteAsync(cancellationToken).ConfigureAwait(False)
            End Using

            ' Dedupe into a distinct edge set first. UMLS stores every edge twice
            ' (PAR and its CHD inverse), so the raw stage is ~2x, and feeding
            ' duplicates into the recursion multiplies work at every level.
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
CREATE TEMP TABLE edges AS
SELECT DISTINCT child_cui, parent_cui FROM pg_temp.mrrel_stage
 WHERE child_cui <> parent_cui;
CREATE INDEX ON edges (child_cui);"
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using

            Using cmd = conn.CreateCommand()
                ' UNION (not UNION ALL) prunes paths already seen, which is what
                ' stops a DAG with many routes between two concepts from
                ' exploding. min(dist) then collapses remaining duplicates to the
                ' shortest path.
                cmd.CommandText = "
TRUNCATE umls.concept_ancestor;

WITH RECURSIVE closure AS (
    SELECT child_cui AS descendant_cui, parent_cui AS ancestor_cui, 1 AS dist
      FROM edges
    UNION
    SELECT c.descendant_cui, e.parent_cui, c.dist + 1
      FROM closure c
      JOIN edges e ON e.child_cui = c.ancestor_cui
     WHERE c.dist < @max_depth
)
INSERT INTO umls.concept_ancestor (descendant_cui, ancestor_cui, min_distance)
SELECT descendant_cui, ancestor_cui, min(dist)
  FROM closure
 WHERE descendant_cui <> ancestor_cui
 GROUP BY descendant_cui, ancestor_cui"
                cmd.Parameters.Add(New NpgsqlParameter("max_depth", NpgsqlDbType.Integer) With {.Value = maxDepth})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using

        Return Await CountAsync("umls.concept_ancestor", cancellationToken).ConfigureAwait(False)
    End Function

    ''' <summary>Shape of the loaded hierarchy, for the load-completeness guard.</summary>
    Public Async Function GetHierarchyStatsAsync(
            cancellationToken As CancellationToken) As Task(Of UmlsHierarchyStats)

        Const Sql As String = "
SELECT count(*),
       count(DISTINCT descendant_cui),
       COALESCE(max(min_distance), 0)
  FROM umls.concept_ancestor"

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                    Return New UmlsHierarchyStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2))
                End Using
            End Using
        End Using
    End Function
```

`CountAsync` whitelists table names - add `"umls.concept_ancestor"` to its
`Select Case` (around line 415) or it throws `ArgumentException`.

Then add the stats type at the end of the file:

```vb
''' <summary>Outcome of <see cref="UmlsMetathesaurusStore.GetHierarchyStatsAsync"/>.</summary>
Public NotInheritable Class UmlsHierarchyStats

    Public Sub New(closureRows As Long, distinctDescendants As Long, maxDistance As Integer)
        Me.ClosureRows = closureRows
        Me.DistinctDescendants = distinctDescendants
        Me.MaxDistance = maxDistance
    End Sub

    Public ReadOnly Property ClosureRows As Long
    Public ReadOnly Property DistinctDescendants As Long
    Public ReadOnly Property MaxDistance As Integer

    Public Function Describe() As String
        Return $"umls.concept_ancestor holds {ClosureRows:N0} rows over " &
               $"{DistinctDescendants:N0} descendant concepts, max depth {MaxDistance}."
    End Function

End Class
```

- [ ] **Step 4: Run to verify pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~LoadConceptHierarchy|FullyQualifiedName~HierarchyStats"
```

Expected: 7 tests PASS, `Skipped: 0`.

- [ ] **Step 5: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Data contexts/eligibility/tests
git commit -m "Load the SNOMED concept hierarchy and precompute its transitive closure"
```

---

### Task 4: CLI command and guard

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/CliCompositionTests.vb`

**Interfaces:**
- Consumes: `UmlsRrfReader.ReadRelations` (Task 2), `LoadConceptHierarchyAsync` / `GetHierarchyStatsAsync` (Task 3).
- Produces: `load-umls --hierarchy-only [--max-depth N]`, and `Program.IsHierarchyOnly(args)`.

Follows the `--semantic-types-only` precedent from 0.1.35 exactly: a repair/rebuild
path that skips the destructive truncate-and-reload of atoms and concepts, plus a
guard that refuses to report success on an implausible result.

- [ ] **Step 1: Write the failing tests**

Append to `CliCompositionTests.vb`, before the `BuildServices` helper:

```vb
    ' ============ load-umls --hierarchy-only ============

    <Fact>
    Public Sub HierarchyOnly_flag_is_detected()
        Assert.True(Program.IsHierarchyOnly({"load-umls", "--rrf-dir", "D:\umls", "--hierarchy-only"}))
    End Sub

    <Fact>
    Public Sub HierarchyOnly_flag_is_case_insensitive()
        Assert.True(Program.IsHierarchyOnly({"load-umls", "--HIERARCHY-ONLY"}))
    End Sub

    <Fact>
    Public Sub HierarchyOnly_is_false_when_absent()
        Assert.False(Program.IsHierarchyOnly({"load-umls", "--rrf-dir", "D:\umls"}))
    End Sub

    ' Guards against a prefix match treating an unrelated flag as the real one.
    <Fact>
    Public Sub HierarchyOnly_does_not_match_a_different_flag()
        Assert.False(Program.IsHierarchyOnly({"load-umls", "--hierarchy-only-please"}))
    End Sub
```

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~HierarchyOnly"
```

Expected: BUILD FAILURE - `IsHierarchyOnly` is not a member of `Program`.

- [ ] **Step 3: Add the flag helper**

Next to `IsSemanticTypesOnly` in `Program.vb`:

```vb
    ''' <summary>
    ''' True when --hierarchy-only is present. Exact match (not a prefix), so an
    ''' unrelated flag beginning with the same text does not trigger it.
    ''' </summary>
    Friend Function IsHierarchyOnly(args As String()) As Boolean
        Return args.Any(Function(a) String.Equals(a, "--hierarchy-only", StringComparison.OrdinalIgnoreCase))
    End Function
```

**Do not write `Friend Shared`.** `Program` is a VB `Module` (`Program.vb:110`),
where members are implicitly shared and an explicit `Shared` is a compile error.
This mirrors `IsSemanticTypesOnly` and `ParseStudyCount`.

- [ ] **Step 4: Wire it into `RunLoadUmlsAsync`**

In `RunLoadUmlsAsync`, add near the top alongside `semanticTypesOnly`:

```vb
        Dim hierarchyOnly = IsHierarchyOnly(args)
        Dim maxDepth = ParseOptionInt(args, "--max-depth", 5)
```

Extend the file checks - `--hierarchy-only` reads only MRREL:

```vb
        Dim mrrel = Path.Combine(rrfDir, "MRREL.RRF")
        If hierarchyOnly AndAlso Not File.Exists(mrrel) Then
            System.Console.Error.WriteLine($"Not found: {mrrel}") : Return 1
        End If
```

and make the MRCONSO/MRSTY existence checks conditional on `Not hierarchyOnly`
in the same way they are already conditional on `Not semanticTypesOnly`.

Then branch the body. On the `hierarchyOnly` path, skip truncate/atoms/concepts
and semantic types entirely:

```vb
        If hierarchyOnly Then
            ' Rebuild path: leave atoms, concepts and semantic types untouched.
            ' Only umls.concept_ancestor is replaced, so this is safe against a
            ' live system - nothing reads the hierarchy until Part 2 ships.
            System.Console.WriteLine("  --hierarchy-only: rebuilding umls.concept_ancestor from MRREL.RRF")
            System.Console.WriteLine($"    streaming {mrrel} (this file is ~6 GB; the whole of it is read to filter it)")
            Dim closureRows = Await store.LoadConceptHierarchyAsync(
                    UmlsRrfReader.ReadRelations(mrrel), maxDepth, cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine($"    {closureRows:N0} closure rows")

            Dim hstats = Await store.GetHierarchyStatsAsync(cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine("  " & hstats.Describe())

            ' A vocabulary this size cannot legitimately yield a handful of rows.
            ' The semantic-type incident - a partial load that went unnoticed for
            ' two months - is why this is a hard failure and not a log line.
            ' Exit 4 matches the LOAD INCOMPLETE code: DispatchAsync already uses
            ' 1 = usage, 2 = unhandled exception, 3 = cancelled.
            If hstats.ClosureRows < 1000 Then
                System.Console.Error.WriteLine("HIERARCHY LOAD INCOMPLETE - " & hstats.Describe())
                System.Console.Error.WriteLine(
                    "SNOMED's is-a graph yields millions of closure rows. A result this small means " &
                    "MRREL was not read, the SAB filter matched nothing, or the file is truncated.")
                Return 4
            End If

            System.Console.WriteLine("Done.")
            Return 0
        End If
```

- [ ] **Step 5: Update the help text**

Extend the `load-umls` block:

```vb
        System.Console.WriteLine("      --hierarchy-only rebuilds umls.concept_ancestor alone from MRREL.RRF")
        System.Console.WriteLine("      (SNOMED is-a edges, transitive closure to --max-depth levels, default 5).")
        System.Console.WriteLine("      Leaves atoms, concepts and semantic types untouched. Exits 4 if the")
        System.Console.WriteLine("      result is implausibly small.")
```

and add `[--hierarchy-only] [--max-depth N]` to the usage line.

- [ ] **Step 6: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Cli contexts/eligibility/tests/EligibilityProcessing.Data.Tests
git commit -m "Add load-umls --hierarchy-only"
```

---

### Task 5: Measure depth, load production, version, PR

**Files:**
- Modify: `contexts/eligibility/version.json`
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/VersionWebTests.cs`
- Modify: `deploy/eligibility-pipeline/umls-loader.md`

**The closure size is not known in advance and must be measured before committing
to depth 5.** SNOMED's is-a graph is a DAG with multiple inheritance; closure
size grows super-linearly with depth. Depth 5 could be tens of millions of rows,
or considerably more.

- [ ] **Step 1: Measure at increasing depth**

Run the loader at depth 1, then 2, then 3, recording rows and elapsed time:

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- `
  load-umls --rrf-dir D:\umls\2025AB\META --hierarchy-only --max-depth 1
```

Then `--max-depth 2`, then `3`. Each run replaces the table, so they are
independent. Note the closure row count and wall-clock each time.

**Decide the shipped default from the measurements, not from this plan's
suggestion of 5.** Stop increasing depth when either:

- the table exceeds roughly 50M rows (comparable to the largest existing table,
  and past the point where a join stays cheap), or
- a single run approaches `Postgres:OutputCommandTimeoutSeconds` (600s).

If depth 3 already trips either, ship 3 and say so. Rollup levels above 2 are
unlikely to produce useful clusters anyway - an ancestor five levels up tends
toward "Disease".

Record the chosen default in `version.json`'s release note and in the runbook.

- [ ] **Step 2: Run the production load at the chosen depth**

```powershell
$sw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- `
  load-umls --rrf-dir D:\umls\2025AB\META --hierarchy-only --max-depth <chosen>
"EXIT=$LASTEXITCODE ELAPSED=$([int]$sw.Elapsed.TotalSeconds)s"
```

Expect exit 0 and a `Describe()` line with millions of rows.

- [ ] **Step 3: Verify the orientation against REAL data**

This is the step the whole plan exists to protect. The unit tests prove the
reader's mapping; this proves it survived the full pipeline.

```sql
-- Type 2 Diabetes Mellitus should be UNDER Diabetes Mellitus.
SELECT min_distance FROM umls.concept_ancestor
 WHERE descendant_cui = 'C0011860' AND ancestor_cui = 'C0011849';   -- expect a small integer

-- And NOT the reverse.
SELECT count(*) FROM umls.concept_ancestor
 WHERE descendant_cui = 'C0011849' AND ancestor_cui = 'C0011860';   -- expect 0

-- Sanity: a broad concept should have many descendants, few ancestors.
SELECT count(*) FROM umls.concept_ancestor WHERE ancestor_cui   = 'C0011849';  -- expect many
SELECT count(*) FROM umls.concept_ancestor WHERE descendant_cui = 'C0011849';  -- expect few
```

**If the first query returns nothing and the second returns rows, the hierarchy
is inverted. Stop and fix the reader before going further** - every downstream
cluster would roll up to more specific concepts.

- [ ] **Step 4: Measure rollup coverage of the corpus**

The number Part 2's usefulness depends on:

```sql
-- Distinct corpus CUIs that have at least one ancestor. The spec estimated
-- ~66,514 of 132,243 (50.3%) from match_source; this is the real figure.
SELECT count(DISTINCT e.concept_code) AS corpus_cuis,
       count(DISTINCT e.concept_code) FILTER (
         WHERE EXISTS (SELECT 1 FROM umls.concept_ancestor a
                        WHERE a.descendant_cui = e.concept_code)) AS with_ancestors
  FROM public.eligibility e
 WHERE e.concept_code IS NOT NULL AND e.concept_code <> '';
```

Record it in the PR. If it lands far below ~50%, say so plainly - it materially
weakens the case for Part 2, and that is worth knowing before building the UI.

- [ ] **Step 5: Bump the version**

Adds a migration, so **MINOR**: `0.2.1` -> **`0.3.0`** (build resets to 0),
`releaseDate` today. Prepend a `releases` entry in user-facing terms - a concept
hierarchy is now loaded, ready for criteria rollup, nothing user-visible yet.
ASCII only; `releases[0]` must match `current`.

Then update the four version literals and the one `ReleaseDate` assertion in
`VersionWebTests.cs` with the Edit tool (not PowerShell - BOM).

- [ ] **Step 6: Update the runbook**

In `deploy/eligibility-pipeline/umls-loader.md`, add `MRREL.RRF` to the
prerequisites (noting its ~6 GB size), and document `--hierarchy-only` alongside
`--semantic-types-only`, including the chosen `--max-depth` default and the
measured closure size.

- [ ] **Step 7: Full suite, push, PR**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility docs deploy
git commit -F <message-file>
git push -u origin feat/criteria-rollup-design
```

Open the PR with `gh pr create --body-file <file>`. The body must state:

- The measured closure size and chosen depth, with the depth-1/2/3 figures that
  justified it.
- **The orientation verification result**, explicitly - this is the plan's
  central risk.
- The measured corpus rollup coverage from Step 4.
- That **nothing consumes the table yet** - Part 2 does.

---

## Deliberately not in Part 1

The clustering change, `CriterionCluster`, `GetClusterRecordsAsync` member-set
semantics, the UI rollup control, and what Add persists. All Part 2.

## Known risks

**An inverted hierarchy is the failure mode to fear.** It does not error - it
rolls concepts up to more specific terms and reads as merely odd. Task 2's unit
tests and Task 5 Step 3's production check are both there for this one thing.

**Closure size is unmeasured.** Depth 5 is a starting suggestion, not a
commitment. Task 5 Step 1 exists to replace it with a measurement.

**A migration that fails to register presents as skipped tests, not failures.**
`PostgresFixture` converts any startup exception into
"Docker likely unavailable". Check `Skipped: 0` after every migration-touching
step.

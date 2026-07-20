# Semantic Type Load Repair - Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `load-umls` able to repair `umls.semantic_type` on its own, and make it impossible for the command to report success after leaving that table incomplete.

**Architecture:** Two independent changes to the CLI's `load-umls` path plus a store helper. A `--semantic-types-only` flag skips the destructive truncate-and-reload of atoms and concepts, calling the existing (already idempotent) `LoadSemanticTypesAsync` alone. A completeness check compares `umls.semantic_type` against `umls.concept` and fails the command when semantic types are missing - the guard that would have caught the original failure in May 2026.

**Tech Stack:** .NET 8, VB.NET, Npgsql, PostgreSQL 16, xUnit + Testcontainers (`pgvector/pgvector:pg16`).

**Spec:** `docs/superpowers/specs/2026-07-20-semantic-type-repair-design.md` (phase 1 section only)

## Background the implementer needs

`public.eligibility` has 4,439,480 rows, of which 3,479,090 resolved rows have a
NULL `semantic_type`. The cause is that `umls.semantic_type` holds **100 rows
covering 49 CUIs**, against 1,265,171 rows in `umls.concept`. Resolution reads
semantic types from that table when `Umls:Backend = "postgres"`, so they stopped
being populated when the backend was switched.

**The source `MRSTY.RRF` is intact** (212,736,397 bytes, ~3.877M lines). The
mechanism that produced the 100 rows is *not established* - see the spec. Two
candidates: an interrupted load, or the final INSERT running while
`umls.concept` was only partially populated. **This plan does not try to
determine which.** It makes the repair possible and makes the failure loud.

**This phase does not fix the 3.48M corpus rows.** That is phase 2's backfill.
Phase 1 only makes `umls.semantic_type` correct and trustworthy.

## Global Constraints

- Branch `feat/semantic-type-repair` already exists off `origin/main` with the spec committed. Never commit to `main`.
- **ASCII only** in every authored file - no em dashes, en dashes, curly quotes, or ellipsis characters. Use a plain hyphen `-`. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8.
- **Never write files with PowerShell `Set-Content`/`Out-File`** - PS 5.1 adds a UTF-8 BOM. Use the Edit/Write tools.
- **Never pass a multi-line commit message containing double quotes through a PowerShell here-string** - it breaks argument parsing. Use `git commit -F <file>` for anything non-trivial.
- Platform is Windows/PowerShell. Use `$env:VAR` syntax.
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`, never `dotnet build` alone.
- **No migration in this phase.** The `umls.semantic_type` schema is unchanged (its PK change is phase 2). Therefore **no** `docs/specs/database_schema.md` edit, and the version bump is **build only**.
- `Option Strict On` / `Option Infer On` for VB - inherited from `Directory.Build.props`.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `EligibilityProcessing.Data/UmlsMetathesaurusStore.vb` | New `GetLoadCompletenessAsync` helper | 1 |
| `EligibilityProcessing.Cli/Program.vb` | `--semantic-types-only` flag, assertion wiring, exit codes, help text | 2, 3 |
| `Data.Tests/UmlsMetathesaurusIntegrationTests.vb` | Store-level tests (real Postgres) | 1 |
| `Data.Tests/CliCompositionTests.vb` | Arg-parsing tests, no DB | 2 |
| `deploy/eligibility-pipeline/umls-loader.md` | Runbook: new flag, new failure mode, repair procedure | 4 |
| `contexts/eligibility/version.json` | Build bump + release note | 4 |

---

### Task 1: Completeness check in the store

The assertion needs a single source of truth that both the CLI and (in phase 2)
the backfill command can call. It lives on the store, next to `CountAsync`.

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb` (add after `CountAsync`, which ends ~line 428)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsMetathesaurusIntegrationTests.vb`

**Interfaces:**
- Consumes: nothing.
- Produces: `UmlsMetathesaurusStore.GetLoadCompletenessAsync(cancellationToken) As Task(Of UmlsLoadCompleteness)`, and the class `UmlsLoadCompleteness` with `ConceptCount As Long`, `SemanticTypeRowCount As Long`, `SemanticTypeCuiCount As Long`, `IsComplete As Boolean`, `Describe() As String`.

**Why this rule:** every UMLS concept carries at least one semantic type in
MRSTY, and `LoadSemanticTypesAsync` filters to CUIs already in `umls.concept`.
So after a correct load, `SemanticTypeCuiCount` should equal `ConceptCount` and
`SemanticTypeRowCount` should be greater than or equal to it. The current state
(100 rows / 49 CUIs against 1,265,171 concepts) fails both by four orders of
magnitude.

The check keys on **CUI coverage**, not raw rows. A raw-row rule would pass if
one CUI somehow had a million semantic types; CUI coverage is what the resolver
actually depends on.

- [ ] **Step 1: Write the failing tests**

Append to `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsMetathesaurusIntegrationTests.vb`, before the final `End Class`:

```vb
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
    ' would refuse to run on a fresh database, where 0 >= 0 is the correct answer.
    <SkippableFact>
    Public Async Function LoadCompleteness_is_complete_for_an_empty_store() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)

        Dim c = Await store.GetLoadCompletenessAsync(CancellationToken.None)

        Assert.Equal(0L, c.ConceptCount)
        Assert.True(c.IsComplete)
    End Function
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~LoadCompleteness"
```

Expected: BUILD FAILURE - `'GetLoadCompletenessAsync' is not a member of 'UmlsMetathesaurusStore'`.

If Docker is unavailable these will report Skipped once they compile. Confirm they at least **compile and skip** rather than fail. Docker was available in this repo's last verified run, so prefer to start Docker Desktop and get real coverage.

- [ ] **Step 3: Implement the store helper**

In `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb`, insert after `CountAsync` ends (~line 428) and before the loader-writes region:

```vb
    ''' <summary>
    ''' Concept vs semantic-type coverage for the local mirror, used to assert a
    ''' load actually completed.
    ''' </summary>
    ''' <remarks>
    ''' Keys on CUI COVERAGE, not raw row count. Every UMLS concept carries at
    ''' least one semantic type in MRSTY, and LoadSemanticTypesAsync filters to
    ''' CUIs already present in umls.concept - so after a correct load every
    ''' concept CUI must appear in umls.semantic_type. A raw-row rule would pass
    ''' if one CUI had a large number of semantic types while the rest had none.
    '''
    ''' Exists because a May 2026 load left 100 rows covering 49 CUIs against
    ''' 1,265,171 concepts and reported success. Nothing detected it for two
    ''' months, and 3.48M eligibility rows were written with no semantic type.
    ''' </remarks>
    Public Async Function GetLoadCompletenessAsync(
            cancellationToken As CancellationToken) As Task(Of UmlsLoadCompleteness)

        Const Sql As String = "
SELECT (SELECT count(*) FROM umls.concept)                AS concept_count,
       (SELECT count(*) FROM umls.semantic_type)          AS sty_rows,
       (SELECT count(DISTINCT cui) FROM umls.semantic_type) AS sty_cuis"

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                    Return New UmlsLoadCompleteness(
                            conceptCount:=reader.GetInt64(0),
                            semanticTypeRowCount:=reader.GetInt64(1),
                            semanticTypeCuiCount:=reader.GetInt64(2))
                End Using
            End Using
        End Using
    End Function
```

Then create the result class. Put it at the **end of the same file**, after the
closing `End Class` of `UmlsMetathesaurusStore`, alongside the existing
`AtomRow` / `SemanticTypeRow` row types:

```vb
''' <summary>
''' Outcome of <see cref="UmlsMetathesaurusStore.GetLoadCompletenessAsync"/>.
''' </summary>
Public NotInheritable Class UmlsLoadCompleteness

    Public Sub New(conceptCount As Long, semanticTypeRowCount As Long, semanticTypeCuiCount As Long)
        Me.ConceptCount = conceptCount
        Me.SemanticTypeRowCount = semanticTypeRowCount
        Me.SemanticTypeCuiCount = semanticTypeCuiCount
    End Sub

    Public ReadOnly Property ConceptCount As Long
    Public ReadOnly Property SemanticTypeRowCount As Long
    Public ReadOnly Property SemanticTypeCuiCount As Long

    ''' <summary>
    ''' True when every concept CUI has at least one semantic type. An empty
    ''' store is vacuously complete - otherwise the check would refuse to run
    ''' against a fresh database, where 0 of 0 is the correct answer.
    ''' </summary>
    Public ReadOnly Property IsComplete As Boolean
        Get
            Return SemanticTypeCuiCount >= ConceptCount
        End Get
    End Property

    ''' <summary>
    ''' Operator-facing summary. Carries both numbers deliberately: "incomplete"
    ''' alone does not distinguish a near-miss from a total failure.
    ''' </summary>
    Public Function Describe() As String
        Return $"umls.concept has {ConceptCount:N0} concepts; umls.semantic_type covers " &
               $"{SemanticTypeCuiCount:N0} of them ({SemanticTypeRowCount:N0} rows)."
    End Function

End Class
```

Check the existing file for `Imports System.Threading` / `Imports System.Threading.Tasks` at the top; add whichever is missing.

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~LoadCompleteness"
```

Expected: 4 tests PASS (or 4 Skipped without Docker).

- [ ] **Step 5: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsMetathesaurusIntegrationTests.vb
git commit -m "Add UMLS load-completeness check keyed on concept CUI coverage"
```

---

### Task 2: `--semantic-types-only` flag

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb` - `RunLoadUmlsAsync` (lines 501-535) and the help text (~line 903)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/CliCompositionTests.vb`

**Interfaces:**
- Consumes: `GetLoadCompletenessAsync` / `UmlsLoadCompleteness` from Task 1 (used in Task 3, not here).
- Produces: `load-umls --semantic-types-only`, which requires only `MRSTY.RRF` to exist and never calls `TruncateAsync` or `BulkLoadAtomsAsync`.

**Why this exists:** the current command calls `TruncateAsync` first, wiping
`umls.atom` (3.23M rows) and `umls.concept` (1.27M rows) before reloading them.
Both of those tables are **healthy in production**; only semantic types are
broken. Rebuilding two good tables to repair a third is wasteful, and during the
rebuild UMLS resolution returns nothing at all - so the destructive path cannot
be used against a live system.

`LoadSemanticTypesAsync` is already safe to call alone: it stages into a temp
table and inserts with `ON CONFLICT (cui, sty) DO NOTHING`, touching only
`umls.semantic_type`.

- [ ] **Step 1: Write the failing test**

`CliCompositionTests.vb` covers CLI wiring without a database. Append before its final `End Class`:

```vb
    ' ============ load-umls --semantic-types-only ============
    '
    ' The repair path. The default load TRUNCATEs umls.atom and umls.concept
    ' before reloading them; in production those two tables are healthy and only
    ' semantic types are broken, so the destructive path is both wasteful and
    ' unusable against a live system (resolution returns nothing mid-rebuild).

    <Fact>
    Public Sub SemanticTypesOnly_flag_is_detected()
        Assert.True(Program.IsSemanticTypesOnly({"load-umls", "--rrf-dir", "D:\umls", "--semantic-types-only"}))
    End Sub

    <Fact>
    Public Sub SemanticTypesOnly_flag_is_case_insensitive()
        Assert.True(Program.IsSemanticTypesOnly({"load-umls", "--SEMANTIC-TYPES-ONLY"}))
    End Sub

    <Fact>
    Public Sub SemanticTypesOnly_is_false_when_absent()
        Assert.False(Program.IsSemanticTypesOnly({"load-umls", "--rrf-dir", "D:\umls"}))
    End Sub

    ' Guards against a prefix match treating an unrelated flag as the real one.
    <Fact>
    Public Sub SemanticTypesOnly_does_not_match_a_different_flag()
        Assert.False(Program.IsSemanticTypesOnly({"load-umls", "--semantic-types-only-please"}))
    End Sub
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~SemanticTypesOnly"
```

Expected: BUILD FAILURE - `'IsSemanticTypesOnly' is not a member of 'Program'`.

- [ ] **Step 3: Add the flag helper**

The existing flag idiom in this file is an inline
`args.Any(Function(a) String.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))`
(see lines 616 and 698). Extract it as a named, testable method so the test above
can reach it.

In `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb`, add next to `GetOptionValue` (~line 840):

```vb
    ''' <summary>
    ''' True when --semantic-types-only is present. Exact match (not a prefix),
    ''' so an unrelated flag beginning with the same text does not trigger it.
    ''' Friend rather than Private so CliCompositionTests can exercise it without
    ''' a database.
    ''' </summary>
    Friend Function IsSemanticTypesOnly(args As String()) As Boolean
        Return args.Any(Function(a) String.Equals(a, "--semantic-types-only", StringComparison.OrdinalIgnoreCase))
    End Function
```

**Do not write `Friend Shared`.** `Program` is a VB `Module`
(`Program.vb:110`), and module members are implicitly shared - an explicit
`Shared` is a compile error there. This matches the existing
`Friend Function ParseStudyCount(args As String()) As Integer` at
`Program.vb:862`.

`Friend` is visible to the test project because
`EligibilityProcessing.Cli.vbproj:29` already declares
`<InternalsVisibleTo Include="EligibilityProcessing.Data.Tests" />`. No project
change is needed. `args.Any` needs `System.Linq`, which the file already imports
(see the existing `--dry-run` checks at lines 616 and 698).

- [ ] **Step 4: Run the test to verify it passes**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~SemanticTypesOnly"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Branch the load path on the flag**

Replace the body of `RunLoadUmlsAsync` (`Program.vb:501-535`) with:

```vb
    Private Async Function RunLoadUmlsAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim semanticTypesOnly = IsSemanticTypesOnly(args)

        Dim rrfDir = GetOptionValue(args, "--rrf-dir")
        If String.IsNullOrWhiteSpace(rrfDir) Then
            System.Console.Error.WriteLine("load-umls requires --rrf-dir <path> (unpacked UMLS release with MRCONSO.RRF + MRSTY.RRF).")
            Return 1
        End If
        Dim mrconso = Path.Combine(rrfDir, "MRCONSO.RRF")
        Dim mrsty = Path.Combine(rrfDir, "MRSTY.RRF")
        ' --semantic-types-only never reads MRCONSO, so do not demand it: the
        ' repair must work from an MRSTY-only directory.
        If Not semanticTypesOnly AndAlso Not File.Exists(mrconso) Then
            System.Console.Error.WriteLine($"Not found: {mrconso}") : Return 1
        End If
        If Not File.Exists(mrsty) Then System.Console.Error.WriteLine($"Not found: {mrsty}") : Return 1

        Dim store = appHost.Services.GetRequiredService(Of UmlsMetathesaurusStore)()
        Dim opts = appHost.Services.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
        Dim sabs = If(opts.SourceVocabularies, Array.Empty(Of String)())

        Dim atoms As Long = 0
        Dim concepts As Long = 0

        System.Console.WriteLine($"Loading UMLS from {rrfDir}")
        If semanticTypesOnly Then
            ' Repair path: leave umls.atom and umls.concept untouched. Safe to run
            ' against a live system - LoadSemanticTypesAsync stages into a temp
            ' table and inserts ON CONFLICT DO NOTHING, touching only
            ' umls.semantic_type.
            System.Console.WriteLine("  --semantic-types-only: skipping truncate, atoms and concepts")
            atoms = Await store.CountAsync("umls.atom", cancellationToken).ConfigureAwait(False)
            concepts = Await store.CountAsync("umls.concept", cancellationToken).ConfigureAwait(False)
            If concepts = 0 Then
                System.Console.Error.WriteLine(
                    "umls.concept is empty. --semantic-types-only filters MRSTY to CUIs already in umls.concept, " &
                    "so this would load nothing. Run a full load-umls first.")
                Return 1
            End If
        Else
            System.Console.WriteLine($"  vocabularies: {If(sabs.Length = 0, "(all English)", String.Join(", ", sabs))}")
            System.Console.WriteLine("  truncating umls.* ...")
            Await store.TruncateAsync(cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine("  COPY atoms from MRCONSO.RRF ...")
            atoms = Await store.BulkLoadAtomsAsync(UmlsRrfReader.ReadAtoms(mrconso, sabs), cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine($"    {atoms:N0} atoms")
            System.Console.WriteLine("  building umls.concept (preferred names) ...")
            concepts = Await store.RebuildConceptTableAsync(cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine($"    {concepts:N0} concepts")
        End If

        System.Console.WriteLine("  loading semantic types from MRSTY.RRF ...")
        Dim stys = Await store.LoadSemanticTypesAsync(UmlsRrfReader.ReadSemanticTypes(mrsty), cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine($"    {stys:N0} semantic-type rows")
        System.Console.WriteLine($"Done. atoms={atoms:N0} concepts={concepts:N0} semantic_types={stys:N0}.")
        Return 0
    End Function
```

Two behaviours worth noting for review:

- The empty-`umls.concept` guard exists because `LoadSemanticTypesAsync` filters
  `WHERE cui IN (SELECT cui FROM umls.concept)`. With an empty concept table the
  command would load **zero rows and report success** - the exact failure shape
  this phase exists to eliminate.
- `atoms` and `concepts` are read from the database on the repair path so the
  closing summary line stays truthful rather than printing zeros.

- [ ] **Step 6: Update the help text**

In `Program.vb` (~line 903), replace the `load-umls` help block with:

```vb
        System.Console.WriteLine("  EligibilityProcessing.Cli load-umls --rrf-dir <path> [--semantic-types-only]")
        System.Console.WriteLine("      Load a curated UMLS subset into the umls.* schema from an unpacked")
        System.Console.WriteLine("      release (MRCONSO.RRF + MRSTY.RRF). Full rebuild per release. Backs the")
        System.Console.WriteLine("      Umls:Backend=postgres resolver. Run on a build box, then pg_dump/restore.")
        System.Console.WriteLine("      --semantic-types-only reloads umls.semantic_type alone from MRSTY.RRF,")
        System.Console.WriteLine("      leaving atoms and concepts untouched. Use to repair a partial load")
        System.Console.WriteLine("      without rebuilding healthy tables; safe against a running system.")
        System.Console.WriteLine("      The command fails if semantic types do not cover every loaded concept.")
```

- [ ] **Step 7: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/CliCompositionTests.vb
git commit -m "Add load-umls --semantic-types-only repair path"
```

---

### Task 3: Make the command fail on an incomplete load

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb` - end of `RunLoadUmlsAsync`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsMetathesaurusIntegrationTests.vb`

**Interfaces:**
- Consumes: `store.GetLoadCompletenessAsync(...)` returning `UmlsLoadCompleteness` with `.IsComplete` and `.Describe()` (Task 1); the `RunLoadUmlsAsync` shape from Task 2.
- Produces: `load-umls` returns exit code `2` on an incomplete load.

**Exit-code integrity.** The spec asks whether `load-umls` can currently return 0
after leaving semantic types incomplete. **It can** - the existing body has no
check at all between the final count and `Return 0`. So whatever the original
mechanism was, the command had no way to notice. Exit code `2` distinguishes
"ran but produced a bad result" from `1` ("bad arguments / missing file"),
which matters for the `retry-umls-loop.ps1` style wrappers in `deploy/`.

- [ ] **Step 1: Write the failing test**

Append to `UmlsMetathesaurusIntegrationTests.vb` before the final `End Class`:

```vb
    ' The load reports what it wrote, but the completeness check is what decides
    ' success. This asserts the two agree on a healthy load - the CLI's exit-code
    ' branch is exercised manually (see the plan's verification step), since the
    ' CLI entry point needs a full host.
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

    ' MRSTY rows for CUIs outside the curated atom subset are filtered out by
    ' LoadSemanticTypesAsync. Coverage must still be judged against the concepts
    ' that WERE loaded, not against the input file, or every curated load would
    ' look incomplete.
    <SkippableFact>
    Public Async Function LoadCompleteness_ignores_semantic_types_for_unloaded_concepts() As Task
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
        Assert.Equal(1L, c.SemanticTypeCuiCount)   ' C9999999 was filtered out
        Assert.True(c.IsComplete)
    End Function
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~LoadCompleteness"
```

Expected: the two new tests FAIL (or the file fails to compile if Task 1 is not yet applied). The four from Task 1 should still pass.

- [ ] **Step 3: Wire the assertion into the CLI**

In `Program.vb`, replace the last three lines of `RunLoadUmlsAsync` (from
`System.Console.WriteLine($"    {stys:N0} semantic-type rows")` through
`Return 0`) with:

```vb
        System.Console.WriteLine($"    {stys:N0} semantic-type rows")

        ' The load reporting a row count is not evidence it worked. In May 2026 a
        ' load left umls.semantic_type with 100 rows covering 49 CUIs against
        ' 1,265,171 concepts, returned success, and nothing noticed for two
        ' months - by which point 3.48M eligibility rows had been written with no
        ' semantic type. Exit 2 (not 1) so wrapper scripts can distinguish "ran
        ' but produced a bad result" from "bad arguments".
        Dim completeness = Await store.GetLoadCompletenessAsync(cancellationToken).ConfigureAwait(False)
        If Not completeness.IsComplete Then
            System.Console.Error.WriteLine("LOAD INCOMPLETE - " & completeness.Describe())
            System.Console.Error.WriteLine(
                "Every UMLS concept has at least one semantic type, so coverage should be total. " &
                "Re-run with --semantic-types-only against a complete MRSTY.RRF.")
            Return 2
        End If

        System.Console.WriteLine($"Done. atoms={atoms:N0} concepts={concepts:N0} semantic_types={stys:N0}.")
        System.Console.WriteLine("  " & completeness.Describe())
        Return 0
    End Function
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~LoadCompleteness"
```

Expected: 6 tests PASS (4 from Task 1 + 2 here), or 6 Skipped without Docker.

- [ ] **Step 5: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/UmlsMetathesaurusIntegrationTests.vb
git commit -m "Fail load-umls with exit 2 when semantic types do not cover every concept"
```

---

### Task 4: Runbook, version bump, and the real repair

**Files:**
- Modify: `deploy/eligibility-pipeline/umls-loader.md`
- Modify: `contexts/eligibility/version.json`
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/VersionWebTests.cs`

- [ ] **Step 1: Update the runbook**

In `deploy/eligibility-pipeline/umls-loader.md`, replace the sanity-check block
in section 1 (currently lines 47-54) with:

```markdown
`load-umls` prints atom / concept / semantic-type counts **and asserts that
semantic types cover every loaded concept**. It exits `2` if they do not - a
non-zero exit here means the umls schema is not fit to ship, whatever the printed
counts say.

```sql
SELECT count(*) FROM umls.atom;            -- millions (curated subset)
SELECT count(*) FROM umls.concept;         -- ~ unique CUIs loaded
SELECT count(*) FROM umls.semantic_type;   -- >= concept count
SELECT count(DISTINCT cui) FROM umls.semantic_type;  -- == concept count
SELECT cui, pref_name, root_source FROM umls.concept WHERE cui = 'C0020615';  -- Hypoglycemia
```

### Repairing a partial semantic-type load

If `umls.semantic_type` is short but atoms and concepts are healthy, reload
semantic types alone rather than rebuilding everything:

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- `
  load-umls --rrf-dir D:\umls\2025AB\META --semantic-types-only
```

This skips the TRUNCATE and the MRCONSO pass, so `umls.atom` and `umls.concept`
are untouched and resolution keeps working throughout. Only `MRSTY.RRF` needs to
be present.

> **This happened.** A May 2026 load left `umls.semantic_type` with 100 rows
> covering 49 CUIs against 1,265,171 concepts and reported success. It went
> unnoticed for two months, during which 3.48M `public.eligibility` rows were
> written with no semantic type. The assertion above exists so that a partial
> load fails loudly instead of shipping.
```

Also update section 5 ("Yearly / per-release refresh", line ~128) - it currently
states `load-umls` TRUNCATEs and repopulates, which is now only true of the
default path. Append: `--semantic-types-only is the exception: it repairs
umls.semantic_type in place without touching atoms or concepts.`

- [ ] **Step 2: Bump the version**

Read `contexts/eligibility/version.json` first to confirm the current build
number (34 at the time of writing; if `main` has moved, use `current.build + 1`).
Build-only bump - no migration in this phase.

Set `current` to:

```json
  "current": { "major": 0, "minor": 1, "build": 35, "releaseDate": "2026-07-20" },
```

and prepend to `releases`:

```json
    {
      "version": "0.1.35",
      "releaseDate": "2026-07-20",
      "enhancements": [
        "load-umls gained --semantic-types-only, which reloads umls.semantic_type alone from MRSTY.RRF without touching atoms or concepts - use it to repair a partial load without rebuilding healthy tables."
      ],
      "fixes": [
        "load-umls now fails (exit 2) when the loaded semantic types do not cover every concept, instead of reporting success. A partial load previously went undetected."
      ]
    },
```

Keep the file ASCII-only. `releases[0]` must match `current`.

- [ ] **Step 3: Update the version assertions**

`VersionWebTests.cs` hard-codes the version and release date, so a bump always
breaks it. There are **four version literals** (one `v`-prefixed, in the footer
test) and **one `ReleaseDate` assertion** - the date is easy to miss because only
one of the four tests checks it.

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~VersionWebTests"
```

Read the failures, then update with the Edit tool - **not** PowerShell string
replacement, which writes a UTF-8 BOM and produces a spurious first-line diff.

- [ ] **Step 4: Verify the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, 0 skipped if Docker is running. Test count up by 10 from baseline (6 store tests + 4 CLI arg tests).

- [ ] **Step 5: Run the actual repair**

This is the point of the phase. The production database is idle and backups were
taken 2026-07-20.

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- `
  load-umls --rrf-dir D:\umls\2025AB\META --semantic-types-only
```

Expect roughly **1.3M-1.8M rows** (1,265,171 concepts at ~1.0-1.4 semantic types
each) and **exit code 0**. Check it:

```powershell
echo $LASTEXITCODE
```

A non-zero exit means the assertion caught something - do not proceed to phase 2.

Confirm in the database:

```sql
SELECT count(*) AS sty_rows, count(DISTINCT cui) AS sty_cuis FROM umls.semantic_type;
SELECT count(*) AS concept_rows FROM umls.concept;   -- sty_cuis should equal this
SELECT sty, count(*) FROM umls.semantic_type GROUP BY sty ORDER BY 2 DESC LIMIT 10;
```

**Do not expect `public.eligibility` to change.** The 3.48M rows with NULL
`semantic_type` stay NULL until phase 2's backfill. This step fixes the source
table only.

- [ ] **Step 6: Commit, push, open the PR**

```powershell
git add contexts/eligibility/version.json contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/VersionWebTests.cs deploy/eligibility-pipeline/umls-loader.md
git commit -m "Bump version to 0.1.35 and document the semantic-type repair path"
git push -u origin feat/semantic-type-repair
```

Open the PR with `gh pr create --body-file <file>` (not an inline here-string -
double quotes in the body break PowerShell argument parsing). The body must state:

- The corpus impact: 3.48M resolved rows with no semantic type, and that **this
  phase does not fix them** - phase 2's backfill does.
- That the failure mechanism is **not established**; both candidate explanations
  and why the tidier one (interrupted COPY) does not fit.
- The measured repair result from step 5.

---

## Self-review notes

Deliberately **not** in this phase, all deferred to phase 2 per the spec: the
`umls.semantic_type` PK change to `(cui, tui)`, the `semantic_type_dim` table,
the `semantic_type_tuis` array column, and the corpus backfill.

The CLI's exit-code branch has **no automated test** - `RunLoadUmlsAsync` needs a
full `IHost` and real RRF files. Task 3 tests the completeness rule that drives
it at the store level, and step 5 exercises the success path for real. The
failure path is exercised only by the production state that prompted this work.
That gap is accepted rather than hidden; closing it would mean a CLI harness this
phase does not justify.

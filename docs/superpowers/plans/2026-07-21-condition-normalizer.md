# Condition Normalizer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Map the free-text condition strings on `public.eligibility_study_detail.conditions` to UMLS CUIs in a reusable dictionary table, so corpus analytics can slice by condition.

**Architecture:** A new dictionary table `public.condition_concept` keyed on the normalized condition string. Resolution runs in three tiers - an exact atom lookup that deliberately bypasses the scorer, a deterministic tie-break when the exact lookup is ambiguous, and the existing FTS/trigram search plus `UmlsMatchScorer` for everything else. The pipeline fills the dictionary lazily per trial; a Tools card and CLI verb backfill it in bulk.

**Tech Stack:** .NET 8, VB.NET (Core / Data / Cli), C# (Web), Npgsql, PostgreSQL 16, xUnit + Testcontainers.

**Spec:** [docs/superpowers/specs/2026-07-21-condition-normalizer-design.md](../specs/2026-07-21-condition-normalizer-design.md). Read section 2 before starting - it records the measurements that justify the tiering, and two of them (2.2 especially) will look wrong if you skim.

## Global Constraints

- **ASCII only** in every authored file - source, SQL, comments, commit messages. No em/en dashes, curly quotes, or ellipsis characters. Windows PowerShell 5.1 misreads them and has repeatedly broken `.ps1` parsing.
- **Never write files with PowerShell `Set-Content` / `Out-File`** - they add a BOM. Use the Write/Edit tools.
- `Option Strict On`, `Option Infer On`, `Nullable enable` come from [Directory.Build.props](../../../Directory.Build.props). Explicit conversions are required in VB.
- **Verification is `dotnet test contexts/eligibility/Eligibility.sln`**, never `dotnet build`. A green build with red tests is not a passing change.
- **Every new public function ships with a test in the same commit.**
- A new migration must be registered in **two** places or the whole Postgres suite fails: `MigrationResourceNames` in `PostgresGateway.vb` **and** an `<EmbeddedResource>` with an explicit `<LogicalName>` in `EligibilityProcessing.Data.vbproj`.
- A migration bumps at least the MINOR version and requires a matching update to [docs/specs/database_schema.md](../../specs/database_schema.md) in the same change.
- Work on branch `feat/condition-normalizer` (already created, spec already committed). Never commit to `main`.
- Do not change `UmlsMatchScorer.MatchThreshold` (0.45). The condition threshold is a separate constant.

---

## File Structure

**Create:**

| Path | Responsibility |
|---|---|
| `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V24__condition_concept.sql` | the table + indexes |
| `contexts/eligibility/src/EligibilityProcessing.Core/ConceptKey.vb` | the one normalization function, shared by VB and mirrored in SQL |
| `contexts/eligibility/src/EligibilityProcessing.Core/ConditionConcept.vb` | `ConditionConceptEntry`, `ConditionResolution`, `ConditionMatchTier`, `IConditionConceptStore` |
| `contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizer.vb` | the three-tier resolution logic |
| `contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizeJob.vb` | the bulk backfill job |
| `contexts/eligibility/src/EligibilityProcessing.Data/ConditionConceptStore.vb` | all `condition_concept` SQL |
| `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb` | tier logic, no DB |
| `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizeJobTests.vb` | job orchestration, fake store |
| `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb` | real Postgres |

**Modify:** `PostgresGateway.vb` (migration list), `EligibilityProcessing.Data.vbproj` (embedded resource), `UmlsMetathesaurusStore.vb` (delegate `NormalizeConcept`), `ToolJobs.vb`, `PipelineOrchestrator.vb`, `CompositionRoot.vb`, `Cli/Program.vb`, `Web/ToolJobRequest.cs`, `Web/ToolJobRunner.cs`, `Web/ToolJobState.cs`, `Web/Controllers/HomeController.cs`, `Web/Models/ToolsViewModel.cs`, `Web/Views/Home/Tools.cshtml`, `tests/.../PostgresFixture.vb`, `docs/specs/database_schema.md`, `contexts/eligibility/version.json`.

---

## Task 1: Migration V24 and the condition_concept table

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V24__condition_concept.sql`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:55-56`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/EligibilityProcessing.Data.vbproj:86-88`
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresFixture.vb` (ResetAsync TRUNCATE list)
- Modify: `docs/specs/database_schema.md`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb`

**Interfaces:**
- Produces: the table `public.condition_concept` with columns `condition_norm, raw_form, study_count, concept_code, umls_name, match_tier, match_score, resolved_at, created_at`, used by every later task.

- [ ] **Step 1: Write the failing test**

Create `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb`:

```vb
Imports System.Threading
Imports System.Threading.Tasks
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
End Class
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionConceptStoreTests"
```

Expected: FAIL. The assertion on `condition_norm` fails because the table does not exist, so `columns` is empty.

- [ ] **Step 3: Create the migration**

Create `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V24__condition_concept.sql`:

```sql
-- V24: condition -> UMLS CUI dictionary, for slicing corpus analytics by
-- condition.
--
-- public.eligibility_study_detail.conditions is raw AACT free text: 91,600
-- distinct strings over 611,329 mentions, unnormalized (COVID-19 and Covid19 are
-- separate entries), with the top 100 covering only 18 percent of mentions. It
-- cannot back an analytic dimension as-is.
--
-- Keyed on the NORMALIZED string, not on (nct_id, condition). Normalization is
-- study-independent, so a dictionary is ~90,076 rows rather than 611,329, is
-- re-runnable, and lets a new pipeline run reuse every earlier resolution.
--
-- Resolution tiers (see the design spec, section 5):
--   exact           - one CUI from an exact umls.atom.str_norm match; 63.0% of
--                     mentions. The scorer is deliberately NOT consulted here.
--   exact_ambiguous - exact match, several CUIs, tie-broken; 8.4% of mentions.
--   fuzzy           - FTS/trigram + UmlsMatchScorer at >= 0.60; the remainder.
--   unresolved      - attempted and rejected. resolved_at is set, concept_code
--                     is NULL, so a re-run skips it unless forced.
--
-- Idempotent - CREATE ... IF NOT EXISTS, so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.condition_concept (
    -- ConceptKey.Normalize(raw): lower-invariant, internal whitespace collapsed,
    -- trimmed. The SQL mirror of that function is
    -- regexp_replace(btrim(lower(x)), '\s+', ' ', 'g') and the two are asserted
    -- to agree by ConditionConceptStoreTests.
    condition_norm text         NOT NULL PRIMARY KEY,
    -- The most frequent ORIGINAL casing of this normalized string, and the
    -- string handed to the matcher. Load-bearing: UmlsMatchScorer's acronym
    -- term only fires on a query matching ^[A-Z0-9]{2,6}$, and the corpus holds
    -- COPD (657 studies), Copd (93), Hiv (258), Nsclc (13). Feeding the
    -- lowercased key would silently disable acronym matching.
    raw_form       text         NOT NULL,
    -- Corpus frequency. Orders backfill work so a cancelled run still leaves the
    -- corpus better off. Recomputed in bulk, NOT incrementally maintained by the
    -- pipeline - see the spec section 6.1.
    study_count    integer      NOT NULL DEFAULT 0,
    concept_code   text         NULL,
    umls_name      text         NULL,
    -- exact | exact_ambiguous | fuzzy | unresolved.
    -- NOT called match_source: public.eligibility.match_source and
    -- UmlsMatch.MatchSource both mean the ROOT SOURCE VOCABULARY (MSH,
    -- SNOMEDCT_US). Reusing that name for a tier label would mislead the
    -- analytics joins this table exists to serve.
    match_tier     text         NOT NULL DEFAULT 'unresolved',
    -- numeric(4,3) to match the criteria pipeline's end-to-end typing of match
    -- scores. 0 when unresolved.
    match_score    numeric(4,3) NOT NULL DEFAULT 0,
    -- NULL means never attempted. Set even on failure, so a re-run skips it
    -- unless --force is passed.
    resolved_at    timestamptz  NULL,
    created_at     timestamptz  NOT NULL DEFAULT now()
);

-- Serves the analytics join: condition_concept -> concept_code -> eligibility.
CREATE INDEX IF NOT EXISTS ix_condition_concept_code
    ON public.condition_concept (concept_code)
    WHERE concept_code IS NOT NULL;

-- Serves the backfill's "highest-value unresolved work first" ordering.
CREATE INDEX IF NOT EXISTS ix_condition_concept_pending
    ON public.condition_concept (study_count DESC)
    WHERE resolved_at IS NULL;
```

- [ ] **Step 4: Register the migration in both required places**

In `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb`, add a comma to line 55 and append:

```vb
            "EligibilityProcessing.Data.Migrations.V23__concept_hierarchy.sql",
            "EligibilityProcessing.Data.Migrations.V24__condition_concept.sql"
        }
```

In `contexts/eligibility/src/EligibilityProcessing.Data/EligibilityProcessing.Data.vbproj`, after the V23 entry:

```xml
    <EmbeddedResource Include="Migrations\V24__condition_concept.sql">
      <LogicalName>EligibilityProcessing.Data.Migrations.V24__condition_concept.sql</LogicalName>
    </EmbeddedResource>
```

Forgetting the second is the exact failure that silently skipped ~350 tests twice during the V22/V23 work. It is now a loud failure, but it is still a wasted cycle.

- [ ] **Step 5: Add the table to the fixture reset**

In `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresFixture.vb`, inside the `ResetAsync` TRUNCATE list, add `public.condition_concept,` immediately after `public.eligibility_study_detail,`:

```vb
                    public.eligibility_study_detail,
                    public.condition_concept,
                    public.authoring_study,
```

- [ ] **Step 6: Run the test to verify it passes**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionConceptStoreTests"
```

Expected: PASS.

- [ ] **Step 7: Update the schema doc**

In [docs/specs/database_schema.md](../../specs/database_schema.md):
- Add a `### public.condition_concept` section after `### public.eligibility_study_embedding` (currently line 238), documenting every column above with its purpose, plus both indexes.
- Add a row to the migration-history table (section starting line 606): `V24__condition_concept` with a one-line description.

- [ ] **Step 8: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V24__condition_concept.sql contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb contexts/eligibility/src/EligibilityProcessing.Data/EligibilityProcessing.Data.vbproj contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresFixture.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb docs/specs/database_schema.md
git commit -m "Add V24 condition_concept dictionary table"
```

---

## Task 2: Shared normalization key

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/ConceptKey.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb:38-41`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb`

**Interfaces:**
- Produces: `EligibilityProcessing.Core.ConceptKey.Normalize(value As String) As String`.
- Consumes: nothing.

The spec requires the dictionary key to use the *same* normalization that stamped `umls.atom.str_norm`, so the two can never drift. That function lives in `UmlsMetathesaurusStore` (Data), and Core cannot reference Data. Move the body to Core and have Data delegate - one line, no behaviour change, all three existing callers untouched.

- [ ] **Step 1: Write the failing test**

Create `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb`:

```vb
Imports EligibilityProcessing.Core
Imports Xunit

Public Class ConditionNormalizerTests

    <Theory>
    <InlineData("COPD", "copd")>
    <InlineData("  Breast   Cancer  ", "breast cancer")>
    <InlineData("Non" & vbTab & "Small Cell", "non small cell")>
    <InlineData("", "")>
    <InlineData("   ", "")>
    Public Sub Normalize_lowercases_trims_and_collapses_whitespace(input As String, expected As String)
        Assert.Equal(expected, ConceptKey.Normalize(input))
    End Sub

    <Fact>
    Public Sub Normalize_is_idempotent()
        Dim once = ConceptKey.Normalize("Gastrointestinal   Bleeding")
        Assert.Equal(once, ConceptKey.Normalize(once))
    End Sub
End Class
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~ConditionNormalizerTests"
```

Expected: FAIL to compile - `ConceptKey` is not defined.

- [ ] **Step 3: Create the Core function**

Create `contexts/eligibility/src/EligibilityProcessing.Core/ConceptKey.vb`:

```vb
Imports System.Text.RegularExpressions

''' <summary>
''' The single normalization used for exact concept matching: lower-invariant,
''' internal whitespace collapsed to single spaces, trimmed.
'''
''' This is the form stored as umls.atom.str_norm by the loader, applied to the
''' query by the lookup, and used as the primary key of
''' public.condition_concept. All three MUST go through this one function or an
''' exact match silently stops aligning.
'''
''' The SQL mirror is regexp_replace(btrim(lower(x)), '\s+', ' ', 'g'); the two
''' are asserted to agree by ConditionConceptStoreTests.
''' </summary>
Public Module ConceptKey

    Private ReadOnly WhitespaceRegex As New Regex("\s+", RegexOptions.Compiled)

    Public Function Normalize(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return ""
        Return WhitespaceRegex.Replace(value.Trim().ToLowerInvariant(), " ")
    End Function

End Module
```

- [ ] **Step 4: Delegate from the Data-layer function**

In `contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb`, replace the body at lines 38-41:

```vb
    Public Shared Function NormalizeConcept(value As String) As String
        Return ConceptKey.Normalize(value)
    End Function
```

Leave the XML doc comment above it in place, and leave `WhitespaceRegex` declared at line 23 alone - `NonWordRegex` (line 24) is unrelated and `WhitespaceRegex` may have other uses in that file. If the compiler warns that `WhitespaceRegex` is now unused, delete only that one field.

- [ ] **Step 5: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionNormalizerTests|FullyQualifiedName~UmlsLoaderUnitTests"
```

Expected: PASS. `UmlsLoaderUnitTests` already asserts `NormalizeConcept` behaviour and must be unaffected - that is the regression guard for this move.

- [ ] **Step 6: Add the cross-language agreement test**

Append to `ConditionConceptStoreTests.vb`. This is the test that catches the VB function and the SQL mirror drifting apart, which would silently halve the exact-match rate:

```vb
    <SkippableFact>
    Public Async Function Sql_normalization_matches_ConceptKey_Normalize() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim samples = {"COPD", "  Breast   Cancer  ", "Non-Small Cell", "COVID-19",
                       "Type" & vbTab & "2 Diabetes", "hiv/aids"}

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
```

Add `Imports EligibilityProcessing.Core` to the top of `ConditionConceptStoreTests.vb`.

- [ ] **Step 7: Run and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionConceptStoreTests"
git add contexts/eligibility/src/EligibilityProcessing.Core/ConceptKey.vb contexts/eligibility/src/EligibilityProcessing.Data/UmlsMetathesaurusStore.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb
git commit -m "Move concept normalization into Core so the condition key cannot drift"
```

---

## Task 3: Condition types and the store port

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/ConditionConcept.vb`
- Test: none of its own (types are exercised by Task 4)

**Interfaces:**
- Produces: `ConditionMatchTier`, `ConditionResolution`, `ConditionConceptEntry`, `IConditionConceptStore` - all consumed by Tasks 4, 5, 6, 7, 8.

- [ ] **Step 1: Create the file**

Create `contexts/eligibility/src/EligibilityProcessing.Core/ConditionConcept.vb`:

```vb
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>The four resolution tiers stored in condition_concept.match_tier.</summary>
Public Module ConditionMatchTier
    ''' <summary>Exact umls.atom.str_norm match resolving to exactly one CUI.</summary>
    Public Const Exact As String = "exact"
    ''' <summary>Exact match resolving to several CUIs, tie-broken deterministically.</summary>
    Public Const ExactAmbiguous As String = "exact_ambiguous"
    ''' <summary>No exact atom; resolved by FTS/trigram search plus the scorer.</summary>
    Public Const Fuzzy As String = "fuzzy"
    ''' <summary>Attempted and rejected.</summary>
    Public Const Unresolved As String = "unresolved"
End Module

''' <summary>
''' The outcome of resolving one condition string.
'''
''' Deliberately NOT UmlsMatch: that type's MatchSource property means the root
''' source vocabulary (MSH, SNOMEDCT_US), whereas Tier here means which of the
''' three resolution paths produced the answer.
''' </summary>
Public NotInheritable Class ConditionResolution

    Public Sub New(conceptCode As String, umlsName As String, tier As String, score As Double)
        Me.ConceptCode = conceptCode
        Me.UmlsName = umlsName
        Me.Tier = tier
        Me.Score = score
    End Sub

    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property UmlsName As String
    Public ReadOnly Property Tier As String
    Public ReadOnly Property Score As Double

    Public ReadOnly Property IsResolved As Boolean
        Get
            Return Not String.IsNullOrEmpty(ConceptCode)
        End Get
    End Property

    Public Shared ReadOnly Unresolved As New ConditionResolution(
            conceptCode:="", umlsName:="", tier:=ConditionMatchTier.Unresolved, score:=0.0)

End Class

''' <summary>One row of public.condition_concept.</summary>
Public NotInheritable Class ConditionConceptEntry
    Public Property ConditionNorm As String = ""
    Public Property RawForm As String = ""
    Public Property StudyCount As Integer
    ''' <summary>Empty when unresolved.</summary>
    Public Property ConceptCode As String = ""
    Public Property UmlsName As String = ""
    Public Property MatchTier As String = ConditionMatchTier.Unresolved
    Public Property MatchScore As Double
End Class

''' <summary>
''' Data-access port for the condition dictionary. Implemented by
''' EligibilityProcessing.Data.ConditionConceptStore; faked in Core unit tests so
''' the tier logic can be tested without Postgres.
''' </summary>
Public Interface IConditionConceptStore

    ''' <summary>
    ''' Distinct CUIs whose atoms exactly match <paramref name="conditionNorm"/>
    ''' on umls.atom.str_norm, each carrying the concept's preferred name. Empty
    ''' when there is no exact atom.
    ''' </summary>
    Function LookupExactAsync(conditionNorm As String,
                              cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate))

    ''' <summary>Insert or update one dictionary row, stamping resolved_at.</summary>
    Function UpsertAsync(entry As ConditionConceptEntry,
                         cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Raw condition strings on this study that have no dictionary row yet.
    ''' Empty (the steady-state case) means one indexed query and no work.
    ''' </summary>
    Function GetUnseenConditionsForStudyAsync(nctId As String,
                                              cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

    ''' <summary>
    ''' Insert a dictionary row for every distinct normalized condition string in
    ''' the corpus that lacks one, and refresh raw_form + study_count on every
    ''' row. Returns the number of rows inserted. Idempotent.
    ''' </summary>
    Function SeedFromCorpusAsync(cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' Rows still needing resolution, highest study_count first. When
    ''' <paramref name="force"/> is True every row is returned, not just those
    ''' with resolved_at IS NULL.
    ''' </summary>
    Function GetPendingAsync(limit As Integer, force As Boolean,
                             cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConditionConceptEntry))

    ''' <summary>Count matching GetPendingAsync, for the Tools card headline.</summary>
    Function CountPendingAsync(force As Boolean,
                               cancellationToken As CancellationToken) As Task(Of Integer)

End Interface
```

- [ ] **Step 2: Verify it compiles**

```powershell
dotnet build contexts/eligibility/src/EligibilityProcessing.Core
```

Expected: build succeeds. (Build-only is acceptable here because this task adds no behaviour; the next task's tests cover it.)

- [ ] **Step 3: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/ConditionConcept.vb
git commit -m "Add condition dictionary types and store port"
```

---

## Task 4: The three-tier ConditionNormalizer

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizer.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb` (append)

**Interfaces:**
- Consumes: `IConditionConceptStore`, `IUmlsClient`, `UmlsMatchScorer`, `ConceptKey.Normalize`, `ConditionResolution`, `ConditionMatchTier`.
- Produces: `ConditionNormalizer.FuzzyThreshold As Double`, `ConditionNormalizer.ResolveAsync(rawForm, ct) As Task(Of ConditionResolution)`, `ConditionNormalizer.EnsureForStudyAsync(nctId, ct) As Task(Of Integer)`.

This is the heart of the change. Read spec section 2.2 first: tier 1 must **not** call the scorer.

- [ ] **Step 1: Write the failing tests**

Append to `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb`. Add these imports at the top of the file:

```vb
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
```

Then add the fakes and tests inside the class:

```vb
    ' ---------- fakes ----------

    Private NotInheritable Class FakeStore
        Implements IConditionConceptStore

        Public Property ExactByNorm As New Dictionary(Of String, IReadOnlyList(Of UmlsCandidate))
        Public Property Upserted As New List(Of ConditionConceptEntry)
        Public Property UnseenByStudy As New Dictionary(Of String, IReadOnlyList(Of String))

        Public Function LookupExactAsync(conditionNorm As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IConditionConceptStore.LookupExactAsync
            Dim hit As IReadOnlyList(Of UmlsCandidate) = Nothing
            If ExactByNorm.TryGetValue(conditionNorm, hit) Then Return Task.FromResult(hit)
            Return Task.FromResult(Of IReadOnlyList(Of UmlsCandidate))(Array.Empty(Of UmlsCandidate)())
        End Function

        Public Function UpsertAsync(entry As ConditionConceptEntry, cancellationToken As CancellationToken) _
                As Task Implements IConditionConceptStore.UpsertAsync
            Upserted.Add(entry)
            Return Task.CompletedTask
        End Function

        Public Function GetUnseenConditionsForStudyAsync(nctId As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of String)) Implements IConditionConceptStore.GetUnseenConditionsForStudyAsync
            Dim hit As IReadOnlyList(Of String) = Nothing
            If UnseenByStudy.TryGetValue(nctId, hit) Then Return Task.FromResult(hit)
            Return Task.FromResult(Of IReadOnlyList(Of String))(Array.Empty(Of String)())
        End Function

        Public Function SeedFromCorpusAsync(cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.SeedFromCorpusAsync
            Return Task.FromResult(0)
        End Function

        Public Function GetPendingAsync(limit As Integer, force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of ConditionConceptEntry)) Implements IConditionConceptStore.GetPendingAsync
            Return Task.FromResult(Of IReadOnlyList(Of ConditionConceptEntry))(Array.Empty(Of ConditionConceptEntry)())
        End Function

        Public Function CountPendingAsync(force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.CountPendingAsync
            Return Task.FromResult(0)
        End Function
    End Class

    Private NotInheritable Class FakeUmlsClient
        Implements IUmlsClient

        Public Property Candidates As IReadOnlyList(Of UmlsCandidate) = Array.Empty(Of UmlsCandidate)()
        Public Property LastQuery As String = ""
        Public Property SearchCallCount As Integer

        Public Function SearchAsync(concept As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IUmlsClient.SearchAsync
            LastQuery = concept
            SearchCallCount += 1
            Return Task.FromResult(Candidates)
        End Function

        Public Function GetSemanticTypeAssignmentsAsync(cui As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) Implements IUmlsClient.GetSemanticTypeAssignmentsAsync
            Return Task.FromResult(Of IReadOnlyList(Of SemanticTypeAssignment))(Array.Empty(Of SemanticTypeAssignment)())
        End Function
    End Class

    Private Shared Function NewNormalizer(store As FakeStore, client As FakeUmlsClient) As ConditionNormalizer
        Return New ConditionNormalizer(store, client, New UmlsMatchScorer())
    End Function

    ' ---------- tier 1a ----------

    ' THE regression test for spec section 2.2. An exact atom match is definitive
    ' by construction, so the preferred name is irrelevant. Routing this through
    ' PickBestMatch would score "stroke" against "CVA - Cerebrovascular accident",
    ' land far below 0.60, and reject a perfect match.
    <Fact>
    Public Async Function Tier1a_accepts_exact_match_without_consulting_the_scorer() As Task
        Dim store As New FakeStore()
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US")}
        Dim client As New FakeUmlsClient()

        Dim result = Await NewNormalizer(store, client).ResolveAsync("Stroke", CancellationToken.None)

        Assert.Equal("C0038454", result.ConceptCode)
        Assert.Equal("CVA - Cerebrovascular accident", result.UmlsName)
        Assert.Equal(ConditionMatchTier.Exact, result.Tier)
        Assert.Equal(1.0, result.Score)
        ' The fuzzy search must never have been reached.
        Assert.Equal(0, client.SearchCallCount)
    End Function

    ' ---------- tier 1b ----------

    <Fact>
    Public Async Function Tier1b_prefers_the_cui_whose_pref_name_equals_the_query() As Task
        Dim store As New FakeStore()
        store.ExactByNorm("depression") = {
            New UmlsCandidate("C9999999", "Depressive disorder", "MSH"),
            New UmlsCandidate("C0011570", "Depression", "SNOMEDCT_US")}

        Dim result = Await NewNormalizer(store, New FakeUmlsClient()).ResolveAsync("Depression", CancellationToken.None)

        Assert.Equal("C0011570", result.ConceptCode)
        Assert.Equal(ConditionMatchTier.ExactAmbiguous, result.Tier)
        Assert.Equal(1.0, result.Score)
    End Function

    <Fact>
    Public Async Function Tier1b_falls_back_to_lowest_cui_when_scores_tie() As Task
        Dim store As New FakeStore()
        ' Two candidates, neither equal to the query, both scoring identically
        ' because the names are the same string.
        store.ExactByNorm("ambiguous term") = {
            New UmlsCandidate("C0000200", "Something Else", "MSH"),
            New UmlsCandidate("C0000100", "Something Else", "MSH")}

        Dim result = Await NewNormalizer(store, New FakeUmlsClient()).ResolveAsync("Ambiguous Term", CancellationToken.None)

        Assert.Equal("C0000100", result.ConceptCode)
        Assert.Equal(ConditionMatchTier.ExactAmbiguous, result.Tier)
    End Function

    <Fact>
    Public Async Function Tier1b_accepts_even_when_every_score_is_below_the_fuzzy_threshold() As Task
        Dim store As New FakeStore()
        store.ExactByNorm("cancer") = {
            New UmlsCandidate("C0006826", "Blastoma", "MSH"),
            New UmlsCandidate("C0998888", "Neoplasm unspecified morphology", "MSH")}

        Dim result = Await NewNormalizer(store, New FakeUmlsClient()).ResolveAsync("Cancer", CancellationToken.None)

        ' The string is still an exact atom match; only WHICH concept was in doubt.
        Assert.True(result.IsResolved)
        Assert.Equal(ConditionMatchTier.ExactAmbiguous, result.Tier)
    End Function

    ' ---------- tier 2 ----------

    <Fact>
    Public Async Function Tier2_passes_the_raw_uppercase_form_to_the_search_so_acronyms_score() As Task
        Dim store As New FakeStore()   ' no exact atom -> tier 2
        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0007131", "NSCLC - Non-small cell lung cancer", "SNOMEDCT_US")}}

        Dim result = Await NewNormalizer(store, client).ResolveAsync("NSCLC", CancellationToken.None)

        ' Feeding the lowercased key would disable UmlsMatchScorer's acronym term,
        ' which requires ^[A-Z0-9]{2,6}$ on the raw query.
        Assert.Equal("NSCLC", client.LastQuery)
        Assert.Equal("C0007131", result.ConceptCode)
        Assert.Equal(ConditionMatchTier.Fuzzy, result.Tier)
    End Function

    <Fact>
    Public Async Function Tier2_rejects_a_match_below_the_condition_threshold() As Task
        Dim store As New FakeStore()
        ' "NSC762" is the real trigram best match for "nsclc" and scores ~0.30.
        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0700294", "NSC762", "MSH")}}

        Dim result = Await NewNormalizer(store, client).ResolveAsync("Zzqq Unmatchable Phrase", CancellationToken.None)

        Assert.False(result.IsResolved)
        Assert.Equal(ConditionMatchTier.Unresolved, result.Tier)
        Assert.Equal("", result.ConceptCode)
        Assert.Equal(0.0, result.Score)
    End Function

    <Fact>
    Public Sub Condition_threshold_is_stricter_than_the_pipeline_threshold()
        Assert.Equal(0.6, ConditionNormalizer.FuzzyThreshold)
        Assert.True(ConditionNormalizer.FuzzyThreshold > UmlsMatchScorer.MatchThreshold,
                    "The condition cutoff must be stricter than the criteria pipeline's 0.45")
    End Sub

    ' THE test that justifies choosing 0.60 over 0.45. Without it, every other
    ' tier-2 test would still pass with the threshold left at the pipeline's
    ' 0.45, and the spec's central risk argument would be unverified.
    '
    ' "advanced solid tumors" -> "Solid tumor" measured at 0.478 against the real
    ' corpus: above the pipeline's cutoff, below the condition one. If the scorer
    ' ever changes, the first assertion fails and says exactly why.
    <Fact>
    Public Async Function Tier2_rejects_a_score_between_the_pipeline_and_condition_thresholds() As Task
        Const Query As String = "advanced solid tumors"
        Const CandidateName As String = "Solid tumor"

        Dim score = New UmlsMatchScorer().Score(Query, CandidateName)
        Assert.True(score >= UmlsMatchScorer.MatchThreshold AndAlso score < ConditionNormalizer.FuzzyThreshold,
                    $"Fixture no longer sits in the 0.45-0.60 band (scored {score}); pick another pair.")

        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0280100", CandidateName, "SNOMEDCT_US")}}

        Dim result = Await NewNormalizer(New FakeStore(), client).ResolveAsync(Query, CancellationToken.None)

        ' PickBestMatch would have ACCEPTED this at 0.45. The condition gate rejects it.
        Assert.False(result.IsResolved)
        Assert.Equal(ConditionMatchTier.Unresolved, result.Tier)
    End Function

    <Fact>
    Public Async Function Tier2_accepts_at_exactly_the_condition_threshold() As Task
        ' Boundary is inclusive (>=), matching PickBestMatch's own convention.
        Dim client As New FakeUmlsClient() With {
            .Candidates = {New UmlsCandidate("C0011860", "Diabetes Mellitus", "SNOMEDCT_US")}}

        Dim result = Await NewNormalizer(New FakeStore(), client).ResolveAsync("Diabetes Mellitus", CancellationToken.None)

        Assert.True(result.IsResolved)
        Assert.Equal(ConditionMatchTier.Fuzzy, result.Tier)
        Assert.True(result.Score >= ConditionNormalizer.FuzzyThreshold)
    End Function

    <Fact>
    Public Async Function Empty_input_resolves_to_unresolved_without_touching_the_store() As Task
        Dim client As New FakeUmlsClient()
        Dim result = Await NewNormalizer(New FakeStore(), client).ResolveAsync("   ", CancellationToken.None)

        Assert.False(result.IsResolved)
        Assert.Equal(0, client.SearchCallCount)
    End Function

    ' ---------- per-study hook ----------

    <Fact>
    Public Async Function EnsureForStudy_upserts_only_unseen_strings() As Task
        Dim store As New FakeStore()
        store.UnseenByStudy("NCT001") = {"Stroke", "COPD"}
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US")}
        store.ExactByNorm("copd") = {New UmlsCandidate("C0024117", "COPD", "SNOMEDCT_US")}

        Dim written = Await NewNormalizer(store, New FakeUmlsClient()).EnsureForStudyAsync("NCT001", CancellationToken.None)

        Assert.Equal(2, written)
        Assert.Equal(2, store.Upserted.Count)
        Assert.Contains(store.Upserted, Function(e) e.ConditionNorm = "stroke" AndAlso e.RawForm = "Stroke")
        Assert.Contains(store.Upserted, Function(e) e.ConditionNorm = "copd" AndAlso e.ConceptCode = "C0024117")
    End Function

    <Fact>
    Public Async Function EnsureForStudy_writes_nothing_when_every_string_is_known() As Task
        Dim store As New FakeStore()   ' UnseenByStudy empty -> steady state

        Dim written = Await NewNormalizer(store, New FakeUmlsClient()).EnsureForStudyAsync("NCT002", CancellationToken.None)

        Assert.Equal(0, written)
        Assert.Empty(store.Upserted)
    End Function
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~ConditionNormalizerTests"
```

Expected: FAIL to compile - `ConditionNormalizer` is not defined.

- [ ] **Step 3: Implement the normalizer**

Create `contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizer.vb`:

```vb
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
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~ConditionNormalizerTests"
```

Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizer.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizerTests.vb
git commit -m "Add three-tier condition normalizer"
```

---

## Task 5: ConditionConceptStore (Postgres)

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Data/ConditionConceptStore.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb` (append)

**Interfaces:**
- Consumes: `IConditionConceptStore`, `ConditionConceptEntry`, `UmlsCandidate`, `ConceptKey`.
- Produces: `ConditionConceptStore` class, constructor `New(outputDataSource As NpgsqlDataSource)`.

Note the SQL normalization expression `regexp_replace(btrim(lower(x)), '\s+', ' ', 'g')` appears in three statements below and must stay identical to `ConceptKey.Normalize`. Task 2's cross-language test is what enforces that.

- [ ] **Step 1: Write the failing tests**

Append to `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb`:

```vb
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
        Assert.Equal("C0038454", hits(0).Ui)
        ' The candidate carries the concept's PREFERRED name, not the atom string.
        Assert.Equal("CVA - Cerebrovascular accident", hits(0).Name)
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
```

Add these imports at the top of the file if not already present: `System.Collections.Generic`, `System.Threading`, `System.Threading.Tasks`, `EligibilityProcessing.Core`, `EligibilityProcessing.Data`, `Npgsql`, `Xunit`.

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionConceptStoreTests"
```

Expected: FAIL to compile - `ConditionConceptStore` is not defined.

- [ ] **Step 3: Implement the store**

Create `contexts/eligibility/src/EligibilityProcessing.Data/ConditionConceptStore.vb`:

```vb
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports NpgsqlTypes

''' <summary>
''' Data-access for public.condition_concept (V24), the condition-string to
''' UMLS-CUI dictionary.
'''
''' The SQL normalization expression regexp_replace(btrim(lower(x)), '\s+', ' ', 'g')
''' appears in several statements here and MUST stay identical to
''' ConceptKey.Normalize - ConditionConceptStoreTests asserts they agree, because
''' a divergence would silently stop exact matches aligning.
''' </summary>
Public NotInheritable Class ConditionConceptStore
    Implements IConditionConceptStore

    ' Kept in one place so the three statements below cannot drift from each other.
    Private Const NormalizeSql As String = "regexp_replace(btrim(lower({0})), '\s+', ' ', 'g')"

    Private ReadOnly _dataSource As NpgsqlDataSource

    Public Sub New(outputDataSource As NpgsqlDataSource)
        If outputDataSource Is Nothing Then Throw New ArgumentNullException(NameOf(outputDataSource))
        _dataSource = outputDataSource
    End Sub

    Public Async Function LookupExactAsync(conditionNorm As String,
                                           cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IConditionConceptStore.LookupExactAsync

        If String.IsNullOrWhiteSpace(conditionNorm) Then Return Array.Empty(Of UmlsCandidate)()

        Dim result As New List(Of UmlsCandidate)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                ' DISTINCT on cui: several atoms of the same concept can share a
                ' normalized string (different sources/term types).
                cmd.CommandText = "
SELECT DISTINCT c.cui, c.pref_name, c.root_source
FROM umls.atom a
JOIN umls.concept c ON c.cui = a.cui
WHERE a.str_norm = @norm
ORDER BY c.cui"
                cmd.Parameters.Add(New NpgsqlParameter("norm", NpgsqlDbType.Text) With {.Value = conditionNorm})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New UmlsCandidate(
                                reader.GetString(0),
                                If(reader.IsDBNull(1), "", reader.GetString(1)),
                                If(reader.IsDBNull(2), "", reader.GetString(2))))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function UpsertAsync(entry As ConditionConceptEntry,
                                      cancellationToken As CancellationToken) _
            As Task Implements IConditionConceptStore.UpsertAsync

        If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.ConditionNorm) Then Return

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.condition_concept
    (condition_norm, raw_form, study_count, concept_code, umls_name, match_tier, match_score, resolved_at)
VALUES
    (@norm, @raw, @count, @code, @name, @tier, @score, now())
ON CONFLICT (condition_norm) DO UPDATE SET
    raw_form     = excluded.raw_form,
    concept_code = excluded.concept_code,
    umls_name    = excluded.umls_name,
    match_tier   = excluded.match_tier,
    match_score  = excluded.match_score,
    resolved_at  = now()"
                cmd.Parameters.Add(New NpgsqlParameter("norm", NpgsqlDbType.Text) With {.Value = entry.ConditionNorm})
                cmd.Parameters.Add(New NpgsqlParameter("raw", NpgsqlDbType.Text) With {.Value = entry.RawForm})
                cmd.Parameters.Add(New NpgsqlParameter("count", NpgsqlDbType.Integer) With {.Value = entry.StudyCount})
                cmd.Parameters.Add(New NpgsqlParameter("code", NpgsqlDbType.Text) With {
                        .Value = If(String.IsNullOrEmpty(entry.ConceptCode), CObj(DBNull.Value), entry.ConceptCode)})
                cmd.Parameters.Add(New NpgsqlParameter("name", NpgsqlDbType.Text) With {
                        .Value = If(String.IsNullOrEmpty(entry.UmlsName), CObj(DBNull.Value), entry.UmlsName)})
                cmd.Parameters.Add(New NpgsqlParameter("tier", NpgsqlDbType.Text) With {.Value = entry.MatchTier})
                cmd.Parameters.Add(New NpgsqlParameter("score", NpgsqlDbType.Numeric) With {.Value = CDec(entry.MatchScore)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function GetUnseenConditionsForStudyAsync(nctId As String,
                                                           cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of String)) Implements IConditionConceptStore.GetUnseenConditionsForStudyAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Array.Empty(Of String)()

        Dim result As New List(Of String)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT DISTINCT s.cond
FROM (SELECT unnest(conditions) AS cond
      FROM public.eligibility_study_detail
      WHERE nct_id = @nct) s
WHERE btrim(s.cond) <> ''
  AND NOT EXISTS (
      SELECT 1 FROM public.condition_concept d
      WHERE d.condition_norm = regexp_replace(btrim(lower(s.cond)), '\s+', ' ', 'g'))
ORDER BY s.cond"
                cmd.Parameters.Add(New NpgsqlParameter("nct", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function SeedFromCorpusAsync(cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionConceptStore.SeedFromCorpusAsync

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                ' raw_form = most frequent original casing, ties broken
                ' lexicographically so a re-run reproduces the same choice. That
                ' matters: the scorer's acronym term only fires on uppercase, and
                ' the corpus holds COPD (657 studies) alongside Copd (93).
                cmd.CommandText = "
WITH mentions AS (
    SELECT nct_id, unnest(conditions) AS cond
    FROM public.eligibility_study_detail
),
per_form AS (
    SELECT regexp_replace(btrim(lower(cond)), '\s+', ' ', 'g') AS norm,
           cond AS raw,
           count(DISTINCT nct_id) AS cnt
    FROM mentions
    WHERE btrim(cond) <> ''
    GROUP BY 1, 2
),
rolled AS (
    SELECT norm,
           (array_agg(raw ORDER BY cnt DESC, raw ASC))[1] AS raw_form,
           sum(cnt)::int AS study_count
    FROM per_form
    GROUP BY norm
),
ins AS (
    INSERT INTO public.condition_concept (condition_norm, raw_form, study_count, match_tier)
    SELECT norm, raw_form, study_count, 'unresolved' FROM rolled
    ON CONFLICT (condition_norm) DO UPDATE SET
        raw_form    = excluded.raw_form,
        study_count = excluded.study_count
    RETURNING (xmax = 0) AS inserted
)
SELECT count(*) FILTER (WHERE inserted) FROM ins"
                cmd.CommandTimeout = 0   ' full-corpus aggregation; can exceed the 30s default
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

    Public Async Function GetPendingAsync(limit As Integer, force As Boolean,
                                          cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of ConditionConceptEntry)) Implements IConditionConceptStore.GetPendingAsync

        Dim result As New List(Of ConditionConceptEntry)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT condition_norm, raw_form, study_count
FROM public.condition_concept
WHERE @force OR resolved_at IS NULL
ORDER BY study_count DESC, condition_norm
LIMIT @limit"
                cmd.Parameters.Add(New NpgsqlParameter("force", NpgsqlDbType.Boolean) With {.Value = force})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = Math.Max(1, limit)})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New ConditionConceptEntry With {
                                .ConditionNorm = reader.GetString(0),
                                .RawForm = reader.GetString(1),
                                .StudyCount = reader.GetInt32(2)})
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function CountPendingAsync(force As Boolean,
                                            cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionConceptStore.CountPendingAsync

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT count(*)::int FROM public.condition_concept
WHERE @force OR resolved_at IS NULL"
                cmd.Parameters.Add(New NpgsqlParameter("force", NpgsqlDbType.Boolean) With {.Value = force})
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

End Class
```

Delete the unused `NormalizeSql` const if the compiler flags it - the expression is inlined in each statement for readability, and the const exists only as documentation. If `Option Strict` complains, remove it.

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionConceptStoreTests"
```

Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Data/ConditionConceptStore.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ConditionConceptStoreTests.vb
git commit -m "Add ConditionConceptStore with corpus seeding and pending queries"
```

---

## Task 6: The backfill job

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizeJob.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/ToolJobs.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb:396`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizeJobTests.vb`

**Interfaces:**
- Consumes: `IConditionConceptStore`, `ConditionNormalizer`, `ToolJobSnapshot`, `ToolMetric`, `ToolJobProgressPump`.
- Produces: `ToolJobKind.NormalizeConditions`, `NormalizeConditionsOptions` (properties `Count As Integer = 0`, `DryRun As Boolean`, `Force As Boolean`), `ConditionCounters` (public fields `Done`, `Resolved`, `Unresolved`), `IConditionNormalizeJob` with `CountRemainingAsync(force As Boolean, ct) As Task(Of Integer)` and `RunAsync(options As NormalizeConditionsOptions, progress As IProgress(Of ToolJobSnapshot), ct) As Task(Of ConditionCounters)`, and `ConditionNormalizeJob` implementing it.

- [ ] **Step 1: Add the contracts to ToolJobs.vb**

In `contexts/eligibility/src/EligibilityProcessing.Core/ToolJobs.vb`, extend the enum (lines 15-18):

```vb
Public Enum ToolJobKind
    NormalizeUmls
    EmbedStudies
    NormalizeConditions
End Enum
```

After `EmbedStudiesOptions` (line 39), add:

```vb
''' <summary>
''' Options for the condition-normalization job (mirrors the CLI
''' <c>normalize-conditions</c> switches). <see cref="Count"/> of 0 means "every
''' pending row".
''' </summary>
Public NotInheritable Class NormalizeConditionsOptions
    Public Property Count As Integer = 0
    Public Property DryRun As Boolean
    Public Property Force As Boolean
End Class
```

After `EmbedCounters` (line 102), add:

```vb
''' <summary>Counters for the condition-normalization job.</summary>
Public NotInheritable Class ConditionCounters
    Public Done As Integer
    Public Resolved As Integer
    Public Unresolved As Integer
End Class
```

After `IStudyEmbeddingJob` (line 126), add:

```vb
''' <summary>The condition-normalization maintenance job.</summary>
Public Interface IConditionNormalizeJob
    ''' <summary>Dictionary rows still needing resolution (the Tools tab count).</summary>
    Function CountRemainingAsync(force As Boolean, cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Seed the dictionary from the corpus, then resolve pending rows
    ''' highest study_count first, reporting snapshots through
    ''' <paramref name="progress"/> (may be Nothing).</summary>
    Function RunAsync(options As NormalizeConditionsOptions, progress As IProgress(Of ToolJobSnapshot), cancellationToken As CancellationToken) As Task(Of ConditionCounters)
End Interface
```

- [ ] **Step 2: Write the failing test**

Create `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizeJobTests.vb`:

```vb
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

Public Class ConditionNormalizeJobTests

    ' Minimal store that serves a fixed pending list and records upserts.
    Private NotInheritable Class JobStore
        Implements IConditionConceptStore

        Public Property Pending As New List(Of ConditionConceptEntry)
        Public Property Upserted As New List(Of ConditionConceptEntry)
        Public Property SeedCalls As Integer
        Public Property ExactByNorm As New Dictionary(Of String, IReadOnlyList(Of UmlsCandidate))

        Public Function LookupExactAsync(conditionNorm As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IConditionConceptStore.LookupExactAsync
            Dim hit As IReadOnlyList(Of UmlsCandidate) = Nothing
            If ExactByNorm.TryGetValue(conditionNorm, hit) Then Return Task.FromResult(hit)
            Return Task.FromResult(Of IReadOnlyList(Of UmlsCandidate))(Array.Empty(Of UmlsCandidate)())
        End Function

        Public Function UpsertAsync(entry As ConditionConceptEntry, cancellationToken As CancellationToken) _
                As Task Implements IConditionConceptStore.UpsertAsync
            Upserted.Add(entry)
            Return Task.CompletedTask
        End Function

        Public Function GetUnseenConditionsForStudyAsync(nctId As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of String)) Implements IConditionConceptStore.GetUnseenConditionsForStudyAsync
            Return Task.FromResult(Of IReadOnlyList(Of String))(Array.Empty(Of String)())
        End Function

        Public Function SeedFromCorpusAsync(cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.SeedFromCorpusAsync
            SeedCalls += 1
            Return Task.FromResult(0)
        End Function

        Public Function GetPendingAsync(limit As Integer, force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of ConditionConceptEntry)) Implements IConditionConceptStore.GetPendingAsync
            Return Task.FromResult(Of IReadOnlyList(Of ConditionConceptEntry))(Pending.Take(limit).ToList())
        End Function

        Public Function CountPendingAsync(force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.CountPendingAsync
            Return Task.FromResult(Pending.Count)
        End Function
    End Class

    Private NotInheritable Class NullUmlsClient
        Implements IUmlsClient

        Public Function SearchAsync(concept As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IUmlsClient.SearchAsync
            Return Task.FromResult(Of IReadOnlyList(Of UmlsCandidate))(Array.Empty(Of UmlsCandidate)())
        End Function

        Public Function GetSemanticTypeAssignmentsAsync(cui As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) Implements IUmlsClient.GetSemanticTypeAssignmentsAsync
            Return Task.FromResult(Of IReadOnlyList(Of SemanticTypeAssignment))(Array.Empty(Of SemanticTypeAssignment)())
        End Function
    End Class

    Private Shared Function NewJob(store As JobStore) As ConditionNormalizeJob
        Return New ConditionNormalizeJob(
                store,
                New ConditionNormalizer(store, New NullUmlsClient(), New UmlsMatchScorer()))
    End Function

    <Fact>
    Public Async Function Run_seeds_then_resolves_and_counts_outcomes() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 9})
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "zzqq", .RawForm = "Zzqq", .StudyCount = 1})
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US")}

        Dim counters = Await NewJob(store).RunAsync(
                New NormalizeConditionsOptions(), Nothing, CancellationToken.None)

        Assert.Equal(1, store.SeedCalls)
        Assert.Equal(2, counters.Done)
        Assert.Equal(1, counters.Resolved)
        Assert.Equal(1, counters.Unresolved)
        Assert.Equal(2, store.Upserted.Count)
        Assert.Equal("C0038454", store.Upserted.First(Function(e) e.ConditionNorm = "stroke").ConceptCode)
    End Function

    <Fact>
    Public Async Function DryRun_resolves_but_writes_nothing() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 9})
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US")}

        Dim counters = Await NewJob(store).RunAsync(
                New NormalizeConditionsOptions With {.DryRun = True}, Nothing, CancellationToken.None)

        Assert.Equal(1, counters.Resolved)
        Assert.Empty(store.Upserted)
    End Function

    <Fact>
    Public Async Function Run_preserves_study_count_on_the_upserted_row() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 42})
        store.ExactByNorm("stroke") = {New UmlsCandidate("C0038454", "CVA", "SNOMEDCT_US")}

        Await NewJob(store).RunAsync(New NormalizeConditionsOptions(), Nothing, CancellationToken.None)

        Assert.Equal(42, store.Upserted.Single().StudyCount)
    End Function
End Class
```

- [ ] **Step 3: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~ConditionNormalizeJobTests"
```

Expected: FAIL to compile - `ConditionNormalizeJob` is not defined.

- [ ] **Step 4: Implement the job**

Create `contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizeJob.vb`:

```vb
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' The `normalize-conditions` maintenance job: seed the dictionary from the
''' corpus, then resolve pending rows highest study_count first.
'''
''' Sequential rather than parallel, unlike embed-studies. Tier 1 is a single
''' indexed lookup and only tier 2 costs a ~30ms search, so the bottleneck is
''' modest and a parallel loop would add contention on one Postgres connection
''' pool for little gain. Ordering by study_count DESC means a cancelled run
''' still leaves the corpus measurably better off.
''' </summary>
Public NotInheritable Class ConditionNormalizeJob
    Implements IConditionNormalizeJob

    ' Sentinel for "no explicit --count": resolve everything pending.
    Private Const AllPending As Integer = Integer.MaxValue

    Private ReadOnly _store As IConditionConceptStore
    Private ReadOnly _normalizer As ConditionNormalizer

    Public Sub New(store As IConditionConceptStore, normalizer As ConditionNormalizer)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        If normalizer Is Nothing Then Throw New ArgumentNullException(NameOf(normalizer))
        _store = store
        _normalizer = normalizer
    End Sub

    Public Function CountRemainingAsync(force As Boolean, cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionNormalizeJob.CountRemainingAsync
        Return _store.CountPendingAsync(force, cancellationToken)
    End Function

    Public Async Function RunAsync(options As NormalizeConditionsOptions,
                                   progress As IProgress(Of ToolJobSnapshot),
                                   cancellationToken As CancellationToken) _
            As Task(Of ConditionCounters) Implements IConditionNormalizeJob.RunAsync

        ' Seed first so newly processed trials' conditions are present, and so
        ' raw_form / study_count reflect the current corpus before we order by them.
        Await _store.SeedFromCorpusAsync(cancellationToken).ConfigureAwait(False)

        Dim limit = If(options.Count > 0, options.Count, AllPending)
        Dim pending = Await _store.GetPendingAsync(limit, options.Force, cancellationToken).ConfigureAwait(False)

        Dim counters As New ConditionCounters()
        Dim total = pending.Count
        Dim sw = Stopwatch.StartNew()

        Dim build As Func(Of ToolJobSnapshot) =
            Function() New ToolJobSnapshot(
                ToolJobKind.NormalizeConditions, total, counters.Done, sw.Elapsed,
                New ToolMetric() {
                    New ToolMetric("Resolved", counters.Resolved),
                    New ToolMetric("Unresolved", counters.Unresolved)})

        progress?.Report(build())
        If total = 0 Then Return counters

        Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        Dim progressTask As Task = If(progress Is Nothing,
                                      Task.CompletedTask,
                                      ToolJobProgressPump.PumpAsync(progress, build, progressCts.Token))
        Dim caught As Exception = Nothing

        ' Capture rather than propagate, so the progress reporter is always
        ' stopped and a final snapshot always emitted. VB cannot Await in Finally.
        Try
            For Each entry In pending
                cancellationToken.ThrowIfCancellationRequested()

                Dim resolution = Await _normalizer.ResolveAsync(entry.RawForm, cancellationToken).ConfigureAwait(False)

                If Not options.DryRun Then
                    Await _store.UpsertAsync(New ConditionConceptEntry With {
                            .ConditionNorm = entry.ConditionNorm,
                            .RawForm = entry.RawForm,
                            .StudyCount = entry.StudyCount,
                            .ConceptCode = resolution.ConceptCode,
                            .UmlsName = resolution.UmlsName,
                            .MatchTier = resolution.Tier,
                            .MatchScore = resolution.Score
                        }, cancellationToken).ConfigureAwait(False)
                End If

                If resolution.IsResolved Then
                    counters.Resolved += 1
                Else
                    counters.Unresolved += 1
                End If
                counters.Done += 1
            Next
        Catch ex As Exception
            caught = ex
        End Try

        progressCts.Cancel()
        Try
            Await progressTask.ConfigureAwait(False)
        Catch ex As OperationCanceledException
        End Try
        progress?.Report(build())

        If caught IsNot Nothing Then Throw caught
        Return counters
    End Function

End Class
```

- [ ] **Step 5: Register in DI**

In `contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb`, after the `IStudyEmbeddingJob` registration (ends line 396), add:

```vb
        ' The condition dictionary store is stateless over one data source, like
        ' UmlsMetathesaurusStore, so a singleton is right; the normalizer and job
        ' are scoped to match the other tool jobs (they ride the scoped UMLS cache).
        services.AddSingleton(Of IConditionConceptStore)(
                Function(sp As IServiceProvider) As IConditionConceptStore
                    Return New ConditionConceptStore(sp.GetRequiredService(Of NpgsqlDataSource)())
                End Function)
        services.AddScoped(Of ConditionNormalizer)(
                Function(sp As IServiceProvider) As ConditionNormalizer
                    Return New ConditionNormalizer(
                            store:=sp.GetRequiredService(Of IConditionConceptStore)(),
                            umlsClient:=sp.GetRequiredService(Of IUmlsClient)(),
                            scorer:=sp.GetRequiredService(Of UmlsMatchScorer)())
                End Function)
        services.AddScoped(Of IConditionNormalizeJob)(
                Function(sp As IServiceProvider) As IConditionNormalizeJob
                    Return New ConditionNormalizeJob(
                            store:=sp.GetRequiredService(Of IConditionConceptStore)(),
                            normalizer:=sp.GetRequiredService(Of ConditionNormalizer)())
                End Function)
```

**Before writing this, check how the output `NpgsqlDataSource` is resolved elsewhere in `CompositionRoot.vb`.** If it is registered as a plain `NpgsqlDataSource` singleton the above works as written; if the file uses a keyed registration or constructs the data source inline for `UmlsMetathesaurusStore`, mirror that exact pattern instead. Search for `New UmlsMetathesaurusStore(` and copy how it obtains its data source.

- [ ] **Step 6: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~ConditionNormalizeJobTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/ConditionNormalizeJob.vb contexts/eligibility/src/EligibilityProcessing.Core/ToolJobs.vb contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/ConditionNormalizeJobTests.vb
git commit -m "Add normalize-conditions backfill job"
```

---

## Task 7: Pipeline hook

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/PipelineOrchestrator.vb:706-719`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/` (the existing orchestrator test class)

**Interfaces:**
- Consumes: `ConditionNormalizer.EnsureForStudyAsync`.

The orchestrator already wraps the snapshot capture in a best-effort Try that logs and swallows. The normalizer call goes inside that same Try, so a normalization failure can never fail a trial.

- [ ] **Step 1: Write the failing test**

Find the existing orchestrator test class (search `PipelineOrchestrator` under `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/`) and add a test in its style. The behaviour to pin:

```vb
    <Fact>
    Public Async Function Trial_still_persists_when_condition_normalization_throws() As Task
        ' The condition normalizer is best-effort. A failure inside it must be
        ' logged and swallowed, exactly like a snapshot-capture failure - never
        ' allowed to fail the trial or lose its criteria.
        Dim gateway = NewFakeGateway()          ' use the class's existing helper
        Dim orchestrator = NewOrchestratorWithThrowingConditionNormalizer(gateway)

        Await orchestrator.RunAsync(NewRunRequest(count:=1), CancellationToken.None)

        ' The trial's eligibility rows were still written.
        Assert.NotEmpty(gateway.PersistedRows)
    End Function
```

Adapt the helper names to whatever the existing class already provides - do not invent new fakes if the class has them. If the orchestrator's constructor gains a `ConditionNormalizer` parameter, make it `Optional ... = Nothing` so existing tests keep compiling and the hook is skipped when absent.

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~PipelineOrchestrator"
```

Expected: FAIL - the normalizer is not called at all yet, or the constructor does not accept one.

- [ ] **Step 3: Add the field and constructor parameter**

In `PipelineOrchestrator.vb`, add a private field alongside the existing ones and an optional constructor parameter:

```vb
    Private ReadOnly _conditionNormalizer As ConditionNormalizer
```

Optional so every existing construction site and test keeps compiling:

```vb
            Optional conditionNormalizer As ConditionNormalizer = Nothing
```

and in the body: `_conditionNormalizer = conditionNormalizer`.

- [ ] **Step 4: Call it from the existing best-effort wrapper**

Replace `TryCaptureStudySnapshotAsync` (lines 706-719) with:

```vb
    Private Async Function TryCaptureStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task
        Dim ex As Exception = Nothing
        Try
            Await _gateway.CaptureStudySnapshotAsync(nctId, cancellationToken).ConfigureAwait(False)
            ' Fill the condition dictionary for any string this trial introduces.
            ' In the steady state (after a backfill) this is one indexed query
            ' returning nothing. Inside the same best-effort Try deliberately: a
            ' normalization failure must never fail the trial.
            If _conditionNormalizer IsNot Nothing Then
                Await _conditionNormalizer.EnsureForStudyAsync(nctId, cancellationToken).ConfigureAwait(False)
            End If
            Return
        Catch e As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch e As Exception
            ex = e
        End Try
        _logger.LogWarning(ex, "Failed to capture study snapshot or normalize conditions for {Nct}", nctId)
    End Function
```

- [ ] **Step 5: Pass the normalizer at the composition root**

In `CompositionRoot.vb`, find where `PipelineOrchestrator` is constructed and add
`conditionNormalizer:=sp.GetRequiredService(Of ConditionNormalizer)()` to the argument list.

- [ ] **Step 6: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, full suite, zero skipped (assuming Docker is running).

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/PipelineOrchestrator.vb contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/
git commit -m "Fill the condition dictionary per trial from the pipeline"
```

---

## Task 8: CLI verb

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/CliCompositionTests.vb` (if it enumerates verbs)

**Interfaces:**
- Consumes: `IConditionNormalizeJob`, `NormalizeConditionsOptions`, `ConditionCounters`, `ToolJobSnapshot`.

- [ ] **Step 1: Add the dispatch arm**

In `contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb`, after the `embed-studies` case (lines 167-168):

```vb
                Case "normalize-conditions"
                    Return Await RunNormalizeConditionsAsync(appHost, args, cancellationToken).ConfigureAwait(False)
```

- [ ] **Step 2: Add the command implementation**

After `RunEmbedStudiesAsync` ends, add - modelled directly on it, including the `SnapshotSink` / `ReportProgressAsync` progress rendering it already defines:

```vb
    ' normalize-conditions - map AACT condition strings to UMLS CUIs in
    ' public.condition_concept, so analytics can slice by condition. Seeds the
    ' dictionary from the corpus first, then resolves pending rows highest
    ' study_count first. Safe to re-run (only unresolved rows are touched unless
    ' --force). See docs/superpowers/specs/2026-07-21-condition-normalizer-design.md.
    Private Async Function RunNormalizeConditionsAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim count = ParseOptionInt(args, "--count", 0)
        Dim dryRun = args.Contains("--dry-run")
        Dim force = args.Contains("--force")

        Using scope = appHost.Services.CreateScope()
            Dim job = scope.ServiceProvider.GetRequiredService(Of IConditionNormalizeJob)()
            Dim total = Await job.CountRemainingAsync(force, cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine(
                    $"Normalizing {If(count > 0, Math.Min(count, total), total)} condition string(s)" &
                    $"{If(dryRun, " (dry run)", "")}{If(force, " (force)", "")}...")

            Dim latest As ToolJobSnapshot = Nothing
            Dim sink As New SnapshotSink(Sub(s) latest = s)
            Dim sw = Stopwatch.StartNew()
            Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            Dim progressTask = ReportProgressAsync(
                    total,
                    Function() If(latest Is Nothing, 0, latest.Processed),
                    Function() $"conditions - {MetricValue(latest, "Resolved")} resolved, {MetricValue(latest, "Unresolved")} unresolved",
                    sw, progressCts.Token)
            Dim caught As Exception = Nothing
            Dim counters As ConditionCounters = Nothing

            Try
                counters = Await job.RunAsync(
                        New NormalizeConditionsOptions With {
                            .Count = count, .DryRun = dryRun, .Force = force},
                        sink, cancellationToken).ConfigureAwait(False)
            Catch ex As Exception
                caught = ex
            End Try

            progressCts.Cancel()
            Try
                Await progressTask.ConfigureAwait(False)
            Catch ex As OperationCanceledException
            End Try
            System.Console.WriteLine()

            If caught IsNot Nothing Then
                System.Console.Error.WriteLine($"normalize-conditions failed: {caught.Message}")
                Return 1
            End If

            System.Console.WriteLine(
                    $"Done. {counters.Done} processed, {counters.Resolved} resolved, {counters.Unresolved} unresolved.")
            Return 0
        End Using
    End Function
```

Match the surrounding file's exact use of `SnapshotSink`, `ReportProgressAsync`, `MetricValue` and `ParseOptionInt` - read `RunEmbedStudiesAsync` (line 439) and copy its shape rather than assuming these signatures.

- [ ] **Step 3: Add the help text**

In the header comment block (near line 35) add:

```vb
'   normalize-conditions [--count N] [--dry-run] [--force]
'                                        - map AACT condition strings to UMLS
'                                          CUIs for the analytics condition slice
```

And in the usage printer (near line 1079):

```vb
        System.Console.WriteLine("  EligibilityProcessing.Cli normalize-conditions [--count N] [--dry-run] [--force]")
```

- [ ] **Step 4: Run the tests**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS. If `CliCompositionTests` asserts a list of known verbs, add `normalize-conditions` to it.

- [ ] **Step 5: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Cli/Program.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/CliCompositionTests.vb
git commit -m "Add normalize-conditions CLI verb"
```

---

## Task 9: Tools tab card

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/ToolJobRequest.cs`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/ToolJobRunner.cs:72-81, 156-170`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/ToolJobState.cs:75-80`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/HomeController.cs:372-409, 467-484, 751`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Models/ToolsViewModel.cs`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Tools.cshtml:121, 374-379`

**Interfaces:**
- Consumes: `ToolJobKind.NormalizeConditions`, `NormalizeConditionsOptions`, `IConditionNormalizeJob`.

Three of these edits are in code that currently assumes exactly two job kinds and will compile fine while behaving wrongly. Do all three.

- [ ] **Step 1: Extend the work item**

`ToolJobRequest.cs` - add the third options slot. This is a positional record, so every construction site must be updated (the two existing Tools POST actions pass `null` for the new slot):

```csharp
public sealed record ToolJobRequest(
    Guid JobId,
    ToolJobKind Kind,
    NormalizeUmlsOptions? Normalize,
    EmbedStudiesOptions? Embed,
    NormalizeConditionsOptions? Conditions = null);
```

Defaulting to `null` keeps the two existing call sites compiling unchanged.

- [ ] **Step 2: Fix the runner's dispatch - the load-bearing edit**

`ToolJobRunner.cs` lines 72-81 are an `if/else` whose *else* branch unconditionally assumes `EmbedStudies` and force-unwraps `request.Embed!`. Left alone, a `NormalizeConditions` job would take the else branch and throw a null-reference. Replace with:

```csharp
                if (request.Kind == ToolJobKind.NormalizeUmls)
                {
                    var job = scope.ServiceProvider.GetRequiredService<IUmlsNormalizeJob>();
                    await job.RunAsync(request.Normalize!, progress, linked.Token).ConfigureAwait(false);
                }
                else if (request.Kind == ToolJobKind.NormalizeConditions)
                {
                    var job = scope.ServiceProvider.GetRequiredService<IConditionNormalizeJob>();
                    await job.RunAsync(request.Conditions!, progress, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    var job = scope.ServiceProvider.GetRequiredService<IStudyEmbeddingJob>();
                    await job.RunAsync(request.Embed!, progress, linked.Token).ConfigureAwait(false);
                }
```

And extend `Describe` (lines 156-170), inserting before the `r.Embed` arm:

```csharp
        if (r.Kind == ToolJobKind.NormalizeConditions && r.Conditions is { } c)
        {
            return (c.Count > 0 ? $"count {c.Count}" : "all pending")
                 + (c.DryRun ? ", dry-run" : "")
                 + (c.Force ? ", force" : "");
        }
```

- [ ] **Step 3: Add the wire name**

`ToolJobState.cs` `KindName` (lines 75-80) has a `_ => kind.ToString()` fallback, so without this arm the new kind silently reports `"NormalizeConditions"` instead of the kebab-case name the view keys off:

```csharp
    public static string KindName(ToolJobKind kind) => kind switch
    {
        ToolJobKind.NormalizeUmls => "normalize-umls",
        ToolJobKind.EmbedStudies => "embed-studies",
        ToolJobKind.NormalizeConditions => "normalize-conditions",
        _ => kind.ToString()
    };
```

- [ ] **Step 4: Add the POST action**

In `HomeController.cs`, after `RunEmbedStudies` (ends line 751):

```csharp
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNormalizeConditions(
        [FromServices] RunGate gate,
        [FromServices] Channel<ToolJobRequest> channel,
        int? count,
        bool dryRun = false,
        bool force = false)
    {
        var jobId = Guid.NewGuid();
        if (!gate.TryAcquire(jobId, "normalize-conditions"))
        {
            return Conflict(new { current_run_id = gate.CurrentRunId, activity = gate.CurrentActivity });
        }

        var options = new NormalizeConditionsOptions
        {
            Count = count is > 0 ? count.Value : 0,
            DryRun = dryRun,
            Force = force
        };
        if (!channel.Writer.TryWrite(new ToolJobRequest(jobId, ToolJobKind.NormalizeConditions, null, null, options)))
        {
            gate.Release();
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _audit.WriteAsync("create", "tool_job", jobId.ToString(),
            $"normalize-conditions count={options.Count}"
                + (options.DryRun ? " dry-run" : "") + (options.Force ? " force" : ""),
            HttpContext.RequestAborted);
        return Accepted(new { job_id = jobId, kind = "normalize-conditions" });
    }
```

- [ ] **Step 5: Add the count to both count-producing paths**

`ToolsViewModel.cs` - add:

```csharp
    /// <summary>Condition strings with no UMLS resolution yet.</summary>
    public int ConditionsRemaining { get; init; }
```

`HomeController.Tools` (lines 383-395) - resolve `IConditionNormalizeJob` via `[FromServices]` and add
`ConditionsRemaining = await conditionJob.CountRemainingAsync(false, cancellationToken),` to the success-path `ToolsViewModel`.

`HomeController.ToolCounts` (lines 471-477) - add
`var conditionsRemaining = await conditionJob.CountRemainingAsync(false, cancellationToken);`
and include `conditionsRemaining` in the returned JSON object.

- [ ] **Step 6: Add the card**

In `Tools.cshtml`, after the embed-studies card closes (line 121), add a card modelled on it:

```html
    @* ---------------- normalize-conditions ---------------- *@
    <div class="col-12 col-lg-6">
        <div class="card h-100" data-tool="normalize-conditions">
            <div class="card-body">
                <div class="d-flex justify-content-between align-items-start">
                    <div>
                        <h2 class="h5 mb-1">Normalize conditions</h2>
                        <p class="text-muted small mb-0">
                            Map AACT condition strings to UMLS concepts so analytics
                            can slice by condition. Highest-volume strings first.
                        </p>
                    </div>
                    <div class="text-end">
                        <div class="display-6 lh-1" data-remaining>@Model.ConditionsRemaining.ToString("N0")</div>
                        <div class="text-muted small">unresolved conditions</div>
                    </div>
                </div>

                @if (canRunPipeline)
                {
                    <form id="conditions-form" asp-action="RunNormalizeConditions" method="post"
                          class="row g-2 align-items-end mt-2 tool-run-form">
                        <div class="col-auto">
                            <label for="conditions-count" class="form-label small text-muted mb-1">Count (0 = all)</label>
                            <input id="conditions-count" name="count" type="number" min="0"
                                   value="0" class="form-control form-control-sm" style="width: 7rem;" />
                        </div>
                        <div class="col-auto">
                            <div class="form-check">
                                <input class="form-check-input" type="checkbox" id="conditions-dryrun" name="dryRun" value="true" />
                                <label class="form-check-label small text-muted" for="conditions-dryrun">Dry run</label>
                            </div>
                            <div class="form-check">
                                <input class="form-check-input" type="checkbox" id="conditions-force" name="force" value="true" />
                                <label class="form-check-label small text-muted" for="conditions-force">Force re-resolve</label>
                            </div>
                        </div>
                        <div class="col-12">
                            <button type="submit" class="btn btn-primary btn-sm tool-run-btn"
                                    disabled="@(busy ? "disabled" : null)">Run normalize</button>
                        </div>
                    </form>
                }

                @await Html.PartialAsync("_ToolJobPanel", "normalize-conditions")
            </div>
        </div>
    </div>
```

- [ ] **Step 7: Wire the count refresh**

In `Tools.cshtml` `refreshCounts` (around line 374), after `set("embed-studies", c.embedRemaining);` add:

```javascript
                    set("normalize-conditions", c.conditionsRemaining);
```

- [ ] **Step 8: Run the tests**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, zero skipped.

- [ ] **Step 9: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/
git commit -m "Add normalize-conditions card to the Tools tab"
```

---

## Task 10: Version bump and release notes

**Files:**
- Modify: `contexts/eligibility/version.json`

A migration requires at least a MINOR bump with `build` reset to 0, so 0.3.1 becomes **0.4.0**.

- [ ] **Step 1: Edit version.json**

Set `current` to `{ "major": 0, "minor": 4, "build": 0, "releaseDate": "2026-07-21" }` and prepend to `releases`:

```json
    {
      "version": "0.4.0",
      "releaseDate": "2026-07-21",
      "enhancements": [
        "AACT condition strings are now mapped to UMLS concepts in a new dictionary table, so corpus analytics can slice by condition. Previously the raw field held 91,600 distinct strings with COVID-19 and Covid19 counted separately.",
        "New Tools card and CLI verb normalize-conditions backfill the dictionary, highest-volume strings first, so a cancelled run still leaves the corpus better off.",
        "The pipeline fills the dictionary as it processes trials, so new condition strings resolve without a manual step."
      ],
      "fixes": []
    },
```

Keep the file ASCII-only. `releases[0]` must match `current`.

- [ ] **Step 2: Full verification**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, zero failures, **zero skipped**. A skipped Postgres suite means Docker is not running - fix that before believing the result, because the schema and store tests are exactly what this change needs verified.

- [ ] **Step 3: Commit**

```powershell
git add contexts/eligibility/version.json
git commit -m "Bump version to 0.4.0 for the condition dictionary"
```

---

## Post-implementation verification (against the real corpus)

The spec's acceptance criteria 2 to 5 cannot be checked by unit tests - they need the production corpus. After the backfill runs, verify:

```sql
-- Criterion 2: at least 71% of study mentions resolve.
WITH m AS (SELECT nct_id, unnest(conditions) AS cond FROM public.eligibility_study_detail),
     j AS (SELECT count(DISTINCT m.nct_id) AS studies, d.concept_code
           FROM m JOIN public.condition_concept d
             ON d.condition_norm = regexp_replace(btrim(lower(m.cond)), '\s+', ' ', 'g')
           GROUP BY d.concept_code)
SELECT round(100.0 * sum(studies) FILTER (WHERE concept_code IS NOT NULL) / sum(studies), 1) AS pct_resolved
FROM j;

-- Criteria 3, 4, 5.
SELECT condition_norm, concept_code, umls_name, match_tier, match_score
FROM public.condition_concept
WHERE condition_norm IN ('stroke', 'nsclc', 'covid-19', 'covid19')
ORDER BY condition_norm;
```

Expected: `pct_resolved >= 71`; `stroke` resolves to `C0038454`; `nsclc` is either a non-small-cell-lung-cancer CUI or unresolved, never `C0700294` (NSC762); `covid-19` and `covid19` carry the same `concept_code`.

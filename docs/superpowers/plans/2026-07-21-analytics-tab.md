# Analytics Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new Analytics area with three views over the processed corpus - distinctiveness (lift), trend over time, and concept lookup.

**Architecture:** Live queries throughout, no precomputed aggregate tables. Two composite indexes (migration V25) make the cohort query fast enough; the corpus-wide baseline is memoised through the existing `ICorpusReadCache`. A new `AnalyticsGateway` in Data owns the SQL, a pure `LiftCalculator` in Core owns the arithmetic, and a new `AnalyticsController` in Web owns the three views.

**Tech Stack:** .NET 8, VB.NET (Core / Data), C# (Web), Npgsql, PostgreSQL 18, xUnit + Testcontainers, Bootstrap 5.

**Spec:** [docs/superpowers/specs/2026-07-21-analytics-tab-design.md](../specs/2026-07-21-analytics-tab-design.md). Read section 2 first - it records the measurements, including two performance hypotheses that were tested and disproved, and the reason lift is NOT the sort key.

## Global Constraints

- **ASCII only** in every authored file - VB, C#, SQL, cshtml, JavaScript, comments, commit messages. No em/en dashes (plain hyphen `-`), no curly quotes, no ellipsis characters. Windows PowerShell 5.1 misreads them.
- **Never write files with PowerShell `Set-Content` or `Out-File`** - they add a BOM. Use the Write/Edit tools.
- `Option Strict On`, `Option Infer On` for VB; `Nullable enable` for C#. Explicit conversions required in VB.
- **Verification is `dotnet test contexts/eligibility/Eligibility.sln`, never `dotnet build`.** No task is exempt.
- Every new public function ships with a test in the same commit.
- A new migration must be registered in **two** places or the whole Postgres suite fails: `MigrationResourceNames` in `PostgresGateway.vb` AND an `<EmbeddedResource>` with explicit `<LogicalName>` in `EligibilityProcessing.Data.vbproj`.
- A migration requires updating [docs/specs/database_schema.md](../../specs/database_schema.md) in the same change.
- **Display labels come from `umls.concept.pref_name`, never `eligibility.concept`.** One CUI carries 1,060 distinct extracted label strings.
- Work on branch `feat/analytics-tab` (already created, spec committed). Never commit to `main`.
- Postgres `count(...)` returns bigint - read with `reader.GetInt64(n)` then `CInt(...)`.

---

## File Structure

**Create:**

| Path | Responsibility |
|---|---|
| `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V25__analytics_indexes.sql` | the two composite indexes |
| `contexts/eligibility/src/EligibilityProcessing.Core/AnalyticsTypes.vb` | `ConceptLiftRow`, `TrendPoint`, `ConceptSummary`, `AnalyticsCohort` |
| `contexts/eligibility/src/EligibilityProcessing.Core/IAnalyticsGateway.vb` | the data port |
| `contexts/eligibility/src/EligibilityProcessing.Core/LiftCalculator.vb` | pure arithmetic, support filter, sort |
| `contexts/eligibility/src/EligibilityProcessing.Data/AnalyticsGateway.vb` | all analytics SQL |
| `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/AnalyticsController.cs` | three views + exports |
| `contexts/eligibility/src/EligibilityProcessing.Web/Models/AnalyticsViewModels.cs` | view models |
| `contexts/eligibility/src/EligibilityProcessing.Web/Export/AnalyticsLiftCsv.cs` | lift CSV |
| `contexts/eligibility/src/EligibilityProcessing.Web/Views/Analytics/Index.cshtml` | lift view |
| `contexts/eligibility/src/EligibilityProcessing.Web/Views/Analytics/Trend.cshtml` | trend view |
| `contexts/eligibility/src/EligibilityProcessing.Web/Views/Analytics/Concept.cshtml` | lookup view |
| `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/LiftCalculatorTests.vb` | pure unit tests |
| `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb` | real Postgres |

**Modify:** `PostgresGateway.vb` (migration list), `EligibilityProcessing.Data.vbproj`, `ICorpusReadCache.vb` + `CorpusReadCache.vb` (cached baseline), `CompositionRoot.vb` (registration), `Views/Shared/_Layout.cshtml` + `_IconSprite.cshtml` (nav), `docs/specs/database_schema.md`, `contexts/eligibility/version.json`.

---

## Task 1: Migration V25 and the composite indexes

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V25__analytics_indexes.sql`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:56`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/EligibilityProcessing.Data.vbproj:91`
- Modify: `docs/specs/database_schema.md`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb`

**Interfaces:**
- Produces: indexes `ix_eligibility_concept_nct` and `ix_eligibility_nct_concept` on `public.eligibility`, relied on by every later query task.

- [ ] **Step 1: Write the failing test**

Create `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb`:

```vb
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Xunit

' Integration tests for the analytics reads (V25 indexes + AnalyticsGateway).
Public Class AnalyticsGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function V25_creates_both_analytics_indexes() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim indexes As New List(Of String)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT indexname FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'eligibility'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        indexes.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using

        Assert.Contains("ix_eligibility_concept_nct", indexes)
        Assert.Contains("ix_eligibility_nct_concept", indexes)
    End Function
End Class
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~AnalyticsGatewayTests"
```

Expected: FAIL - neither index exists.

- [ ] **Step 3: Create the migration**

Create `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V25__analytics_indexes.sql`:

```sql
-- V25: composite indexes for the Analytics tab's cohort queries.
--
-- The cohort profile (all concepts in the trials matching a cohort, with
-- distinct-trial counts) measured 3.6s against the production corpus:
-- 4,439,480 criterion rows, 2,600 MB. Two hypotheses were tested before the
-- plan explained where the time went; both are recorded in the design spec
-- section 2.4 so nobody repeats them.
--
-- ix_eligibility_concept_nct - concept_code FIRST. The cohort predicate filters
-- on concept_code, so with concept_code second it can only scan: measured
-- 4,401,106 of 4,439,480 index entries read and discarded. Leading on it lets
-- the filter seek. It also returns rows already ordered by (concept_code,
-- nct_id), which is exactly the order the group-by needs, making the sort for
-- count(DISTINCT nct_id) nearly free - a seek-based plan that yields rows in
-- nct_id order measured SLOWER overall despite gathering data 3.6x faster.
--
-- ix_eligibility_nct_concept - covers the join back from the cohort, so it
-- never touches the heap (measured Heap Fetches: 0).
--
-- Together: 3,600ms -> 1,225ms. Corpus baseline 4,900ms -> 2,000ms.
-- 162 MB each, 324 MB total against a 2,600 MB table, ~13s each to build.
--
-- Both already exist on the production database, created with CREATE INDEX
-- CONCURRENTLY during that measurement. IF NOT EXISTS makes this a no-op there
-- and correct everywhere else - without the migration the schema would not be
-- reproducible.

CREATE INDEX IF NOT EXISTS ix_eligibility_concept_nct
    ON public.eligibility (concept_code, nct_id);

CREATE INDEX IF NOT EXISTS ix_eligibility_nct_concept
    ON public.eligibility (nct_id, concept_code);
```

- [ ] **Step 4: Register the migration in both places**

In `PostgresGateway.vb`, add a comma to line 56 and append:

```vb
            "EligibilityProcessing.Data.Migrations.V24__condition_concept.sql",
            "EligibilityProcessing.Data.Migrations.V25__analytics_indexes.sql"
        }
```

In `EligibilityProcessing.Data.vbproj`, after the V24 entry (line 91):

```xml
    <EmbeddedResource Include="Migrations\V25__analytics_indexes.sql">
      <LogicalName>EligibilityProcessing.Data.Migrations.V25__analytics_indexes.sql</LogicalName>
    </EmbeddedResource>
```

- [ ] **Step 5: Run the test to verify it passes**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~AnalyticsGatewayTests"
```

Expected: PASS.

There is an existing test asserting the last migration name - `PostgresGatewayUnitTests.MigrationNames_are_short_names_in_order_with_latest_last`. Update its expected value to `V25__analytics_indexes`.

- [ ] **Step 6: Update the schema doc**

In `docs/specs/database_schema.md`, add both indexes to the `public.eligibility` index list with a one-line purpose each, and add a `V25__analytics_indexes` row to the migration-history table.

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Data/Migrations/V25__analytics_indexes.sql contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb contexts/eligibility/src/EligibilityProcessing.Data/EligibilityProcessing.Data.vbproj contexts/eligibility/tests/EligibilityProcessing.Data.Tests/ docs/specs/database_schema.md
git commit -m "Add V25 analytics composite indexes"
```

---

## Task 2: Core result types and the cohort enum

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/AnalyticsTypes.vb`
- Test: covered by Task 3's `LiftCalculatorTests.vb`

**Interfaces:**
- Produces: `AnalyticsCohortKind`, `AnalyticsCohort`, `ConceptCount`, `ConceptLiftRow`, `TrendPoint`, `ConceptSummary` - consumed by Tasks 3-9.

Modelled on `CriterionCluster` (the established Core precedent): immutable, positional constructor, `If(x, "")` on every string.

- [ ] **Step 1: Create the file**

Create `contexts/eligibility/src/EligibilityProcessing.Core/AnalyticsTypes.vb`:

```vb
Imports System.Collections.Generic

' Result types for the Analytics tab. Immutable with positional constructors,
' matching CriterionCluster - the established shape for a Core analytics result.

''' <summary>How a cohort of trials is defined.</summary>
Public Enum AnalyticsCohortKind
    ''' <summary>Trials whose criteria mention a concept (optionally its descendants).</summary>
    Concept
    ''' <summary>Trials whose conditions map to a concept (optionally its descendants).</summary>
    Condition
    ''' <summary>Trials with a given eligibility_study_detail.phase.</summary>
    Phase
    ''' <summary>Trials whose start_date falls in a given year.</summary>
    Year
End Enum

''' <summary>
''' A cohort request: which kind, and the single value that selects it. For
''' Concept and Condition the value is a CUI; for Phase a phase string such as
''' PHASE3; for Year a four-digit year.
''' </summary>
Public NotInheritable Class AnalyticsCohort

    Public Sub New(kind As AnalyticsCohortKind, value As String, includeDescendants As Boolean)
        Me.Kind = kind
        Me.Value = If(value, "")
        Me.IncludeDescendants = includeDescendants
    End Sub

    Public ReadOnly Property Kind As AnalyticsCohortKind
    Public ReadOnly Property Value As String

    ''' <summary>
    ''' Only meaningful for Concept and Condition. Phase and Year ignore it -
    ''' there is no hierarchy over a phase or a year.
    ''' </summary>
    Public ReadOnly Property IncludeDescendants As Boolean

End Class

''' <summary>One concept and the number of distinct trials mentioning it.</summary>
Public NotInheritable Class ConceptCount

    Public Sub New(conceptCode As String, trials As Integer)
        Me.ConceptCode = If(conceptCode, "")
        Me.Trials = trials
    End Sub

    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property Trials As Integer

End Class

''' <summary>
''' One row of the distinctiveness view. Sorted by <see cref="ExcessPp"/>
''' descending, NOT by <see cref="Lift"/> - see the design spec section 4.2.
''' </summary>
Public NotInheritable Class ConceptLiftRow

    Public Sub New(conceptCode As String, prefName As String,
                   cohortTrials As Integer, corpusTrials As Integer,
                   pctCohort As Double, pctCorpus As Double,
                   excessPp As Double, lift As Double,
                   definesCohort As Boolean)
        Me.ConceptCode = If(conceptCode, "")
        Me.PrefName = If(prefName, "")
        Me.CohortTrials = cohortTrials
        Me.CorpusTrials = corpusTrials
        Me.PctCohort = pctCohort
        Me.PctCorpus = pctCorpus
        Me.ExcessPp = excessPp
        Me.Lift = lift
        Me.DefinesCohort = definesCohort
    End Sub

    Public ReadOnly Property ConceptCode As String
    ''' <summary>From umls.concept.pref_name, never from eligibility.concept.</summary>
    Public ReadOnly Property PrefName As String
    Public ReadOnly Property CohortTrials As Integer
    Public ReadOnly Property CorpusTrials As Integer
    Public ReadOnly Property PctCohort As Double
    Public ReadOnly Property PctCorpus As Double

    ''' <summary>Percentage points by which the cohort exceeds the corpus. The sort key.</summary>
    Public ReadOnly Property ExcessPp As Double

    ''' <summary>
    ''' Ratio of the two rates. Displayed, not sorted on: lift saturates at
    ''' corpus_size / cohort_size and every concept appearing only inside the
    ''' cohort reaches that ceiling, so it cannot rank them.
    ''' </summary>
    Public ReadOnly Property Lift As Double

    ''' <summary>
    ''' True when this concept is the cohort's own defining concept or one of its
    ''' hierarchy descendants - such a row is tautological. Marked, not hidden.
    ''' Always False for Phase and Year cohorts.
    ''' </summary>
    Public ReadOnly Property DefinesCohort As Boolean

End Class

''' <summary>One year of the trend view.</summary>
Public NotInheritable Class TrendPoint

    Public Sub New(year As Integer, studiesThatYear As Integer,
                   trialsWithConcept As Integer, pctOfYear As Double, isPartial As Boolean)
        Me.Year = year
        Me.StudiesThatYear = studiesThatYear
        Me.TrialsWithConcept = trialsWithConcept
        Me.PctOfYear = pctOfYear
        Me.IsPartial = isPartial
    End Sub

    Public ReadOnly Property Year As Integer

    ''' <summary>
    ''' Denominator - processed studies started that year. Carried so a thin
    ''' year is self-evident rather than looking equal to a well-covered one.
    ''' </summary>
    Public ReadOnly Property StudiesThatYear As Integer

    Public ReadOnly Property TrialsWithConcept As Integer
    Public ReadOnly Property PctOfYear As Double

    ''' <summary>True for the current calendar year, which is a part-year by definition.</summary>
    Public ReadOnly Property IsPartial As Boolean

End Class

''' <summary>Everything the concept lookup view shows about one concept.</summary>
Public NotInheritable Class ConceptSummary

    Public Sub New(conceptCode As String, prefName As String, rootSource As String,
                   semanticTypes As String, ancestorCount As Integer, descendantCount As Integer,
                   trials As Integer, corpusTrials As Integer,
                   byPhase As IReadOnlyList(Of ConceptCount),
                   exampleCriteria As IReadOnlyList(Of String))
        Me.ConceptCode = If(conceptCode, "")
        Me.PrefName = If(prefName, "")
        Me.RootSource = If(rootSource, "")
        Me.SemanticTypes = If(semanticTypes, "")
        Me.AncestorCount = ancestorCount
        Me.DescendantCount = descendantCount
        Me.Trials = trials
        Me.CorpusTrials = corpusTrials
        Me.ByPhase = If(byPhase, CType(Array.Empty(Of ConceptCount)(), IReadOnlyList(Of ConceptCount)))
        Me.ExampleCriteria = If(exampleCriteria, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
    End Sub

    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property PrefName As String
    Public ReadOnly Property RootSource As String
    Public ReadOnly Property SemanticTypes As String

    ''' <summary>Rows in umls.concept_ancestor where this concept is the descendant. 0 means it cannot roll up.</summary>
    Public ReadOnly Property AncestorCount As Integer
    Public ReadOnly Property DescendantCount As Integer

    Public ReadOnly Property Trials As Integer
    Public ReadOnly Property CorpusTrials As Integer

    ''' <summary>Phase label to trial count. ConceptCount.ConceptCode carries the phase label here.</summary>
    Public ReadOnly Property ByPhase As IReadOnlyList(Of ConceptCount)

    ''' <summary>
    ''' Up to five real eligibility.criterion texts. The ONE place raw extracted
    ''' text is shown, and it is labelled as examples - never used as a concept
    ''' label, because one CUI carries over a thousand distinct extracted strings.
    ''' </summary>
    Public ReadOnly Property ExampleCriteria As IReadOnlyList(Of String)

End Class
```

- [ ] **Step 2: Write tests pinning the defaults**

Create `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/AnalyticsTypeTests.vb`. These types carry null-coalescing and defaults that later tasks rely on:

```vb
Imports System.Collections.Generic
Imports EligibilityProcessing.Core
Imports Xunit

Public Class AnalyticsTypeTests

    <Fact>
    Public Sub Null_strings_coalesce_to_empty_not_Nothing()
        Dim row As New ConceptLiftRow(Nothing, Nothing, 1, 1, 1.0, 1.0, 0.0, 1.0, False)
        Assert.Equal("", row.ConceptCode)
        Assert.Equal("", row.PrefName)
    End Sub

    <Fact>
    Public Sub Cohort_defaults_value_to_empty_and_keeps_the_kind()
        Dim c As New AnalyticsCohort(AnalyticsCohortKind.Phase, Nothing, True)
        Assert.Equal(AnalyticsCohortKind.Phase, c.Kind)
        Assert.Equal("", c.Value)
        Assert.True(c.IncludeDescendants)
    End Sub

    <Fact>
    Public Sub ConceptSummary_lists_default_to_empty_not_Nothing()
        ' The views enumerate these directly; a Nothing would throw at render.
        Dim s As New ConceptSummary("C1", "Name", "SRC", "Sty", 0, 0, 0, 0, Nothing, Nothing)
        Assert.NotNull(s.ByPhase)
        Assert.Empty(s.ByPhase)
        Assert.NotNull(s.ExampleCriteria)
        Assert.Empty(s.ExampleCriteria)
    End Sub

    <Fact>
    Public Sub TrendPoint_carries_its_denominator_and_partial_flag()
        Dim p As New TrendPoint(2026, 26498, 450, 1.7, True)
        Assert.Equal(2026, p.Year)
        Assert.Equal(26498, p.StudiesThatYear)
        Assert.True(p.IsPartial)
    End Sub
End Class
```

- [ ] **Step 3: Run the tests**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~AnalyticsTypeTests"
```

Expected: PASS. Verification is `dotnet test`, never `dotnet build` - no task is exempt, and no new public type ships without a test.

- [ ] **Step 4: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/AnalyticsTypes.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/AnalyticsTypeTests.vb
git commit -m "Add analytics result types"
```

---

## Task 3: LiftCalculator

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/LiftCalculator.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/LiftCalculatorTests.vb`

**Interfaces:**
- Consumes: `ConceptCount`, `ConceptLiftRow`.
- Produces: `LiftCalculator.DefaultMinimumSupport As Integer`, `LiftCalculator.Build(cohortCounts, corpusCounts, cohortSize, corpusSize, prefNames, definingCodes, minimumSupport) As IReadOnlyList(Of ConceptLiftRow)`.

This is where the spec's central decision lives. **Sorted by excess percentage points, not lift.**

- [ ] **Step 1: Write the failing tests**

Create `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/LiftCalculatorTests.vb`:

```vb
Imports System.Collections.Generic
Imports System.Linq
Imports EligibilityProcessing.Core
Imports Xunit

Public Class LiftCalculatorTests

    Private Shared Function Counts(ParamArray pairs As (String, Integer)()) As IReadOnlyList(Of ConceptCount)
        Return pairs.Select(Function(p) New ConceptCount(p.Item1, p.Item2)).ToList()
    End Function

    Private Shared Function Names(ParamArray pairs As (String, String)()) As IReadOnlyDictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)
        For Each p In pairs
            d(p.Item1) = p.Item2
        Next
        Return d
    End Function

    <Fact>
    Public Sub Concept_at_exactly_corpus_rate_scores_lift_one_and_zero_excess()
        Dim rows = LiftCalculator.Build(
                cohortCounts:=Counts(("C1", 10)),
                corpusCounts:=Counts(("C1", 100)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Dim r = rows.Single()
        Assert.Equal(1.0, r.Lift, 3)
        Assert.Equal(0.0, r.ExcessPp, 3)
        Assert.Equal(10.0, r.PctCohort, 3)
        Assert.Equal(10.0, r.PctCorpus, 3)
    End Sub

    <Fact>
    Public Sub Concept_rarer_in_cohort_than_corpus_yields_negative_excess()
        Dim rows = LiftCalculator.Build(
                Counts(("C1", 1)), Counts(("C1", 500)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Dim r = rows.Single()
        Assert.True(r.ExcessPp < 0, $"expected negative excess, got {r.ExcessPp}")
        Assert.True(r.Lift < 1.0)
    End Sub

    ' THE test for the spec's central decision. Lift saturates at
    ' corpusSize/cohortSize and cannot rank concepts that reach the ceiling.
    ' If anyone reverts the sort key to lift, this fails.
    <Fact>
    Public Sub Rows_are_ordered_by_excess_not_by_lift()
        ' BIG: 50% of cohort vs 10% of corpus -> excess 40pp, lift 5
        ' TINY: 2% of cohort vs 0.1% of corpus -> excess 1.9pp, lift 20
        Dim rows = LiftCalculator.Build(
                Counts(("BIG", 500), ("TINY", 20)),
                Counts(("BIG", 1000), ("TINY", 10)),
                cohortSize:=1000, corpusSize:=10000,
                prefNames:=Names(("BIG", "Big"), ("TINY", "Tiny")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Equal("BIG", rows(0).ConceptCode)
        Assert.Equal("TINY", rows(1).ConceptCode)
        ' Confirm the orderings genuinely disagree, so the test is meaningful.
        Assert.True(rows(1).Lift > rows(0).Lift,
                    "fixture is wrong: lift ordering must disagree with excess ordering")
    End Sub

    <Fact>
    Public Sub Minimum_support_is_inclusive_at_the_threshold()
        Dim rows = LiftCalculator.Build(
                Counts(("KEEP", 10), ("DROP", 9)),
                Counts(("KEEP", 10), ("DROP", 9)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("KEEP", "Keep"), ("DROP", "Drop")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=10)

        Assert.Single(rows)
        Assert.Equal("KEEP", rows.Single().ConceptCode)
    End Sub

    <Fact>
    Public Sub Zero_corpus_count_does_not_divide_by_zero()
        Dim rows = LiftCalculator.Build(
                Counts(("C1", 5)), Counts(),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("C1", "Thing")),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Dim r = rows.Single()
        Assert.False(Double.IsNaN(r.Lift))
        Assert.False(Double.IsInfinity(r.Lift))
    End Sub

    <Fact>
    Public Sub Defining_codes_are_flagged_but_not_removed()
        Dim rows = LiftCalculator.Build(
                Counts(("DEF", 50), ("OTHER", 20)),
                Counts(("DEF", 60), ("OTHER", 200)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(("DEF", "Definer"), ("OTHER", "Other")),
                definingCodes:=New HashSet(Of String)({"DEF"}),
                minimumSupport:=1)

        Assert.Equal(2, rows.Count)
        Assert.True(rows.Single(Function(r) r.ConceptCode = "DEF").DefinesCohort)
        Assert.False(rows.Single(Function(r) r.ConceptCode = "OTHER").DefinesCohort)
    End Sub

    <Fact>
    Public Sub Empty_cohort_returns_empty_rather_than_throwing()
        Dim rows = LiftCalculator.Build(
                Counts(), Counts(("C1", 10)),
                cohortSize:=0, corpusSize:=1000,
                prefNames:=Names(),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Empty(rows)
    End Sub

    <Fact>
    Public Sub Missing_pref_name_falls_back_to_the_concept_code()
        ' Never falls back to extracted concept text - that is the point.
        Dim rows = LiftCalculator.Build(
                Counts(("C9", 10)), Counts(("C9", 10)),
                cohortSize:=100, corpusSize:=1000,
                prefNames:=Names(),
                definingCodes:=New HashSet(Of String)(),
                minimumSupport:=1)

        Assert.Equal("C9", rows.Single().PrefName)
    End Sub
End Class
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~LiftCalculatorTests"
```

Expected: FAIL to compile - `LiftCalculator` is not defined.

- [ ] **Step 3: Implement the calculator**

Create `contexts/eligibility/src/EligibilityProcessing.Core/LiftCalculator.vb`:

```vb
Imports System.Collections.Generic
Imports System.Linq

''' <summary>
''' Turns cohort and corpus concept counts into ranked distinctiveness rows.
''' Pure - no database, no I/O - so the ranking rule is unit-testable.
'''
''' Rows are ordered by EXCESS PERCENTAGE POINTS, not by lift. Lift saturates:
''' its maximum is corpusSize / cohortSize, and every concept that appears only
''' inside the cohort reaches that ceiling, so it cannot rank them. Measured on
''' a diabetes cohort the top eleven rows by lift all tied at 9.82, putting
''' "insulin pen injector" and "recurrent severe manic episodes" alongside real
''' findings. Ordering by excess produced hypertension, BMI, cardiovascular
''' disease, smoking and HbA1c instead. See the design spec section 4.2.
'''
''' Lift is still computed and displayed, because the two answer different
''' questions - "how much more common" versus "how many times more common".
''' </summary>
Public NotInheritable Class LiftCalculator

    ''' <summary>
    ''' Concepts below this many cohort trials are dropped. 71.3% of corpus
    ''' concepts appear in five or fewer trials, so without a floor the ranking
    ''' fills with one-trial noise carrying enormous ratios.
    ''' </summary>
    Public Const DefaultMinimumSupport As Integer = 10

    Private Sub New()
        ' Static-only.
    End Sub

    Public Shared Function Build(
            cohortCounts As IReadOnlyList(Of ConceptCount),
            corpusCounts As IReadOnlyList(Of ConceptCount),
            cohortSize As Integer,
            corpusSize As Integer,
            prefNames As IReadOnlyDictionary(Of String, String),
            definingCodes As ISet(Of String),
            minimumSupport As Integer) As IReadOnlyList(Of ConceptLiftRow)

        If cohortCounts Is Nothing OrElse cohortCounts.Count = 0 Then
            Return Array.Empty(Of ConceptLiftRow)()
        End If
        ' A zero-size cohort has no rates to compute; nothing meaningful to say.
        If cohortSize <= 0 OrElse corpusSize <= 0 Then
            Return Array.Empty(Of ConceptLiftRow)()
        End If

        Dim corpusByCode As New Dictionary(Of String, Integer)
        If corpusCounts IsNot Nothing Then
            For Each c In corpusCounts
                corpusByCode(c.ConceptCode) = c.Trials
            Next
        End If

        Dim floor = Math.Max(1, minimumSupport)
        Dim rows As New List(Of ConceptLiftRow)

        For Each c In cohortCounts
            If c.Trials < floor Then Continue For

            Dim corpusTrials As Integer = 0
            corpusByCode.TryGetValue(c.ConceptCode, corpusTrials)

            Dim pctCohort = 100.0 * c.Trials / cohortSize
            Dim pctCorpus = 100.0 * corpusTrials / corpusSize

            ' A concept absent from the corpus counts cannot have a ratio. Report
            ' lift 0 rather than infinity or NaN, which no UI can render and no
            ' sort can order. Excess is still meaningful and is the sort key.
            Dim lift As Double = If(pctCorpus > 0.0, pctCohort / pctCorpus, 0.0)

            Dim name As String = Nothing
            If prefNames Is Nothing OrElse Not prefNames.TryGetValue(c.ConceptCode, name) Then
                name = Nothing
            End If

            rows.Add(New ConceptLiftRow(
                    conceptCode:=c.ConceptCode,
                    prefName:=If(String.IsNullOrEmpty(name), c.ConceptCode, name),
                    cohortTrials:=c.Trials,
                    corpusTrials:=corpusTrials,
                    pctCohort:=pctCohort,
                    pctCorpus:=pctCorpus,
                    excessPp:=pctCohort - pctCorpus,
                    lift:=lift,
                    definesCohort:=definingCodes IsNot Nothing AndAlso definingCodes.Contains(c.ConceptCode)))
        Next

        ' Excess first. Cohort trials then concept code break ties, so a re-run
        ' reproduces the same order.
        Return rows _
            .OrderByDescending(Function(r) r.ExcessPp) _
            .ThenByDescending(Function(r) r.CohortTrials) _
            .ThenBy(Function(r) r.ConceptCode, StringComparer.Ordinal) _
            .ToList()
    End Function

End Class
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~LiftCalculatorTests"
```

Expected: PASS, all eight.

- [ ] **Step 5: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/LiftCalculator.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/LiftCalculatorTests.vb
git commit -m "Add LiftCalculator ranked by excess percentage points"
```

---

## Task 4: IAnalyticsGateway port and the cohort + profile queries

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/IAnalyticsGateway.vb`
- Create: `contexts/eligibility/src/EligibilityProcessing.Data/AnalyticsGateway.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb` (append)

**Interfaces:**
- Consumes: `AnalyticsCohort`, `ConceptCount`, `TrendPoint`, `ConceptSummary`.
- Produces: `IAnalyticsGateway` with `GetCohortProfileAsync`, `GetCohortSizeAsync`, `GetCorpusProfileAsync`, `GetCorpusTrialCountAsync`, `GetCohortDefiningCodesAsync`, `GetPrefNamesAsync`, `GetTrendAsync`, `GetConceptSummaryAsync`, `SearchConceptsAsync`; and `AnalyticsGateway` implementing it with constructor `New(outputDataSource As NpgsqlDataSource)`.

- [ ] **Step 1: Create the port**

Create `contexts/eligibility/src/EligibilityProcessing.Core/IAnalyticsGateway.vb`:

```vb
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' Read-only analytics queries over the processed corpus. Separate from
''' IPostgresGateway, which owns pipeline persistence and carries ~70 methods -
''' these reads share none of that concern.
''' </summary>
Public Interface IAnalyticsGateway

    ''' <summary>Distinct trials matching the cohort.</summary>
    Function GetCohortSizeAsync(cohort As AnalyticsCohort,
                                cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Per-concept distinct-trial counts within the cohort.</summary>
    Function GetCohortProfileAsync(cohort As AnalyticsCohort,
                                   cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptCount))

    ''' <summary>
    ''' Per-concept distinct-trial counts across the whole corpus - the lift
    ''' baseline. Identical for every request, so callers memoise it.
    ''' </summary>
    Function GetCorpusProfileAsync(cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptCount))

    ''' <summary>Distinct trials with any resolved concept - the baseline denominator.</summary>
    Function GetCorpusTrialCountAsync(cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' The concept codes that define this cohort - its own concept plus, when
    ''' descendants are included, those descendants. Empty for Phase and Year.
    ''' Used to flag tautological rows, not to remove them.
    ''' </summary>
    Function GetCohortDefiningCodesAsync(cohort As AnalyticsCohort,
                                         cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

    ''' <summary>Preferred names for the given CUIs, from umls.concept.</summary>
    Function GetPrefNamesAsync(conceptCodes As IReadOnlyList(Of String),
                               cancellationToken As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, String))

    ''' <summary>Per-year prevalence of one concept as a share of that year's processed studies.</summary>
    Function GetTrendAsync(conceptCode As String,
                           currentYear As Integer,
                           cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of TrendPoint))

    ''' <summary>Everything the lookup view shows, or Nothing when the CUI is unknown.</summary>
    Function GetConceptSummaryAsync(conceptCode As String,
                                    cancellationToken As CancellationToken) As Task(Of ConceptSummary)

    ''' <summary>Concepts whose preferred name matches, most-used first, capped at limit.</summary>
    Function SearchConceptsAsync(term As String, limit As Integer,
                                 cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptSummary))

End Interface
```

- [ ] **Step 2: Write the failing tests**

Append to `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb`:

```vb
    Private Async Function SeedRowAsync(nctId As String, criterion As String,
                                        conceptCode As String, conceptText As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.eligibility (nct_id, criterion, domain, concept, concept_code,
                                semantic_type, qualifier, time_window, original_text,
                                umls_name, match_score, match_source)
VALUES (@n, @cr, '', @ct, @cc, '', '', '', '', '', 0, '')"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("cr", criterion)
                cmd.Parameters.AddWithValue("ct", conceptText)
                cmd.Parameters.AddWithValue("cc", conceptCode)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedConceptAsync(cui As String, prefName As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.concept (cui, pref_name, root_source) VALUES (@c, @p, 'SNOMEDCT_US')
ON CONFLICT (cui) DO UPDATE SET pref_name = excluded.pref_name"
                cmd.Parameters.AddWithValue("c", cui)
                cmd.Parameters.AddWithValue("p", prefName)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function SeedAncestorAsync(descendant As String, ancestor As String) As Task
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.concept_ancestor (descendant_cui, ancestor_cui, min_distance)
VALUES (@d, @a, 1) ON CONFLICT DO NOTHING"
                cmd.Parameters.AddWithValue("d", descendant)
                cmd.Parameters.AddWithValue("a", ancestor)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function Concept_cohort_includes_descendants_when_asked() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_PARENT", "Parent")
        Await SeedConceptAsync("C_CHILD", "Child")
        Await SeedConceptAsync("C_OTHER", "Other")
        Await SeedAncestorAsync("C_CHILD", "C_PARENT")
        Await SeedRowAsync("NCT001", "Inclusion", "C_PARENT", "parent")
        Await SeedRowAsync("NCT002", "Inclusion", "C_CHILD", "child")
        Await SeedRowAsync("NCT003", "Inclusion", "C_OTHER", "other")

        Dim g As New AnalyticsGateway(_fixture.DataSource)

        Dim withKids = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_PARENT", True), CancellationToken.None)
        Assert.Equal(2, withKids)

        Dim withoutKids = Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_PARENT", False), CancellationToken.None)
        Assert.Equal(1, withoutKids)
    End Function

    <SkippableFact>
    Public Async Function Cohort_profile_counts_distinct_trials_not_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_A", "A")
        ' Same concept twice in ONE trial must count once.
        Await SeedRowAsync("NCT001", "Inclusion", "C_A", "a")
        Await SeedRowAsync("NCT001", "Exclusion", "C_A", "a again")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim prof = Await g.GetCohortProfileAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_A", False), CancellationToken.None)

        Assert.Equal(1, prof.Single(Function(p) p.ConceptCode = "C_A").Trials)
    End Function

    <SkippableFact>
    Public Async Function Defining_codes_cover_the_concept_and_its_descendants() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedAncestorAsync("C_CHILD", "C_PARENT")

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim codes = Await g.GetCohortDefiningCodesAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_PARENT", True), CancellationToken.None)

        Assert.Contains("C_PARENT", codes)
        Assert.Contains("C_CHILD", codes)

        ' Phase cohorts have no defining concepts - nothing is tautological.
        Dim none = Await g.GetCohortDefiningCodesAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Phase, "PHASE3", False), CancellationToken.None)
        Assert.Empty(none)
    End Function

    <SkippableFact>
    Public Async Function All_four_cohort_kinds_return_the_same_shape() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await SeedConceptAsync("C_A", "A")
        Await SeedRowAsync("NCT001", "Inclusion", "C_A", "a")
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.eligibility_study_detail (nct_id, phase, start_date, conditions)
VALUES ('NCT001', 'PHASE3', DATE '2023-05-01', ARRAY['Thing'])"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim kinds = {
            New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_A", False),
            New AnalyticsCohort(AnalyticsCohortKind.Phase, "PHASE3", False),
            New AnalyticsCohort(AnalyticsCohortKind.Year, "2023", False)
        }

        ' The view renders all four uniformly, so every kind must return a
        ' well-formed profile - never Nothing, never a throw.
        For Each k In kinds
            Dim size = Await g.GetCohortSizeAsync(k, CancellationToken.None)
            Dim prof = Await g.GetCohortProfileAsync(k, CancellationToken.None)
            Assert.True(size >= 0)
            Assert.NotNull(prof)
        Next
    End Function

    <SkippableFact>
    Public Async Function Unknown_cohort_value_returns_empty_not_an_error() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim g As New AnalyticsGateway(_fixture.DataSource)
        Dim prof = Await g.GetCohortProfileAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_NOPE", False), CancellationToken.None)

        Assert.Empty(prof)
        Assert.Equal(0, Await g.GetCohortSizeAsync(
                New AnalyticsCohort(AnalyticsCohortKind.Concept, "C_NOPE", False), CancellationToken.None))
    End Function
```

Add `Imports System.Linq` to the top of the test file.

- [ ] **Step 3: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~AnalyticsGatewayTests"
```

Expected: FAIL to compile - `AnalyticsGateway` is not defined.

- [ ] **Step 4: Implement the gateway's cohort and profile methods**

Create `contexts/eligibility/src/EligibilityProcessing.Data/AnalyticsGateway.vb` with the class header and the six lift-related methods. Model the file on `ConditionConceptStore.vb`: `Imports` block, XML doc, `Implements IAnalyticsGateway`, one `NpgsqlDataSource` field, guard-clause constructor, inline `cmd.CommandText`.

The cohort predicate is built by a private helper so all four kinds share one shape:

```vb
    ' Returns the SQL fragment selecting the cohort's nct_ids, plus binds its
    ' parameters. All four kinds produce a single-column set of nct_id so the
    ' callers can treat them identically.
    Private Shared Function CohortSql(cohort As AnalyticsCohort) As String
        Select Case cohort.Kind
            Case AnalyticsCohortKind.Concept
                If cohort.IncludeDescendants Then
                    Return "
SELECT DISTINCT e.nct_id FROM public.eligibility e
WHERE e.concept_code = @val
   OR e.concept_code IN (SELECT descendant_cui FROM umls.concept_ancestor WHERE ancestor_cui = @val)"
                End If
                Return "SELECT DISTINCT e.nct_id FROM public.eligibility e WHERE e.concept_code = @val"

            Case AnalyticsCohortKind.Condition
                If cohort.IncludeDescendants Then
                    Return "
SELECT DISTINCT d.nct_id
FROM public.eligibility_study_detail d
JOIN LATERAL unnest(d.conditions) AS cond(txt) ON true
JOIN public.condition_concept cc
  ON cc.condition_norm = regexp_replace(btrim(lower(cond.txt)), '\s+', ' ', 'g')
WHERE cc.concept_code = @val
   OR cc.concept_code IN (SELECT descendant_cui FROM umls.concept_ancestor WHERE ancestor_cui = @val)"
                End If
                Return "
SELECT DISTINCT d.nct_id
FROM public.eligibility_study_detail d
JOIN LATERAL unnest(d.conditions) AS cond(txt) ON true
JOIN public.condition_concept cc
  ON cc.condition_norm = regexp_replace(btrim(lower(cond.txt)), '\s+', ' ', 'g')
WHERE cc.concept_code = @val"

            Case AnalyticsCohortKind.Phase
                Return "SELECT DISTINCT d.nct_id FROM public.eligibility_study_detail d WHERE d.phase = @val"

            Case Else ' Year
                Return "
SELECT DISTINCT d.nct_id FROM public.eligibility_study_detail d
WHERE d.start_date IS NOT NULL
  AND EXTRACT(year FROM d.start_date)::int = @val_int"
        End Select
    End Function
```

`GetCohortSizeAsync` wraps that as `SELECT count(*) FROM (<cohort>) c`. `GetCohortProfileAsync` is:

```sql
WITH cohort AS (<cohort sql>)
SELECT e.concept_code, count(DISTINCT e.nct_id)
FROM public.eligibility e JOIN cohort c ON c.nct_id = e.nct_id
WHERE e.concept_code <> ''
GROUP BY e.concept_code
```

`GetCorpusProfileAsync`:

```sql
SELECT concept_code, count(DISTINCT nct_id)
FROM public.eligibility WHERE concept_code <> '' GROUP BY concept_code
```

`GetCorpusTrialCountAsync`:

```sql
SELECT count(DISTINCT nct_id) FROM public.eligibility WHERE concept_code <> ''
```

`GetCohortDefiningCodesAsync` returns empty for Phase and Year without touching the database; for Concept and Condition it returns the value plus, when descendants are included, `SELECT descendant_cui FROM umls.concept_ancestor WHERE ancestor_cui = @val`.

`GetPrefNamesAsync`:

```sql
SELECT cui, pref_name FROM umls.concept WHERE cui = ANY(@codes)
```

Bind arrays as `NpgsqlDbType.Array Or NpgsqlDbType.Text` with `.Value = codes.ToArray()`. Bind `@val` as text and `@val_int` as integer, parsing the year with `Integer.TryParse` and returning an empty result when it does not parse.

- [ ] **Step 5: Register in DI**

In `CompositionRoot.vb`, after the `IConditionConceptStore` block (ends line 408), add:

```vb
        ' Stateless over one data source, like ConditionConceptStore, so singleton.
        services.AddSingleton(Of IAnalyticsGateway)(
                Function(sp As IServiceProvider) As IAnalyticsGateway
                    Dim outputDs = sp.GetRequiredKeyedService(Of NpgsqlDataSource)(OutputDataSourceKey)
                    Return New AnalyticsGateway(outputDs)
                End Function)
```

- [ ] **Step 6: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~AnalyticsGatewayTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/IAnalyticsGateway.vb contexts/eligibility/src/EligibilityProcessing.Data/AnalyticsGateway.vb contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb
git commit -m "Add AnalyticsGateway cohort and profile queries"
```

---

## Task 5: Cache the corpus baseline

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/ICorpusReadCache.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/CorpusReadCache.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Program.cs` (constructor arg)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/` (existing CorpusReadCache test class, or a new one)

**Interfaces:**
- Consumes: `IAnalyticsGateway.GetCorpusProfileAsync`, `GetCorpusTrialCountAsync`.
- Produces: `ICorpusReadCache.GetCorpusConceptProfileAsync(cancellationToken) As Task(Of CorpusConceptProfile)` and `CorpusConceptProfile` (Core) carrying `Counts As IReadOnlyList(Of ConceptCount)` and `TrialCount As Integer`.

The corpus baseline measured **2.0s** and is identical for every lift request. `ICorpusReadCache` is VB in Core and its comment explicitly rejects whole-gateway decoration, so this is a new method on the existing interface, keyed like the others.

- [ ] **Step 1: Add the result type to AnalyticsTypes.vb**

```vb
''' <summary>
''' The corpus-wide lift baseline: per-concept trial counts plus the denominator.
''' Cached, because it is identical for every lift request and measured at 2.0s.
''' </summary>
Public NotInheritable Class CorpusConceptProfile

    Public Sub New(counts As IReadOnlyList(Of ConceptCount), trialCount As Integer)
        Me.Counts = If(counts, CType(Array.Empty(Of ConceptCount)(), IReadOnlyList(Of ConceptCount)))
        Me.TrialCount = trialCount
    End Sub

    Public ReadOnly Property Counts As IReadOnlyList(Of ConceptCount)
    Public ReadOnly Property TrialCount As Integer

End Class
```

- [ ] **Step 2: Write the failing test**

Find the existing `CorpusReadCache` test class under `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/`; if none exists, create `CorpusReadCacheAnalyticsTests.vb`. The behaviour to pin, using a fake `IAnalyticsGateway` that counts calls:

```vb
    <Fact>
    Public Async Function Corpus_profile_is_fetched_once_and_then_served_from_cache()
        Dim gateway As New CountingAnalyticsGateway()
        Dim cache = NewCache(gateway, TimeSpan.FromMinutes(1))

        Await cache.GetCorpusConceptProfileAsync(CancellationToken.None)
        Await cache.GetCorpusConceptProfileAsync(CancellationToken.None)

        Assert.Equal(1, gateway.ProfileCalls)
    End Function

    <Fact>
    Public Async Function Corpus_profile_bypasses_the_cache_when_the_ttl_is_zero()
        Dim gateway As New CountingAnalyticsGateway()
        Dim cache = NewCache(gateway, TimeSpan.Zero)

        Await cache.GetCorpusConceptProfileAsync(CancellationToken.None)
        Await cache.GetCorpusConceptProfileAsync(CancellationToken.None)

        Assert.Equal(2, gateway.ProfileCalls)
    End Function
```

Write `CountingAnalyticsGateway` as a local fake implementing all nine `IAnalyticsGateway` members, returning empty results from the ones this test does not exercise and incrementing `ProfileCalls` in `GetCorpusProfileAsync`.

- [ ] **Step 3: Run to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~CorpusReadCache"
```

Expected: FAIL - `GetCorpusConceptProfileAsync` is not defined.

- [ ] **Step 4: Extend the interface and implementation**

Add to `ICorpusReadCache.vb`:

```vb
    ''' <summary>
    ''' Corpus-wide per-concept trial counts, the lift baseline for the Analytics
    ''' tab. Measured at 2.0s and identical for every request, so it is cached on
    ''' the same TTL as the dashboard aggregate.
    ''' </summary>
    Function GetCorpusConceptProfileAsync(
            cancellationToken As CancellationToken) As Task(Of CorpusConceptProfile)
```

In `CorpusReadCache.vb`: add `Private Const CorpusProfileKey As String = "corpus:concept-profile"`, take an `IAnalyticsGateway` in the constructor alongside the existing `IPostgresGateway`, and implement the method with the same `IsEnabled` / `TryGetValue` / `Set` shape the existing two methods use.

Update the DI factory in `Program.cs` (lines 43-52) to pass `sp.GetRequiredService<IAnalyticsGateway>()`.

- [ ] **Step 5: Run to verify it passes**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS with 0 skipped.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/ contexts/eligibility/src/EligibilityProcessing.Web/Program.cs contexts/eligibility/tests/EligibilityProcessing.Core.Tests/
git commit -m "Cache the corpus concept profile for the lift baseline"
```

---

## Task 6: AnalyticsController and the lift view

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/AnalyticsController.cs`
- Create: `contexts/eligibility/src/EligibilityProcessing.Web/Models/AnalyticsViewModels.cs`
- Create: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Analytics/Index.cshtml`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Shared/_Layout.cshtml`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Shared/_IconSprite.cshtml`

**Interfaces:**
- Consumes: `IAnalyticsGateway`, `ICorpusReadCache.GetCorpusConceptProfileAsync`, `LiftCalculator.Build`, all the Core types.

Follow `AuthoringController` exactly: class-level `[Authorize]`, constructor injection, and every action wrapped in `try/catch` that logs a warning and re-renders the **same view** with `ErrorMessage` on the view model - not a 500.

- [ ] **Step 1: Create the view models**

`AnalyticsViewModels.cs` holds `AnalyticsLiftViewModel` (cohort kind, value, include-descendants, minimum support, `IReadOnlyList<ConceptLiftRow> Rows`, `int CohortSize`, `int CorpusSize`, `string? ErrorMessage`, `bool HasError => ErrorMessage is not null`), plus `AnalyticsTrendViewModel` and `AnalyticsConceptViewModel` used by Tasks 7 and 8.

- [ ] **Step 2: Create the controller with the Index (lift) action**

The action binds flat nullable query params, matching `HomeController.Results` rather than a bound model: `string? kind, string? value, bool includeDescendants, int? minSupport`.

It resolves the cohort, fetches cohort size, cohort profile, cached corpus profile, defining codes and preferred names, then calls `LiftCalculator.Build`. On a first visit with no `value`, it renders the empty form rather than running a query.

- [ ] **Step 3: Create the view**

`Views/Analytics/Index.cshtml`, matching `Results.cshtml` house style: `@model` fully qualified, `ViewData["Title"]`, `Model.HasError` guard wrapping the body in `else`, `<form method="get" asp-action="Index">` inside a `.card`, Bootstrap `form-select-sm` controls in a `.row.g-2`.

The table shows: concept, cohort trials, % cohort, % corpus, excess pp, lift, and a badge on rows where `DefinesCohort` is true reading "defines cohort". The phase selector carries a note that only ~28% of studies have a real interventional phase.

- [ ] **Step 4: Add the nav entry**

In `_Layout.cshtml`, after the Results `<li>` (around line 107), add an entry using the controller-match helper:

```cshtml
                    <li class="nav-item">
                        <a class="@NavClassController("Analytics")" asp-area="" asp-controller="Analytics" asp-action="Index" title="Analytics - what is distinctive about a set of trials, and how criteria change over time">
                            <svg class="ep-ico" aria-hidden="true" focusable="false"><use href="#ep-analytics"></use></svg><span class="visually-hidden">Analytics</span>
                        </a>
                    </li>
```

Add a matching `#ep-analytics` symbol to `_IconSprite.cshtml`, following the shape of the existing symbols there.

- [ ] **Step 5: Run the suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, 0 skipped.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/
git commit -m "Add Analytics controller, lift view and nav entry"
```

---

## Task 7: Trend view

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/AnalyticsGateway.vb` (add `GetTrendAsync`)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/AnalyticsController.cs` (add `Trend` action)
- Create: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Analytics/Trend.cshtml`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb` (append)

**Interfaces:**
- Produces: `GetTrendAsync(conceptCode, currentYear, ct) As Task(Of IReadOnlyList(Of TrendPoint))`.

This SQL was validated against production at 0.47s:

```sql
WITH yr AS (
  SELECT EXTRACT(year FROM d.start_date)::int AS y, count(*)::numeric AS studies
  FROM public.eligibility_study_detail d
  WHERE d.start_date IS NOT NULL
  GROUP BY 1),
hits AS (
  SELECT EXTRACT(year FROM d.start_date)::int AS y, count(DISTINCT e.nct_id)::numeric AS trials
  FROM public.eligibility e
  JOIN public.eligibility_study_detail d ON d.nct_id = e.nct_id
  WHERE e.concept_code = @cui AND d.start_date IS NOT NULL
  GROUP BY 1)
SELECT yr.y, yr.studies::int, COALESCE(hits.trials, 0)::int,
       100.0 * COALESCE(hits.trials, 0) / yr.studies
FROM yr LEFT JOIN hits USING (y)
ORDER BY yr.y
```

`IsPartial` is set where `y = currentYear`. The year comes in as a parameter rather than being read from the clock inside the gateway, so the test can pin it.

Tests to add: a seeded concept produces one point per year with the right denominator; a year where the concept is absent still appears with 0 and its study count (a `LEFT JOIN`, not an inner one, or the line would silently skip years); the current year is flagged partial and no other year is.

The view plots up to five concepts, always as percentage of that year's studies, with the partial year labelled and each point's study count available. All years are included - the spec forbids a hard cutoff, because the corpus's current skew toward 2019+ reflects processing progress and is expected to resolve.

- [ ] **Step 1: Write the failing tests** (as described above)
- [ ] **Step 2: Run to verify they fail**
- [ ] **Step 3: Implement `GetTrendAsync` with the SQL above**
- [ ] **Step 4: Add the `Trend` action and view**
- [ ] **Step 5: Run `dotnet test contexts/eligibility/Eligibility.sln`** - expect PASS, 0 skipped
- [ ] **Step 6: Commit** - `git commit -m "Add analytics trend view"`

---

## Task 8: Concept lookup view

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/AnalyticsGateway.vb` (add `GetConceptSummaryAsync`, `SearchConceptsAsync`)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/AnalyticsController.cs` (add `Concept` action)
- Create: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Analytics/Concept.cshtml`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Results.cshtml` (link concept codes)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Analysis.cshtml` (link concept codes)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/AnalyticsGatewayTests.vb` (append)

Spec section 4.4 requires the lookup to be reachable from **both** Results and the
Analysis tab, not Results alone. Find where each renders a concept code and wrap
it in a link; if Analysis renders concept codes in more than one place, link them
all, and if it renders none, say so in your report rather than inventing a
location.

The summary SQL was validated at 2.5ms:

```sql
SELECT c.cui, c.pref_name, c.root_source,
       (SELECT string_agg(DISTINCT st.sty, '; ') FROM umls.semantic_type st WHERE st.cui = c.cui),
       (SELECT count(*) FROM umls.concept_ancestor a WHERE a.descendant_cui = c.cui),
       (SELECT count(*) FROM umls.concept_ancestor a WHERE a.ancestor_cui = c.cui),
       (SELECT count(DISTINCT e.nct_id) FROM public.eligibility e WHERE e.concept_code = c.cui)
FROM umls.concept c WHERE c.cui = @cui
```

The phase breakdown (88ms) and the five example criteria are separate statements:

```sql
SELECT COALESCE(NULLIF(d.phase, ''), '(none)'), count(DISTINCT e.nct_id)
FROM public.eligibility e JOIN public.eligibility_study_detail d ON d.nct_id = e.nct_id
WHERE e.concept_code = @cui GROUP BY 1 ORDER BY 2 DESC
```

```sql
SELECT DISTINCT e.criterion FROM public.eligibility e
WHERE e.concept_code = @cui AND e.criterion <> '' LIMIT 5
```

Name search (195ms):

```sql
SELECT c.cui, c.pref_name,
       (SELECT count(DISTINCT e.nct_id) FROM public.eligibility e WHERE e.concept_code = c.cui) AS trials
FROM umls.concept c WHERE c.pref_name ILIKE @term
ORDER BY trials DESC LIMIT @limit
```

Bind `@term` as `'%' + term + '%'` **as a parameter**, never by concatenating into the SQL.

Tests: a seeded concept returns its preferred name, semantic types and counts; an unknown CUI returns `Nothing` rather than throwing; the example criteria are capped at five; search matches on preferred name and orders by trial count.

In `Results.cshtml`, render each non-empty `concept_code` as a link to `Url.Action("Concept", "Analytics", new { code = ... })`.

- [ ] **Step 1: Write the failing tests**
- [ ] **Step 2: Run to verify they fail**
- [ ] **Step 3: Implement both gateway methods with the SQL above**
- [ ] **Step 4: Add the `Concept` action, the view, and the Results link**
- [ ] **Step 5: Run `dotnet test contexts/eligibility/Eligibility.sln`** - expect PASS, 0 skipped
- [ ] **Step 6: Commit** - `git commit -m "Add analytics concept lookup view"`

---

## Task 9: CSV export

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Web/Export/AnalyticsLiftCsv.cs`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/AnalyticsController.cs` (add `ExportLift`)

Follow the three-layer pattern already in the codebase: a per-export `*Csv` static class builds the string via `CsvWriter.Build(headers, rows)`, and the action returns `ExportResults.CsvFile(csv, name)`.

Headers: `concept_code, pref_name, cohort_trials, corpus_trials, pct_cohort, pct_corpus, excess_pp, lift, defines_cohort`. **Raw counts are included deliberately** - a shared CSV must be checkable independently rather than being a list of unverifiable ratios.

Unlike view actions, export actions return `StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message })` on failure - that is the established convention in `AuthoringController.ExportCriteria`.

- [ ] **Step 1: Write a unit test** asserting `AnalyticsLiftCsv.Build` emits the header row and one row per input, with `defines_cohort` rendered as `true`/`false`
- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement `AnalyticsLiftCsv` and the `ExportLift` action**
- [ ] **Step 4: Run `dotnet test contexts/eligibility/Eligibility.sln`** - expect PASS, 0 skipped
- [ ] **Step 5: Commit** - `git commit -m "Add analytics lift CSV export"`

---

## Task 10: Version bump

**Files:**
- Modify: `contexts/eligibility/version.json`

A migration requires at least a MINOR bump with `build` reset to 0, so 0.4.2 becomes **0.5.0**.

- [ ] **Step 1: Edit version.json**

Set `current` to `{ "major": 0, "minor": 5, "build": 0, "releaseDate": "2026-07-21" }` and prepend a matching `releases[0]` entry describing the Analytics tab in the style of the existing entries. Keep the file ASCII-only.

Several tests pin the version literal (`VersionWebTests.cs`) - update them.

- [ ] **Step 2: Full verification**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, 0 failed, **0 skipped**. A skipped Postgres suite means Docker is not running - fix that before believing the result.

- [ ] **Step 3: Commit**

```powershell
git add contexts/eligibility/version.json contexts/eligibility/tests/
git commit -m "Bump version to 0.5.0 for the Analytics tab"
```

---

## Post-implementation verification (against the real corpus)

Acceptance criteria 2 and 3 need the production corpus. After deploying, open the lift view with a Concept cohort of `C0011849` (diabetes), descendants included, minimum support 10, and confirm:

- the page renders in under 2s warm;
- **hypertension, BMI, cardiovascular disease, smoking and HbA1c rank above Adult and informed consent**;
- Adult's lift column reads approximately 1.1, and its excess is small;
- the diabetes rows carry the "defines cohort" badge.

Measured on production while designing, the expected top rows by excess are: Diabetes mellitus +55.5pp, Pregnancy +16.0, Type 2 diabetes +15.5, Hypertension +14.2, Type 1 diabetes +13.5, Adult +7.4, BMI +7.1.

If the ordering instead shows "insulin pen injector" or "recurrent severe manic episodes" near the top, the sort key has been reverted to lift - see spec section 4.2.

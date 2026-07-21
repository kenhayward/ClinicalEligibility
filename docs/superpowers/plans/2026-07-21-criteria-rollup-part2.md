# Criteria Rollup - Part 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Authoring Analysis tab group related eligibility criteria under a shared broader concept, so "Type 2 Diabetes Mellitus" and "Diabetes Mellitus" stop fragmenting into separate clusters.

**Architecture:** `ClusterCommonCriteriaAsync` gains a `rollupLevel`. At level 0 it is byte-identical to today. Above 0, a CTE ranks candidate ancestors by how many of the clustered concepts each covers, and every concept adopts the winner - one decision across the whole result set, which is what stops siblings diverging. `GetClusterRecordsAsync` takes the member CUIs so Records and Normalize keep working on a rolled-up cluster.

**Tech Stack:** .NET 8, VB.NET (Core/Data), C# (Web), Npgsql, Razor + vanilla JS, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-07-20-criteria-hierarchy-rollup-design.md` (Part 2)

**Depends on Part 1** (merged, 0.3.0): `umls.concept_ancestor` loaded at depth 2, 3,919,516 rows, orientation verified.

## The rule, and why the obvious one fails

**Read this before writing any SQL.** The spec originally specified a rule that
measurement disproved, and the failure is not obvious from reading the schema.

SNOMED is multi-parent. A concept typically has *many* ancestors at a given
distance:

| Concept | Ancestors at distance 1 | at distance 2 |
|---|---|---|
| Type 1 DM (`C0011854`) | 3 | 8 |
| Type 2 DM (`C0011860`) | 1 | 5 |

Picking "the furthest ancestor within N levels" resolves to
`ORDER BY min_distance DESC LIMIT 1`, which chooses **arbitrarily among ties**.
Measured: Type 1 selected *Digestive system disease*, Type 2 selected
*Endocrinopathy* - so the two **failed to merge at level 2 despite merging
perfectly at level 1**. Rollup got worse as the level rose.

**The rule that ships**, decided once across the clustered set rather than per
concept:

1. **Candidates** - every ancestor reachable at `min_distance <= rollupLevel`
   from any clustered concept.
2. **Coverage** - rank by the number of distinct clustered concepts covered,
   descending. An ancestor covering only one concept is discarded: it merges
   nothing, so rolling up to it only makes the label vaguer.
3. **Specificity tiebreak** - fewer global descendants wins. Validated: for the
   three glucose concepts, four ancestors tied at 3 covered, and specificity
   ranked *Hyperglycaemia* (119 descendants) first and *Endocrinopathy* (1,211)
   last, correctly rejecting the over-broad one.
4. **CUI tiebreak** - so the result is deterministic run to run.

A concept no winning ancestor covers keeps its own key and appears as its own
cluster.

## Global Constraints

- Branch `feat/criteria-rollup-part2` exists off `origin/main` with the spec correction committed. Never commit to `main`.
- **ASCII only** in every authored file. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8.
- **Never write files with PowerShell `Set-Content`/`Out-File`** (adds a BOM). Use Edit/Write.
- **Never pass a multi-line commit message with double quotes through a PowerShell here-string.** Use `git commit -F <file>`.
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`. **A pass with skips is not a pass** - `PostgresFixture` turns any startup failure into "Docker likely unavailable", so confirm `Skipped: 0`.
- **No migration.** Version bump is **build only** (0.3.0 -> 0.3.1), and no `database_schema.md` change.
- **Only levels 0, 1 and 2 exist.** Part 1 loaded depth 2 by measurement (depth 3 ran at 97% of the command timeout). The UI must not offer a level the data cannot serve.
- Level 0 must stay byte-identical to today's behaviour - it is the default and the no-regression path.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/CriterionCluster.vb` | Carry ancestor + members + level | 1 |
| `Data/PostgresGateway.vb` | Rollup CTE in `ClusterCommonCriteriaAsync` | 1 |
| `Core/IPostgresGateway.vb`, `Data/PostgresGateway.vb` | `GetClusterRecordsAsync` member CUIs | 2 |
| `Web/Controllers/AuthoringController.cs` | Bind level, project new fields, pass members | 3 |
| `Web/Views/Authoring/Edit.cshtml` | Level control, ancestor column, Add payload | 3 |
| `version.json` | Version | 4 |

---

### Task 1: Rollup in the clustering query

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/CriterionCluster.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/IPostgresGateway.vb` (`ClusterCommonCriteriaAsync` signature)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:3447` (`ClusterCommonCriteriaAsync`)
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/TestFakes.vb:536`, `PipelineOrchestratorTests.vb:1437` (constructor call sites)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Produces:
  - `CriterionCluster` gains `AncestorCode As String`, `AncestorConcept As String`, `MemberCodes As IReadOnlyList(Of String)`, `RollupLevel As Integer` (all after the existing positional args).
  - `IPostgresGateway.ClusterCommonCriteriaAsync(nctIds, rollupLevel As Integer, cancellationToken)`.

`CriterionCluster`'s constructor is positional with no optional parameters, so
every construction site breaks. That is intentional - it forces each to be
considered rather than silently defaulting.

- [ ] **Step 1: Write the failing tests**

Append to `PostgresGatewayIntegrationTests.vb`, before the `MakeResolved` helper.
`InsertEligibilityRowAsync(nctId, criterion, concept, domain, conceptCode, ...)`
already exists in `PostgresFixture`; check its parameter order before using it.

```vb
    ' ============ ClusterCommonCriteria rollup ============

    Private Async Function SeedHierarchyAsync(edges As (Child As String, Parent As String)()) As Task
        Dim store As New UmlsMetathesaurusStore(_fixture.DataSource)
        Await store.LoadConceptHierarchyAsync(
                edges.Select(Function(e) New ConceptEdgeRow With {.ChildCui = e.Child, .ParentCui = e.Parent}),
                2, CancellationToken.None)
    End Function

    ' Level 0 is the default and the no-regression path: it must behave exactly
    ' as it did before rollup existed.
    <SkippableFact>
    Public Async Function Cluster_level_zero_groups_by_exact_concept_identity() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "type 2 diabetes", conceptCode:="C0011860")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "type 1 diabetes", conceptCode:="C0011854")
        Await SeedHierarchyAsync({("C0011860", "C0011849"), ("C0011854", "C0011849")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, 0, CancellationToken.None)

        Assert.Equal(2, clusters.Count)
        Assert.All(clusters, Sub(c) Assert.Equal(0, c.RollupLevel))
        Assert.All(clusters, Sub(c) Assert.Equal("", c.AncestorCode))
    End Function

    <SkippableFact>
    Public Async Function Cluster_level_one_merges_siblings_under_their_parent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "type 2 diabetes", conceptCode:="C0011860")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "type 1 diabetes", conceptCode:="C0011854")
        Await SeedHierarchyAsync({("C0011860", "C0011849"), ("C0011854", "C0011849")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, 1, CancellationToken.None)

        Dim merged = Assert.Single(clusters)
        Assert.Equal("C0011849", merged.AncestorCode)
        Assert.Equal(2, merged.StudyCount)
        Assert.Equal({"C0011854", "C0011860"}, merged.MemberCodes.OrderBy(Function(m) m).ToArray())
    End Function

    ' THE REGRESSION TEST for the rule that was wrong. Both concepts share
    ' C0011849 at distance 1, but have DIFFERENT extra ancestors at distance 2.
    ' The superseded "furthest ancestor within N" rule picked a different distant
    ' ancestor for each and split them. Coverage ranking must keep them together.
    <SkippableFact>
    Public Async Function Cluster_level_two_still_merges_when_distant_ancestors_differ() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "type 2 diabetes", conceptCode:="C0011860")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "type 1 diabetes", conceptCode:="C0011854")
        ' Shared parent, then divergent grandparents - the real SNOMED shape.
        Await SeedHierarchyAsync({
            ("C0011860", "C0011849"), ("C0011854", "C0011849"),
            ("C0011849", "C0014130"),
            ("C0011854", "C0012242")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, 2, CancellationToken.None)

        Dim merged = Assert.Single(clusters)
        Assert.Equal(2, merged.MemberCodes.Count)
    End Function

    ' Specificity tiebreak: when two ancestors cover the same concepts, the one
    ' with fewer global descendants wins, so a cluster is not labelled with an
    ' over-broad concept.
    <SkippableFact>
    Public Async Function Cluster_prefers_the_more_specific_of_two_equal_ancestors() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "concept a", conceptCode:="C0000001")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "concept b", conceptCode:="C0000002")
        ' Both roll to NARROW and BROAD. BROAD also has many other descendants,
        ' so it is less specific and must lose.
        Await SeedHierarchyAsync({
            ("C0000001", "C0000900"), ("C0000002", "C0000900"),
            ("C0000900", "C0000999"),
            ("C0000011", "C0000999"), ("C0000012", "C0000999"),
            ("C0000013", "C0000999"), ("C0000014", "C0000999")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, 2, CancellationToken.None)

        Dim merged = Assert.Single(clusters)
        Assert.Equal("C0000900", merged.AncestorCode)
    End Function

    ' An ancestor covering only one clustered concept merges nothing, so rolling
    ' up to it would only make the label vaguer. The concept keeps its own key.
    <SkippableFact>
    Public Async Function Cluster_does_not_roll_up_a_concept_with_no_shared_ancestor() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "lonely", conceptCode:="C0000001")
        Await SeedHierarchyAsync({("C0000001", "C0000900")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001"}, 1, CancellationToken.None)

        Dim only = Assert.Single(clusters)
        Assert.Equal("", only.AncestorCode)
        Assert.Equal("C0000001", only.ConceptCode)
    End Function

    ' Concepts with no SNOMED edges are ~47% of the corpus. They must still be
    ' returned, unrolled, not dropped.
    <SkippableFact>
    Public Async Function Cluster_returns_concepts_absent_from_the_hierarchy() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "no edges", conceptCode:="C9999999")
        Await SeedHierarchyAsync({("C0011860", "C0011849")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001"}, 2, CancellationToken.None)

        Dim only = Assert.Single(clusters)
        Assert.Equal("C9999999", only.ConceptCode)
        Assert.Equal("", only.AncestorCode)
    End Function

    ' Unresolved criteria have no CUI, so the hierarchy cannot apply. They keep
    ' the lowercased-text fallback at every level.
    <SkippableFact>
    Public Async Function Cluster_unresolved_criteria_group_by_text_at_every_level() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "Anaemia", conceptCode:="")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "anaemia", conceptCode:="")

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, 2, CancellationToken.None)

        Dim only = Assert.Single(clusters)
        Assert.False(only.Resolved)
        Assert.Equal(2, only.StudyCount)
    End Function

    ' Inclusion and Exclusion are different criteria even for the same concept -
    ' rollup must not merge across them.
    <SkippableFact>
    Public Async Function Cluster_never_merges_inclusion_with_exclusion() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "type 2 diabetes", conceptCode:="C0011860")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Exclusion", "type 1 diabetes", conceptCode:="C0011854")
        Await SeedHierarchyAsync({("C0011860", "C0011849"), ("C0011854", "C0011849")})

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, 1, CancellationToken.None)

        Assert.Equal(2, clusters.Count)
    End Function
```

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~Cluster_level|FullyQualifiedName~Cluster_prefers|FullyQualifiedName~Cluster_does_not_roll|FullyQualifiedName~Cluster_returns_concepts|FullyQualifiedName~Cluster_unresolved|FullyQualifiedName~Cluster_never_merges"
```

Expected: BUILD FAILURE - `ClusterCommonCriteriaAsync` has no `rollupLevel`, and `CriterionCluster` has no `AncestorCode`.

- [ ] **Step 3: Extend `CriterionCluster`**

Append four parameters to the constructor and four properties:

```vb
            studyCount As Integer,
            recordCount As Integer,
            ancestorCode As String,
            ancestorConcept As String,
            memberCodes As IReadOnlyList(Of String),
            rollupLevel As Integer)
        ...
        Me.AncestorCode = If(ancestorCode, "")
        Me.AncestorConcept = If(ancestorConcept, "")
        Me.MemberCodes = If(memberCodes, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.RollupLevel = rollupLevel
```

```vb
    ''' <summary>
    ''' The broader concept this cluster was rolled up to, or empty when it was
    ''' not rolled up - either because rollupLevel was 0, or because no ancestor
    ''' covered more than this one concept.
    ''' </summary>
    Public ReadOnly Property AncestorCode As String

    ''' <summary>Display name for <see cref="AncestorCode"/>. Empty when not rolled up.</summary>
    Public ReadOnly Property AncestorConcept As String

    ''' <summary>
    ''' The concept codes merged into this cluster. Needed to fetch the cluster's
    ''' records: the ancestor CUI is not any row's concept_code, so a lookup by
    ''' group key alone would match nothing.
    ''' </summary>
    Public ReadOnly Property MemberCodes As IReadOnlyList(Of String)

    ''' <summary>The level this cluster was produced at. 0 = exact concept identity.</summary>
    Public ReadOnly Property RollupLevel As Integer
```

Update the class header comment, which currently says grouping is by "concept
identity".

- [ ] **Step 4: Add `rollupLevel` to the interface and implement the query**

In `IPostgresGateway.vb`, add `rollupLevel As Integer` to
`ClusterCommonCriteriaAsync` between `nctIds` and `cancellationToken`, and
document that 0 means exact concept identity.

In `PostgresGateway.vb`, replace the body of `ClusterCommonCriteriaAsync`.
**Keep the existing level-0 SQL exactly as it is** and branch, rather than
writing one query that tries to serve both - the no-regression guarantee is
easier to see when the old path is untouched:

```vb
        If rollupLevel <= 0 Then
            Return Await ClusterExactAsync(nctIds, cancellationToken).ConfigureAwait(False)
        End If
```

Move today's SQL verbatim into `ClusterExactAsync`, constructing
`CriterionCluster` with `ancestorCode:=""`, `ancestorConcept:=""`,
`memberCodes:=` the single concept code (or empty when unresolved), and
`rollupLevel:=0`.

The rollup query:

```vb
        ' Rollup grouping. The winning ancestor is chosen ONCE across the whole
        ' clustered set, not per concept - that is what stops two siblings
        ' picking different distant ancestors and failing to merge. See the
        ' spec's "rollup rule" section for the measurement that ruled out the
        ' per-concept alternative.
        Const RollupSql As String = "
WITH base AS (
    SELECT criterion,
           COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)) AS leaf_key,
           NULLIF(concept_code, '') AS cui,
           concept, concept_code, semantic_type, nct_id
      FROM public.eligibility
     WHERE nct_id = ANY(@ids)
),
concepts AS (
    SELECT DISTINCT cui FROM base WHERE cui IS NOT NULL
),
-- Candidate ancestors, ranked by how many of the clustered concepts each one
-- covers. HAVING > 1 discards ancestors that merge nothing: rolling a lone
-- concept up to a parent only makes its label vaguer.
cand AS (
    SELECT a.ancestor_cui,
           count(DISTINCT a.descendant_cui) AS covered
      FROM umls.concept_ancestor a
      JOIN concepts c ON c.cui = a.descendant_cui
     WHERE a.min_distance <= @rollup_level
     GROUP BY a.ancestor_cui
    HAVING count(DISTINCT a.descendant_cui) > 1
),
-- Specificity: total descendants across the whole hierarchy. Fewer = tighter.
spec AS (
    SELECT cd.ancestor_cui, cd.covered,
           (SELECT count(*) FROM umls.concept_ancestor g
             WHERE g.ancestor_cui = cd.ancestor_cui) AS breadth
      FROM cand cd
),
-- Each concept adopts the best-ranked ancestor that covers it. DISTINCT ON with
-- this ORDER BY is the whole rule: coverage, then specificity, then CUI for
-- determinism.
pick AS (
    SELECT DISTINCT ON (a.descendant_cui)
           a.descendant_cui AS cui,
           s.ancestor_cui
      FROM umls.concept_ancestor a
      -- REQUIRED. Without this join the CTE picks an ancestor for every
      -- descendant in the whole 3.9M-row hierarchy that happens to sit under a
      -- candidate - verified during review, it returned concepts nowhere near
      -- the clustered set. Restricting to the clustered concepts is both the
      -- correctness fix and what keeps the query small.
      JOIN concepts c2 ON c2.cui = a.descendant_cui
      JOIN spec s ON s.ancestor_cui = a.ancestor_cui
     WHERE a.min_distance <= @rollup_level
     ORDER BY a.descendant_cui, s.covered DESC, s.breadth ASC, s.ancestor_cui
)
SELECT b.criterion,
       COALESCE(p.ancestor_cui, b.leaf_key)                       AS group_key,
       bool_or(b.cui IS NOT NULL)                                 AS resolved,
       COALESCE(max(anc.pref_name), max(b.concept))               AS concept,
       COALESCE(max(p.ancestor_cui), max(COALESCE(b.concept_code, ''))) AS concept_code,
       COALESCE((SELECT string_agg(DISTINCT s2, ', ' ORDER BY s2)
                   FROM unnest(array_agg(b.semantic_type)) AS s2
                  WHERE s2 IS NOT NULL AND s2 <> ''), '')         AS semantic_type,
       count(DISTINCT b.nct_id)                                   AS study_count,
       count(*)                                                   AS record_count,
       COALESCE(max(p.ancestor_cui), '')                          AS ancestor_code,
       COALESCE(max(anc.pref_name), '')                           AS ancestor_concept,
       COALESCE(array_agg(DISTINCT b.cui) FILTER (WHERE b.cui IS NOT NULL),
                ARRAY[]::text[])                                  AS member_codes
  FROM base b
  LEFT JOIN pick p ON p.cui = b.cui
  LEFT JOIN umls.concept anc ON anc.cui = p.ancestor_cui
 GROUP BY b.criterion, COALESCE(p.ancestor_cui, b.leaf_key)
 ORDER BY study_count DESC, record_count DESC"
```

Bind `@ids` as a text array and `@rollup_level` as an integer. Read
`member_codes` with `reader.GetFieldValue(Of String())(index)`.

**This query was executed against the real hierarchy during review.** For the
three glucose concepts (`C0011860`, `C0011854`, `C0271650`) at level 2 it
returns exactly three `pick` rows, all adopting `C0020456` (Hyperglycaemia) - so
they merge into one cluster. If an implementation returns picks for concepts
outside the clustered set, the `concepts c2` join is missing.

Note the `LEFT JOIN umls.concept` for the ancestor's display name: `pref_name`
can be absent if the ancestor is outside the curated atom subset, hence the
`COALESCE` back to the leaf concept text.

- [ ] **Step 5: Fix the two fake construction sites**

`TestFakes.vb:536` and `PipelineOrchestratorTests.vb:1437` construct
`CriterionCluster`. Append `ancestorCode:="", ancestorConcept:="",
memberCodes:=Array.Empty(Of String)(), rollupLevel:=0` to each.

- [ ] **Step 6: Run to verify pass, then the full suite**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~Cluster_"
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: 8 new tests PASS, `Skipped: 0`. The full suite surfaces the
`ClusterCommonCriteriaAsync` caller in `AuthoringController.cs:373` - Task 3
fixes it; for now pass `0` to keep the tree building.

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src contexts/eligibility/tests
git commit -F <message-file>
```

---

### Task 2: Cluster records for a rolled-up cluster

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/IPostgresGateway.vb:633`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:3506`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Produces: `GetClusterRecordsAsync(nctIds, criterion, groupKey, memberCodes As IReadOnlyList(Of String), cancellationToken)`.

**Why this is not optional polish.** The current SQL matches:

```sql
COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)) = @group_key
```

For a rolled-up cluster the group key is an **ancestor CUI**, which is not any
row's `concept_code`. The predicate matches **nothing**. Records would show an
empty expander and Normalize would send zero texts to the LLM - and a Normalize
button that silently produces nothing reads as "no common phrasing found"
rather than as broken.

- [ ] **Step 1: Write the failing tests**

```vb
    <SkippableFact>
    Public Async Function ClusterRecords_returns_all_members_of_a_rolled_up_cluster() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "type 2 diabetes", conceptCode:="C0011860")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "type 1 diabetes", conceptCode:="C0011854")

        Dim rows = Await _fixture.Gateway.GetClusterRecordsAsync(
                {"NCT00000001", "NCT00000002"}, "Inclusion", "C0011849",
                {"C0011860", "C0011854"}, CancellationToken.None)

        Assert.Equal(2, rows.Count)
    End Function

    ' Level 0 passes no members and must still match on the group key alone.
    <SkippableFact>
    Public Async Function ClusterRecords_falls_back_to_group_key_when_no_members_given() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "type 2 diabetes", conceptCode:="C0011860")

        Dim rows = Await _fixture.Gateway.GetClusterRecordsAsync(
                {"NCT00000001"}, "Inclusion", "C0011860",
                Array.Empty(Of String)(), CancellationToken.None)

        Assert.Single(rows)
    End Function

    ' Unresolved clusters roll up to nothing, so their group key stays the
    ' 'concept:<text>' form and members are empty.
    <SkippableFact>
    Public Async Function ClusterRecords_still_matches_unresolved_text_group_keys() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "Anaemia", conceptCode:="")

        Dim rows = Await _fixture.Gateway.GetClusterRecordsAsync(
                {"NCT00000001"}, "Inclusion", "concept:anaemia",
                Array.Empty(Of String)(), CancellationToken.None)

        Assert.Single(rows)
    End Function
```

- [ ] **Step 2: Run to verify failure, then implement**

Add the parameter to the interface and the implementation, and change the
predicate so members win when present:

```sql
WHERE nct_id = ANY(@ids) AND criterion = @criterion
  AND (
        -- Rolled-up cluster: match the concepts that were merged into it. The
        -- group key is an ancestor CUI and matches no row directly.
        (@members IS NOT NULL AND array_length(@members, 1) > 0
         AND concept_code = ANY(@members))
     OR -- Level 0, or an unrolled cluster: today's behaviour, unchanged.
        ((@members IS NULL OR array_length(@members, 1) IS NULL)
         AND COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)) = @group_key)
      )
ORDER BY nct_id, id
```

Bind `@members` as a text array, DBNull when empty.

- [ ] **Step 3: Run to verify pass, full suite, commit**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~ClusterRecords_"
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src contexts/eligibility/tests
git commit -m "Fetch cluster records by member concepts when rolled up"
```

---

### Task 3: Controller and UI

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/AuthoringController.cs:359` (`Cluster`), `:408` (`ClusterRecords`), `:454` (`Normalize`)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Authoring/Edit.cshtml`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/AuthoringControllerTests.cs`

**Interfaces:**
- Consumes: `CriterionCluster.AncestorCode/AncestorConcept/MemberCodes/RollupLevel` (Task 1), `GetClusterRecordsAsync(..., memberCodes, ...)` (Task 2).

- [ ] **Step 1: Write the failing test**

`AuthoringControllerTests.cs` posts to these actions already - follow its
existing shape (it uses `HttpMethod.Post` with form content at `:133`).

```csharp
    // Rollup level is a bound query/form value. Levels above 2 do not exist -
    // Part 1 loaded the hierarchy at depth 2 by measurement.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cluster_accepts_valid_rollup_levels(int level)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/Authoring/Cluster?rollupLevel={level}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("nctIds", "NCT00000001")
        });

        var response = await client.SendAsync(request);

        // Backend is unreachable in this factory, so a 500 with the gateway's
        // message is the expected outcome - what matters is that binding and
        // validation let the request through rather than rejecting the level.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
```

Check `AuthedFactory`'s name and antiforgery handling in that file before
writing - the Authoring actions use `[ValidateAntiForgeryToken]`, so the test
may need the XHR header pattern used by the existing tests.

- [ ] **Step 2: Change the controller**

`Cluster` gains `int rollupLevel = 0`, **clamped to 0..2**:

```csharp
        // Clamped, not validated-and-rejected: a stale bookmark asking for
        // level 5 should give the closest sensible answer, not an error. Only
        // 0-2 exist - Part 1 loaded the hierarchy at depth 2.
        var level = Math.Clamp(rollupLevel, 0, 2);
```

Pass it to `ClusterCommonCriteriaAsync`, and extend the `Project` lambda with
`ancestorCode`, `ancestorConcept`, `memberCodes` and `rollupLevel`.

`ClusterRecords` and `Normalize` each gain `string[]? memberCodes = null` and
pass it through to `GetClusterRecordsAsync`, normalised with the same
trim/non-empty/distinct idiom used for `nctIds` at `:361-365`.

- [ ] **Step 3: Change the view**

In `Edit.cshtml`:

- Add a rollup control beside `#cluster-topn`:

```html
<label for="cluster-rollup" class="form-label small text-muted mb-1">Group by</label>
<select id="cluster-rollup" class="form-select form-select-sm">
    <option value="0" selected>Exact concept</option>
    <option value="1">Broader (1 level)</option>
    <option value="2">Broader (2 levels)</option>
</select>
```

- Send it with the cluster request, alongside the existing `nctIds`.
- In `renderClusterTable` (`:663`), add a **Rolled up to** column showing
  `c.ancestorConcept` plus the member count when `c.ancestorCode` is non-empty,
  and an em-dash-free blank otherwise. Roughly half of concepts have no SNOMED
  edges, so blanks are expected and normal - the column is what makes that read
  as *incomplete* rather than *inconsistent*.
- Bump the detail row's `colspan="7"` (`:698`) to 8.
- Pass `c.memberCodes` through the Records and Normalize fetches.
- In `syncAddButtons` (`:881`), change the dedup key from
  `type + "|" + conceptCode` to `type + "|" + groupKey`. That is already correct
  at level 0 and stays correct above it. **Note the existing early return when
  the code is empty** - unresolved clusters currently never dedupe; keep that
  behaviour keyed on `groupKey` instead.
- In the Add handler (`:855`), when `ancestorCode` is non-empty send it as
  `conceptCode` and `ancestorConcept` as `concept`, and make the source note
  say so:

```js
sourceNote: "From cluster: " + label + (c.ancestorCode
    ? " (rolled up from " + c.memberCodes.length + " concepts, " + c.studyCount + " studies)"
    : " (" + c.studyCount + " studies)")
```

**Why the ancestor's CUI and not a member's** (spec decision): the criterion
genuinely is about the broader concept, and `authoring_criterion_source` already
snapshots every underlying leaf row with its own code, so lineage survives.

- [ ] **Step 4: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Web contexts/eligibility/tests
git commit -m "Add the rollup level control to the Analysis tab"
```

---

### Task 4: Version, manual verification, PR

- [ ] **Step 1: Bump the version**

Build only - no migration. `0.3.0` -> **`0.3.1`**, `releaseDate` today. Prepend a
`releases` entry in user-facing terms: criteria clustering can now group related
criteria under a broader shared concept. ASCII only; `releases[0]` must match
`current`.

Update the four version literals and the one `ReleaseDate` assertion in
`VersionWebTests.cs` with the Edit tool (not PowerShell - BOM).

- [ ] **Step 2: Manual verification**

The automated tests cover the rule against seeded hierarchies. This is the first
time it runs against the real 3.9M-row hierarchy and a real study set.

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web
```

On an authoring study's Analysis tab:

1. Find similar studies, cluster at **Exact concept**. Note the cluster count -
   this is the level-0 baseline and must look exactly as it did before.
2. Switch to **Broader (1 level)** and re-cluster. Expect **fewer, larger**
   clusters, and a populated "Rolled up to" column on some rows.
3. **Check a rolled-up row's Records expander** - it must list rows from *all*
   merged concepts, not be empty. This is the Task 2 fix and the most likely
   thing to be silently broken.
4. **Normalize a rolled-up cluster** - it must return a sentence, not blank.
5. Add a rolled-up cluster to the study, save, and confirm the criterion carries
   the ancestor's concept code and the source note names the rollup.
6. Switch to **Broader (2 levels)** - clusters should merge further, not
   fragment. Fragmentation at level 2 is the exact failure the corrected rule
   fixes; if it reappears, stop and re-read the rule section.

- [ ] **Step 3: Push and open the PR**

Body must state: the rule that shipped and why the original was wrong (with the
diabetes measurement), that level 0 is unchanged, that ~47% of concepts cannot
roll up and blanks are expected, and which manual checks were run.

---

## Deliberately not in Part 2

- **Persisting the rollup level** on `authoring_criterion` - the ancestor code is
  stored, which is what matters; the level is a UI choice, not data.
- **OMOP adoption** for the ~47% of concepts without SNOMED edges. The table
  shape makes that a load change.
- **The corpus-wide prevalence browser** (goal B), still deferred.

## Known risks

**The rule is subtle and the wrong version looks plausible.** The regression test
`Cluster_level_two_still_merges_when_distant_ancestors_differ` is the guard. If
it is ever weakened, the feature silently degrades to worse-than-level-0.

**Records and Normalize fail silently if Task 2 is skipped or wrong** - an empty
expander and a blank Normalize read as "nothing found", not as a bug. Manual
check 3 exists for this.

**Partial coverage is visible.** Roughly half of distinct concepts have no SNOMED
edges. The "Rolled up to" column showing blanks is the honest presentation.

# Semantic Type UX - Phase 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Results semantic-type filter return every row that carries the selected type, from a dropdown of real semantic types rather than string combinations.

**Architecture:** The filter moves from exact match on the joined display string to array containment on `semantic_type_tuis` (phase 2). The dropdown sources from `umls.semantic_type_dim` (132 rows) instead of a DISTINCT scan over 3.9M rows, and becomes multi-select. The Results table keeps rendering the display string - TUIs stay a filter-side concern, so `EligibilityRow` is untouched.

**Tech Stack:** .NET 8, VB.NET (Core/Data), C# (Web), Npgsql, Razor + Bootstrap 5, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-07-20-semantic-type-repair-design.md` (phase 3 section)

**Depends on phase 2** (`feat/semantic-type-restructure`, PR #35) for `semantic_type_tuis` and `umls.semantic_type_dim`. This branch is stacked on it; rebase onto `main` once #35 merges.

## The problem, measured

`EligibilityFilter.SemanticType` is exact match on the whole joined string
(`PostgresGateway.vb:1452`). A row carrying several semantic types is invisible
unless the user picks that exact combination.

Measured on the production corpus after phase 2's backfill:

| | Rows |
|---|---|
| Filtering `Pharmacologic Substance` today (exact string) | 44,800 |
| Rows that actually carry it (`semantic_type_tuis @> ARRAY['T121']`) | **118,867** |
| **Under-reported** | **62%** |

The dropdown offers **215 distinct strings**; there are **132 real semantic
types**. Users pick combinations, not types.

## Decisions taken before planning

1. **No Results CSV export.** The spec claimed one existed and omitted
   `semantic_type`. It does not exist - `Export/ExportResults.cs` is a 29-line
   generic CSV helper with no column list. Building one is greenfield and out of
   scope; Task 5 removes the claim from the spec.
2. **The table renders names only.** TUIs are internal identifiers that mean
   nothing to a reader. Keeping them filter-side leaves `EligibilityRow`
   untouched and avoids shifting reader ordinals 7-13 in the page query.
3. **Multi-select**, per the spec. No multi-value filter UI exists anywhere in
   this app, so the GET round-trip - form submission, pager link preservation,
   selected-state rendering - is invented here. That is the hard part of this
   phase, not the SQL.

## Global Constraints

- Branch `feat/semantic-type-ux`, stacked on `feat/semantic-type-restructure`. Never commit to `main`.
- **ASCII only** in every authored file. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8.
- **Never write files with PowerShell `Set-Content`/`Out-File`** (adds a BOM). Use Edit/Write.
- **Never pass a multi-line commit message with double quotes through a PowerShell here-string.** Use `git commit -F <file>`.
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`.
- **No migration in this phase.** Version bump is **build only** (0.2.0 -> 0.2.1), and **no** `database_schema.md` change.
- `PostgresGateway.vb` is in `EligibilityProcessing.Data`.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/EligibilityFilter.vb` | Scalar `SemanticType` -> `SemanticTypeTuis` collection | 1 |
| `Core/EligibilityFilterOptions.vb` | Options carry `(tui, name)` pairs | 2 |
| `Data/PostgresGateway.vb` | Containment predicate; dim-sourced options; sort | 1, 2 |
| `Web/Controllers/HomeController.cs` | Bind repeated query params; legacy redirect | 3 |
| `Web/Views/Home/Results.cshtml` | Multi-select control; pager preservation | 3 |
| `Data/PostgresGateway.vb` (cluster) | Analysis-tab aggregate | 4 |
| `version.json`, spec | Version, spec correction | 5 |

---

### Task 1: Filter on the TUI array

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/EligibilityFilter.vb` (whole class, 48 lines)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:1452` (predicate), `:1473` and `:1505` (bindings)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Produces: `EligibilityFilter.SemanticTypeTuis As IReadOnlyList(Of String)` replacing `SemanticType As String`; constructor param `semanticTypeTuis As IReadOnlyList(Of String) = Nothing`.

**Two constraints that will bite if ignored:**

- **Every constructor parameter must stay `Optional`.** `Models/ResultsViewModel.cs:17` is `public EligibilityFilter Filter { get; init; } = new();` - a required parameter breaks it.
- **`IsEmpty` (`EligibilityFilter.vb:36-41`) uses `Is Nothing` checks.** A collection needs a count check, or an empty-but-non-null list makes the filter look non-empty and suppresses the "no filter" path.

- [ ] **Step 1: Write the failing tests**

Append to `PostgresGatewayIntegrationTests.vb` before the `MakeResolved` helper:

```vb
    ' ============ semantic-type containment filter ============
    '
    ' The old filter matched the whole joined string, so a row carrying several
    ' semantic types was invisible unless the user picked that exact combination.
    ' Measured on production: 44,800 rows matched "Pharmacologic Substance"
    ' exactly, against 118,867 that carry it - a 62% under-report.

    <SkippableFact>
    Public Async Function SearchEligibility_matches_a_row_carrying_the_type_among_others() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        ' One row whose ONLY type is T121, one where T121 is one of three.
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000001", "C1", {"T121"})
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000002", "C2", {"T116", "T121", "T126"})
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000003", "C3", {"T047"})

        Dim page = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(semanticTypeTuis:={"T121"}), 50, 0, "", CancellationToken.None)

        Assert.Equal(2L, page.TotalRows)
    End Function

    ' Multi-select is OR, not AND: "show me anything that is either of these".
    <SkippableFact>
    Public Async Function SearchEligibility_treats_multiple_tuis_as_any_of() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000001", "C1", {"T121"})
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000002", "C2", {"T047"})
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000003", "C3", {"T999"})

        Dim page = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(semanticTypeTuis:={"T121", "T047"}), 50, 0, "", CancellationToken.None)

        Assert.Equal(2L, page.TotalRows)
    End Function

    <SkippableFact>
    Public Async Function SearchEligibility_ignores_an_empty_tui_filter() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowWithTuisAsync("NCT00000001", "C1", {"T121"})

        Dim page = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(semanticTypeTuis:=Array.Empty(Of String)()), 50, 0, "", CancellationToken.None)

        Assert.Equal(1L, page.TotalRows)   ' empty list = no filter, not "match nothing"
    End Function

    ' Unresolved rows have a NULL array. Containment must not match them, and
    ' must not error on the NULL.
    <SkippableFact>
    Public Async Function SearchEligibility_excludes_rows_with_no_semantic_types() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000009", "crit", "concept", conceptCode:="")

        Dim page = Await _fixture.Gateway.SearchEligibilityAsync(
                New EligibilityFilter(semanticTypeTuis:={"T121"}), 50, 0, "", CancellationToken.None)

        Assert.Equal(0L, page.TotalRows)
    End Function
```

Add the fixture helper to `PostgresFixture.vb`, next to `InsertEligibilityRowAsync`:

```vb
    ''' <summary>Inserts one public.eligibility row with an explicit TUI array.</summary>
    Public Async Function InsertEligibilityRowWithTuisAsync(
            nctId As String, conceptCode As String, tuis As String()) As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO public.eligibility
                        (nct_id, criterion, domain, concept, concept_code, semantic_type_tuis, match_score)
                    VALUES (@n, 'Inclusion', 'Disease', 'concept', @cc, @tuis, 0)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("cc", conceptCode)
                cmd.Parameters.AddWithValue("tuis", tuis)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function
```

Check `SearchEligibilityAsync`'s real signature in `IPostgresGateway.vb:327-344` before writing these - the `(filter, limit, offset, sort, ct)` shape above is from the call site and may differ in parameter order or name.

- [ ] **Step 2: Run to verify failure**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~SearchEligibility_matches_a_row_carrying|FullyQualifiedName~SearchEligibility_treats_multiple|FullyQualifiedName~SearchEligibility_ignores_an_empty|FullyQualifiedName~SearchEligibility_excludes_rows_with_no"
```

Expected: BUILD FAILURE - no `semanticTypeTuis` parameter.

- [ ] **Step 3: Rework the filter**

Replace the `semanticType` parameter, property and `IsEmpty` clause in
`EligibilityFilter.vb`:

```vb
            Optional semanticTypeTuis As IReadOnlyList(Of String) = Nothing)
        ...
        ' Blank entries dropped and the list deduped, so a stray "" from a form
        ' post cannot turn "no filter" into "match nothing".
        Me.SemanticTypeTuis = If(semanticTypeTuis, CType(Array.Empty(Of String)(), IReadOnlyList(Of String))) _
                .Select(Function(t) If(t, "").Trim()) _
                .Where(Function(t) t.Length > 0) _
                .Distinct(StringComparer.OrdinalIgnoreCase) _
                .ToArray()
```

```vb
    ''' <summary>
    ''' Semantic type ids to match. Multi-value and OR-combined: a row matches if
    ''' it carries ANY of them. Empty means "no filter", NOT "match nothing".
    ''' </summary>
    Public ReadOnly Property SemanticTypeTuis As IReadOnlyList(Of String)
```

and in `IsEmpty`, replace `AndAlso SemanticType Is Nothing` with
`AndAlso SemanticTypeTuis.Count = 0`.

Update the class header comment (`EligibilityFilter.vb:1-3`), which currently
lists `SemanticType` under "exact match (column = @value)".

- [ ] **Step 4: Rework the predicate and bindings**

`PostgresGateway.vb:1452`, inside the shared `whereClause` Const:

```sql
  AND (@semantic_type_tuis IS NULL OR semantic_type_tuis && @semantic_type_tuis)
```

`&&` is array overlap - true when the two arrays share at least one element,
which is the OR semantics above. It uses the GIN index added in V22. A NULL
`semantic_type_tuis` on the row side yields NULL (not true), so unresolved rows
are excluded without a special case.

Both binding sites - `:1473` (page) and `:1505` (count) - change from
`AddTextParam` to an array parameter. Bind **DBNull when the list is empty**, so
the `@semantic_type_tuis IS NULL` arm short-circuits the predicate:

```vb
        cmd.Parameters.Add(New NpgsqlParameter("semantic_type_tuis", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                .Value = If(filter.SemanticTypeTuis.Count = 0,
                            CObj(DBNull.Value), CObj(filter.SemanticTypeTuis.ToArray()))})
```

The same array-param idiom already appears in this file at `:1616` and `:3432`.

- [ ] **Step 5: Run to verify pass, then full suite**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~SearchEligibility_"
dotnet test contexts/eligibility/Eligibility.sln
```

The full suite surfaces every remaining `SemanticType` caller. Expect breaks in
`HomeController.cs:128-130`, `Results.cshtml`, and the test files listed in the
plan header - Task 3 fixes the web ones.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/EligibilityFilter.vb contexts/eligibility/src/EligibilityProcessing.Data contexts/eligibility/tests
git commit -m "Filter Results on the semantic-type TUI array instead of the joined string"
```

---

### Task 2: Dropdown from the dimension table

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/EligibilityFilterOptions.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:1515-1562` (`GetEligibilityFilterOptionsAsync`)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

**Interfaces:**
- Produces: `EligibilityFilterOptions.SemanticTypes As IReadOnlyList(Of SemanticTypeOption)` where `SemanticTypeOption` has `Tui As String` and `Name As String`.

**A performance win that comes free.** The comment at `PostgresGateway.vb:1530-1540`
records that `semantic_type` is one of only **two** columns still costing a real
`SELECT DISTINCT` over ~3.9M rows. Sourcing from `umls.semantic_type_dim` (132
rows, PK-ordered) removes that scan entirely. Say so in the comment rather than
leaving a stale note about a scan that no longer happens.

Also: the options are cached by `maxDropdownSize` alone
(`CorpusReadCache.vb:84-105`). The dim-sourced list is not affected by that cap,
which is worth a comment so nobody later assumes the cap still applies to it.

- [ ] **Step 1: Write the failing test**

```vb
    <SkippableFact>
    Public Async Function FilterOptions_semantic_types_come_from_the_dimension_not_the_corpus() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.semantic_type_dim (tui, sty) VALUES
  ('T121','Pharmacologic Substance'), ('T047','Disease or Syndrome')
ON CONFLICT (tui) DO NOTHING"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
        ' No eligibility rows at all: the list must still populate, proving it
        ' does not come from a DISTINCT over the corpus.
        Dim options = Await _fixture.Gateway.GetEligibilityFilterOptionsAsync(100, CancellationToken.None)

        Assert.Equal(2, options.SemanticTypes.Count)
        ' Ordered by display name so the dropdown reads alphabetically.
        Assert.Equal("Disease or Syndrome", options.SemanticTypes(0).Name)
        Assert.Equal("T047", options.SemanticTypes(0).Tui)
    End Function
```

Confirm `GetEligibilityFilterOptionsAsync`'s real signature at
`IPostgresGateway.vb` before writing this.

- [ ] **Step 2: Run to verify failure, then implement**

Add `SemanticTypeOption` to `EligibilityFilterOptions.vb`:

```vb
''' <summary>One selectable semantic type: the stable id and its display name.</summary>
Public NotInheritable Class SemanticTypeOption
    Public Sub New(tui As String, name As String)
        Me.Tui = If(tui, "")
        Me.Name = If(name, "")
    End Sub
    Public ReadOnly Property Tui As String
    Public ReadOnly Property Name As String
End Class
```

Change `SemanticTypes` to `IReadOnlyList(Of SemanticTypeOption)`, and update
`Empty` (`:39-40`) to pass an empty list rather than `Nothing`.

In `GetEligibilityFilterOptionsAsync`, drop `"semantic_type"` from the `columns`
array at `:1527-1528` and load it separately:

```vb
        ' From the 132-row dimension, not a DISTINCT over ~3.9M rows. That scan
        ' was one of only two left after the estimate-based pre-filter; it is
        ' gone. The maxDropdownSize cap does not apply here - the dimension is
        ' bounded by UMLS at ~132 entries.
        Const SemanticTypeSql As String = "SELECT tui, sty FROM umls.semantic_type_dim ORDER BY sty"
```

Update the comment block at `:1530-1540` so it no longer claims
`semantic_type` is scanned.

- [ ] **Step 3: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src contexts/eligibility/tests
git commit -m "Source the semantic-type dropdown from the dimension table"
```

---

### Task 3: Multi-select UI and the pager

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/HomeController.cs:109-152`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Results.cshtml:61-77` (control), `:217` and `:234` (pager)
- Test: `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/WebTests.cs`

**Interfaces:**
- Consumes: `EligibilityFilter.SemanticTypeTuis` (Task 1), `SemanticTypeOption` (Task 2).
- Produces: `GET /Home/Results?semanticTypeTuis=T121&semanticTypeTuis=T047`.

**This is the genuinely new part.** There is no multi-value filter UI anywhere in
this app. The closest analogue is `string[]? nctIds` in `HomeController.cs:288-297`,
but that is a POST body binding, not a GET round-trip.

**The pager is the trap.** `Results.cshtml:217` and `:234` use
`asp-route-semanticType="@Model.Filter.SemanticType"`. **`asp-route-*` cannot
bind a collection to repeated query parameters** - it will stringify the list and
silently produce a broken link. The pager must build its query string manually.

- [ ] **Step 1: Write the failing tests**

Append to `WebTests.cs`:

```csharp
    // Multi-value GET binding: ?semanticTypeTuis=T121&semanticTypeTuis=T047.
    // No other filter in this app is multi-value, so this round-trip is new.
    [Fact]
    public async Task Results_accepts_repeated_semantic_type_parameters()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Results?semanticTypeTuis=T121&semanticTypeTuis=T047");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A legacy bookmark uses the old single-name parameter. It must not 500,
    // and must not silently return unfiltered results as though it had worked.
    [Fact]
    public async Task Results_tolerates_the_legacy_semantic_type_parameter()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Results?semanticType=Pharmacologic+Substance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Results_renders_a_multi_select_for_semantic_types()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/Home/Results");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("name=\"semanticTypeTuis\"", body);
        Assert.Contains("multiple", body);
    }
```

Note the backend is unreachable in `WebTests.Factory` (port 1), so the page
renders its inline-error path. These assert the request binds and the control is
emitted, not that rows come back - that is Task 1's integration coverage.

- [ ] **Step 2: Run to verify failure, then change the controller**

In `HomeController.cs`, replace the `string? semanticType = null` parameter at
`:116` with:

```csharp
        string[]? semanticTypeTuis = null,
        string? semanticType = null,   // legacy single-name param; see below
```

and the filter construction at `:128-130`:

```csharp
            // Legacy support: bookmarks and saved links from before the filter
            // moved to TUIs carry ?semanticType=<display name>. Resolve it to a
            // TUI rather than ignoring it - silently returning unfiltered
            // results would look like the filter had been applied.
            var tuis = (semanticTypeTuis ?? Array.Empty<string>())
                .Select(s => s?.Trim() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            if (tuis.Count == 0 && !string.IsNullOrWhiteSpace(semanticType))
            {
                var match = options.SemanticTypes
                    .FirstOrDefault(o => string.Equals(o.Name, semanticType, StringComparison.OrdinalIgnoreCase));
                if (match is not null) { tuis.Add(match.Tui); }
            }

            var filter = new EligibilityFilter(
                nctId: nctId, criterion: criterion, domain: domain,
                concept: concept, conceptCode: conceptCode, semanticTypeTuis: tuis);
```

Order matters: the options must be loaded before this block. Check the existing
action body - if options are fetched after the filter is built, move the fetch up.

Update the XML doc at `:104-107`, which enumerates the filterable columns.

- [ ] **Step 3: Change the view control**

Replace `Results.cshtml:61-77` with:

```cshtml
                <div class="col-md-4 col-lg-3">
                    <label for="f-semanticTypeTuis" class="form-label small text-muted mb-1">
                        Semantic type <span class="text-muted">(any of)</span>
                    </label>
                    @* Multi-select, unlike the other five controls: a criterion
                       can carry several semantic types, and matching the whole
                       joined string under-reported by 62% on the real corpus.
                       Values are TUIs (stable across UMLS releases); the names
                       are display only. *@
                    <select id="f-semanticTypeTuis" name="semanticTypeTuis" multiple size="4"
                            class="form-select form-select-sm">
                        @foreach (var opt in Model.Options.SemanticTypes)
                        {
                            <option value="@opt.Tui"
                                    selected="@Model.Filter.SemanticTypeTuis.Contains(opt.Tui)">@opt.Name</option>
                        }
                    </select>
                </div>
```

There is no text-input fallback here, unlike the other five controls: the
dimension is bounded at ~132 entries, so the "too many distinct values" path
that `EligibilityFilterOptions` documents cannot apply. Update the header
comment at `:5-8`, which says "Same control set across all six filterable
columns".

- [ ] **Step 4: Fix the pager**

`Results.cshtml:217` and `:234` currently carry
`asp-route-semanticType="@Model.Filter.SemanticType"`. Remove those attributes
and append the repeated parameters to the generated URL instead. Add a local
helper near `TextInput` (`:9-10`):

```cshtml
@functions {
    // asp-route-* cannot bind a collection to repeated query parameters - it
    // stringifies the list and produces a silently broken link. Build the
    // repeated params by hand and append them to the tag helper's URL.
    static string TuiQuery(IReadOnlyList<string> tuis) =>
        string.Concat(tuis.Select(t => "&semanticTypeTuis=" + Uri.EscapeDataString(t)));
}
```

Applying it depends on how each pager link is generated - if they use
`asp-action`/`asp-route-*`, the cleanest fix is to build the whole href with
`Url.Action(...)` plus the appended query, rather than mixing the two. Read
`:205-240` and pick one approach for both links; do not leave one on tag helpers
and one manual.

- [ ] **Step 5: Manual verification - this is where multi-select is actually proven**

The automated tests confirm binding and rendering, not behaviour.

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web
```

1. `/Home/Results` - the semantic-type control is a multi-select listing type
   names alphabetically, not combinations.
2. Select `Pharmacologic Substance`, submit. Row count should be **far larger**
   than the old filter gave - on the production corpus, ~118,867 against 44,800.
3. Select two types. Results include rows carrying either.
4. **Page to page 2.** The selection must survive - this is the pager fix, and
   the most likely thing to be broken.
5. Clear the selection and submit: all rows return (empty = no filter, not
   "match nothing").
6. Visit `/Home/Results?semanticType=Pharmacologic+Substance` - the legacy link
   still filters.

- [ ] **Step 6: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src/EligibilityProcessing.Web contexts/eligibility/tests
git commit -m "Multi-select semantic-type filter with pager preservation"
```

---

### Task 4: Analysis-tab aggregate

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb:3418`
- Test: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`

`ClusterCommonCriteriaAsync` groups by concept identity and collapses semantic
type with `COALESCE(max(semantic_type), '')` (`:3418`) - `semantic_type` is not
in the `GROUP BY` (`:3423-3425`). `max()` over a text column picks the
lexicographically largest string, which is arbitrary. It was defensible when one
CUI had one string; after phase 2 it still is, since canonicalisation made the
string a function of the CUI - **but the grouping key is
`COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept))`, so
unresolved criteria group by concept text and can span several CUIs with
different semantic types.** For those groups `max()` silently picks one.

- [ ] **Step 1: Write the failing test**

```vb
    ' Unresolved criteria group by lowercased concept text, so one cluster can
    ' span rows with different semantic types. max() picked one arbitrarily.
    <SkippableFact>
    Public Async Function ClusterCommonCriteria_does_not_silently_pick_one_semantic_type() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Await _fixture.InsertEligibilityRowAsync("NCT00000001", "Inclusion", "anaemia",
                conceptCode:="", semanticType:="Disease or Syndrome")
        Await _fixture.InsertEligibilityRowAsync("NCT00000002", "Inclusion", "anaemia",
                conceptCode:="", semanticType:="Finding")

        Dim clusters = Await _fixture.Gateway.ClusterCommonCriteriaAsync(
                {"NCT00000001", "NCT00000002"}, CancellationToken.None)

        Dim cluster = clusters.Single(Function(c) c.Concept = "anaemia")
        ' Both types are represented, in a stable order - not one arbitrary pick.
        Assert.Equal("Disease or Syndrome, Finding", cluster.SemanticType)
    End Function
```

Check `InsertEligibilityRowAsync`'s parameters and
`ClusterCommonCriteriaAsync`'s signature before writing this.

- [ ] **Step 2: Implement**

Replace `:3418` with a deterministic aggregate over the distinct names present
in the group:

```sql
       COALESCE((SELECT string_agg(DISTINCT s, ', ' ORDER BY s)
                   FROM unnest(array_agg(semantic_type)) AS s
                  WHERE s IS NOT NULL AND s <> ''), '') AS semantic_type,
```

This keeps `CriterionCluster.SemanticType` a display string, so
`Analysis.cshtml:428` and `CriterionCluster.vb` are unchanged.

- [ ] **Step 3: Full suite and commit**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
git add contexts/eligibility/src contexts/eligibility/tests
git commit -m "Stop the Analysis cluster silently picking one semantic type"
```

---

### Task 5: Version, spec correction, PR

- [ ] **Step 1: Bump the version**

Build-only - no migration in this phase. `0.2.0` -> **`0.2.1`**, `releaseDate`
today. Prepend a `releases` entry in user-facing terms: the semantic-type filter
now returns every matching row and offers real types rather than combinations.
ASCII only; `releases[0]` must match `current`.

Then update the four version literals and the one `ReleaseDate` assertion in
`VersionWebTests.cs` with the Edit tool (not PowerShell - BOM).

- [ ] **Step 2: Correct the spec**

`docs/superpowers/specs/2026-07-20-semantic-type-repair-design.md`, phase 3
section, states:

> **Results CSV export** (`Export/ExportResults.cs`) currently **omits**
> `semantic_type`. Add it.

**This is factually wrong** and must be removed, not quietly dropped.
`ExportResults.cs` is a 29-line generic CSV helper (`CsvFile(string csv, string
downloadName)`) with no column list, no `EligibilityFilter`, and no rows - there
is no Results export at all. Replace the line with a note that none exists and
that building one is out of scope.

- [ ] **Step 3: Full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, 0 skipped with Docker running.

- [ ] **Step 4: Commit, push, PR**

Push and open the PR with `gh pr create --body-file <file>`. The body must state:

- The measured before/after (44,800 -> 118,867 rows for `Pharmacologic
  Substance`; 215 combination strings -> 132 real types).
- That the dropdown no longer costs a DISTINCT scan over 3.9M rows.
- That multi-select is the app's **first** multi-value GET filter, that
  `asp-route-*` cannot express it, and that the pager builds its query manually
  as a result.
- That legacy `?semanticType=<name>` links are resolved rather than ignored.
- Which manual checks were run - especially paging with a selection active.

If PR #35 (phase 2) has merged by now, rebase onto `main` first so this PR shows
only phase 3's diff.

---

## Deliberately not in this phase

- **A Results CSV export.** None exists; building one is greenfield.
- **Rendering TUIs in the results table.** Names only, so `EligibilityRow` and
  the page query's reader ordinals are untouched.
- **Phase 4** (authoring tables). Until it lands, authoring rows keep the legacy
  string format.

## Known risks

**The pager is the most likely thing to break**, and it breaks *silently* -
`asp-route-*` will stringify a collection into a malformed link rather than
erroring. Manual check 4 exists for exactly this.

**`EligibilityFilter`'s constructor must keep every parameter optional**, or
`ResultsViewModel.cs:17` (`= new()`) stops compiling.

**Empty must mean "no filter", not "match nothing".** Both the filter's
normalisation and the SQL's `IS NULL` arm enforce this, and a test covers it -
getting it wrong makes an unfiltered Results page return zero rows.

# Semantic type: repair, restructure, backfill

Date: 2026-07-20
Status: approved - four phases, one PR each

## Problem

Two independent defects, discovered while scoping OMOP hierarchy expansion.

### 1. Semantic types stopped being populated (live regression)

`umls.semantic_type` holds **100 rows covering 49 distinct CUIs**, against
**1,265,171** rows in `umls.concept`. The MRSTY load is effectively empty.

`UmlsOptions.Backend` selects between `"rest"` (the UTS API) and `"postgres"`
(the local mirror). The REST path returned semantic types from the API; the
Postgres path reads them from `umls.semantic_type`
(`UmlsMetathesaurusStore.vb:244`). The switchover is visible in the corpus:

| Month | Resolved rows | With semantic type |
|---|---|---|
| 2026-05 | 396,977 | 90.4% |
| 2026-06 | 2,675,294 | 5.5% |
| 2026-07 | 912,842 | 0.0% (1 row of 912,842) |

**3,479,090 resolved rows currently have `semantic_type IS NULL`.**

The loader code and its call order are correct (`UmlsMetathesaurusStore.vb:306`,
`Cli/Program.vb:520-533`), and **the source file is intact** - the operator's
`MRSTY.RRF` is 212,736,397 bytes / ~3.877M lines, with well-formed records.

**Root cause: the load was interrupted partway through its final step.** The 100
surviving rows are a *prefix* of the file, not a sample:

| Evidence | Reading |
|---|---|
| `min(cui)` = `C0000005` | The first CUI in MRSTY, matching line 1 of the file |
| `max(cui)` = `C0000343` | 49 consecutive CUIs from the start |
| All 100 rows pass the `umls.concept` filter | The `WHERE cui IN (...)` filter is not implicated |
| `tui` non-null on every row | The data itself is valid |

The 100th line of the operator's file is `C0000340` while the table's max is
`C0000343`, so the table is a *filtered* prefix - the `WHERE cui IN (...)` clause
dropped some early CUIs, and 100 survivors reach roughly line 105-110.

**The mechanism is not established.** Two hypotheses, and the tidier one does not
survive scrutiny:

- *Interrupted COPY / `pg_restore`.* *Does not fit.* A binary COPY is
  transactional - aborting it mid-stream yields zero rows, not a committed
  prefix. The same applies to an interrupted `pg_restore` of the table.
- *The INSERT ran against a partially-populated `umls.concept`.* **Fits the
  evidence.** If `mrsty_stage` loaded fully but `umls.concept` held only ~49 CUIs
  when the final INSERT executed, the filter would produce exactly this shape:
  100 rows, 49 distinct CUIs clustered at the start of CUI order, all passing the
  filter, all with valid TUIs. That points at `RebuildConceptTableAsync` and
  `LoadSemanticTypesAsync` overlapping, or the steps being run out of order -
  not at the COPY.

Confirming this needs the load's console output or server logs from May 2026,
which may no longer exist. **The repair does not depend on resolving it**, but
the guard does: an assertion that only checks the final row count catches either
mechanism, which is the argument for adding it regardless.

The seed is ruled out - `SeedDump.cs:31` covers `eligibility_umls_retry` only and
never touches the `umls` schema.

Nothing detected this for two months. **That is what makes the completeness
assertion below the primary fix rather than a safeguard.**

A related open question: whichever mechanism applied, the run should not have
reported success. Phase 1 should establish whether `load-umls` can return 0 after
a partial or empty semantic-type load - if it can, the assertion is treating a
symptom and the exit-code path needs fixing too.

### 2. The representation is ambiguous and the filter under-reports

`semantic_type` is a `", "`-joined display string. Two consequences:

**It cannot be parsed back.** Several UMLS semantic type names contain commas -
`Amino Acid, Peptide, or Protein` is real and present in the corpus, e.g. the
value `Amino Acid, Peptide, or Protein, Enzyme` (1,776 rows). Splitting on `", "`
cannot recover the components.

**The filter silently under-reports.** `EligibilityFilter.SemanticType` is exact
match on the whole string (`PostgresGateway.vb:1437`). Measured:

- Filtering `Pharmacologic Substance` returns **6,389** rows.
- Rows that actually carry that semantic type: **19,674**.
- **Under-reporting by 68%.**

The dropdown offers **194 distinct strings**, 81 of them combinations, against
~127 real UMLS semantic types. Users pick combinations, not types.

### Ordering is also inconsistent

Legacy REST-era values preserve UMLS API order and are **not** alphabetical
(`Organic Chemical, Biologically Active Substance`). The Postgres backend queries
`ORDER BY sty`, so it sorts. The same CUI therefore renders two different strings
depending on when the row was written, splitting any `GROUP BY`.

### Corpus baseline (measured 2026-07-19/20)

| Metric | Value |
|---|---|
| `public.eligibility` rows | 4,439,480 |
| Resolved (non-empty `concept_code`) | 3,985,113 (89.8%) |
| Distinct CUIs | 132,243 |
| Distinct trials | 317,153 |
| Resolved rows with NULL semantic_type | 3,479,090 |
| Distinct `semantic_type` strings | 194 (81 multi-valued) |
| Rows with multi-valued semantic_type | 26,948 |

Table size ~1,964 MB. `id bigserial PRIMARY KEY` exists, so batched backfill by
id range is index-backed.

## Decisions

1. **Canonical sorted form everywhere.** One representation. The backfill
   recomputes the ~500K legacy rows as well as the 3.48M NULL rows, so ordering
   is uniform and `GROUP BY` is correct.
2. **TUI array as the analytic column.** `semantic_type_tuis text[]`, GIN
   indexed. TUIs are stable across UMLS releases; names get reworded. The display
   string is *derived from* the array, so the two cannot drift.
3. **Four phases, one PR each.** The surface is too large for one reviewable
   change: a migration, three list-to-string join sites, an orchestrator shim,
   the dedup merge key, the normalization cache, two authoring tables, the
   filter, dropdown, sort, exports, and a format-locking test.

### Why phases 2 and 3 split

Phase 2 leaves the display string populated and the UI unchanged. The risky data
migration therefore lands **without** simultaneously changing what users see, and
if phase 3 needs revision the corpus is already correct.

## Phase 1: fix the MRSTY load

No schema change. Version bump: **build only**.

### Do not run `load-umls` as it stands

`RunLoadUmlsAsync` calls `TruncateAsync` first, which wipes `umls.atom` and
`umls.concept` (`UmlsMetathesaurusStore.vb:262`). With the Postgres backend
active, that would drop resolution to zero for anything running. The database is
currently idle, but the safe path should exist regardless.

### Change

Add `--semantic-types-only` to `load-umls`: skip `TruncateAsync` and
`BulkLoadAtomsAsync`, call `LoadSemanticTypesAsync` alone. That function already
stages into a temp table and inserts with `ON CONFLICT (cui, sty) DO NOTHING`, so
it is idempotent and touches only `umls.semantic_type`.

### Completeness assertion

`load-umls` must fail loudly when `umls.semantic_type` row count is below
`umls.concept` row count. Every UMLS concept carries at least one semantic type,
so the ratio is >= 1.0 by construction. 100 rows against 1.27M concepts should
have been a hard error in May.

Applies to both the full and `--semantic-types-only` paths.

### Exit-code integrity

Establish whether `load-umls` can exit 0 after a partial COPY. The evidence above
shows a run that left a 100-row prefix without anyone noticing, which means
either the failure was outside the CLI (`pg_restore`) or a non-fatal path exists
inside it. If the latter, fix the exit code as well - an assertion that catches a
bad state is weaker than a load that refuses to claim success.

### Verification

`umls.semantic_type` moves from 100 rows to roughly 1.3M-1.8M (1,265,171
concepts at ~1.0-1.4 semantic types each), and `count(DISTINCT cui)` approaches
the `umls.concept` count. The source file needs no attention - it is intact.

## Phase 2: data model, write path, backfill

Adds a migration. Version bump: **MINOR** (reset build to 0), plus a
`docs/specs/database_schema.md` update in the same change.

### Schema

- `public.eligibility.semantic_type_tuis text[]`, GIN indexed.
- `umls.semantic_type`: PK changes from `(cui, sty)` to `(cui, tui)`, and `tui`
  becomes `NOT NULL`.

  **Correction (measured 2026-07-20).** This section originally justified the
  change by saying `DISTINCT ON (cui, sty)` discards a conflicting TUI
  arbitrarily. That is **wrong**: TUI and STY are a perfect 132/132 bijection
  across all 3,876,942 rows, with zero `(cui, tui)` duplicates, so nothing is
  discarded.

  The change is still correct, for a different reason. `--semantic-types-only`
  (phase 1) is **additive**, so if a future UMLS release renames a semantic type,
  `ON CONFLICT (cui, sty)` would insert a second row for the same `(cui, tui)`.
  Keying on TUI makes the additive load idempotent against renames. The loader
  changes with it: `DISTINCT ON (cui, tui)` and `ON CONFLICT (cui, tui)`.

  The loader changes with it: `LoadSemanticTypesAsync`
  (`UmlsMetathesaurusStore.vb:337`) becomes `DISTINCT ON (cui, tui)` and its
  `ON CONFLICT (cui, sty)` becomes `ON CONFLICT (cui, tui)`.

  **Ordering hazard.** The migration must delete any rows with a NULL `tui`
  before applying `NOT NULL`, or it will fail. Measured 2026-07-20: there are
  **zero** such rows, so this is a defensive no-op - kept because a future
  partial load could reintroduce them.
- New `umls.semantic_type_dim (tui PRIMARY KEY, sty NOT NULL)` - ~127 rows.
  Populated **by the migration itself**, via
  `INSERT ... SELECT DISTINCT tui, sty FROM umls.semantic_type`, so it does not
  require another `load-umls` run. The loader also refreshes it thereafter, so a
  vocabulary refresh keeps it current. Lets names resolve for display without
  scanning the large table.

`semantic_type` (text) is **retained** as the display column.

### Canonical string

`string_agg(sty, ', ' ORDER BY sty)`, derived from the TUI array via the
dimension table. Deterministic, and derived rather than independently
constructed so the two representations cannot diverge.

### Write path

Four sites produce or consume the joined string and must change together:

- `ResolvedRecord.vb:35-37` - the canonical join. Becomes TUI-aware; carries both
  the TUI list and the derived display string.
- `UmlsNormalizeJob.vb:124` - same join, normalization job.
- `Cli/Program.vb:654-655` - same join, retry command.
- `DuplicateConceptMerger.vb:88-94` - splits the string back on `", "`. The
  comment at `:90` claims "semantic-type names don't contain ', ' in practice",
  which is **false**. The code is currently safe only because it splits and
  immediately re-joins with the same separator, so the string round-trips. Under
  a real list representation that safety disappears. Merge on the TUI array; fix
  the comment.

Also:

- `PipelineOrchestrator.vb:467-469` wraps an already-joined string from the
  normalization cache as a **one-element list** so the constructor re-joins it
  verbatim. That shim breaks under a real multi-value representation and must be
  replaced by reading the cached TUI array.
- `umls.concept_normalization.semantic_type` (V20) caches the same joined string.

  **Correction (2026-07-20).** This section implied the cache needs a TUI column.
  It does not. The cache already stores `concept_code`, so the orchestrator looks
  the assignments up from that CUI instead of reusing the cached string. No
  schema change to `concept_normalization`, and the stale string is simply not
  read on that path any more. Cost: one extra lookup per cache hit - a
  sub-millisecond local query, memoized per run by `UmlsCache`.

The dedup merge key `(ConceptCode, SemanticType, Criterion)`
(`DuplicateConceptMerger.vb:39,47`) becomes TUI-array-based. Note this changes
dedup identity: rows previously distinguished by differently-ordered strings will
now merge. That is the intended correction, not a regression.

### Backfill

A CLI command following the existing maintenance pattern (`normalize-umls`,
`embed-studies`), batched by `id` range, cancellable, with progress reporting.

- Scope: **all** rows with a resolved `concept_code`, not only NULL ones. The
  ~500K legacy rows are rewritten to canonical order.
- Writes both `semantic_type_tuis` and the derived `semantic_type`.
- Expected yield: **total**. Measured 2026-07-20, after phase 1 widened the load
  to every MRSTY CUI, **0** of the corpus's 132,243 distinct CUIs are absent from
  `umls.semantic_type` - down from 19,133 before the widening. All 3,985,113
  resolved rows can be filled. The unmapped count must still be **reported**,
  since it is the regression signal, but it should read 0.
- `ix_eligibility_semantic_type` indexes the changed column, so these are
  non-HOT updates. Expect table and index bloat across 4M rows: a `VACUUM
  (ANALYZE)` pass is **part of the job**, not an afterthought.
- Must be re-runnable. A partial run followed by a full run must converge.

### Tests

`ResolvedRecordTests.vb:62-83` format-locks the exact `", "` join, including
`"Disease or Syndrome, Mental Process"`. It changes with the representation and
gains cases for comma-containing names - the case the current design cannot
express.

`DuplicateConceptMergerTests` gains a case proving two differently-ordered
legacy strings for the same CUI now merge.

Integration tests cover: backfill idempotence, canonical ordering, the
comma-containing name round-trip, and the unmapped-CUI count.

## Phase 3: UX

No migration. Version bump: **build only**.

- **Filter** moves from exact string equality to array containment
  (`semantic_type_tuis && ARRAY[...]`), so selecting `Pharmacologic Substance`
  returns 19,674 rows rather than 6,389.
- **Dropdown** sources from `umls.semantic_type_dim`: ~127 real types instead of
  194 combinations. `GetEligibilityFilterOptionsAsync`
  (`PostgresGateway.vb:1500-1547`) currently does a full DISTINCT scan for this
  column; sourcing from the dimension removes that scan.
- **Multi-select** becomes possible and should be offered - the array makes
  "any of these types" a natural query.
- **Sort** (`PostgresGateway.vb:2151`, `semantic_type_asc`) continues to sort the
  display string.
- **Results CSV export - this line was wrong and is withdrawn.** It claimed
  `Export/ExportResults.cs` omits `semantic_type`. There is no Results export at
  all: that file is a 29-line generic helper (`CsvFile(string csv, string
  downloadName)`) with no column list, no `EligibilityFilter` and no rows. The
  only CSV carrying a semantic-type column is the **authoring** export. Building
  a Results export is greenfield work - streaming a filtered slice of ~4.4M rows,
  with its own decisions about row caps and download UX - and is out of scope
  here. Decided 2026-07-20: not planned.
- **Analysis tab** (`Views/Home/Analysis.cshtml:428`) renders the string; the
  cluster query aggregates with `COALESCE(max(semantic_type), '')`
  (`PostgresGateway.vb:3403`), which is arbitrary across a group. Revisit against
  the array.

`EligibilityFilter.SemanticType` becomes a collection. `EligibilityFilterOptions`
changes shape accordingly.

## Phase 4: authoring surface - CLOSED, NOT BUILT (2026-07-20)

**The planned schema change was dropped after measurement. This section records
why, so it is not re-proposed on the same reasoning.**

The plan was: add `semantic_type_tuis` to `authoring_criterion` (V6) and
`authoring_criterion_source` (V10), with the matching changes to
`AuthoringController.cs:381,505,547,667`;
`PostgresGateway.vb:2743,2760,2782,2803,3010-3036,3078-3094`;
`Models/AuthoringCriterionForm.cs:21`, `Models/AuthoringCriterionSourceForm.cs:18`;
`AuthoringCriterion.vb:19`, `AuthoringCriterionSource.vb:20`;
`Export/AuthoringCriteriaCsv.cs:37`, `Export/AuthoringCriteriaAuditCsv.cs:103,118`;
and 11 sites in `Views/Authoring/Edit.cshtml`.

### Why it was dropped

Three findings, all measured against production on 2026-07-20:

1. **Nothing queries authoring semantic types.** They appear as a hidden input, a
   tooltip (`Edit.cshtml:355,367`) and two CSV columns. No filter, no grouping,
   no aggregation. The TUI array exists to make containment queries possible;
   there are none here to enable.
2. **The field is never user-edited.** It is copied from the corpus, so it cannot
   drift independently of a source that is now canonical.
3. **The inconsistency was two rows, and it self-heals.** Of 57
   `authoring_criterion` rows and 100 `authoring_criterion_source` rows, exactly
   **2** carried a non-canonical string - both the same ordering artefact
   (`Organic Chemical, Biologically Active Substance` for `C0005437` and
   `C0010294`). New rows are populated from `ClusterCommonCriteriaAsync` and
   corpus snapshots, both canonical since phases 2 and 3, so the format converges
   without intervention.

Building the schema change would have been consistency for its own sake: a
migration, four gateway methods, two core types, two form models, two exports and
eleven view sites, serving no query anyone makes.

### What was done instead

The two rows were canonicalised in place with a single UPDATE against
`umls.semantic_type`. Verified afterwards: 0 non-canonical rows in either table,
and 0 CUIs rendering more than one string.

### If this is revisited

The right trigger is a real requirement - something that needs to filter, group
or aggregate authoring criteria by semantic type. At that point the array can be
added against a concrete use, and this section is the record of why it was not
added speculatively.

**Standing property, worth stating plainly:** the authoring tables carry semantic
types as a **display string only**, by design. Anything analytic should go
through `public.eligibility`.

## Risks

**The backfill rewrites ~4M rows in a 2 GB table.** Bloat is expected; VACUUM is
part of the job. Backups exist (seed export and embeddings export taken
2026-07-20).

**Dedup identity changes.** Merging on the TUI array rather than the string means
some records that were previously distinct will merge. Intended, but it changes
row counts on reprocessing, so it should not be diagnosed later as data loss.

**Phase 1 must land and be verified before phase 2 backfills anything.**
Backfilling from a still-empty `umls.semantic_type` would write NULLs across the
corpus and look like success. The backfill command must therefore refuse to run
when `umls.semantic_type` fails the same completeness check phase 1 introduces -
a guard, not a convention, because the failure mode is silent.

## Acceptance - all met, measured 2026-07-20

| Criterion | Result |
|---|---|
| `umls.semantic_type` covers every CUI in `umls.concept`, and `load-umls` refuses to report success otherwise | **Met.** 3,876,942 rows over 3,530,466 CUIs; 0 of 1,265,171 concepts uncovered. `load-umls` exits 4 otherwise. |
| Every resolved row in `public.eligibility` has a populated `semantic_type_tuis`, or is counted in a reported unmapped total | **Met.** 3,985,113 of 3,985,113; 0 unmapped. |
| The same CUI produces one identical display string everywhere in the corpus | **Met.** 0 CUIs render more than one string, in `public.eligibility` and in the authoring tables. |
| Filtering Results by `Pharmacologic Substance` returns every row carrying it | **Met**, though not at the number originally written here. The spec said 19,674; that was measured when only 506,023 rows had semantic types at all. After the backfill the real figures are **44,800 -> 118,867**. |
| The semantic-type dropdown lists real UMLS semantic types, not combinations | **Met.** 132 types, from `umls.semantic_type_dim`, replacing 215 combination strings. |

## Out of scope

OMOP / CONCEPT_ANCESTOR hierarchy expansion, which prompted this work. See
[docs/research/2026-07-19-eligibility-analytics-options.md](../../research/2026-07-19-eligibility-analytics-options.md).
The CUI-to-OMOP crosswalk design resumes once semantic types are trustworthy,
since semantic type is the natural routing signal for OMOP domain assignment.

# Analytics Tab - Design

Date: 2026-07-21
Status: approved, ready for implementation planning
Branch: `feat/analytics-tab`

## 1. Purpose

A new Analytics area over the processed corpus, with three views:

1. **Distinctiveness (lift)** - what is unusual about how a chosen set of trials recruits, versus the corpus.
2. **Trend** - how a concept's prevalence changes year on year.
3. **Concept lookup** - everything known about one concept, and the hub that links to the other two.

This is **sub-project 2 of 2**. Sub-project 1 (the condition normalizer, spec
`2026-07-21-condition-normalizer-design.md`) shipped in 0.4.0-0.4.2 and is what
unblocks the Condition cohort here.

Deliberately **not** included: a plain ranked-browse view. Raw prevalence is
dominated by boilerplate (see 2.2) and a leaderboard of it would be true and
useless.

## 2. Measurements

Every figure below was measured against the live corpus on 2026-07-21. They are
recorded so a later reader can tell which decisions were evidence-based.

### 2.1 Corpus size

| Quantity | Value |
|---|---|
| `public.eligibility` criterion rows | 4,439,480 |
| ... with a resolved `concept_code` | 3,985,113 |
| distinct (concept_code, nct_id) pairs | 3,854,517 |
| distinct concepts | 132,243 |
| `public.eligibility_study_detail` studies | 316,558 |
| `public.eligibility` total size | 2,600 MB |

### 2.2 Raw prevalence is boilerplate

Top concepts corpus-wide: Adult 191,581 trials (60% of the corpus), pregnancy
106,479, informed consent 97,595. Lift demotes these correctly - Adult scores
1.1 in a diabetes cohort - while surfacing real signal: Type 2 diabetes ~17,
and hypertension as a genuine comorbidity finding. This is the whole reason the
tab ranks by lift rather than count.

### 2.3 Concept counts are extremely long-tailed

| Trials per concept | Concepts | Share |
|---|---|---|
| 1 | 52,185 | 39.5% |
| 2-5 | 42,080 | 31.8% |
| 6-20 | 22,346 | 16.9% |
| 21-100 | 11,077 | 8.4% |
| 100+ | 4,555 | 3.4% |

**71.3% of concepts appear in five or fewer trials.** Unguarded lift would rank
every one-trial concept at the top with an enormous ratio and no meaning. This
is what section 4.2's minimum support exists for.

### 2.4 Performance, and two wrong hypotheses

The cohort profile (diabetes: 32,162 trials, 647,931 criterion rows) started at
**3.6s**. Two plausible fixes were tested and both were wrong before the right
one was found, so all three are recorded:

| Change | Cohort profile |
|---|---|
| baseline, no indexes | 3,600ms |
| `(nct_id, concept_code)` covering index alone | 3,390ms - almost no gain |
| plus `(concept_code, nct_id)` - **the shipped design** | **1,225ms** |
| pre-deduplicated pair table instead - measured, not shipped | 322ms |

The pair table was the first hypothesis, dismissed early on a bad comparison
(`count(*)` against `count(DISTINCT)` over the *same* undeduplicated table, which
removes no duplicates and so measures nothing), then revisited and measured
properly at the end. It wins on speed but costs 491 MB, a 36s rebuild and a
staleness window; the indexes were chosen instead because 1.2s is adequate behind
the asynchronous loading the tab needs anyway. See section 9.

Reading the plan was what settled it. The covering index did work -
`Heap Fetches: 0` - but the column order was wrong for the cohort predicate,
which filters on `concept_code`: as the second column it could only scan,
discarding 4,401,106 of 4,439,480 index entries. Adding the reverse-order index
let that filter seek.

The dominant remaining cost is a **Sort of 596,280 rows** to satisfy
`count(DISTINCT nct_id)`. Rewriting the cohort predicate to seek made data
gathering 3.6x faster (1,064ms to 292ms) but the total *worse* (1,480ms),
because the seek-based plan yields rows in `nct_id` order, whereas scanning the
`(concept_code, nct_id)` index yields them already ordered for the group-by,
making the sort nearly free. That is why the scanning plan wins.

Corpus-wide baseline: **4.9s before the indexes, 2.0s after.**

### 2.5 Year coverage is currently skewed

| Year | Studies |
|---|---|
| 2010-2016 | 436 - 2,794 per year |
| 2017 | 4,594 |
| 2018 | 12,424 |
| 2019 | 29,323 |
| 2020-2025 | 32,801 - 39,885 per year |
| 2026 | 26,498 (partial year) |

This reflects processing coverage, not clinical reality. It is expected to
resolve as the pipeline completes the corpus, so the design does **not**
hard-code a cutoff year - that would become wrong. See 4.3.

### 2.6 Other constraints carried from earlier work

- **Labels must come from `umls.concept.pref_name`**, never `eligibility.concept`.
  One CUI (C0032961) carries 1,060 distinct extracted label strings.
- **Phase is only ~28% usable**: `NA` 45.5%, empty 26.1%. Values are `PHASE3`,
  not `Phase 3`.
- **Condition** is available at 92.2% resolution, 77.1% of resolved mentions
  rolling up, via `public.condition_concept`.

## 3. Architecture

Everything is a **live query**. No precomputed aggregate tables, no refresh
lifecycle, no staleness window, and no second source of corpus truth.

### 3.1 Migration V25 - two composite indexes

```sql
CREATE INDEX IF NOT EXISTS ix_eligibility_concept_nct
    ON public.eligibility (concept_code, nct_id);

CREATE INDEX IF NOT EXISTS ix_eligibility_nct_concept
    ON public.eligibility (nct_id, concept_code);
```

162 MB each, 324 MB total against a 2,600 MB table, ~13s each to build.

`(concept_code, nct_id)` serves the cohort predicate by seek *and* returns rows
pre-ordered for the group-by. `(nct_id, concept_code)` covers the join back so
it never touches the heap.

**Both indexes already exist on the production database**, created during the
measurement in section 2.4 using `CREATE INDEX CONCURRENTLY`. The migration uses
`IF NOT EXISTS`, so applying it is a no-op there and correct everywhere else.
This is deliberate: without the migration the schema is not reproducible.

Per the schema-doc rule, `docs/specs/database_schema.md` is updated in the same
change - the index list for `public.eligibility` and the migration-history table.
Registration is required in **two** places: `MigrationResourceNames` in
`PostgresGateway.vb` and an `<EmbeddedResource>` with an explicit `<LogicalName>`
in `EligibilityProcessing.Data.vbproj`.

### 3.2 The corpus baseline is cached, not recomputed

Lift needs corpus-wide per-concept trial counts on every request. That result is
identical for all requests and changes only when the corpus does, so it is
memoised through the existing `ICorpusReadCache`, reusing its existing
invalidation after pipeline batches and tool jobs. A warm lift request therefore
costs the cohort query alone, ~1.2s.

### 3.3 New units

- **`AnalyticsController`** (C#, Web) - not new actions on `HomeController`,
  which already carries dashboard, runs, history, analysis, results and tools.
  `AuthoringController` is the precedent. Thin: parameter binding, view models,
  read-tolerant error handling, CSV export.
- **`AnalyticsGateway`** (VB, Data) - not new methods on `PostgresGateway`,
  which is ~2,600 lines and owns pipeline persistence. Three focused query
  methods rather than one parameterised query that grows a flag per view.
- **`LiftCalculator`** (VB, Core) - the pure arithmetic, so it is unit-testable
  with no database.

Nav gets an Analytics entry through the existing `NavClassController` helper.

## 4. The three views

### 4.1 Cohort definition, shared by the lift view

A cohort is a set of `nct_id`. Four ways to define one, all producing the same
shape so the view renders them uniformly:

| Cohort | Definition |
|---|---|
| Concept | trials whose criteria mention a concept, optionally including hierarchy descendants via `umls.concept_ancestor` |
| Condition | trials whose `conditions` map through `public.condition_concept` to a concept, also hierarchy-aware |
| Phase | `eligibility_study_detail.phase` equals a chosen value |
| Year | `EXTRACT(year FROM start_date)` equals a chosen value |

Phase offers only real interventional phases. `NA` and empty are **not** offered:
they are not phases, they are 71.6% of the corpus, and offering them would imply
a comparison that does not exist. The view states the covered share on screen.

### 4.2 View 1 - Distinctiveness (lift)

For each concept in the cohort:

```
lift = (cohort_trials / cohort_size) / (corpus_trials / corpus_size)
```

Columns: concept preferred name, cohort trials, % of cohort, % of corpus, lift.
Sorted by lift descending.

**Minimum support: a concept must appear in at least N cohort trials, default
10, adjustable in the UI.** Section 2.3 is why. Raw counts are always displayed
next to the ratio, so no row is ever a bare number the reader cannot interrogate,
and the threshold is one visible knob rather than hidden smoothing.

Empty cohort, or a cohort where nothing clears the support floor, renders an
explicit empty state naming the reason - not a blank table.

### 4.3 View 2 - Trend

Pick **up to five** concepts; plot each as a **percentage of that year's
processed trials**. Percentage rather than raw count is permanent, not a
workaround: trial volume grows year on year regardless of processing coverage.
Five is a readability cap on a line chart, not a technical limit.

All years are included; there is no cutoff, because section 2.5's skew is
temporary and a hard-coded year would become wrong. Two honesty mechanisms
instead:

- the most recent year is **labelled partial** (it is a part-year by definition);
- every point carries its underlying study count, so thin years are self-evident
  while coverage completes.

### 4.4 View 3 - Concept lookup

Entered by typing a CUI or name, or by clicking a concept code in Results or the
Analysis tab. Shows:

- preferred name, semantic types, and whether the concept has hierarchy, with
  ancestor and descendant counts;
- corpus prevalence, in trials and as a percentage;
- breakdown by phase and by year;
- **five** real `criterion` texts drawn from the corpus, so the reader can see
  how the concept is actually phrased in practice. These come from
  `eligibility.criterion` (the extracted criterion sentence) and are clearly
  presented as examples - this is the one place raw extracted text appears, and
  it is never used as a label (see 2.6).

Two actions out: use as a lift cohort, or add to a trend. This view is the hub -
it is how a reader gets from "I saw this code somewhere" to either other view.

## 5. Error handling

Every view catches and degrades with a message rather than failing the page,
matching Results and Analysis. This matters more here than elsewhere: a 1.2s
query against a corpus during a pipeline run is a normal condition, not an error.

Authorization is read-only throughout - class-level `[Authorize]`, as Results and
Analysis use. No `PipelineOps`, no `AuthorWrite`. The tab writes nothing.

## 6. Export

CSV per view, reusing `Export/CsvWriter.cs`. The lift export carries the same
columns as the screen **including the raw counts**, so a shared file can be
checked independently rather than being a list of unverifiable ratios.

## 7. Testing

Per the project rule, every new function ships with tests and verification is
`dotnet test`, not `dotnet build`.

**Unit (`EligibilityProcessing.Core.Tests`)**, against `LiftCalculator`:

- lift arithmetic on known inputs, including a concept at exactly corpus rate
  scoring 1.0
- the minimum-support filter: a concept at the threshold is kept, one below is
  dropped (boundary, inclusive)
- a corpus count of zero cannot divide by zero
- partial-year labelling picks the correct year

**Integration (`EligibilityProcessing.Data.Tests`, real Postgres)**:

- V25 applies and both indexes exist
- each of the four cohort definitions returns the same result shape, so the view
  can render them uniformly - this is the test that keeps the four paths honest
- the hierarchy-aware cohorts include descendants, and exclude non-descendants
- trend returns one row per year with its study count
- concept lookup returns prevalence, phase and year breakdowns for a seeded
  concept
- an empty cohort returns an empty result rather than throwing

## 8. Acceptance criteria

1. `dotnet test contexts/eligibility/Eligibility.sln` passes with zero skipped.
2. A cohort profile for a large cohort (diabetes, 32,162 trials) renders in
   under 2s warm.
3. The lift view for a diabetes cohort ranks Type 2 diabetes and hypertension
   above Adult and informed consent. This is the end-to-end check that the tab
   does the job it exists for.
4. No view displays a label sourced from `eligibility.concept`.
5. `database_schema.md` updated in the same commit as the migration.
6. `version.json` bumped to **0.5.0** - a migration requires at least a MINOR
   bump with `build` reset to 0 - with `releases[0]` matching `current`.

## 9. Risks and open items

- **`pref_name` quality is inherited, not controlled.** C0030193 renders as
  "Dolor", C0006826 as "Blastoma". Every view displays these, so users will see
  them. Choosing a better display name per CUI is its own piece of work and is
  explicitly out of scope here.
- **Year coverage is skewed today** (section 2.5). Expected to resolve as the
  pipeline completes the corpus; the design avoids hard-coding around it.
- **Phase covers only 28% of studies.** Stated on screen rather than hidden.
- **The condition cohort inherits the normalizer's limits**: 92.2% resolution,
  and 77.1% of resolved mentions roll up. A condition slice is therefore a
  sample, not a census, and the view should not imply otherwise.
- **1.2s is not instant.** If the tab feels sluggish in real use, the measured
  fallback is the pre-deduplicated pair table at 322ms (section 2.4). That is a
  later change of query shape, not a redesign.

## 10. Explicitly not in scope

- A ranked-browse view (see section 1).
- Saved, shared or scheduled analyses.
- Precomputed aggregate tables of any kind.
- Improving `pref_name` display quality.
- Any change to the extraction pipeline or the condition normalizer.

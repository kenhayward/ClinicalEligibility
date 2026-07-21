# Criteria clustering: hierarchy rollup

Date: 2026-07-20
Status: approved - ships as two implementations

## Problem

The Authoring Analysis tab finds similar studies, clusters their common
eligibility criteria, and LLM-normalizes each cluster into a canonical statement.
Its clustering key is **exact concept identity**
(`PostgresGateway.vb:3460`):

```sql
COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)) AS group_key
```

So criteria that mean the same thing at the level a trial designer cares about
land in separate clusters. "Type 2 Diabetes Mellitus" (`C0011860`) and
"Diabetes Mellitus" (`C0011849`) are different rows, different normalize calls,
and different Add decisions - even though a designer scanning the tab wants one
line that says *diabetes*.

The tab already filters out singletons client-side (`Edit.cshtml:646`), so
fragmentation does not just add noise: **it suppresses signal**. A concept split
five ways across five studies looks like five one-study clusters and disappears
entirely.

## Decision

Group by an **ancestor** concept at a user-chosen rollup level, using a
SNOMED-derived CUI hierarchy built from UMLS `MRREL`.

### Why not OMOP first

The research
([docs/research/2026-07-19-eligibility-analytics-options.md](../../research/2026-07-19-eligibility-analytics-options.md))
recommends OMOP `CONCEPT_ANCESTOR`, and that recommendation stands for
corpus-wide analytics. It is not the right *first* step here:

- OMOP needs an ATHENA download, a `code` column that
  `umls.atom` does not have (the loader never read MRCONSO field 13), and a
  CUI-to-OMOP crosswalk - none of which exist.
- `MRREL` carries `CUI1`/`CUI2` **directly**, so a SNOMED-scoped parent/child
  graph needs no crosswalk, no `code` column, no ATHENA download, and no licence
  beyond the UMLS one already held.
- It delivers the same user-visible improvement on the slice that matters most:
  the corpus is 45.7% Condition-domain, which is where SNOMED is strongest.

**The table is shaped like OMOP's `CONCEPT_ANCESTOR` on purpose.** If the OMOP
route is later adopted, it becomes a data swap rather than a code change - the
rollup SQL and the entire UI survive.

### Measured scope (2026-07-20)

| Metric | Value |
|---|---|
| Resolved rows | 3,985,113 |
| Distinct CUIs | 132,243 |
| **Distinct CUIs matched via SNOMEDCT_US** | **66,514 (50.3%)** |
| Rows matched via SNOMEDCT_US | 2,255,167 (56.6%) |
| Distinct Condition-domain CUIs | 57,546 |

A CUI can match via different sources on different rows, so the SNOMED figures
count CUIs with **at least one** SNOMED-sourced match. Treat them as an estimate
of rollup eligibility, not an exact bound.

**Coverage is partial by construction and must stay visible.** Roughly half the
distinct concepts have no SNOMED edges. At any rollup level some clusters merge
and others do not, and the UI has to say which - otherwise the counts look
arbitrary rather than honestly incomplete.

## Part 1: the hierarchy table

Adds a migration. Version bump: **MINOR**.

### Schema

```
umls.concept_ancestor (
    descendant_cui text NOT NULL,
    ancestor_cui   text NOT NULL,
    min_distance   integer NOT NULL,
    PRIMARY KEY (descendant_cui, ancestor_cui)
)
```

Deliberately mirrors OMOP's `CONCEPT_ANCESTOR` (ancestor, descendant, distance)
so a later swap is a load change only.

### Load

Extend the RRF loader to read `MRREL.RRF`, keeping rows where
`SAB = 'SNOMEDCT_US'` and `REL IN ('PAR', 'CHD')`, normalised into a single
`(child, parent)` edge orientation. Then compute the transitive closure to a
bounded depth (**5 levels**), recording `min_distance`.

**Precomputed, not a recursive CTE at query time.** Clustering runs
interactively over the criteria of up to 200 studies; a recursive walk per
cluster would be felt. This is also OMOP's own design, which is what keeps the
swap cheap.

### The orientation must be proven, not assumed

`MRREL.REL` describes the relationship of the **second** concept to the first, so
`REL='PAR'` should mean `CUI2` is the parent of `CUI1`. **This spec does not
assert that.** Getting it backwards produces an inverted hierarchy that looks
plausible and rolls up to nonsense.

The loader ships with a test asserting a known real case:

> `C0011860` (Type 2 Diabetes Mellitus) must have `C0011849` (Diabetes Mellitus)
> as an ancestor - and **not** the reverse.

If the orientation is flipped, that test fails loudly. Verify empirically against
the loaded data before trusting any rollup output.

### Load-completeness guard

Following the precedent set by `load-umls`'s semantic-type assertion (0.1.35):
the command reports edge count, closure row count, and the number of distinct
descendants, and fails rather than reporting success on an implausibly small
result. The semantic-type incident - a partial load that went unnoticed for two
months - is the reason this is a hard check and not a log line.

## Part 2: rollup in clustering

No migration. Version bump: **build only**.

### Query

`ClusterCommonCriteriaAsync` gains a `rollupLevel` parameter.

**Level 0 is exactly today's behaviour.** The feature is opt-in and the default
path is unchanged, so there is no regression surface for existing users.

### The rollup rule (CORRECTED 2026-07-21 - the original was wrong)

**Superseded rule, recorded so it is not reintroduced.** This spec originally
said the group key should be the *furthest ancestor within the level* - the
greatest `min_distance <= rollupLevel`. **Measurement against the loaded
hierarchy disproved it.**

SNOMED is multi-parent, so a concept typically has *many* ancestors at a given
distance, not one. Measured for the two diabetes types at `min_distance <= 2`:

| Concept | Ancestors at distance 1 | Ancestors at distance 2 |
|---|---|---|
| Type 1 DM (`C0011854`) | 3 (incl. Diabetes mellitus) | 8 |
| Type 2 DM (`C0011860`) | 1 (Diabetes mellitus) | 5 |

"Furthest within N" resolves to `ORDER BY min_distance DESC LIMIT 1`, which
picks **arbitrarily among ties**. In practice Type 1 selected *Digestive system
disease* and Type 2 selected *Endocrinopathy*, so the two **failed to merge at
level 2 despite merging perfectly at level 1**. The rule made rollup worse as
the level rose, which is backwards.

### The rule that ships

**The group key is the ancestor shared by the most of the concepts actually
being clustered.** The choice is made once across the result set, not
independently per concept - which is precisely why siblings cannot diverge.

For the concepts in one clustering run:

1. **Candidates**: every ancestor reachable at `min_distance <= rollupLevel`
   from any of those concepts.
2. **Rank by coverage**: the number of distinct clustered concepts that ancestor
   covers, descending.
3. **Tiebreak on specificity**: fewer global descendants in
   `umls.concept_ancestor` wins - the tightest grouping that still covers the
   same set.
4. **Final tiebreak on CUI**, so the result is deterministic.

A concept that no chosen ancestor covers keeps its own CUI as the key, so it
still appears as its own cluster rather than vanishing.

Validated against the loaded hierarchy. For Type 1 DM, Type 2 DM and Impaired
glucose tolerance at level 2, four ancestors tie at 3 concepts covered, and
specificity separates them correctly:

| Ancestor | Global descendants | Rank |
|---|---|---|
| Hyperglycaemia | 119 | **chosen** |
| Disorder of glucose metabolism | 136 | |
| Disorder of glucose regulation | 186 | |
| Endocrinopathy | 1,211 | last - correctly rejected as too broad |

Unresolved criteria are untouched - no CUI, no hierarchy, so they keep the
lowercased-text fallback they have today.

`CriterionCluster` gains `AncestorCode`, `AncestorConcept`, `MemberCodes` and
`RollupLevel`. Its constructor is positional with no optional parameters, so
every construction site changes: `PostgresGateway.vb:3489` plus the fakes in
`TestFakes.vb:536` and `PipelineOrchestratorTests.vb:1437`.

### The integration cost that is easy to miss

`GetClusterRecordsAsync` (`PostgresGateway.vb:3505`) resolves `groupKey` against
a **single** concept identity. Once a cluster spans several CUIs it must take the
**member CUI set** instead.

This is not optional polish. Without it, both the Records expander and Normalize
return nothing at level > 0 - and a Normalize button that silently produces
nothing is worse than one that errors, because the user reads it as "no common
phrasing found".

`syncAddButtons` (`Edit.cshtml:881`) moves its dedup key from
`type|conceptCode` to `type|groupKey`. That is already correct at level 0 and
stays correct above it.

### UI

- A rollup-level control beside `#cluster-topn`, defaulting to 0.
- A column showing the ancestor concept when a row rolled up, with the count of
  concepts merged into it; blank when it did not. This is what makes partial
  coverage read as a fact rather than an inconsistency.
- The detail row's `colspan="7"` (`Edit.cshtml:698`) becomes 8.

### What Add persists

**The ancestor's CUI** goes on the `authoring_criterion`.

The alternatives were rejected: leaving `concept_code` empty makes a resolved
criterion look unresolved, and picking the most common member CUI is arbitrary -
a plurality is not a meaning. The ancestor is what the user actually accepted:
the point of rollup is that "Diabetes Mellitus" is the right level of description
for the criterion.

Lineage is not lost. `authoring_criterion_source` already snapshots every
underlying leaf row with its own `concept_code` (`V10__authoring_criterion_source.sql`),
so the specific concepts that were rolled up remain recorded.

The source note records the rollup explicitly, e.g.
`From cluster: Diabetes Mellitus (rolled up from 4 concepts, 12 studies)`.

## Testing

**Part 1 (hierarchy load)** - integration tests against real Postgres:

- The orientation test above. This is the one that matters most.
- Closure correctness: a three-level chain yields `min_distance` 1, 2 and 3.
- `min_distance` is the minimum where two paths of different length exist.
- Depth is bounded at 5 - a six-level chain produces no row at distance 6.
- The completeness guard fails on a deliberately truncated edge load.

**Part 2 (rollup)** - integration tests:

- Level 0 output is byte-identical to today's.
- Two criteria whose CUIs share a parent merge at level 1 and not at level 0.
- **Multi-parent concepts still merge**: two concepts that share one ancestor but
  differ in their other ancestors land in the same cluster. This is the case the
  superseded "furthest ancestor" rule failed, and the regression test for it -
  Type 1 and Type 2 diabetes must cluster together at level 2, not split.
- **The chosen ancestor is the most specific among equally-covering
  candidates**, so a cluster is not labelled with an over-broad concept.
- The choice is deterministic: the same input yields the same ancestor every run.
- A CUI with no SNOMED edges does not roll up at any level, and its cluster is
  still returned.
- `GetClusterRecordsAsync` returns the union of member rows for a rolled-up
  cluster.
- An unresolved criterion clusters by text at every level.

The LLM normalize step is unchanged and needs no new coverage; it receives a
larger set of original texts, which is exactly what it was built for.

## Risks

**An inverted hierarchy is the failure mode to fear.** It would not error - it
would produce clusters rolled up to *more specific* concepts, which reads as
merely odd rather than wrong. The orientation test is the only thing standing
between that and a shipped feature.

**Partial coverage will be visible and may read as a bug.** Half the distinct
concepts cannot roll up. The UI column showing which rows rolled up is what turns
that from "inconsistent" into "incomplete, and here is where".

**Rollup can over-merge.** A broad ancestor can make a cluster useless
("Endocrinopathy" spans 1,211 concepts). Two things bound this: the level is
user-controlled and defaults to 0, and the specificity tiebreak prefers the
tightest ancestor that covers the same concepts. No attempt is made to pick a
level automatically.

**Only levels 1 and 2 exist.** Part 1 loaded the hierarchy at depth 2, chosen by
measurement - depth 3 ran at 97% of the command timeout. The UI must not offer
a level the data cannot serve.

## Out of scope

- **The corpus-wide prevalence browser** (goal B). Deferred deliberately: this
  change exercises the hierarchy against a real workload with a real user first.
  If rollup does not improve the Analysis tab, a browser built on it would not
  have helped either.
- **OMOP adoption.** Revisit when either cross-vocabulary rollup is needed, or
  the ~50% of concepts without SNOMED edges become a practical limit rather than
  a theoretical one. The table shape makes that a load change.
- **`umls.atom.code`** and the CUI-to-OMOP crosswalk - only needed for OMOP.

# Condition Normalizer - Design

Date: 2026-07-21
Status: approved, ready for implementation planning
Branch: `feat/condition-normalizer`

## 1. Purpose

Map the free-text condition strings on `public.eligibility_study_detail.conditions`
to UMLS CUIs, so that corpus analytics can slice by condition without the
free-text field's duplication and long tail destroying the slice.

This is **sub-project 1 of 2**. Sub-project 2 is the Analytics tab (distinctiveness
/ lift, trend over time, concept-first lookup). Only the *Condition* slice of that
tab depends on this work; the concept-cohort, phase and year slices do not. The two
are specified and shipped separately because this one changes data and the other
changes UI, and coupling them would produce a plan too large to review.

## 2. The problem, measured

All figures below were measured against the live corpus on 2026-07-21 and are the
justification for every design decision that follows. They are recorded here so a
later reader can tell which choices were evidence-based and which were judgement.

Corpus size:

| Quantity | Value |
|---|---|
| `public.eligibility` criterion rows | 4,439,480 |
| ... with a resolved `concept_code` | 3,985,113 |
| distinct (concept_code, nct_id) pairs | 3,854,517 |
| `public.eligibility_study_detail` studies | 316,558 |

The condition field:

| Quantity | Value |
|---|---|
| condition mentions (unnested) | 611,329 |
| distinct raw strings | 91,600 |
| distinct lowercased strings | 90,076 |

The field is unnormalized: `COVID-19` (2,942 studies) and `Covid19` (1,997) are
separate entries. The top 100 condition strings cover only 108,042 of 611,329
mentions (18%), so there is no small head that could be hand-curated instead. The
single most frequent value is `Healthy`, which is not a disease.

### 2.1 Exact matching alone covers 71% of volume, and 63% is unambiguous

Joining `lower(trim(condition))` against `umls.atom.str_norm`:

| Exact match | Distinct strings | Share | Study mentions | Share |
|---|---|---|---|---|
| matched | 31,830 | 35.3% | 436,429 | 71.4% |
| unmatched | 58,246 | 64.7% | 174,898 | 28.6% |

This is the classic Zipf shape - the head is well formed, the tail is malformed.
It is why tier 1 is worth having on its own and why the fuzzy tier is an
improvement rather than a prerequisite.

Splitting the matched set by how many distinct CUIs the string maps to:

| Bucket | Distinct strings | Study mentions | Share of mentions |
|---|---|---|---|
| exact, unambiguous (1 CUI) | 30,255 | 385,085 | 63.0% |
| exact, ambiguous (>1 CUI) | 1,575 | 51,344 | 8.4% |
| no exact match | 58,246 | 174,898 | 28.6% |

The great majority of the exact-match set is unambiguous and needs no scoring,
no threshold and no tie-break. Only 1,575 strings require disambiguation. This
three-way split is the structure of the algorithm in section 5.

### 2.2 The preferred name is usually not the query string

This measurement changed the design, so it is recorded in full.

`UmlsMetathesaurusStore.SearchCandidatesAsync` returns `UmlsCandidate.Name` =
`umls.concept.pref_name`, **not** the atom string that produced the hit.
`UmlsMatchScorer.PickBestMatch` therefore scores the query against the preferred
name. For the highest-volume exact matches, those two strings usually differ:

| Condition | CUI | pref_name | Equal? |
|---|---|---|---|
| stroke | C0038454 | CVA - Cerebrovascular accident | no |
| covid-19 | C5203670 | Disease caused by 2019-nCoV | no |
| cancer | C0006826 | Blastoma | no |
| pain | C0030193 | Dolor | no |
| obesity | C0028754 | Obese | no |
| anxiety | C0003467 | Anxiousness | no |
| breast cancer | C0006142 | Breast cancer | yes |
| healthy | C3898900 | Healthy | yes |
| depression | C0011570 | Depression | yes |

Three of ten agree. Routing an exact atom match through the scorer would compare
`stroke` to `CVA - Cerebrovascular accident`, score far below any sane threshold,
and reject a perfect match.

The consequence is that **tier 1 must not use the scorer at all**. An exact
normalized match to a UMLS atom is definitive by construction: atoms are the
synonyms of their concept, so matching one is matching the concept. The scorer is
needed only where there is a genuine choice to make.

### 2.3 The unmatched tail is real text with mechanical defects

The highest-volume unmatched strings are not junk:

| String | Studies | Defect |
|---|---|---|
| advanced solid tumor / tumors | 695 / 481 | singular vs plural |
| pregnancy related | 637 | modifier |
| non small cell lung cancer | 602 | missing hyphens |
| opioid use | 441 | modifier |
| nsclc | 390 | acronym |
| covid | 343 | abbreviation |
| atrial fibrillation (af) | 292 | parenthetical gloss |
| depression, anxiety | 272 | two conditions in one string |
| hiv/aids | 258 | compound |

### 2.4 Trigram alone is not sufficient - acronyms fail

Best trigram match against `umls.atom.str`:

| String | Best match | Score | Verdict |
|---|---|---|---|
| non small cell lung cancer | Non-Small Cell Lung Cancer (C0007131) | 1.000 | correct |
| atrial fibrillation (af) | AF - Atrial fibrillation (C0004238) | 1.000 | correct |
| covid | COVID-19 (C5203670) | 0.667 | correct |
| hiv/aids | AIDS (C0001175) | 0.556 | acceptable |
| advanced solid tumors | Solid tumor (C0280100) | 0.478 | correct |
| nsclc | NSC762 (C0700294) | 0.300 | **wrong** |

`nsclc` shares no trigrams with `non-small cell lung cancer`, so trigram
similarity is structurally incapable of resolving it. This is exactly why
`UmlsMatchScorer` carries a separate acronym term. The conclusion is that tier 2
must go through the existing scorer rather than use raw trigram similarity - and,
per section 2.2, that tier 1 must not go through it at all.

### 2.5 Cost

A single unresolved lookup (FTS arm plus trigram arm) measured at ~30ms. Only
tier 2 pays this: 58,246 strings, roughly 30 minutes single-threaded for a full
backfill, comparable to the existing `embed-studies` job. Tiers 1a and 1b are a
single indexed lookup on `ix_umls_atom_str_norm` and are negligible by
comparison, which means 71.4% of the corpus resolves in the fast path.

## 3. Non-goals

- **Splitting multi-condition strings** such as `depression, anxiety` (272
  studies). Real, but a long tail with its own ambiguity about which CUI wins.
  Not in v1.
- **Curating or hand-correcting mappings.** No review UI, no human-in-the-loop
  queue. Rejected during design as too much scope for v1; the stored
  `match_score` leaves the door open.
- **Changing the criteria pipeline's own 0.45 threshold.** Untouched.
- **Any analytics UI.** That is sub-project 2.

## 4. Data model

### 4.1 New table (migration V24)

A dictionary keyed by the normalized string, **not** a per-study mention log.
Normalization is study-independent, so a dictionary is ~90,076 rows rather than
611,329, is re-runnable, and lets a new pipeline run reuse all prior work.

```sql
CREATE TABLE public.condition_concept (
    condition_norm  text PRIMARY KEY,
    raw_form        text        NOT NULL,
    study_count     integer     NOT NULL DEFAULT 0,
    concept_code    text        NULL,
    umls_name       text        NULL,
    match_source    text        NOT NULL,
    match_score     numeric(4,3) NOT NULL DEFAULT 0,
    resolved_at     timestamptz NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_condition_concept_code
    ON public.condition_concept (concept_code)
    WHERE concept_code IS NOT NULL;

CREATE INDEX ix_condition_concept_pending
    ON public.condition_concept (study_count DESC)
    WHERE resolved_at IS NULL;
```

Column notes:

- `condition_norm` - `lower(trim(raw))`, the dedup key. Matches the semantics of
  `UmlsMetathesaurusStore.NormalizeConcept`, which is what stamped
  `umls.atom.str_norm`; the implementation MUST call that same function rather
  than re-implement lowercasing, so the two can never drift.
- `raw_form` - the **most frequent original casing** of this normalized string.
  This is the string handed to the matcher, and the choice is load-bearing (see
  4.2).
- `study_count` - corpus frequency, used to order backfill work so the
  highest-value strings resolve first.
- `match_source` - `exact` | `exact_ambiguous` | `fuzzy` | `unresolved`, one per
  tier in section 5. Keeping `exact_ambiguous` distinct rather than folding it
  into `exact` costs nothing now and lets the Analytics tab exclude that 8.4% if
  it proves noisy.
- `match_score` - `numeric(4,3)`, consistent with the criteria pipeline's
  end-to-end typing of match scores. `0` when unresolved.
- `resolved_at` - NULL means never attempted. A row that was attempted and failed
  has `resolved_at` set, `match_source = 'unresolved'`, and `concept_code` NULL,
  so a re-run can skip it unless forced.

Per the schema-doc rule, [docs/specs/database_schema.md](../../specs/database_schema.md)
is updated in the same change: the new table section, the migration-history table,
and the index list.

Migration registration is required in **two** places or the suite fails loudly
(this is now a hard test failure rather than a silent skip, as of PR #40):
`MigrationResourceNames` in `PostgresGateway.vb`, and an `<EmbeddedResource>` with
an explicit `<LogicalName>` in `EligibilityProcessing.Data.vbproj`.

### 4.2 Casing is load-bearing

`UmlsMatchScorer.AcronymContribution` only fires when the raw query matches
`^[A-Z0-9]{2,6}$`. The corpus stores mixed casing for the same acronym:

| Raw form | Studies |
|---|---|
| COPD | 657 |
| HIV | 506 |
| NSCLC | 377 |
| Hiv | 258 |
| Copd | 93 |
| Nsclc | 13 |

If the dictionary keyed on lowercase and fed `nsclc` to the matcher, the acronym
term would never fire and every acronym would stay unresolved. Therefore
`raw_form` holds the most frequent original casing and **that** is the matcher
input. Ties are broken by choosing the lexicographically first form, so the
choice is deterministic and a re-run reproduces it.

## 5. Resolution algorithm

Three tiers, following the measured structure in section 2.1. The scorer is used
only in tiers 1b and 2, where a genuine choice exists.

**Tier 1a - exact and unambiguous (63.0% of mentions).**
Look up `condition_norm` against `umls.atom.str_norm`. If every matching atom
resolves to a single distinct CUI, accept it directly:
`match_score = 1.000`, `match_source = 'exact'`. **The scorer is not consulted**,
for the reason established in section 2.2. This is one indexed lookup on
`ix_umls_atom_str_norm`, so it is far cheaper than the full three-arm search as
well as more accurate.

**Tier 1b - exact but ambiguous (8.4% of mentions).**
The atoms resolve to more than one CUI, so pick one deterministically:

1. prefer the CUI whose `pref_name` normalizes exactly to `condition_norm`;
2. otherwise the highest `UmlsMatchScorer.Score(raw_form, pref_name)`;
3. otherwise the lexicographically lowest CUI.

Rule 3 exists purely so a re-run reproduces the same answer. Accept regardless of
score - the string is still an exact atom match, so the only question was which
concept, not whether. Record `match_source = 'exact_ambiguous'` and
`match_score = 1.000`, so the Analytics tab can exclude this 8.4% if it ever
proves problematic.

**Tier 2 - no exact atom (28.6% of mentions).**
Only here does the existing search path apply, and no new matching code is needed:

1. `candidates = IUmlsClient.SearchAsync(raw_form, ct)` - the FTS and trigram
   arms of `SearchCandidatesAsync`, already gated by `PostgresUmlsClient`.
2. `match = UmlsMatchScorer.PickBestMatch(raw_form, candidates)` - applies the
   hard-coded 0.45 floor and returns `UmlsMatch.Unresolved` below it.
3. Accept only if `match.MatchScore >= ConditionMatchThreshold` (0.60);
   `match_source = 'fuzzy'`.

Below threshold in tier 2: `match_source = 'unresolved'`, `concept_code` NULL,
`match_score` 0, `resolved_at` set.

Note that tier 2 inherits the pref_name-scoring behaviour described in section
2.2, which makes it conservative - it will reject some correct matches whose
preferred name reads nothing like the query. That is the acceptable direction to
err, and it is why the coverage floor in section 9 is set at the tier 1 figure.

### 5.1 Why 0.60 rather than the pipeline's 0.45

A wrong condition mapping does not announce itself - it silently misfiles trials
into the wrong analytic slice, where it is invisible. That is a worse failure mode
than the criteria pipeline's, where a bad match is visible next to its criterion
text in the Results browser. Concretely, `advanced solid tumors` scores 0.478 on
trigram, which would clear 0.45 but is close enough to the line to be
uncomfortable, and `nsclc` at 0.300 must be rejected.

The threshold is a named constant, `ConditionMatchThreshold`, on the normalizer -
**not** a change to `UmlsMatchScorer.MatchThreshold`, which stays 0.45 for the
criteria pipeline. Because `match_score` is persisted, the threshold can be
retuned later by re-running the backfill with `--force` rather than by
re-extracting anything.

## 6. Pipeline integration

`public.eligibility_study_detail` has exactly one writer:
`CaptureStudySnapshotAsync` (`PostgresGateway.vb:1902`), called per trial from
`PipelineOrchestrator.vb:272` via `TryCaptureStudySnapshotAsync`, before the LLM
call, already best-effort - non-cancellation exceptions are logged and swallowed.

That is the hook. After the snapshot upsert, for each condition string on the
trial:

- **already in the dictionary** - one indexed primary-key lookup, and increment
  nothing (see 6.1). Effectively free, and after backfill this is the
  overwhelming majority.
- **unseen** - resolve as in section 5 and insert the row. Most unseen strings
  take tier 1 and cost a single indexed lookup; only a tier 2 string costs the
  ~30ms search.

Average trial carries 1.9 conditions (611,329 / 316,558), so steady-state cost is
about two indexed lookups per trial. Normalization inherits the existing swallow,
so it can never fail a trial. `conditions` is `text[] NOT NULL DEFAULT '{}'`, so
there is no NULL case.

Inline resolution was chosen over record-only-then-batch so that analytics stay
current with no manual step. The dictionary short-circuit is what makes this
affordable.

### 6.1 study_count maintenance

`study_count` is a corpus statistic used only for backfill ordering and for
reporting coverage. It is **not** incrementally maintained by the pipeline -
doing so correctly would require knowing whether this trial had previously
contributed the same string, which the per-trial hook cannot cheaply determine,
and a drifting count is worse than a stale one. It is recomputed in bulk by the
Tools job and by the CLI verb. This is a deliberate, documented approximation.

## 7. Tools card and CLI

A third `ToolJobKind.NormalizeConditions`, mirroring `embed-studies` exactly. Note
that the semantic-type backfill is CLI-only and is **not** a precedent here; the
`embed-studies` card is.

Required pieces:

- `ToolJobs.vb`: `ToolJobKind.NormalizeConditions`, a
  `NormalizeConditionsOptions` record, an `IConditionNormalizeJob` interface with
  `CountRemainingAsync` and `RunAsync(options, progress, ct)`.
- `ToolJobRunner.cs`: a branch in `ExecuteAsync`, plus `Describe`; `KindName` in
  `ToolJobState.cs`.
- `HomeController.cs`: `RunNormalizeConditions` POST, `[HttpPost]` +
  `[Authorize(Policy = "PipelineOps")]` + `[ValidateAntiForgeryToken]`, acquiring
  `RunGate` and returning 202 / 409 / 503 like the existing actions, and writing
  an audit row.
- `Tools.cshtml`: a fifth card using `_ToolJobPanel`, and a count in
  `ToolCounts`.
- `ToolJobRequest.cs`: the new options on the record.

Behaviour:

- Work is ordered `study_count DESC`, so a cancelled run still leaves the corpus
  measurably better off.
- `--dry-run` reports what would be resolved without writing.
- `force` re-resolves rows that already have `resolved_at` set, which is how a
  threshold change or a UMLS reload is applied.
- Progress through the existing `IProgress(Of ToolJobSnapshot)` and the 500ms
  SignalR pump.

The CLI gets the same job behind `normalize-conditions`, sharing the
`IConditionNormalizeJob` implementation exactly as `embed-studies` does.

## 8. Testing

Per the project testing rule, every new function ships with tests and
verification is `dotnet test`, not `dotnet build`.

Unit tests (`EligibilityProcessing.Core.Tests`) for the normalizer:

- normalization key equals `NormalizeConcept` output (guards against drift)
- most-frequent raw form is selected; ties broken lexicographically
- **tier 1a does not consult the scorer**: a single-CUI exact atom match whose
  `pref_name` is wildly dissimilar to the query still resolves, with score 1.000.
  This is the direct regression test for section 2.2 and must fail if anyone
  "simplifies" tier 1 into `PickBestMatch`.
- tier 1b tie-breaks in order: pref_name equality, then score, then lowest CUI;
  and a fixed input produces the same CUI on repeat runs
- tier 1b accepts even when the best score is below 0.60 (the string is an exact
  atom match; only the concept choice was in doubt)
- a tier 2 score of exactly 0.60 is accepted (boundary, inclusive), 0.599
  rejected
- a rejected tier 2 match writes `unresolved` with NULL `concept_code`, score 0,
  and a non-NULL `resolved_at`
- the acronym case: given candidates for `NSCLC`, the uppercase raw form is what
  reaches the scorer

Integration tests (`EligibilityProcessing.Data.Tests`, real Postgres):

- V24 applies and the table/indexes exist
- dictionary upsert is idempotent - resolving the same string twice yields one
  row
- an atom seeded against two CUIs takes the tier 1b path; against one CUI, tier
  1a
- `study_count` recompute produces the expected counts from a seeded
  `eligibility_study_detail`
- backfill honours `study_count DESC` ordering
- `force` re-resolves an already-resolved row; without it, the row is skipped

Pipeline test: a trial whose condition normalization throws still completes and
still persists its criteria (the swallow is load-bearing and must be asserted,
not assumed).

## 9. Acceptance criteria

1. `dotnet test contexts/eligibility/Eligibility.sln` passes with zero skipped.
2. After a full backfill against the production corpus, at least **71%** of
   condition study-mentions resolve to a CUI. That is the measured tier 1 total
   (63.0% unambiguous + 8.4% ambiguous), which the algorithm accepts by
   construction, so anything less indicates a defect rather than a tuning
   shortfall. Tier 2 is upside on top.
3. `stroke` resolves to C0038454 despite its preferred name being
   `CVA - Cerebrovascular accident`. This is the regression test for section 2.2
   and would have failed the original design.
4. `nsclc` resolves to a non-small-cell-lung-cancer CUI, or is `unresolved` -
   it must not resolve to `NSC762`.
5. `COVID-19` and `Covid19` resolve to the same CUI. This is the whole point of
   the exercise and is the single clearest end-to-end check.
6. A pipeline run's per-trial wall clock is not measurably worse once the
   dictionary is warm.
7. `database_schema.md` is updated in the same commit as the migration.
8. `version.json` bumped to **0.4.0** - a migration requires at least a MINOR
   bump with `build` reset to 0 - with a `releases[0]` entry matching `current`.

## 10. Risks and open items

- **Coverage after the fuzzy tier is estimated, not measured.** Tier 1 at 71.4%
  is measured; the fuzzy tier's incremental yield is inferred from six sampled
  strings. If it lands materially below expectation the design still holds, since
  acceptance criterion 2 is set at the measured floor.
- **Tier 1b may pick the wrong concept for an ambiguous string.** 1,575 strings
  and 8.4% of mentions are exposed. The tie-break is deterministic but not
  clever, and the sampled `cancer -> Blastoma (C0006826)` shows the local
  `pref_name` choice can itself be odd. Mitigated by recording
  `exact_ambiguous` separately so the slice can exclude it, and by the fact that
  the CUI is at least always a concept the string genuinely names.
- **`pref_name` quality is inherited, not controlled.** Several preferred names
  in the local mirror read oddly (`Dolor` for pain, `Blastoma` for cancer). This
  affects tier 1b tie-breaking and tier 2 scoring, and will also affect how
  concepts are labelled in the Analytics tab. Out of scope here, but worth
  knowing before sub-project 2 displays these names to users.
- **`Healthy` is the most common condition value** and will resolve to some CUI.
  It is a legitimate mapping but a misleading slice. The Analytics tab (PR 2)
  should treat it as a candidate for exclusion; nothing here needs to change.
- **Multi-condition strings resolve to at most one CUI**, so `depression,
  anxiety` will either match one or fall below threshold. Accepted for v1.
- **`study_count` is a stale approximation between Tools runs** (section 6.1).
  Documented rather than fixed.

## 11. What sub-project 2 will need from this

The Analytics tab's condition slice joins
`eligibility_study_detail.conditions` -> `condition_concept.condition_norm` ->
`concept_code`, and may then roll up through `umls.concept_ancestor` exactly as
the criteria rollup does. Nothing in that join is added here beyond the table
itself.

Separately, and **not** part of this sub-project: the cohort-profile query for the
lift view measured at 3.6s, which is too slow to feel interactive. A precomputed
distinct (concept_code, nct_id) index is the likely fix, but note that at
3,854,517 distinct pairs versus 3,985,113 resolved rows it saves almost no rows -
its value is in removing the DISTINCT aggregation and allowing an index in both
directions. That is a sub-project 2 decision and is recorded here only so the
measurement is not lost.

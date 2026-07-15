# Eligibility Processing — Technology-Independent Specification

**Version**: 1.1
**Status**: Captured from production n8n workflow `PISX5yTFaetCAH5F` (active version `ef65a19b-721a-4a1e-9710-55e7e65a82a0`, validated by Run 75). The .NET re-implementation ([architecture doc](Eligibility_Processing_DotNet_Architecture.md)) is built and end-to-end-tested but not yet validated against Run 75.
**Purpose**: Sufficient detail to re-implement the system on any technology stack while preserving observed behaviour, data fidelity, and performance characteristics. No architecture, language, framework, or vendor is prescribed.

**v1.1 deltas** (re-implementation enhancements, see §2.12, §6.4 update, §9 strikethroughs):
- §2.11 cancellation is implemented in the .NET re-implementation (per-run cancellation token + dashboard Cancel button).
- §2.12 (new) per-trial audit table — records every trial's lifecycle from "passed to LLM" to terminal status, with stage-specific diagnostic columns. Closes the visibility gap behind §9.1.
- §6.4 LLM-returns-malformed-JSON is no longer silently dropped — the audit table records it distinctly from "LLM returned `[]`" so operators can tell truncation from intentional empty extraction.

---

## 1. System Purpose

The system ingests raw clinical trial eligibility-criteria free text from the AACT public clinical trials database, decomposes it into discrete, structured eligibility statements, resolves each clinical concept against the UMLS Metathesaurus, and persists the resulting structured records to a downstream relational store for analytical and graph-construction use.

The core value created by the system:

1. **Structuring** — converts unstructured prose into a normalised row-per-criterion table.
2. **Classification** — labels each criterion by domain (closed taxonomy) and inclusion/exclusion role.
3. **Coding** — assigns UMLS Concept Unique Identifiers (CUIs), preferred names, source vocabularies, and semantic types where confidence is sufficient.
4. **Provenance** — preserves the original source phrase verbatim per record, alongside the model-derived canonical form.
5. **Incremental progression** — processes the database forward from a watermark, never re-processing already-stored trials unless explicitly invalidated.

Out of scope for the system: clinical decision support, patient matching, trial outcome data, recruitment status, sponsor/site metadata, downstream graph construction (a separate consumer).

---

## 2. Functional Requirements

### 2.1 Invocation modes

The system MUST support three independent invocation modes, all converging on the same processing pipeline:

| Mode | Trigger | Input field | Default behaviour |
|---|---|---|---|
| **Interactive** | Human form submission | `StudyCount` (integer) | User enters batch size; default 8 |
| **Programmatic** | Sub-workflow / library call | `StudyCount` (integer) | Caller supplies batch size |
| **External** | HTTP webhook | none | Fixed batch size = 500 (production-run mode) |

All three modes converge into a single normalised input: `StudyCount: integer` plus a captured `StartedAt: epoch-ms` for runtime metrics. `StudyCount` MUST coerce to an integer, defaulting to `10` if missing or non-numeric.

### 2.2 Watermark-driven selection

Selection of trials to process MUST be driven by a single mutable watermark, stored externally to the source database, identifying the **highest NCT_ID already processed**.

- The watermark MUST default to `NCT00000000` when no prior processing has occurred or when the persisted value is empty/null/the string "undefined" or "null".
- The watermark MUST be derived as `COALESCE(MAX(NCT_ID), 'NCT00000000')` from the output store at the start of each run, and pushed into the watermark store before trial selection.
- This watermark-from-output-store pattern guarantees idempotency: re-running after a crash will resume from the last successfully persisted trial.
- The watermark MUST be a single shared value, not partitioned by user/session.

### 2.3 Source filtering

For each run, candidate trials MUST be selected from the source eligibility table with the following filters applied in a single query:

1. `nct_id > <watermark>` (strict greater-than)
2. `criteria IS NOT NULL`
3. `LENGTH(TRIM(criteria)) >= 50` — exclude effectively empty criteria
4. `criteria` does NOT match (case-insensitive) any of:
   - `%please contact%`
   - `%contact site for%`
   - `%contact study%`
5. Sort: `nct_id ASC`
6. Limit: `StudyCount`

These filters are non-negotiable; they remove known low-value rows that otherwise consume LLM budget for zero structured output.

### 2.4 LLM extraction

For each selected trial, the system MUST call a large language model to extract discrete eligibility statements from the raw criteria text.

#### 2.4.1 Model contract

- **Model class**: instruction-tuned LLM capable of structured JSON output. Production reference: Gemma 4 26B A4B Q8_0 GGUF served via llama.cpp; equivalent open or commercial models acceptable.
- **Sampling**: temperature 0.3 (low determinism for taxonomy stability), max output tokens 3500.
- **Timeout per call**: 600 seconds.
- **API style**: OpenAI-compatible chat completions (system + user messages). Implementation MAY use a different shape provided the prompt content and structured-output guarantees are preserved.

#### 2.4.2 Prompt — system message (verbatim required behaviour)

The system prompt MUST instruct the model to:

- Output ONLY a JSON array; response begins with `[` and ends with `]`; nothing before or after.
- For each criterion produce an object with exactly these keys, in any order: `NCT_ID`, `Criterion`, `Domain`, `Concept`, `Qualifier`, `TimeWindow`, `OriginalText`.
- **Field semantics**:
  - `NCT_ID` — repeat the supplied trial identifier on every entry.
  - `Criterion` — exactly one of: `Inclusion`, `Exclusion`.
  - `Domain` — exactly one of the following closed list; if none fit, `Other`:
    `Disease`, `Laboratory Test`, `Surgery`, `Drug Treatment`, `Allergy`, `Cardiovascular Function`, `Reproductive Status`, `Performance Status`, `Infection`, `Comorbidity`, `Substance Use`, `General Health`, `Consent`, `Age`, `Sex`, `Pregnancy`, `Genetic`, `Medical Device`, `Imaging`, `Vital Signs`, `Mental Health`, `Lifestyle`, `Vaccination`, `Other`.
  - `Concept` — canonical clinical entity name, 1–5 words, standard medical term (not the verbatim source phrase). MUST NOT contain qualifiers, history-of phrases, or modifiers — those go in `Qualifier`.
  - `Qualifier` — SHORT clinical-state modifier, 1–3 words. Permitted contents: clinical state (`normal`, `elevated`, `uncontrolled`, `stable`), stage (`Stage III`, `ECOG 0-1`), status (`HER2-positive`, `signed`, `diagnosed`), or empty. MUST NOT contain verb phrases, geography, recruitment context, demographics, temporal phrases, or any phrase over 3 words.
  - `TimeWindow` — temporal limitation (e.g. `< 30 days`, `within 12 months prior`, `> 8 years old`). Empty if none.
  - `OriginalText` — the source phrase with leading bullet markers (`*`, `-`, `•`, `·`, `◦`) and surrounding whitespace stripped. Single contiguous span only — never concatenate sentences.

- **Extraction guidelines** (MUST appear in prompt):
  1. Prefer over-segmentation: extract every distinct, clinically meaningful criterion. There is **no maximum** number of entries per trial — never drop a genuine criterion to fit a count; still avoid redundant or low-yield items. (Historical note: the original n8n workflow capped output at 25 entries per trial; that cap was removed.)
  2. Skip non-clinical context (geographic, administrative, recruitment-setting statements).
  3. Each entry must describe a clinically meaningful inclusion or exclusion condition.
  4. Each `(Criterion, Concept)` pair must be UNIQUE within the output for the trial.
  5. If about to emit a structurally identical entry to one already emitted (same `Concept` and `Criterion`), STOP — do not emit it.

- **Output rules** (MUST appear in prompt): no fences, no preamble, no commentary, no trailing explanation; response starts with `[` and ends with `]`; character immediately after the closing `]` MUST be end of response; target ≤1500 output tokens for typical trials.

- The prompt MUST include at least one worked example showing a 3-row output for a real-looking trial.

#### 2.4.3 Prompt — user message

The user message MUST be:

```
NCT_ID: <trial identifier>
Criteria:
<raw criteria text from source>
```

#### 2.4.4 Retry and failure behaviour

- **Per-call retries**: up to 2 attempts with a 5-second delay between attempts.
- **On terminal LLM failure**: emit a side-channel error notification (see §2.10), continue the batch — a single failed trial MUST NOT abort the batch. The failed trial's downstream processing produces no output rows.

#### 2.4.5 Concurrency

- The system MUST support concurrent LLM calls. Production reference: 8 in-flight calls, served by two llama.cpp backends behind an nginx load balancer using the `random two least_conn` algorithm.
- The concurrency cap MUST be configurable per run; it MUST NOT exceed the aggregate slot count of the underlying model server pool (`per-server-parallel × server-count`).
- The system MUST batch LLM calls; recommended batch size at the orchestration layer: `min(16, StudyCount)`.

### 2.5 LLM output parsing

The raw LLM response MUST be parsed defensively. The parser MUST:

1. Extract the response text from whichever of these fields is populated: `text`, `output`, `message`, `content`.
2. Skip rows where the response is null/undefined/empty (failed calls).
3. Strip an opening ```` ```json ```` or ```` ``` ```` fence and a trailing ```` ``` ```` fence if present.
4. Locate the first `[` or `{` and discard everything before it (handles models that emit a preamble despite the prompt).
5. Attempt to `JSON.parse` (or language-equivalent). On parse failure, the row is silently dropped (the next batch will not re-attempt — see §6 for the resume contract).
6. If the parsed result is an array, fan out one downstream record per element. If it is a single object, emit that one record.
7. For each record, strip leading bullet markers and surrounding whitespace from `OriginalText` using the regex equivalent of `/^\s*[*\-•·◦]\s*/`.
8. **Pairing**: preserve the linkage from each emitted record back to its source trial item (see §2.7 on pairing semantics). *(.NET deviation: the `NCT_ID` is stamped from the batch-supplied trial identifier — which is authoritative — rather than the model's echoed value, so a transposed/typo'd id from the LLM cannot persist a row under the wrong trial. The model's value is used only as a fallback when no trial id is supplied.)*
9. **Empty-output safety net**: if zero records survive across the whole batch, emit one placeholder record with all fields empty so the downstream pipeline's per-item topology does not collapse. The placeholder MUST be skipped at the persistence stage because its `NCT_ID` is empty.

### 2.6 UMLS concept resolution

For each extracted criterion record, the system MUST attempt to assign a UMLS Concept Unique Identifier (CUI) and supporting metadata.

#### 2.6.1 UMLS search

- Endpoint: UMLS UTS REST API `search/current`
- Query params: `string=<Concept>`, `searchType=words`, `returnIdType=concept`, `pageSize=5`, `apiKey=<secret>`
- Format: JSON response
- Timeout: 10 seconds
- On error: the call MUST continue (return empty results), NOT fail the pipeline.

#### 2.6.2 Best-match scoring

From the up to 5 candidates returned, select a single best match using a composite score that takes the MAXIMUM of three independent signals:

**Signal 1 — Levenshtein similarity**
```
levSim(a, b) = 1 - (editDistance(a, b) / max(len(a), len(b)))
```
Both strings lower-cased and trimmed before comparison. Returns 1.0 for identical strings, 0.0 for fully disjoint.

**Signal 2 — Jaccard containment**
```
tokens(s) = lowercase words from s, split on non-word chars, dropping:
            - tokens shorter than 2 chars
            - the stopword set {the, a, an, of, for, and, or, to, in, on,
                                with, by, at, from, is, are, as}

jaccardContainment(a, b) = |tokens(a) ∩ tokens(b)| / min(|tokens(a)|, |tokens(b)|)
```
Returns 0 if either token set is empty. Containment (divide by `min`) is used rather than full Jaccard so a short query fully contained in a long candidate scores 1.0.

**Signal 3 — Acronym bonus**
If the raw query (case preserved) matches `/^[A-Z0-9]{2,6}$/` (likely an acronym), and the candidate name contains the query as a whole word (case-insensitive, word boundaries), award a base value of 0.5; otherwise 0. The acronym signal is then combined with 30% of the Levenshtein signal:
```
acronymContribution = acrBase + 0.3 × levSim
```

**Composite**
```
score = max(levSim, jaccardContainment, acronymContribution)
```

**Decision threshold**: a candidate is accepted only if its score ≥ **0.45**. Below threshold, the record is treated as unresolved (no CUI, empty UMLS fields). The score MUST be persisted, rounded to 3 decimal places.

Captured fields from the best match: `ConceptCode` (CUI / `ui` field), `UmlsName` (`name`), `MatchSource` (`rootSource`), `MatchScore`.

#### 2.6.3 Semantic-type fetch (resolved records only)

For records with a non-empty `ConceptCode`, fetch the semantic type list:

- Endpoint: `content/current/CUI/<ConceptCode>`
- Query params: `apiKey=<secret>`
- Format: JSON response
- On error: continue without semantic type; do not fail the record.

`SemanticType` is populated as a comma-separated string of `semanticTypes[].name`. Records without a resolved CUI MUST receive an empty `SemanticType`.

#### 2.6.4 Branching topology

The pipeline MUST fork after best-match selection on `ConceptCode != ''`:

- **Resolved branch**: fetch semantic type, then merge.
- **Unresolved branch**: skip semantic-type fetch, merge directly.

Both branches MUST converge into a single ordered stream before persistence. Failing to merge the two branches will silently drop unresolved records — this is a known failure mode and the merge step is mandatory.

### 2.7 Item pairing contract

The pipeline expands and contracts at multiple stages (one trial → many criteria → many UMLS lookups → one merged stream → one persistence step per trial). To preserve correctness:

- Each extracted criterion record MUST carry a back-pointer to the source trial item from which it was derived. In the n8n implementation this is the `pairedItem` field; equivalent mechanics in other engines (correlation id, parent key, etc.) MUST be used.
- The "Pick Best Match" stage retrieves the original criterion fields by looking up the **upstream parsed record by paired index**, not by trusting the UMLS response payload.
- The semantic-type extraction stage carries forward the merged record from "Pick Best Match", not from the UMLS response.
- Persistence groups records by `NCT_ID` and emits one transaction per trial (see §2.8).

Implementations on engines that do not natively provide a pairedItem-equivalent MUST attach an explicit correlation id at the LLM-output expansion stage and carry it end-to-end.

### 2.8 Persistence

#### 2.8.1 Output record schema

Each output row MUST contain exactly the following columns, in this canonical order, all string-typed in the staging interface even where logically numeric (the persistence layer is responsible for any final type coercion):

| Column | Source | Notes |
|---|---|---|
| `NCT_ID` | LLM | Trial identifier |
| `Criterion` | LLM | `Inclusion` or `Exclusion` |
| `Domain` | LLM | Closed taxonomy (24 values + `Other`) |
| `Concept` | LLM | Canonical clinical entity, 1–5 words |
| `ConceptCode` | UMLS resolution | CUI; empty when unresolved |
| `SemanticType` | UMLS semantic-type fetch | Comma-separated; empty when unresolved or fetch failed |
| `Qualifier` | LLM | Short clinical-state modifier |
| `TimeWindow` | LLM | Temporal restriction |
| `OriginalText` | LLM | Verbatim source span, bullet-stripped |
| `UmlsName` | UMLS resolution | Preferred name of matched concept; empty when unresolved |
| `MatchScore` | Best-match scoring | Numeric in [0, 1], 3 d.p.; 0 when unresolved |
| `MatchSource` | UMLS resolution | Root source vocabulary; empty when unresolved |

#### 2.8.2 Write semantics

For each trial in the batch (identified by non-empty `NCT_ID`), persistence MUST:

1. Open a transaction.
2. `DELETE` all existing rows for that `NCT_ID` in the output table.
3. `INSERT` all newly extracted rows for that `NCT_ID`.
4. Commit.

This DELETE+INSERT-per-trial-in-its-own-transaction pattern is mandatory:

- It makes the system **trial-idempotent** — re-running a trial replaces its output cleanly.
- It bounds blast radius — a SQL failure on one trial does not corrupt others in the batch.
- It plays correctly with the watermark scheme — once trial N is committed, the watermark naturally advances on the next run.

String escaping for SQL values MUST handle embedded single quotes by doubling (`'` → `''`); NULL handling MUST map `null`/`undefined` to the unquoted SQL keyword `NULL`.

Records with an empty or null `NCT_ID` MUST be filtered out before any DELETE is emitted — the placeholder safety-net record from §2.5 step 9 is silently dropped here.

### 2.9 Runtime metrics

The system MUST capture and report, at minimum:

- Wall-clock runtime of the batch (`Date.now() - StartedAt` style)
- Studies successfully persisted
- Total criteria rows persisted
- Average criteria per study
- Average wall clock per study

These metrics MUST be available for inclusion in the completion notification (§2.10) and SHOULD also be exposed via a runtime telemetry channel (logs / observability backend).

### 2.10 Notifications

#### 2.10.1 Completion notification

On successful batch completion, the system MUST emit a notification containing:

- Status: `success`
- Studies processed (count)
- Total criteria (sum of row_count across all trials)
- Avg criteria/study (1 d.p.)
- Workflow runtime (formatted as `Xm Ys` or `Ys`)
- Avg runtime/study (formatted as `X.Xs`)
- A link to re-trigger the workflow (the form endpoint)

The reference implementation sends this via email; the channel is configurable (email, Slack, webhook, message queue, etc.).

The completion notification MUST be emitted **once per batch run**, not per item or per trial.

#### 2.10.2 Error notification

On LLM call failure (after retries exhausted), the system MUST emit an error notification once per batch run, indicating that a step failed in the execution. The notification does NOT need to enumerate every failure; one alert per batch is sufficient to prompt operator review.

The reference implementation routes the error notification through the same email channel.

### 2.11 Run control

The system MUST support a graceful stop capability for long production runs:

- An external control channel (e.g. HTTP endpoint or dashboard button) MUST be able to signal cancellation of the current run.
- Cancellation MUST propagate cooperatively through whatever execution model the implementation uses (per-trial cancellation token, message queue, etc.) — in-flight LLM/UMLS calls are allowed to complete or fail.
- Trials still in flight at cancellation time MUST record a terminal audit state distinguishable from success / failure (see §2.12).
- Resumption is automatic on next invocation due to the watermark contract (§2.2) — trials that committed before cancellation stay committed; those that didn't will be re-picked-up.

The reference n8n workflow defined this contract but did not confirm implementation; the .NET re-implementation provides it via a per-run cancellation token signalled by a dashboard Cancel button.

### 2.12 Per-trial audit (re-implementation enhancement)

The system MUST record an audit row for every trial it processes, capturing the trial's full lifecycle through the pipeline. This is a re-implementation enhancement on top of the reference n8n workflow, which inferred "did this trial get processed?" from the absence of output rows — an unreliable signal that confuses "not picked up yet" with "picked up but produced nothing".

The audit record MUST be:

1. **Inserted before the LLM call** with status indicating in-flight processing.
2. **Updated to its terminal state** when the trial completes (success or any failure mode).
3. **Keyed by (run_id, nct_id)** so the same trial can be re-processed across runs without overwriting prior history.

Each audit row MUST carry at minimum:

- `run_id`, `nct_id` — primary key.
- `started_at`, `finished_at` — wall-clock bounds; `finished_at` is null for in-flight rows.
- `status` — terminal state from a fixed enum (see below).
- LLM diagnostics — succeeded/failed flag, finish reason, prompt + completion token counts.
- Counters — parsed record count, persisted row count.
- `error_message` when applicable.

**Status enum** (text in the column, the implementation MAY use enum constants):

| Status | Meaning |
|---|---|
| `running` | Audit row created; trial in flight; finished_at null. |
| `success` | LLM call succeeded, parser emitted ≥1 record, persistence succeeded. |
| `parse_empty` | LLM call succeeded, parser returned valid JSON (e.g. `[]`), zero records emitted. The trial still passes through (DELETE+empty INSERT). Distinct from `parse_invalid_json`. |
| `parse_invalid_json` | LLM call succeeded but the response could not be parsed — most commonly truncation by the `max_tokens` cap. The `error_message` SHOULD include enough context (e.g. finish_reason, completion_tokens) to diagnose the root cause. |
| `llm_failed` | Terminal LLM transport/protocol failure after retries. |
| `persist_failed` | Persistence raised after parse succeeded. |
| `failed` | Generic post-LLM, pre-persist failure (UMLS, scoring, etc.). |
| `cancelled` | User cancellation interrupted the trial mid-flight (see §2.11). |

Implementations MUST treat audit writes as **best-effort** — an audit-write failure MUST NOT abort the trial; log it and continue. Audit writes do not need to be transactional with the persistence write.

The audit table is what makes "this trial produced no rows, why?" answerable in one query rather than by inferring from the output table's absence.

### 2.13 Per-trial study snapshot (re-implementation enhancement)

The system SHOULD capture, for every trial it processes, a **snapshot of the source study metadata and structured eligibility detail** into the output store. This is a re-implementation enhancement on top of the reference n8n workflow, which read those fields live from the source database every time an operator viewed a trial — coupling the operator UI's availability to the source database's.

The snapshot record MUST be:

1. **Keyed by `nct_id` alone** — one row per trial, not per run. Unlike the audit record (§2.12), study metadata is a property of the trial, not of a processing attempt.
2. **Refreshed on every run** that processes the trial (UPSERT), so a re-run picks up any changes in the source database.
3. Treated as **best-effort** — a snapshot-capture failure MUST NOT abort the trial; log it and continue. Capture is independent of the trial's processing outcome, so even a trial that later fails LLM extraction or parsing still carries a snapshot.

Each snapshot row carries the fields a single-trial detail view needs:

- **Study card** — brief and official title, overall status, phase, study type, start / completion / primary-completion dates, enrollment count and type, lead sponsor, why-stopped, brief summary, the list of conditions, and the list of interventions (type + name).
- **Eligibility detail** — the raw free-text criteria, gender, minimum / maximum age, healthy-volunteers flag, sampling method, population, and the adult / child / older-adult age-group flags.
- **`captured_at`** — when the snapshot was last refreshed.

Consumers of the snapshot (e.g. a per-trial detail view) SHOULD prefer the snapshot and fall back to a live source-database read only when no snapshot exists for the trial. Trials processed before the snapshot store existed MAY be backfilled by an out-of-band pass that reads the source database for every already-processed `nct_id`.

---

## 3. External Interfaces

### 3.1 Input — source database

- **Connection**: read-only credentials to the source clinical trials database.
- **Table**: `ctgov.eligibilities` (AACT schema convention) with at minimum the columns `nct_id` (text) and `criteria` (text).
- **Volume reference**: AACT contains hundreds of thousands of trials; per-trial criteria text ranges from ~50 characters to ~30 KB.

### 3.2 Output — structured store

- **Connection**: read/write credentials to a relational store of equivalent capability to PostgreSQL.
- **Table**: `public.eligibility` with the columns enumerated in §2.8.1.
- **Read access required by the system**: it queries `MAX(NCT_ID)` from this table to derive the watermark (§2.2).

### 3.3 Output — watermark store

- **Engine**: any durable key-value store with at-least-once write semantics is acceptable; the reference implementation uses an internal "datastore" keyed by `LastStudyUpdated`.
- **Required operations**: get-current, set-current.
- **Failure mode**: if the watermark store is unavailable at the start of a run, the system MUST abort the run with a clear error — running without the watermark would reprocess from `NCT00000000` and corrupt the output table.

### 3.4 External — UMLS UTS REST API

- **Base URL**: `https://uts-ws.nlm.nih.gov/rest/`
- **Endpoints used**:
  - `GET /search/current` — concept search.
  - `GET /content/current/CUI/{cui}` — semantic type retrieval.
- **Auth**: API key, supplied as a query parameter (`apiKey`). The key MUST be sourced from secret storage (environment variable, secrets manager); it MUST NOT be embedded in code, config files committed to source control, or logs.
- **Rate-limit posture**: UMLS does not publish hard rate limits but throttles abusive clients. The reference workload sustains ~25 lookups/second without observed throttling.

### 3.5 External — LLM inference service

- **Protocol**: OpenAI-compatible Chat Completions (`POST /v1/chat/completions`) preferred; native model API acceptable if the orchestration layer adapts.
- **Auth**: API key (passed through transparently by the load balancer even when not validated by llama.cpp).
- **Resilience**: the LLM endpoint MUST be replaceable behind a load balancer without changes to the orchestration layer (see `Eligibility_LoadBalancer_Setup.md` for the reference deployment).

### 3.6 Triggers — invocation surface

- **HTTP form**: GET-equivalent that renders a single-field form (`StudyCount`, integer, required, default 8) and POSTs back to fire the workflow.
- **HTTP webhook**: hard-coded to launch with `StudyCount = 500`. Reserved for scheduled / automated production runs.
- **In-process / sub-workflow call**: programmatic invocation with `StudyCount: number` as the sole input.

### 3.7 Notifications

- **Channel**: email or equivalent messaging. SMTP / API-based mail providers both acceptable.
- **Destinations**: configurable per environment.

---

## 4. Data Model

### 4.1 Source record (read-only)

```
ctgov.eligibilities {
  nct_id    : text  (PK in source; format NCTxxxxxxxx, 11 chars)
  criteria  : text  (raw eligibility text, mixed inclusion/exclusion sections)
}
```

### 4.2 Intermediate record (LLM output, post-parse)

```
{
  NCT_ID       : string
  Criterion    : enum { "Inclusion", "Exclusion" }
  Domain       : enum (closed list of 25 values, see §2.4.2)
  Concept      : string  (1-5 words, canonical)
  Qualifier    : string  (0-3 words, or empty)
  TimeWindow   : string  (or empty)
  OriginalText : string  (verbatim, bullet-stripped)
}
```

### 4.3 Resolved record (post-UMLS)

Adds:

```
{
  ConceptCode  : string  (UMLS CUI, e.g. "C0011860"; empty if unresolved)
  UmlsName     : string  (UMLS preferred name; empty if unresolved)
  MatchScore   : number  (0.000 – 1.000, 3 d.p.; 0 if unresolved)
  MatchSource  : string  (root source vocabulary e.g. "MSH", "SNOMEDCT_US"; empty if unresolved)
  SemanticType : string  (comma-separated names; empty if unresolved or fetch failed)
}
```

### 4.4 Persisted record (output table)

```
public.eligibility {
  NCT_ID       : text
  Criterion    : text
  Domain       : text
  Concept      : text
  ConceptCode  : text
  SemanticType : text
  Qualifier    : text
  TimeWindow   : text
  OriginalText : text
  UmlsName     : text
  MatchScore   : numeric  (or text if persistence is text-typed end-to-end)
  MatchSource  : text
}
```

Note: the production schema currently has no primary key, no indexes on `NCT_ID`, no foreign key to the source. A re-implementation SHOULD add at minimum:

- An index on `NCT_ID` (drives both the DELETE in §2.8.2 and the watermark MAX query).
- A surrogate primary key (uuid or bigserial) for downstream reference.
- A `created_at` timestamp.

### 4.5 Watermark record

```
{
  key   : "LastStudyUpdated"
  value : text  (NCT_ID format, default "NCT00000000")
}
```

---

## 5. Pipeline — End-to-End Sequence

The system MUST execute the following logical stages, in order, for each batch invocation. Stage names below are logical; physical realisation may collapse or split stages provided the contract is preserved.

1. **Trigger normalisation** — accept input from one of three trigger types; coerce to `{StudyCount: integer, StartedAt: epoch_ms}`. Default StudyCount: 10. Webhook overrides StudyCount to 500.

2. **Watermark read** — query `SELECT COALESCE(MAX(NCT_ID), 'NCT00000000')` from the output store.

3. **Watermark write** — store the value retrieved in step 2 to the watermark store under key `LastStudyUpdated`, applying the empty/undefined/null normalisation from §2.2.

4. **Trial selection** — query the source store for the next `StudyCount` trials per §2.3.

5. **LLM call (per-trial, parallel)** — submit each trial's `(nct_id, criteria)` pair to the LLM with the system prompt of §2.4.2. Concurrency-capped, with retries and continue-on-error semantics. Output: one raw LLM response per trial.

6. **Response parse and expand (per-trial → per-criterion)** — apply the parsing rules of §2.5. Each trial expands to 0..25 criterion records. Preserve back-pointer to source trial.

7. **UMLS concept search (per-criterion)** — call UMLS search per §2.6.1. Continue on error.

8. **Best-match scoring (per-criterion)** — apply composite scoring of §2.6.2 against the up-to-5 candidates; accept above threshold.

9. **Branch on resolution status** — split into "has ConceptCode" vs "no ConceptCode" streams.

10. **Semantic-type fetch (resolved only)** — call UMLS CUI content endpoint per §2.6.3.

11. **Semantic-type extraction (resolved only)** — extract and concatenate semantic type names.

12. **Merge** — converge resolved and unresolved streams into a single ordered stream of fully-populated records.

13. **SQL generation (per-trial)** — group records by `NCT_ID`, emit one transactional `BEGIN; DELETE … ; INSERT … ; COMMIT;` block per trial. Empty `NCT_ID` records (the placeholder safety-net) are filtered here.

14. **Persistence (per-trial)** — execute each generated SQL block against the output store. Failures here do not roll back other trials.

15. **Completion notification** — emit once per batch with metrics per §2.9.

16. **Error notification** — emit once per batch if any LLM call failed terminally.

A control flow diagram of the same pipeline:

```
   Triggers (Form | Webhook | SubFlow)
                  │
                  ▼
            Normalise input → {StudyCount, StartedAt}
                  │
                  ▼
            Read watermark from output store
                  │
                  ▼
            Write watermark to watermark store
                  │
                  ▼
            Select next N trials from source
                  │
                  ▼
            ┌──── For each trial (concurrent) ────┐
            │                                     │
            │   LLM call → raw response           │
            │                                     │
            └─────────────────────────────────────┘
                  │
                  ▼
            Parse + expand to criterion records
                  │
                  ▼
            ┌──── For each criterion (concurrent) ┐
            │                                     │
            │   UMLS search                       │
            │       │                             │
            │       ▼                             │
            │   Best-match scoring                │
            │       │                             │
            │       ▼                             │
            │   IF ConceptCode? ──── yes ────┐    │
            │       │                        ▼    │
            │       no              Fetch semantic│
            │       │                        │    │
            │       │                        ▼    │
            │       │              Extract names  │
            │       │                        │    │
            │       └────────► Merge ◄───────┘    │
            └─────────────────────────────────────┘
                  │
                  ▼
            Group by NCT_ID → emit per-trial SQL
                  │
                  ▼
            Execute SQL (per-trial transactions)
                  │
                  ▼
            Send completion notification
```

---

## 6. Operational Contracts

### 6.1 Idempotency

- A batch is **trial-idempotent**: re-running a trial replaces its persisted output entirely (§2.8.2).
- A batch is **batch-idempotent against crashes**: re-running after a crash advances the watermark from whatever was last successfully persisted (§2.2).
- A batch is NOT idempotent against UMLS API non-determinism: the same `Concept` queried at different times MAY produce different scores if UMLS data has been updated. This is acceptable; clinical concepts rarely change.

### 6.2 Resume semantics

- After a crash mid-batch, all trials persisted before the crash remain in the output store with full data.
- The next run will detect the new MAX(NCT_ID), set the watermark, and resume with the next strictly-greater trial.
- Trials that failed LLM extraction in the previous run will NOT be retried unless their NCT_ID is manually removed from the output store. **This is the system's primary correctness gap and a re-implementation SHOULD provide a "force re-process" mode keyed by a list of NCT_IDs.**

### 6.3 Performance reference (Run 75)

The current production benchmark to validate against:

| Metric | Value |
|---|---:|
| Studies processed | 50 |
| Rows persisted | 374 |
| UMLS resolution rate | ~88% (330/374) |
| Wall clock | ~11 minutes |
| Avg criteria per study | ~7.5 |
| Avg per-study wall clock | ~13 seconds |
| Concurrency | 8 in-flight |
| Backends | 2 × llama.cpp servers (parallel=4 each) |

Production target: **500–1000 studies per run**. A re-implementation MUST scale to this range without degradation in resolution rate.

### 6.4 Failure modes and required tolerance

| Failure | Required behaviour |
|---|---|
| LLM timeout / 5xx | Retry up to 2 times with 5s delay; on terminal failure, drop trial, continue batch, emit error notification |
| LLM returns malformed JSON | Drop trial silently (no rows persisted in `public.eligibility`); watermark advances naturally next run only after surrounding trials commit. **Re-implementations SHOULD record this in the per-trial audit (§2.12) distinctly from "LLM returned []" so operators can tell truncation from legitimate empty extraction.** **Gap**: this trial will not be retried — see §6.2. |
| UMLS search 5xx/timeout | Treat as no results; criterion persists with empty UMLS fields |
| UMLS semantic-type 5xx/timeout | Persist record with empty SemanticType |
| Source DB unreachable | Fail run cleanly; no partial writes |
| Output DB unreachable | Fail run cleanly; no notification beyond standard observability |
| Watermark store unreachable | Abort run with explicit error (see §3.3) |
| Empty selection (no trials match) | Complete successfully with zero rows; emit completion notification |
| Single trial with many criteria | LLM extracts every distinct criterion — no fixed entry cap (output bounded only by the model's token budget) |
| Single trial returns `[]` (e.g. "Please contact site for information") | Parser emits zero records for that trial; safety-net placeholder ensures topology survives; persistence filters out the placeholder |

### 6.5 Secrets handling

- **UMLS_API_KEY** — required at runtime, sourced from environment / secret store.
- **LLM endpoint credentials** — API key passed to the load balancer; the LB forwards transparently.
- **Email/notification credentials** — sourced from environment / secret store.
- **Database credentials** (source DB, output DB, watermark store) — sourced from environment / secret store.

Secrets MUST NOT appear in logs. The full LLM prompt and response MAY be logged at DEBUG; the API key in the URL MUST be redacted before logging.

### 6.6 Observability requirements

A production-grade implementation MUST expose:

- Per-batch metrics: trials processed, rows persisted, wall clock, LLM call count, LLM retry count, UMLS calls, UMLS resolution rate.
- Per-stage timings: input read, LLM end-to-end, UMLS end-to-end, persistence.
- Error stream: terminal LLM failures, UMLS failures, persistence failures, parse failures, with NCT_ID where available.
- A historical run table (run_id, started_at, ended_at, studies, rows, resolution_rate, failed) for trend analysis. The reference implementation tracks this informally as "Run 75 et al." — a re-implementation SHOULD persist it.

### 6.7 Configuration surface

The following MUST be configurable at deploy time without code changes:

- `StudyCount` defaults (per trigger type)
- LLM endpoint URL
- LLM model name
- LLM temperature, max tokens, timeout
- LLM concurrency cap
- LLM retry count and delay
- UMLS endpoint base URL
- UMLS match threshold (currently 0.45)
- UMLS search pageSize (currently 5)
- LLM prompt entry cap (currently 25)
- Notification destination(s)
- Database connection strings

---

## 7. Cross-Cutting Concerns

### 7.1 Concurrency model

The pipeline has two natural parallelism axes:

1. **Trial-level parallelism** — N trials processed simultaneously through the LLM stage.
2. **Criterion-level parallelism** — for any given trial, all its M criteria can hit UMLS in parallel.

A re-implementation MAY collapse these into a single worker pool, OR maintain them as separate stages with their own concurrency caps. The reference implementation uses a single n8n-level concurrency setting that gates the LLM stage; downstream stages (UMLS, persistence) inherit ordering implicitly via the engine's per-item execution model.

Recommended limits for a re-implementation:

- LLM concurrency: ≤ aggregate slot count of model server pool.
- UMLS concurrency: ≤ 25/sec sustained, with burst tolerance.
- Persistence concurrency: serialised per-NCT_ID (a given trial's DELETE+INSERT must not race against itself); parallel across distinct NCT_IDs is safe.

### 7.2 Determinism

- LLM output is non-deterministic even at temperature 0.3. Re-running a trial WILL produce slightly different extractions. The system accepts this and overwrites prior output on re-process (§2.8.2).
- UMLS matching is deterministic for a given UMLS data version and a given LLM-produced `Concept` string.

### 7.3 Data quality contract

The system makes no guarantees about correctness of extracted criteria — only structural compliance with §2.4.2. Downstream consumers MUST treat the output as model-extracted and validate before using in clinical decision contexts. A `MatchScore < 0.45` is the system's own admission of low confidence; a `MatchScore >= 0.45` is moderate confidence, not certainty.

### 7.4 Backpressure

The system has no explicit backpressure mechanism. The LLM concurrency cap implicitly bounds memory; the per-trial transactional persistence implicitly bounds DB write pressure. A re-implementation processing batches in the thousand-trial range SHOULD add explicit backpressure between the LLM stage and the UMLS stage to avoid memory accumulation of in-flight criterion records.

### 7.5 Timezones and timestamps

- All timestamps in metrics are derived from epoch milliseconds; timezone is not encoded.
- A re-implementation persisting `created_at` MUST use UTC ISO-8601 with timezone marker.

---

## 8. Acceptance Criteria for a Re-Implementation

A re-implemented system MUST pass the following functional and non-functional checks to be considered behaviourally equivalent:

**Functional**

1. Given the same source trial input and an idle output store, the re-implementation produces a row count within ±15% of the reference for a 50-study sample (LLM stochasticity accounts for the band).
2. UMLS resolution rate is within ±3 percentage points of the reference (88% ± 3pp).
3. Schema of output rows matches §2.8.1 exactly (columns, order, types).
4. All three trigger modes (form, webhook, sub-workflow call) invoke the same pipeline with the same result.
5. A crash injected after persisting the first 5 trials of a 10-study run, followed by re-invocation, produces all 10 trials persisted with no duplicates.
6. A trial whose criteria text contains only "Please contact site for information" produces zero rows and does not break the batch.
7. A trial with many distinct criteria produces one row per distinct criterion, with no fixed upper bound (output limited only by the LLM token budget).
8. A `MatchScore` of exactly 0 is reserved for unresolved records; resolved records have a `MatchScore >= 0.45`.
9. Re-running a trial that was previously persisted with 8 rows, and now extracts 6, results in exactly 6 rows for that NCT_ID in the output table (DELETE+INSERT semantics, not append).

**Non-functional**

10. 50-study run completes in ≤ 15 minutes on equivalent hardware (reference: ~11 min).
11. 500-study run completes without OOM, watermark drift, or DB lock contention.
12. UMLS API key, DB credentials, and SMTP credentials are NOT present in any logged output.
13. Killing the worker process mid-batch leaves the output store in a consistent state (no half-written trials).

---

## 9. Known Issues / Gaps Carried Forward From Reference

These are issues observed in the reference implementation that a re-implementation SHOULD explicitly resolve:

1. **No retry of trials whose LLM call ultimately failed** — the watermark advances around them and they are never revisited. Recommended: a `eligibility_failed` table with `(nct_id, last_attempted_at, attempt_count, last_error)` and a separate retry mode. *Addressed in the .NET re-implementation at the audit level (eligibility_failed + eligibility_study); per-NCT-ID retry trigger is deferred at the UI level.*
2. **No primary key or `created_at` on the output table** — limits downstream change tracking. *Addressed: V1 schema adds bigserial PK + created_at timestamptz.*
3. **MatchScore typed as string in transit** — currently coerced via SQL. Re-implementations SHOULD type it as numeric end-to-end. *Addressed: `numeric(4,3)` end-to-end.*
4. **Watermark monotonicity assumes NCT_IDs are issued in ascending order** — this is true for ClinicalTrials.gov today but not guaranteed for synthetic test data. A re-implementation MAY also persist a "processed_set" for explicit membership tests. *Partially addressed: the §2.12 audit table provides per-run membership history, but the watermark contract itself still relies on monotonicity.*
5. **Single-tenant by design** — no notion of `run_id`, `tenant_id`, or environment partitioning in the output table. Multi-tenant deployments will need schema extension. *Run-level partitioning is provided by `eligibility_run` and `eligibility_study`; tenant-level remains future work.*
6. **The UMLS match threshold (0.45) is a single global value** — domain-specific thresholds (e.g. lower for Disease, higher for Drug Treatment) may produce better resolution rates.
7. **No caching of UMLS lookups** — the same `Concept` (e.g. "Pregnancy") is re-queried every time it appears. A re-implementation SHOULD cache by lower-cased `Concept` for the duration of a run, at minimum. *Addressed: per-run `UmlsCache` decorator.*
8. **Error notification is binary** — "something failed". No detail, no NCT_ID list. A re-implementation SHOULD attach a structured error summary. *Addressed: `BatchResult.FailedNctIds` and structured SMTP body.*
9. **Diagnostic blindness for trials that produce zero output rows** — the reference workflow loses the distinction between "not picked up yet", "LLM returned []", "LLM truncated", "parser dropped malformed JSON", and "persistence failed". All present as absence in the output table. *Addressed in §2.12: per-trial audit table with stage-specific status enum.*

---

## 10. Glossary

| Term | Definition |
|---|---|
| **AACT** | Aggregate Analysis of ClinicalTrials.gov — a relational mirror of ClinicalTrials.gov maintained by Duke. The source of raw trial data. |
| **CUI** | Concept Unique Identifier — UMLS's stable identifier for a clinical concept, format `C` followed by 7 digits (e.g. `C0011860`). |
| **NCT_ID** | ClinicalTrials.gov's per-trial identifier, format `NCT` followed by 8 digits. |
| **UMLS** | Unified Medical Language System — the NLM's metathesaurus of clinical vocabularies. |
| **UTS** | UMLS Terminology Services — the REST API endpoint for UMLS lookups. |
| **Criterion** | One eligibility statement, either inclusion or exclusion, extracted from a trial. |
| **Concept** | The canonical clinical entity name for a criterion, model-extracted. |
| **Domain** | The taxonomic class of a criterion (closed set of 25 values). |
| **Watermark** | The highest NCT_ID already processed; used to gate the next batch. |
| **Batch / Run** | A single invocation of the pipeline, processing up to `StudyCount` trials. |
| **Trial-idempotent** | Re-processing a trial produces a clean overwrite of its prior rows, not duplicates. |

---

## 11. Access Control & Auditing (Web Application)

This section is **additive** to the original n8n-derived pipeline contract: it specifies the access-control and auditing requirements for the web application (dashboard, Authoring area, and management UI). It is technology-independent; section 2.10 of the architecture document describes the .NET realisation. It does **not** change any extraction-pipeline behaviour above. (Numbered after the glossary deliberately, so the existing `§`-references in source comments stay stable.)

### 11.1 Authentication

- Every web page and every state-changing action MUST require an authenticated user. Two machine-to-machine exceptions remain unauthenticated: the liveness probe and the webhook trigger (the latter retains its own shared-secret gate, §3 / §2.1).
- A user MUST be able to sign in with **either** a user ID + password **or** a linked external identity provider (Google), for user convenience. The two are linked on a single account by **email**: a Google sign-in is matched to an existing account whose email matches.
- Credentials and the user↔role mapping MUST be stored in the application database, not in configuration.
- Passwords MUST be stored only as a salted one-way hash, never in plaintext or reversibly encrypted.
- **First-run bootstrap**: when no user accounts exist, the first sign-in attempt MUST collect initial credentials (user ID + password only — not an external provider) and create that account with the **Owner** role. Once any account exists, the bootstrap path MUST be closed (re-checked at the moment of account creation, not only on display).

### 11.2 Roles & permissions

Each user is assigned exactly **one** role:

| Role | Permissions |
|---|---|
| **Owner** | All permissions. |
| **Administrator** | All permissions. |
| **Author** | Full read **and write** access to the Authoring area; **read-only** access to everything else. |
| **Viewer** | **Read-only** access to everything. |

- Owner and Administrator are equivalent in permission. The Owner role is **protected**: the system MUST NOT allow the last Owner to be deleted or demoted, and only an Owner may change or delete another Owner's account.
- "Read-only" means a role MUST NOT be able to invoke create / update / delete actions outside its allowed scope (server-enforced). Hiding controls in the UI is a convenience, not the boundary.
- Author MUST NOT be able to run pipeline operations (trigger / re-run / cancel / delete history); those are Owner/Administrator only.

### 11.3 Account management

- An administrator (Owner/Administrator) MUST be able to list, add, change the role of, and delete user accounts, subject to the protected-Owner rule (§11.2).
- The signed-in user's identity (profile image for external accounts, or user ID for password accounts) and their role MUST be shown in the application; account management MUST be reachable from there for administrators.

### 11.4 Unknown external identity

- A Google sign-in whose email does not match an existing **active** account MUST be refused entry, and the user MUST be told that access requires an administrator to create an account.
- On such a refusal, the system MUST notify administrators (reusing the existing notification channel, §2.10 / §3) with the would-be user's name and email so an account can be created later.

### 11.5 Auditing

- The authored-study and authored eligibility-criteria records MUST record the user who **created** the record and the user who **last updated** it.
  - The creator of a new authored study MUST be recorded on that study; the last updater MUST be recorded on update.
  - The user who added each normalised or manual authored eligibility-criterion entry MUST be recorded on that record; the user who updates a criterion MUST be recorded on update. (This requires that updating the criteria list preserve each surviving row's creator/created-time rather than recreating rows.)
- An append-only **audit log** MUST record, at minimum, the date/time, the acting user, the action taken, and a reference to the affected record, for: every manual create / update / delete of a record, and every login. The log need not be surfaced in the UI.

---

*End of specification.*

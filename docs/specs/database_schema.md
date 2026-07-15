# Database Schema

Detailed reference for the **output** PostgreSQL database — the store the
pipeline and the Authoring feature own and write to. The **source** AACT
database (`ctgov.*`) is external and read-only; the tables this system reads
from it are summarised at the end.

> **Keep this current.** This document is hand-maintained and must be updated in
> the same change as any schema migration. See
> [How the schema is managed](#how-the-schema-is-managed).

## How the schema is managed

- Schema lives as ordered, idempotent SQL migrations under
  [`src/EligibilityProcessing.Data/Migrations/`](../../src/EligibilityProcessing.Data/Migrations),
  embedded as resources and registered in `PostgresGateway.MigrationResourceNames`.
- `PostgresGateway.EnsureSchemaAsync` applies every migration in declaration
  order on startup and via the CLI `migrate` command. Each migration is
  idempotent (`CREATE … IF NOT EXISTS`, `ALTER … ADD COLUMN IF NOT EXISTS`,
  guarded `DO` blocks), so re-running is always safe.
- `PostgresGateway.MigrationNames` exposes the applied set (short names, in
  order); the CLI `migrate` banner reports the count and current target level.
- There is **no schema-version table** — migrations are unconditionally
  re-applied and rely on their own idempotency. "Current level" means the
  latest migration embedded in the build, not a per-database marker.
- This document describes the **effective** schema after all migrations
  (e.g. dropped tables are gone; altered columns show their final type).

### Conventions

- All application tables are in the `public` schema; identifiers are
  `snake_case`.
- Timestamps are `timestamptz`, usually `NOT NULL DEFAULT now()`.
- Surrogate keys for Authoring entities are `uuid` (assigned in app code); AACT
  trials are keyed by their `nct_id` (`text`).
- Match scores are `numeric(4,3)` end-to-end (range `[0, 1]`).
- Many text columns are written empty-string-vs-NULL deliberately; the gateway's
  `NullIfEmpty` collapses genuinely-empty values to `NULL` on write.

### Extensions

| Extension | Added in | Used by |
| :--- | :--- | :--- |
| `vector` (pgvector) | V7 | `eligibility_study_embedding.embedding`; cosine KNN similarity search. (Phase 2: `umls.concept_embedding`.) |
| `pg_trgm` | V8 | GIN trigram indexes for `ILIKE` substring filters on `public.eligibility`; fuzzy concept lookup on `umls.atom` (V17) |

---

## Output tables

Twelve application tables exist in the effective schema (the V1
`eligibility_watermark` table was dropped in V4 — see
[migration history](#migration-history)). The last two — `app_user` and
`audit_log` — back authentication, role-based authorization, and auditing.

```
eligibility_run ─────────┐ (run_id, logical only — no FK)
                         │
eligibility_study ◀──────┘   per (run_id, nct_id) audit
eligibility ─────────────    pipeline output rows (per nct_id, no FK)
eligibility_failed ──────    permanent-failure ledger
eligibility_study_detail ─   per-trial source snapshot
eligibility_study_embedding  per-trial topic vector (Authoring similarity)

authoring_study ◀──────────┬── authoring_eligibility   (1:1, FK CASCADE)
                           └── authoring_criterion      (1:N, FK CASCADE)
                                     └── authoring_criterion_source  (1:N, FK CASCADE)
```

The pipeline tables are linked only logically by `nct_id` / `run_id` (no foreign
keys — the per-trial DELETE+INSERT discipline and crash-resume depend on rows
being independently writable). The Authoring tables use real foreign keys with
`ON DELETE CASCADE`.

### public.eligibility

The pipeline's output: one row per extracted, UMLS-resolved criterion record.
Written per-trial as `DELETE WHERE nct_id = X; INSERT …` inside one transaction
(trial-idempotent, crash-resumable). *(V1; indexes extended in V8.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `id` | `bigserial` | no | seq | Primary key. Volatile — reassigned when a trial is re-processed. |
| `nct_id` | `text` | no | | AACT trial id. |
| `criterion` | `text` | no | | `Inclusion` \| `Exclusion`. |
| `domain` | `text` | no | | |
| `concept` | `text` | no | | |
| `concept_code` | `text` | yes | | UMLS CUI; empty when below the 0.45 match threshold. |
| `semantic_type` | `text` | yes | | Empty when unresolved. |
| `qualifier` | `text` | yes | | |
| `time_window` | `text` | yes | | |
| `original_text` | `text` | yes | | Source snippet(s); merged duplicates are space-joined. |
| `umls_name` | `text` | yes | | Empty when unresolved. |
| `match_score` | `numeric(4,3)` | no | `0` | Composite UMLS score; `0` when unresolved. |
| `match_source` | `text` | yes | | Empty when unresolved. |
| `created_at` | `timestamptz` | no | `now()` | |

**Indexes:** `ix_eligibility_nct_id (nct_id)` (V1);
`ix_eligibility_created_at (created_at DESC, id DESC)` — default Results sort;
`ix_eligibility_domain`, `ix_eligibility_concept_code`,
`ix_eligibility_semantic_type` — exact-match filters;
`ix_eligibility_criterion_trgm`, `ix_eligibility_concept_trgm` — GIN trigram for
`ILIKE` filters (all V8); `ix_eligibility_unresolved (nct_id) WHERE concept_code
IS NULL OR concept_code = ''` — partial index backing the `retry-umls` trial
selection (V19).

### public.eligibility_run

One row per batch run (the Runs tab + `status` cards read this). *(V1;
`concurrency_cap` added V15.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `run_id` | `uuid` | no | | Primary key. |
| `started_at` | `timestamptz` | no | | |
| `ended_at` | `timestamptz` | yes | | NULL while in flight. |
| `trigger_source` | `text` | no | | `webhook` \| `cli` \| `cli-recent` \| dashboard … |
| `study_count` | `integer` | no | | Requested batch size. |
| `studies_processed` | `integer` | yes | | |
| `rows_persisted` | `integer` | yes | | |
| `resolution_rate` | `numeric(4,3)` | yes | | |
| `status` | `text` | no | | `running` \| `completed` \| `cancelled` \| … |
| `error_summary` | `text` | yes | | |
| `concurrency_cap` | `integer` | yes | | Trial concurrency cap (`Pipeline:LlmConcurrencyCap`) in effect for the run; shown in the Runs table next to aggregate Tok/s. NULL for runs recorded before V15 (V15). |

> The Runs table's aggregate **Tok/s** is computed at read time, not stored:
> `SUM(eligibility_study.llm_completion_tokens) FILTER (WHERE status = 'success')`
> per `run_id` ÷ run wall clock. Failed/truncated trials are excluded so they
> don't distort the throughput guide.

### public.eligibility_study

Per-trial audit table — one row per `(run_id, nct_id)`. This is the **progress
ledger**: the next batch anti-joins the distinct `nct_id`s here (replacing the
old `MAX(nct_id)` watermark). Written `running` just before the LLM call,
finalised to a terminal status afterward. *(V2; `llm_raw_response` added V3;
five `llm_stopped_*` / `llm_truncated` diagnostics added V9; `llm_ms` / `umls_ms`
/ `persist_ms` phase timings added V16.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `run_id` | `uuid` | no | | PK part. |
| `nct_id` | `text` | no | | PK part. |
| `started_at` | `timestamptz` | no | | |
| `finished_at` | `timestamptz` | yes | | |
| `status` | `text` | no | | `running` \| `success` \| `llm_failed` \| `parse_empty` \| `parse_invalid_json` \| `persist_failed` \| `cancelled`. |
| `llm_succeeded` | `boolean` | yes | | |
| `llm_finish_reason` | `text` | yes | | OpenAI finish reason. |
| `llm_prompt_tokens` | `integer` | yes | | |
| `llm_completion_tokens` | `integer` | yes | | |
| `parsed_record_count` | `integer` | yes | | Records the parser emitted. |
| `persisted_row_count` | `integer` | yes | | Rows that reached `public.eligibility`. |
| `error_message` | `text` | yes | | |
| `llm_raw_response` | `text` | yes | | Raw model output (V3); TOAST-compressed. |
| `llm_stopped_eos` | `boolean` | yes | | llama.cpp stop diagnostics (V9). |
| `llm_stopped_limit` | `boolean` | yes | | |
| `llm_stopped_word` | `boolean` | yes | | |
| `llm_stopping_word` | `text` | yes | | |
| `llm_truncated` | `boolean` | yes | | |
| `llm_ms` | `integer` | yes | | Wall-clock ms in the LLM call(s), incl. any reasoning-escalation retry (V16). |
| `umls_ms` | `integer` | yes | | Wall-clock ms in the sequential per-criterion UMLS resolution loop (V16). |
| `persist_ms` | `integer` | yes | | Wall-clock ms in the DELETE+INSERT persist transaction (V16). |

**Primary key:** `(run_id, nct_id)`.
**Indexes:** `ix_eligibility_study_nct_id`, `ix_eligibility_study_status`,
`ix_eligibility_study_started_at` (V2).

### public.eligibility_failed

Permanent-failure ledger — one row per `nct_id` that exhausted retries. *(V1.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `nct_id` | `text` | no | | Primary key. |
| `last_attempted` | `timestamptz` | no | | |
| `attempt_count` | `integer` | no | `1` | |
| `last_error` | `text` | yes | | |

### public.eligibility_umls_retry

Per-trial bookkeeping for the `retry-umls` CLI command, which re-resolves UMLS
gaps (rows in `public.eligibility` whose `concept_code` is empty) against the
configured backend without re-running the LLM, UPDATING only the five UMLS
columns in place. One row per trial *attempted*; the trial-selection query
anti-joins this table so consecutive batches advance (and `--force` re-includes
already-recorded trials after a corpus refresh). *(V19.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `nct_id` | `text` | no | | Primary key. |
| `retried_at` | `timestamptz` | no | `now()` | Last attempt time (refreshed on re-attempt). |
| `rows_attempted` | `integer` | no | `0` | Unresolved rows tried in the last attempt. |
| `rows_resolved` | `integer` | no | `0` | Of those, how many newly cleared the 0.45 threshold. |

### public.eligibility_study_detail

Per-trial snapshot of source study metadata + eligibility detail (one row per
`nct_id`), captured from AACT during processing so the Analysis tab renders
without a live AACT connection. *(V5.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `nct_id` | `text` | no | | Primary key. |
| `captured_at` | `timestamptz` | no | `now()` | Snapshot time. |
| `brief_title` | `text` | yes | | — study ID card — |
| `official_title` | `text` | yes | | |
| `overall_status` | `text` | yes | | |
| `phase` | `text` | yes | | |
| `study_type` | `text` | yes | | |
| `start_date` | `date` | yes | | |
| `completion_date` | `date` | yes | | |
| `primary_completion_date` | `date` | yes | | |
| `enrollment` | `integer` | yes | | |
| `enrollment_type` | `text` | yes | | |
| `source` | `text` | yes | | Lead sponsor. |
| `why_stopped` | `text` | yes | | |
| `brief_summary` | `text` | yes | | |
| `conditions` | `text[]` | no | `'{}'` | |
| `interventions` | `jsonb` | no | `'[]'` | Array of `{"type","name"}`. |
| `criteria` | `text` | yes | | — raw eligibility block — |
| `gender` | `text` | yes | | |
| `minimum_age` | `text` | yes | | |
| `maximum_age` | `text` | yes | | |
| `healthy_volunteers` | `text` | yes | | |
| `sampling_method` | `text` | yes | | |
| `population` | `text` | yes | | |
| `adult` | `boolean` | yes | | |
| `child` | `boolean` | yes | | |
| `older_adult` | `boolean` | yes | | |

### public.eligibility_study_embedding

Per-processed-trial topic embedding for the Authoring similarity search. One row
per AACT trial that has `public.eligibility` rows. *(V7; `embedding` pinned to
`vector(1024)` + HNSW index in V8.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `nct_id` | `text` | no | | Primary key. |
| `embedding` | `vector(1024)` | no | | Pinned to 1024 dims in V8 to allow HNSW; one model per corpus. |
| `model` | `text` | no | | Embedding model that produced the row. |
| `source_text` | `text` | yes | | Topic text that was embedded. |
| `embedded_at` | `timestamptz` | no | `now()` | |

**Index:** `ix_eligibility_study_embedding_hnsw` — HNSW on
`(embedding vector_cosine_ops)` for the `<=>` cosine operator (V8).

---

## Authoring tables

> These `public.authoring_*` tables back the **Authoring** feature (designing new,
> not-yet-registered studies from the processed-trial corpus). Created by
> migrations V6/V10/V12/V13/V14, written by the Authoring UI (`/Authoring`). The
> `eligibility_study_embedding` corpus index (below) feeds the Analysis tab's
> "Find Similar" and is produced by `embed-studies`.

All separate from AACT-extracted data. Real FKs with `ON DELETE CASCADE`.

### public.authoring_study

A user-designed, not-yet-registered study's characteristics. *(V6.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `authoring_study_id` | `uuid` | no | | Primary key (surrogate). |
| `study_id` | `text` | yes | | User-facing Study ID (e.g. protocol number). Required & unique (case-insensitive) for studies created after V13; fixed once set. NULL for legacy rows. |
| `label` | `text` | no | | Display label. |
| `source_kind` | `text` | no | | `blank` \| `aact` \| `authored`. |
| `source_ref` | `text` | yes | | NCT_ID or origin `authoring_study_id`. |
| `created_at` | `timestamptz` | no | `now()` | |
| `updated_at` | `timestamptz` | no | `now()` | Touched on every child save. |
| `brief_title` | `text` | yes | | — study ID card (mirrors `eligibility_study_detail`) — |
| `official_title` | `text` | yes | | |
| `overall_status` | `text` | yes | | |
| `phase` | `text` | yes | | |
| `study_type` | `text` | yes | | |
| `start_date` | `date` | yes | | |
| `completion_date` | `date` | yes | | |
| `primary_completion_date` | `date` | yes | | |
| `enrollment` | `integer` | yes | | |
| `enrollment_type` | `text` | yes | | |
| `source` | `text` | yes | | |
| `why_stopped` | `text` | yes | | |
| `brief_summary` | `text` | yes | | |
| `conditions` | `text[]` | no | `'{}'` | |
| `interventions` | `jsonb` | no | `'[]'` | Array of `{"type","name"}`. |
| `created_by` | `uuid` | yes | | `app_user.user_id` who created the study (no FK; V12). |
| `last_updated_by` | `uuid` | yes | | `app_user.user_id` of the last writer (no FK; V12). |

**Indexes:** `ix_authoring_study_updated_at (updated_at DESC)` (V6); `ux_authoring_study_study_id` — partial unique index on `lower(study_id) WHERE study_id IS NOT NULL`, enforcing case-insensitive Study ID uniqueness while exempting legacy NULLs (V13).

### public.authoring_eligibility

High-level eligibility data, 1:1 with `authoring_study`. *(V6.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `authoring_study_id` | `uuid` | no | | PK; FK → `authoring_study(authoring_study_id)` ON DELETE CASCADE. |
| `criteria` | `text` | yes | | |
| `gender` | `text` | yes | | |
| `minimum_age` | `text` | yes | | |
| `maximum_age` | `text` | yes | | |
| `healthy_volunteers` | `text` | yes | | |
| `sampling_method` | `text` | yes | | |
| `population` | `text` | yes | | |
| `adult` | `boolean` | yes | | |
| `child` | `boolean` | yes | | |
| `older_adult` | `boolean` | yes | | |

### public.authoring_criterion

The ordered list of authored criteria for a study. Saved by **upsert** in one
transaction: rows no longer present are deleted, and each supplied row is
`INSERT … ON CONFLICT (authoring_criterion_id) DO UPDATE`, so a row's identity,
`created_at`, and `created_by` survive across edits (pre-V12 this was a
DELETE-all + re-INSERT-all that regenerated everything). *(V6;
`created_by`/`last_updated_by` added V12; `manual_reason` added V14.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `authoring_criterion_id` | `uuid` | no | | Primary key. Stable across edits (round-tripped from the editor). |
| `authoring_study_id` | `uuid` | no | | FK → `authoring_study(authoring_study_id)` ON DELETE CASCADE. |
| `ordinal` | `integer` | no | | Position within the study. |
| `criterion` | `text` | no | | `Inclusion` \| `Exclusion`. |
| `normalized_text` | `text` | no | | Editable normalized statement. |
| `concept` | `text` | yes | | |
| `concept_code` | `text` | yes | | |
| `semantic_type` | `text` | yes | | |
| `domain` | `text` | yes | | |
| `source_note` | `text` | yes | | Provenance note (originating cluster). |
| `manual_reason` | `text` | yes | | Free-text rationale for a manually-added criterion (no lineage); shown in the criteria-tab expansion area and the audit CSV export (V14). |
| `created_at` | `timestamptz` | no | `now()` | Preserved across edits (set on insert only). |
| `updated_at` | `timestamptz` | no | `now()` | Refreshed on each update. |
| `created_by` | `uuid` | yes | | `app_user.user_id` who added the entry (set on insert only; no FK; V12). |
| `last_updated_by` | `uuid` | yes | | `app_user.user_id` of the last editor (no FK; V12). |

**Index:** `ix_authoring_criterion_study (authoring_study_id, ordinal)` (V6).

### public.authoring_criterion_source

Lineage of an authored criterion: a per-record **snapshot** of the
`public.eligibility` rows it was normalized from. Snapshot rather than an FK to
`public.eligibility` because that table is rebuilt per-trial (so `eligibility.id`
is volatile); `eligibility_id` is kept only as a best-effort link. Rewritten with
its parent criterion on every save (the criterion DELETE cascades here). *(V10.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `authoring_criterion_source_id` | `uuid` | no | | Primary key. |
| `authoring_criterion_id` | `uuid` | no | | FK → `authoring_criterion(authoring_criterion_id)` ON DELETE CASCADE. |
| `eligibility_id` | `bigint` | yes | | Best-effort link to `public.eligibility.id` (volatile; not an FK). |
| `nct_id` | `text` | no | | Source trial. |
| `criterion` | `text` | yes | | Source row's `Inclusion`/`Exclusion`. |
| `domain` | `text` | yes | | Snapshot. |
| `concept` | `text` | yes | | Snapshot. |
| `concept_code` | `text` | yes | | Snapshot. |
| `semantic_type` | `text` | yes | | Snapshot. |
| `qualifier` | `text` | yes | | Snapshot. |
| `time_window` | `text` | yes | | Snapshot. |
| `original_text` | `text` | yes | | Snapshot of the source phrasing. |
| `match_score` | `numeric(4,3)` | yes | | Snapshot. |
| `created_at` | `timestamptz` | no | `now()` | |

**Index:** `ix_authoring_criterion_source_criterion (authoring_criterion_id)` (V10).

---

## Auth & audit tables

Back the web host's authentication, role-based authorization, and auditing.
There are intentionally **no foreign keys** from `audit_log` (or the
`created_by`/`last_updated_by` attribution columns) to `app_user`: audit history
is immutable and must survive — and never block — a user deletion.

### public.app_user

One row per application user. A row supports password login, Google login, or
both (account linking by email) — `password_hash` and `google_subject` are each
nullable. *(V11.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `user_id` | `uuid` | no | | Primary key. Used as the claim subject + attribution id. |
| `user_name` | `text` | no | | Login "userid". Case-insensitively unique. |
| `email` | `text` | no | | Case-insensitively unique; the Google-linking match key. |
| `display_name` | `text` | no | `''` | |
| `role` | `text` | no | | `Owner` \| `Administrator` \| `Author` \| `Viewer`. |
| `password_hash` | `text` | yes | | BCrypt hash; NULL for Google-only accounts. |
| `google_subject` | `text` | yes | | Google `sub`; NULL for password-only accounts. |
| `picture_url` | `text` | yes | | Google profile picture (avatar). |
| `is_active` | `boolean` | no | `true` | Inactive users cannot sign in. |
| `created_at` | `timestamptz` | no | `now()` | |
| `updated_at` | `timestamptz` | no | `now()` | |
| `last_login_at` | `timestamptz` | yes | | Set on each successful login. |
| `signing_password_hash` | `text` | yes | | BCrypt hash for 21-CFR-Part-11 e-signature re-authentication; separate from the login password. NULL until the user sets a signing password. Falls back to `password_hash` for re-auth when NULL. *(V21.)* |
| `password_updated_at` | `timestamptz` | yes | | Timestamp of the last login-password change. NULL for accounts that have never changed their password. *(V21.)* |
| `signing_password_updated_at` | `timestamptz` | yes | | Timestamp of the last signing-password change. NULL until a signing password is set or changed. *(V21.)* |

**Indexes:** `ux_app_user_user_name` (unique on `lower(user_name)`),
`ux_app_user_email` (unique on `lower(email)`), `ux_app_user_google` (partial
unique on `google_subject` where not null) (all V11).

### public.audit_log

Append-only audit trail: every manual create/update/delete plus every login.
Not yet surfaced in the UI. *(V12.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `audit_id` | `bigint` | no | identity | Primary key (`GENERATED ALWAYS AS IDENTITY`). |
| `occurred_at` | `timestamptz` | no | `now()` | |
| `user_id` | `uuid` | yes | | Acting user; NULL for failed/unknown logins (no FK). |
| `user_label` | `text` | no | | Snapshot of userid/email so deleted users stay legible. |
| `action` | `text` | no | | `create` \| `update` \| `delete` \| `login` \| `login_denied` \| `bootstrap` \| `role_change`. |
| `entity_type` | `text` | no | | e.g. `authoring_study`, `authoring_criterion`, `app_user`, `session`, `eligibility_study`. |
| `entity_id` | `text` | yes | | Affected record id (text, so composite/text keys fit). |
| `detail` | `text` | yes | | Free-text summary. |

**Indexes:** `ix_audit_log_occurred_at (occurred_at DESC)`,
`ix_audit_log_entity (entity_type, entity_id)` (V12).

---

## Source database (AACT, read-only)

These live in the external AACT mirror's `ctgov` schema. This system **only
reads** them; AACT owns the authoritative schema and the columns below are just
those the gateway depends on. Readers coerce across AACT mirror type variations
(e.g. `healthy_volunteers` typed boolean or text; dates as text).

| Table | Read by | Key columns used |
| :--- | :--- | :--- |
| `ctgov.eligibilities` | trial selection + source-eligibility snapshot | `nct_id`, `criteria`, `gender`, `minimum_age`, `maximum_age`, `healthy_volunteers`, `sampling_method`, `population`, `adult`, `child`, `older_adult` |
| `ctgov.studies` | study ID card | `nct_id`, `brief_title`, `official_title`, `overall_status`, `phase`, `study_type`, `start_date`, `completion_date`, `primary_completion_date`, `enrollment`, `enrollment_type`, `source`, `why_stopped` |
| `ctgov.brief_summaries` | study ID card | `nct_id`, `description` |
| `ctgov.conditions` | study ID card | `nct_id`, `name` |
| `ctgov.interventions` | study ID card | `nct_id`, `intervention_type`, `name` |

Trial selection filters out rows whose `criteria` contains "please contact" /
"contact site for" / "contact study" (pipeline spec §2.3).

**Selection performance index (app-managed).** When the source and output
databases are co-located (same host/port/db), the app creates a partial index
on the AACT-owned table at startup:

```sql
CREATE INDEX IF NOT EXISTS ix_eligibilities_selectable_nct_id
ON ctgov.eligibilities (nct_id)
WHERE criteria IS NOT NULL
  AND length(trim(criteria)) >= 50
  AND criteria NOT ILIKE '%please contact%'
  AND criteria NOT ILIKE '%contact site for%'
  AND criteria NOT ILIKE '%contact study%';
```

Its predicate mirrors the selection filter exactly, so the next-batch anti-join
walks this index alone (index-only) instead of heap-fetching the wide `criteria`
text for every already-processed trial it skips. Created idempotently by
`PostgresGateway.EnsureSourcePerformanceIndexesAsync` (it is **not** a V-numbered
output-DB migration — it lives on the source schema, which an AACT reload may
drop, hence the on-startup re-create). A one-time `VACUUM ctgov.eligibilities`
after each AACT load lets the scan skip the visibility-map heap fetches too.

---

## UMLS Metathesaurus store (`umls` schema)

Optional local copy of a curated UMLS subset, queried by `PostgresUmlsClient`
when `Umls:Backend = "postgres"` — an alternative to the remote UTS REST API
behind the same `IUmlsClient` seam. Populated out-of-band by the CLI `load-umls`
command (parsing an unpacked UMLS release) or restored from a `pg_dump` built on
a GPU box; **never** written by the running pipeline. The loader TRUNCATEs and
repopulates per release. *(V17; FTS `str_tsv` column added V18; a
`umls.concept_embedding` table is planned for the Phase-2 semantic layer.)*

### umls.atom

One row per searchable string. A CUI has many atoms (preferred name, synonyms,
abbreviations across source vocabularies) — this is the synonym table the
exact/trigram lookup searches.

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `cui` | `text` | no | | UMLS concept id, e.g. `C0020615`. |
| `str` | `text` | no | | Raw atom string (trigram-indexed). |
| `str_norm` | `text` | no | | Lower/trim-normalized form (exact-match indexed). |
| `sab` | `text` | no | | Source vocabulary (`SNOMEDCT_US`, `MSH`, …). |
| `tty` | `text` | yes | | Term type (`PT`, `SY`, `AB`, …). |
| `is_pref` | `boolean` | no | `false` | Preferred atom for its concept. |
| `str_tsv` | `tsvector` | no | generated | `to_tsvector('english', str)`, STORED. Backs the FTS-ranked lookup arm (V18). |

**Indexes:** `ix_umls_atom_str_tsv` GIN `(str_tsv)` (FTS `@@` + `ts_rank`, V18);
`ix_umls_atom_str_trgm` GIN `(str gin_trgm_ops)` (fuzzy `%` typo fallback);
`ix_umls_atom_str_norm (str_norm)` (exact); `ix_umls_atom_cui (cui)` (join).

### umls.concept

One row per CUI with a chosen preferred name + source vocab; supplies
`UmlsCandidate.Name` / `RootSource`. Derived from `umls.atom` by the loader.

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `cui` | `text` | no | | Primary key. |
| `pref_name` | `text` | no | | Preferred name (by SAB/TTY/`is_pref` priority). |
| `root_source` | `text` | no | `''` | Source vocab of the preferred name. |

### umls.semantic_type

CUI → semantic type name(s), from MRSTY. Backs `GetSemanticTypesAsync`.

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `cui` | `text` | no | | PK part. |
| `tui` | `text` | yes | | Semantic type id (`T047`, …). |
| `sty` | `text` | no | | PK part — semantic type name. |

**Primary key:** `(cui, sty)` (also serves the `WHERE cui = @cui` lookup).

### umls.concept_normalization

LLM concept-normalization cache (the `normalize-umls` command + the pipeline's
inline cache consult). For each *distinct* UMLS-unresolved concept, the LLM
normalize endpoint produces a canonical clinical term; that term is re-resolved
through the local lexical store (0.45 scorer floor) and the outcome cached here.
Doubles as the resumable anti-join for `normalize-umls` **and** a reusable
concept→CUI map the extraction pipeline reads inline (cheap PK lookup, no LLM) to
resolve a criterion on first pass. Keyed by `concept_norm` =
`UmlsMetathesaurusStore.NormalizeConcept(concept)` (lower / trim / collapse-ws), so
case + spacing variants share one row. *(V20.)*

| Column | Type | Null | Default | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `concept_norm` | `text` | no | | Primary key — normalized lookup key. |
| `normalized_term` | `text` | no | `''` | LLM canonical term (`''`/`NONE` when not a biomedical concept). |
| `concept_code` | `text` | yes | | UMLS CUI; NULL when unresolved. |
| `umls_name` | `text` | yes | | |
| `match_source` | `text` | yes | | |
| `match_score` | `numeric(4,3)` | no | `0` | Re-lookup composite score; `0` when unresolved. |
| `semantic_type` | `text` | yes | | Comma-joined names. |
| `resolved` | `boolean` | no | `false` | Did the normalized term clear the 0.45 floor? |
| `normalized_at` | `timestamptz` | no | `now()` | Last attempt time. |

---

## Migration history

| Migration | Effect |
| :--- | :--- |
| `V1__schema.sql` | `eligibility`, `eligibility_watermark`, `eligibility_run`, `eligibility_failed` |
| `V2__study_table.sql` | `eligibility_study` (per-trial audit) + its indexes |
| `V3__study_raw_response.sql` | adds `eligibility_study.llm_raw_response` |
| `V4__drop_watermark.sql` | drops `eligibility_watermark` (the output store *is* the watermark) |
| `V5__study_detail.sql` | `eligibility_study_detail` (per-trial source snapshot) |
| `V6__authoring.sql` | `authoring_study`, `authoring_eligibility`, `authoring_criterion` |
| `V7__study_embeddings.sql` | `vector` extension + `eligibility_study_embedding` |
| `V8__performance_indexes.sql` | pins `embedding` to `vector(1024)` + HNSW index; `pg_trgm` + Results-browser indexes on `eligibility` |
| `V9__llm_stop_diagnostics.sql` | adds five `llm_stopped_*` / `llm_truncated` columns to `eligibility_study` |
| `V10__authoring_criterion_source.sql` | `authoring_criterion_source` (authored-criterion lineage) |
| `V11__auth.sql` | `app_user` (auth/RBAC) + case-insensitive unique indexes on user_name/email and partial unique on google_subject |
| `V12__audit.sql` | adds `created_by`/`last_updated_by` to `authoring_study` + `authoring_criterion`; `audit_log` (append-only audit trail) |
| `V13__authoring_study_id.sql` | adds `authoring_study.study_id` (user-facing Study ID) + partial unique index `ux_authoring_study_study_id` on `lower(study_id)` |
| `V14__authoring_criterion_manual_reason.sql` | adds `authoring_criterion.manual_reason` (free-text rationale for manually-added criteria) |
| `V15__run_concurrency_cap.sql` | adds `eligibility_run.concurrency_cap` (trial concurrency cap used for the run) |
| `V16__study_phase_timings.sql` | adds `eligibility_study.llm_ms` / `umls_ms` / `persist_ms` (per-trial phase wall-clock for concurrency diagnostics) |
| `V17__umls_metathesaurus.sql` | adds the `umls` schema (`atom`, `concept`, `semantic_type`) + trigram/exact indexes for local UMLS resolution |
| `V18__umls_fts.sql` | adds `umls.atom.str_tsv` (generated `tsvector`) + GIN index for FTS-ranked concept lookup |
| `V19__umls_retry.sql` | adds `eligibility_umls_retry` (per-trial UMLS-only retry bookkeeping) + partial index `ix_eligibility_unresolved` on `eligibility(nct_id)` WHERE `concept_code` empty |
| `V20__umls_concept_normalization.sql` | adds `umls.concept_normalization` (LLM concept→CUI normalization cache; backs `normalize-umls` + the pipeline's inline cache consult) |
| `V21__signing_credentials.sql` | adds three columns to `public.app_user`: `signing_password_hash` (BCrypt hash for e-signature re-auth), `password_updated_at` (timestamp of last login-password change), `signing_password_updated_at` (timestamp of last signing-password change). All nullable via `ADD COLUMN IF NOT EXISTS`. |

## Related specs

- [`Eligibility_Processing_DotNet_Architecture.md`](Eligibility_Processing_DotNet_Architecture.md) — schema in context of the .NET design (§2.2 / §6 mapping).
- [`Eligibility_Processing_Specification.md`](Eligibility_Processing_Specification.md) — the pipeline contract behind the `eligibility*` tables.

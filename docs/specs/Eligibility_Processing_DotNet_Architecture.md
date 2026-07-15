# Eligibility Processing — .NET 8.0 / VB.NET Architectural Specification

**Version**: 1.2
**Status**: Implemented. Re-implementation of the n8n production workflow `PISX5yTFaetCAH5F` on a unified .NET 8.0 stack (VB.NET libraries + C# Web host), plus the additive **Authoring** feature.
**Source spec**: `Eligibility_Processing_Specification.md` v1.0; **Authoring** feature spec: `authoring specification.md` v1.2.
**Reference benchmark**: Run 75 — 50 studies, 374 rows, ~88% UMLS resolution, ~11 min wall clock, concurrency 8.
**Target stack**: .NET 8.0 LTS, VB.NET for libraries + CLI, C# for the ASP.NET Core Web host (Microsoft no longer ships VB.NET ASP.NET Core templates). Cross-platform (Windows + Linux self-hosting).

**v1.2 delta** — adds the **Authoring** feature (`authoring specification.md`): a new `/Authoring` web area for designing not-yet-registered studies, with pgvector-backed similarity search over the processed-trial corpus, common-criteria clustering, and LLM criterion normalization. It is additive — it introduces no new project and changes no existing extraction-pipeline behaviour. Per-project additions are flagged inline below; section 2.9 summarises it end-to-end.

---

## 1. Solution Topology

```
EligibilityProcessing.sln
│
├── src/
│   ├── EligibilityProcessing.Core/                  VB.NET — pipeline orchestration + contracts + audit DTOs + Authoring domain + users/roles/audit
│   ├── EligibilityProcessing.Data/                  VB.NET — Postgres gateway + embedded migrations (V1–V12)
│   ├── EligibilityProcessing.Llm/                   VB.NET — LLM + embedding + normalizer clients, embedded prompts
│   ├── EligibilityProcessing.Umls/                  VB.NET — UMLS client + cache decorator + log redaction
│   ├── EligibilityProcessing.Notifications/         VB.NET — SMTP / channel-agnostic notification dispatch
│   ├── EligibilityProcessing.Hosting/               VB.NET — shared DI composition root + DotEnvLoader
│   ├── EligibilityProcessing.Cli/                   VB.NET — operator CLI (migrate / run / status / llm-probe / embed-studies)
│   └── EligibilityProcessing.Web/                   C# — ASP.NET Core MVC + SignalR + POST /trigger + Authoring area + auth/RBAC + account mgmt
│
├── tests/
│   ├── EligibilityProcessing.Core.Tests/            VB.NET — pure-logic + orchestrator with fakes
│   ├── EligibilityProcessing.Data.Tests/            VB.NET — Postgres integration (Testcontainers)
│   ├── EligibilityProcessing.Llm.Tests/             VB.NET — prompt builder + HTTP stub
│   ├── EligibilityProcessing.Umls.Tests/            VB.NET — UTS client + cache + redaction
│   └── EligibilityProcessing.Integration.Tests/     C# — Web host end-to-end (WebApplicationFactory) + auth/role gating + password hashing + SMTP sink
│
└── deploy/
    ├── docker-compose.yml
    └── Dockerfile.web
```

All projects target `net8.0`. SDK pinned to 8.0.318 via `global.json`. `Option Strict On` / `Option Infer On` for VB; nullable enable for C#. Shared MSBuild settings live in `Directory.Build.props`.

**Language deviation from the spec.** Architecture spec section 1 (source spec) said "VB.NET throughout". Microsoft has not shipped VB.NET ASP.NET Core templates since .NET Core 3.x, so the Web host is C#. The boundary is clean — `Microsoft.Extensions.DependencyInjection` composes the VB libraries from C# without friction.

**Topology change from v1.0 architecture.** v1.0 specified two ASP.NET Core hosts: a standalone `EligibilityProcessing.Webhook` for `POST /trigger`, and a separate `EligibilityProcessing.Web` for the dashboard. In practice that meant the orchestrator and the SignalR hub lived in different processes, so trigger-driven runs never reached the dashboard's live feed. Merged: the Web host now serves both `POST /trigger` (with the original auth + rate limit + RunGate + BackgroundService plumbing) and the dashboard. Spec section 2.7 invocation semantics are preserved verbatim; only deployment topology changed.

---

## 2. Project Responsibilities

### 2.1 `EligibilityProcessing.Core` (primary library)

The orchestration heart. Contains:

- **Domain types** (immutable classes): `Trial`, `CriterionRecord`, `ResolvedRecord`, `BatchResult`, `RunMetrics`, `RunConfiguration`, `OrchestratorOptions`.
- **Source-DB DTOs** for the Analysis tab: `StudyDetails`, `Intervention`, `SourceEligibilityDetails`.
- **Output-DB DTOs**: `EligibilityRow`, `EligibilityFilter`, `EligibilityFilterOptions`, `EligibilityResultPage`.
- **Audit DTOs**: `StudyExecution` (per-trial audit row), `StudyFilter`, `StudyExecutionPage`. `LlmParseResult` pairs the parser's records with an outcome (`success` / `empty_array` / `invalid_json`) so the orchestrator can distinguish truncation from legitimate empty extraction.
- **`PipelineOrchestrator`** — implements the spec section 5 sequence: trial selection (anti-join against already-attempted NCT_IDs) → LLM fan-out → parse/expand → UMLS resolution → branch/merge → persistence → notification. Each per-trial pass writes audit-row Start and captures the study snapshot (§2.13) before the LLM call, then writes audit-row Finish in every exit path (success / `llm_failed` / `parse_empty` / `parse_invalid_json` / `persist_failed` / `failed` / `cancelled`). Audit and snapshot writes are best-effort — failures log a warning, never abort the trial.
- **`LlmResponseParser`** — implements spec section 2.5 defensive parsing rules (fence stripping, preamble discard, bullet stripping). `ParseWithOutcome()` surfaces the parse outcome distinctly; `Parse()` is a convenience wrapper returning just the records.
- **`UmlsMatchScorer`** — implements spec section 2.6.2 composite scoring (Levenshtein, Jaccard containment, acronym bonus, 0.45 threshold).
- **`IPipelineHooks`** — observer interface for progress events (`OnBatchStartedAsync` / `OnTrialStartedAsync` / `OnTrialCompletedAsync` / `OnBatchCompletedAsync` / `OnBatchCancelledAsync`), consumed by the Web SignalR broadcaster.
- **Authoring domain (v1.2, section 2.9)** — mutable entities `AuthoringStudy`, `AuthoringEligibility`, `AuthoringCriterion` (which carries a `Sources` list of `AuthoringCriterionSource` lineage rows, plus `CreatedBy`/`LastUpdatedBy` attribution) and the read models `AuthoringStudySummary`, `AuthoringStudyAggregate`; Analysis-phase types `SimilarStudy`, `CriterionCluster`, `StudyEmbeddingInput`, plus the `EmbeddingTextBuilder` helper; the boundary contracts `IEmbeddingClient` (+ `EmbeddingResult`) and `ICriteriaNormalizer` (+ `NormalizationResult`). Pure logic / contracts only — implementations live in `Llm`.
- **Auth/audit domain (v1.3, section 2.10)** — `AppUser`, the `Role` enum + `Roles` helper module (text↔enum + `IsAdminLevel` / `CanAuthorWrite`), `AuditEntry`, and the `IAccessRequestNotifier` contract (+ `NullAccessRequestNotifier`; SMTP impl in `Notifications`).

Dependencies: abstractions only (`IPostgresGateway`, `ILlmClient`, `IUmlsClient`, `INotificationSink`, `IPipelineHooks`, `IEmbeddingClient`, `ICriteriaNormalizer`, `IAccessRequestNotifier`). No transport, no SQL, no HTTP — pure logic.

**Concurrency.** The orchestrator uses `Parallel.ForEachAsync` capped at `OrchestratorOptions.LlmConcurrencyCap`. Per-trial criterion-level UMLS work is sequential within a trial (the criterion count per trial is bounded by the LLM token budget rather than a fixed cap — see spec section 2.4.2), and the `UmlsCache` decorator deduplicates repeat concept lookups within a run.

**NuGet**: `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`. Levenshtein is hand-rolled (small enough not to warrant a dependency).

### 2.2 `EligibilityProcessing.Data`

Postgres access using `Npgsql 10.x` (no EF — the workload is bulk INSERT/DELETE per-trial, EF adds no value).

- **`IPostgresGateway`** + **`PostgresGateway`** — implementation.
  - **Pipeline / spec section 5**:
    - `SelectNextTrialsAsync(excludedNctIds, direction, studyCount, ct)` — spec section 2.3 filters in a single parameterised query against the source DB. `direction` picks Forward (earliest unprocessed first) or Recent (most-recent unprocessed first) ordering; `excludedNctIds` anti-joins out trials already attempted.
    - `GetAttemptedNctIdsAsync(ct)` — distinct `nct_id` set from `eligibility_study` (any status). This is the .NET replacement for the spec section 2.2 watermark: instead of an `nct_id > MAX(nct_id)` cutoff (which breaks once Forward and Recent batches mix), the orchestrator passes this set to `SelectNextTrialsAsync` as the anti-join exclusion. See section 3.5.
    - `GetSourceTrialAsync(nctId, ct)` — fetches one trial directly by `nct_id`, bypassing the selection filters; backs the orchestrator's re-run path.
    - `PersistTrialAsync(nctId, records, ct)` — single transaction: `BEGIN; DELETE … WHERE nct_id = @p; INSERT (multi-row VALUES) … ; COMMIT;` per spec section 2.8.2.
    - `RecordRunAsync(runMetrics, ct)` — upserts to `eligibility_run`.
    - `RecordFailedTrialAsync(nctId, error, ct)` — upserts to `eligibility_failed` (closes spec section 9.1 gap).
    - `GetRecentRunsAsync(limit, ct)` — backs the dashboard's Runs tab.
  - **Audit (V2 enhancement)**:
    - `StartStudyAsync(runId, nctId, startedAt, ct)` — inserts a `running` row in `eligibility_study` keyed by `(run_id, nct_id)`. UPSERT so re-acquiring the same trial resets cleanly.
    - `FinishStudyAsync(execution, ct)` — updates the audit row to its terminal state with all diagnostic columns populated.
    - `GetStudiesAsync(filter, sortBy, page, pageSize, ct)` — paged audit browse for the Studies tab.
    - `GetStudyHistoryAsync(nctId, ct)` — every audit row for one trial, newest first, for the Analysis tab's processing-history panel.
  - **Dashboard read paths**:
    - `SearchEligibilityAsync(filter, sortBy, page, pageSize, ct)` — paged `public.eligibility` browse with `COUNT(*) OVER()` for the pager.
    - `GetEligibilityFilterOptionsAsync(maxDropdownSize, ct)` — per-column distinct-value queries bounded with `LIMIT n+1` so high-cardinality columns short-circuit. Drives Results-tab dropdown vs. text-input selection.
    - `GetStudyDetailsAsync(nctId, ct)` — joins `ctgov.studies` + `ctgov.brief_summaries`, follows up with `ctgov.conditions` and `ctgov.interventions` for the Analysis ID card.
    - `GetSourceEligibilityAsync(nctId, ct)` — full `ctgov.eligibilities` row including structured columns (gender, age bounds, healthy-volunteers flag, sampling method, population, adult/child/older_adult).
  - **Study snapshot (V5 enhancement, spec section 2.13)**:
    - `CaptureStudySnapshotAsync(nctId, ct)` — reads `GetStudyDetailsAsync` + `GetSourceEligibilityAsync` from the source DB and UPSERTs the result into `eligibility_study_detail` on the output DB (keyed by `nct_id`, refreshing `captured_at`). A no-op when the trial has no source row. Called per-trial by the orchestrator (best-effort) and by the CLI `backfill-details` command.
    - `GetStudySnapshotAsync(nctId, ct)` — returns the persisted snapshot from `eligibility_study_detail`, or `Nothing` when the trial has not been snapshotted yet. Backs the Analysis tab's snapshot-first read.
  - **Authoring feature (V6 / V7 / V10 enhancement, section 2.9, `authoring specification.md`)**:
    - Study CRUD: `ListAuthoringStudiesAsync`, `GetAuthoringStudyAsync` (loads each criterion's `authoring_criterion_source` lineage rows), `CreateAuthoringStudyAsync`, `UpdateAuthoringStudyAsync`, `SaveAuthoringEligibilityAsync`, `SaveAuthoringCriteriaAsync` (replace-all in one transaction; also rewrites each criterion's lineage rows, which the criterion DELETE cascades), `DeleteAuthoringStudyAsync`.
    - Analysis: `FindSimilarStudiesAsync(queryVector, limit, ct)` — pgvector cosine KNN over `eligibility_study_embedding`, restricted to studies with `public.eligibility` rows; `ClusterCommonCriteriaAsync(nctIds, ct)` — groups `public.eligibility` rows by criterion + concept identity (concept_code, or lower-cased concept text when unresolved), ordered by commonality; `GetClusterRecordsAsync` — the rows behind one cluster.
    - Embedding backfill: `GetStudiesToEmbedAsync(model, ct)`, `UpsertStudyEmbeddingAsync(nctId, embedding, model, sourceText, ct)`. The embedding vector is bound as a text literal and cast to `vector`, avoiding a `Pgvector.Npgsql` type-handler dependency.
- **AACT type-tolerance**. Source-DB readers (`ReadStringOrEmpty`, `ReadNullableDate`, `ReadNullableInt32`, `ReadNullableBoolean`) coerce across AACT mirror variations — e.g. `healthy_volunteers` typed as boolean in some snapshots and text in others; some mirrors expose dates as text. The reader inspects `GetDataTypeName(ord)` and stringifies / parses defensively.
- **Connection management**: keyed `NpgsqlDataSource` singletons (`source` and `output`) registered in DI. Source DS is optional so output-only tools (CLI migrate) can omit it.

**Migrations** ship as embedded resources and are applied in declaration order by `EnsureSchemaAsync` on every startup (and by CLI `migrate`). Each migration is idempotent (`CREATE IF NOT EXISTS`).

```
src/EligibilityProcessing.Data/Migrations/
├── V1__schema.sql            — eligibility, eligibility_watermark, eligibility_run, eligibility_failed
├── V2__study_table.sql       — eligibility_study (per-trial audit)
├── V3__study_raw_response.sql — adds eligibility_study.llm_raw_response
├── V4__drop_watermark.sql    — drops eligibility_watermark (the output store IS the watermark)
├── V5__study_detail.sql      — eligibility_study_detail (per-trial study snapshot)
├── V6__authoring.sql         — authoring_study / authoring_eligibility / authoring_criterion (Authoring feature)
├── V7__study_embeddings.sql  — vector extension + eligibility_study_embedding (Authoring similarity search)
├── V8__performance_indexes.sql — scaling indexes on the output tables
├── V9__llm_stop_diagnostics.sql — adds llama.cpp stop-reason diagnostic columns to eligibility_study
├── V10__authoring_criterion_source.sql — authoring_criterion_source (authored-criterion lineage)
├── V11__auth.sql             — app_user (auth / RBAC; case-insensitive unique user_name/email, partial-unique google_subject)
└── V12__audit.sql            — created_by/last_updated_by on authoring_study + authoring_criterion; audit_log (append-only)
```

`PostgresGateway.MigrationNames` exposes these (short names, in order) so the CLI `migrate` banner can report how many migrations and the current target level rather than a hard-coded string.

V1 schema (spec section 4.4 plus the section 4.4 recommendations applied):

```sql
CREATE TABLE IF NOT EXISTS public.eligibility (
  id            bigserial PRIMARY KEY,
  nct_id        text         NOT NULL,
  criterion     text         NOT NULL,
  domain        text         NOT NULL,
  concept       text         NOT NULL,
  concept_code  text,
  semantic_type text,
  qualifier     text,
  time_window   text,
  original_text text,
  umls_name     text,
  match_score   numeric(4,3) NOT NULL DEFAULT 0,
  match_source  text,
  created_at    timestamptz  NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_eligibility_nct_id ON public.eligibility(nct_id);

CREATE TABLE IF NOT EXISTS public.eligibility_watermark (...);
CREATE TABLE IF NOT EXISTS public.eligibility_run (...);
CREATE TABLE IF NOT EXISTS public.eligibility_failed (...);
```

V2 schema — per-trial audit (re-implementation enhancement, see spec doc Appendix or this doc's section 2.10):

```sql
CREATE TABLE IF NOT EXISTS public.eligibility_study (
  run_id                uuid        NOT NULL,
  nct_id                text        NOT NULL,
  started_at            timestamptz NOT NULL,
  finished_at           timestamptz,
  status                text        NOT NULL,
  llm_succeeded         boolean,
  llm_finish_reason     text,
  llm_prompt_tokens     integer,
  llm_completion_tokens integer,
  parsed_record_count   integer,
  persisted_row_count   integer,
  error_message         text,
  PRIMARY KEY (run_id, nct_id)
);
CREATE INDEX IF NOT EXISTS ix_eligibility_study_nct_id     ON public.eligibility_study(nct_id);
CREATE INDEX IF NOT EXISTS ix_eligibility_study_status     ON public.eligibility_study(status);
CREATE INDEX IF NOT EXISTS ix_eligibility_study_started_at ON public.eligibility_study(started_at);
```

Status enum (text in the column, constants on `StudyExecution`):
`running` / `success` / `parse_empty` / `parse_invalid_json` / `llm_failed` / `persist_failed` / `failed` / `cancelled`.

V5 schema — per-trial study snapshot (re-implementation enhancement, spec section 2.13). One row per `nct_id` (not per run), UPSERTed each run. `conditions` is a `text[]`; `interventions` is `jsonb` (an array of `{type, name}` objects). Mirrors the `StudyDetails` + `SourceEligibilityDetails` projections so the Analysis tab can render without a live AACT connection:

```sql
CREATE TABLE IF NOT EXISTS public.eligibility_study_detail (
  nct_id                  text        NOT NULL PRIMARY KEY,
  captured_at             timestamptz NOT NULL DEFAULT now(),
  brief_title             text,
  official_title          text,
  overall_status          text,
  phase                   text,
  study_type              text,
  start_date              date,
  completion_date         date,
  primary_completion_date date,
  enrollment              integer,
  enrollment_type         text,
  source                  text,
  why_stopped             text,
  brief_summary           text,
  conditions              text[]      NOT NULL DEFAULT '{}',
  interventions           jsonb       NOT NULL DEFAULT '[]',
  criteria                text,
  gender                  text,
  minimum_age             text,
  maximum_age             text,
  healthy_volunteers      text,
  sampling_method         text,
  population              text,
  adult                   boolean,
  child                   boolean,
  older_adult             boolean
);
```

V6 / V7 / V10 schema — Authoring feature (full DDL in `authoring specification.md` §4). V6 adds three tables, separate from the AACT-extracted data: `authoring_study` (a not-yet-registered study's characteristics, surrogate uuid key), `authoring_eligibility` (1:1 high-level eligibility data), and `authoring_criterion` (the ordered authored-criteria list). V10 adds `authoring_criterion_source` — the lineage of each authored criterion: a per-record **snapshot** of the `public.eligibility` rows it was normalized from, FK to `authoring_criterion` with `ON DELETE CASCADE` (so the replace-all criteria save clears stale lineage). It is a snapshot rather than an FK to `public.eligibility` because that table is rebuilt per-trial (DELETE+INSERT), making `eligibility.id` volatile; the id is kept only as a best-effort link. V7 enables the `vector` extension and adds `eligibility_study_embedding` — one topic-embedding row per processed study:

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS public.eligibility_study_embedding (
  nct_id      text        NOT NULL PRIMARY KEY,
  embedding   vector      NOT NULL,   -- dimensionless: independent of the embedding model
  model       text        NOT NULL,
  source_text text,
  embedded_at timestamptz NOT NULL DEFAULT now()
);
```

The `embedding` column is a bare `vector` (no fixed dimension) so the schema does not depend on a specific embedding model; every row is written by one model (recorded in `model`), so cosine-distance comparisons are always same-dimension. At the current corpus size (~22k studies) an exact sequential KNN scan is well under 100 ms, so no HNSW index is created.

### 2.3 `EligibilityProcessing.Llm`

OpenAI-compatible chat completions client targeting the nginx-fronted llama.cpp pool.

- **`ILlmClient`** + **`LlmClient`** — `Task(Of LlmResponse) CompleteAsync(LlmRequest, CancellationToken)`.
- HTTP transport: `HttpClient` registered via `IHttpClientFactory` with a named client `"llamacpp"`. Polly v8 retry pipeline added via `Microsoft.Extensions.Http.Resilience`:
  - Timeout: 600s (spec section 2.4.1), set on `HttpClient.Timeout`.
  - Retry: 2 attempts, 5s fixed delay (spec section 2.4.4) via `HttpRetryStrategyOptions`. The default `ShouldHandle` predicate retries on transient HTTP responses (5xx, 408, 429) and `HttpRequestException` / `TimeoutRejectedException`.
- **`PromptBuilder`** — encapsulates the system + user prompt construction (spec section 2.4.2 / 2.4.3). System prompt is a static embedded resource (`Prompts/system.v1.md`) to keep it versionable and testable. Includes the worked example required by spec section 2.4.2.
- **`LlmRequest`** carries `(NctId, CriteriaText, Temperature=0.3, MaxTokens=8000, Timeout=600s)`. **MaxTokens deviates from spec section 2.4.1's 3500 reference value** — at 3500 the production model truncated ~60% of trials with verbose criteria (visible as `parse_invalid_json` audit rows with `finish_reason=length`). 8000 gives ample headroom; configurable via `Llm:MaxTokens`.
- **`LlmResponse`** carries `(NctId, RawText, FinishReason, PromptTokens, CompletionTokens, Succeeded, Error)`. The `FinishReason` and token counts feed observability and the per-trial audit row (`eligibility_study.llm_finish_reason` / `llm_prompt_tokens` / `llm_completion_tokens`).
- Concurrency: the orchestrator's `Parallel.ForEachAsync` `MaxDegreeOfParallelism` controls in-flight calls.
- API key forwarded transparently; sourced from `LlmOptions.ApiKey` via `IConfiguration`. The LB doesn't validate it but we send a non-empty value.

**Authoring clients (v1.2, section 2.9):**

- **`IEmbeddingClient`** + **`EmbeddingClient`** — `Task(Of EmbeddingResult) EmbedAsync(text, ct)` against an OpenAI-compatible `POST /v1/embeddings` endpoint. Configured under `Embedding:*` (`EmbeddingOptions`); `BaseUrl` / `ApiKey` fall back to `Llm:*` when blank, since the same server usually serves both. `EmbeddingOptions.MaxInputChars` (default 1500) caps the request so input cannot exceed a fixed-context embedding model's sequence limit. Named HttpClient `"embedding"` with a Polly retry handler.
- **`ICriteriaNormalizer`** + **`CriteriaNormalizer`** — LLM-merges a cluster's `original_text` phrasings into one canonical statement. Reuses the chat-completions endpoint (`LlmOptions`) with a dedicated `Prompts/normalize.v1.md` system prompt; plain-text output, defensively trimmed of stray fences/quotes. Token budget is `LlmOptions.NormalizeMaxTokens` (default 2000) so a "thinking" model has headroom to reach its answer. Named HttpClient `"normalizer"`.
- `PromptBuilder` also exposes `NormalizeSystemPrompt` (embedded `Prompts/normalize.v1.md`) and `BuildNormalizeUserMessage`.

### 2.4 `EligibilityProcessing.Umls`

UMLS UTS REST client.

- **`IUmlsClient`** + **`UmlsClient`** with two methods:
  - `SearchAsync(concept, ct)` → `IReadOnlyList(Of UmlsCandidate)` (up to 5).
  - `GetSemanticTypesAsync(cui, ct)` → `IReadOnlyList(Of String)`.
- HTTP transport: named client `"umls"` with 10s timeout, Polly retry (1 attempt) for transient errors. Continue-on-error semantics per §2.6.1 — exceptions are caught at the client boundary and surfaced as empty results.
- **`UmlsCache`** — in-memory `ConcurrentDictionary(Of String, CachedEntry)` keyed by lower-cased concept, scoped to a single run, resolves gap §9.7. Entry holds `(Candidates, SemanticTypesByCui, FetchedAt)`. Optional Redis backing via `IDistributedCache` for multi-run cache reuse.
- API key passed as query parameter, redacted from logs by a custom `DelegatingHandler` (`UmlsLogRedactionHandler`) per §6.5.

### 2.5 `EligibilityProcessing.Notifications`

Channel-agnostic notification dispatch.

- **`INotificationSink`** with `SendCompletionAsync(BatchResult, ct)` and `SendErrorAsync(BatchResult, ct)`.
- Implementations: `SmtpNotificationSink` (MailKit), `SlackWebhookSink`, `GenericWebhookSink`. All three can be registered concurrently — the orchestrator awaits a fan-out `Task.WhenAll`.
- Single-emission-per-batch guarantee enforced at the orchestrator, not the sink (§2.10).
- Template rendering via `Scriban` from embedded `.sbn` templates so notification copy can be edited without recompile.
- **`SmtpAccessRequestNotifier`** (v1.3) implements the Core `IAccessRequestNotifier` over the same `ISmtpEmailSender` transport, emailing admins when an unrecognised Google account attempts sign-in (section 2.10). Registered alongside `SmtpNotificationSink` in `CompositionRoot` when SMTP is configured (otherwise the Core `NullAccessRequestNotifier` no-op stands in).

### 2.6 `EligibilityProcessing.Hosting`

Shared composition root used by every host. Single `AddEligibilityPipeline(IServiceCollection, IConfiguration)` extension method wires the pipeline + every gateway / client / parser / scorer; each host adds host-specific services on top. Also exposes `DotEnvLoader.LoadDotEnv()` which walks up from the working directory to find `.env` and overlays values into process environment variables before the host builder reads configuration — single `.env` file works from Visual Studio, `dotnet run`, and Docker (`env_file:` directive) identically.

### 2.7 `EligibilityProcessing.Cli`

VB.NET console application, the operator interface for ad-hoc runs and diagnostics.

- Commands:
  - `migrate` — applies every embedded migration in order (V1 … V12 + future). Idempotent. Reports the count and current target level (from `PostgresGateway.MigrationNames`) rather than a fixed banner.
  - `run [--count N] [--recent]` — execute one batch synchronously, stream progress to stdout.
  - `status` — print dashboard counters from the output DB.
  - `llm-probe <NCT_ID> "<criteria text>"` — diagnostic. Sends a single criteria string through the production prompt + LLM client + parser and prints the raw response, finish reason, token usage, and the parser's records. Used to answer "did the LLM return [] or did the parser drop everything?" for trials that show as `parse_empty` / `parse_invalid_json` in the audit.
  - `backfill-details` — snapshots study metadata + eligibility detail (spec section 2.13) into `eligibility_study_detail` for every trial already in `eligibility_study`. New runs snapshot themselves during processing; this covers trials processed before the snapshot store existed.
  - `embed-studies` — Authoring feature (section 2.9). Gap-filler for `eligibility_study_embedding`: embeds every processed study that has a snapshot but no embedding under the configured model. The extraction pipeline now embeds studies inline (section 2.9), so this is no longer required after a normal run — it covers studies processed before inline embedding existed, or whose inline embedding failed. Idempotent — only fills gaps.
  - `reset --confirm` — TRUNCATEs every output table. Destructive; requires `--confirm`. The source DB is never touched.
- Configuration: `appsettings.json` + `.env` via `DotEnvLoader` + environment variables.
- Exit codes: `0` success, `1` configuration error, `2` runtime failure, `3` cancelled (Ctrl+C).
- Ctrl+C captured and turned into a cooperative `CancellationToken` so the orchestrator can unwind cleanly.

### 2.8 `EligibilityProcessing.Web` (C#)

ASP.NET Core MVC + SignalR. Hosts the dashboard *and* the trigger surface so the SignalR hub and the orchestrator share a process — broadcasts reach the dashboard's live feed without cross-process IPC.

**Trigger surface** (folded in from what spec section 2.7 / arch v1.0 called the standalone Webhook host):

- `POST /trigger` — fires a run with `StudyCount=500` per spec section 2.1. Returns `202 Accepted` with `{run_id, started_at, study_count}`.
- `POST /trigger?count=N` — variant for non-default counts.
- `GET /health` — liveness probe.
- Auth: shared-secret header (`X-Eligibility-Token`) compared against `Webhook:Secret` configuration. Constant-time comparison via `CryptographicOperations.FixedTimeEquals`.
- Run execution is fire-and-forget: the endpoint enqueues a `RunRequest` into a `Channel<RunRequest>` bounded to 1; `BatchRunner` (an `IHostedService`) drains it and runs the orchestrator inside a DI scope so the scoped `UmlsCache` lives only for the duration of that run.
- `RunGate` (singleton) holds at most one in-flight `(runId, CancellationTokenSource)` pair — a second trigger arriving while busy returns `409 Conflict` with the current run id.
- Rate limiting: ASP.NET Core 8's `RateLimiter` middleware, fixed window 1 req / 60s on `/trigger`. The window settings are bound via `Options.Configure<TDep>` so they resolve lazily (lets test factories override them).

**Cancellation**:

- `BatchRunner` invokes the orchestrator with a linked `CancellationTokenSource(stoppingToken, gate.CurrentToken)`. The dashboard's Cancel button posts to `/Home/Cancel`, which calls `RunGate.Cancel()` — the per-run token fires, the orchestrator unwinds via `ExceptionDispatchInfo.Capture(...)` (VB-friendly because `Await` is illegal inside `Catch`), the audit row records `status=cancelled`, and `OnBatchCancelledAsync` broadcasts a terminal event over SignalR. Host shutdown is distinguished from user-cancel and re-thrown so the `BackgroundService` exits cleanly.

**Dashboard tabs**:

| Tab | Route | What |
|---|---|---|
| Dashboard | `/` | Trigger / Cancel buttons, headline metric cards (studies successful, eligibility rows persisted, studies failed with per-status breakdown, UMLS resolution rate, tokens used), most-recent-run summary, live SignalR activity stream. |
| Runs | `/Home/Runs` | Recent runs from `eligibility_run`, sortable. |
| Results | `/Home/Results` | Filterable + sortable + paginated (20/page) browse of `public.eligibility`. Filters auto-pick `<select>` vs free-text per column based on cardinality (≤ 100 → dropdown). NCT ID column links to Analysis. |
| Analysis | `/Home/Analysis` | Per-trial deep dive. ID card + eligibility detail served **snapshot-first** from `eligibility_study_detail` (spec section 2.13), falling back to a live AACT read (`ctgov.studies` + `brief_summaries` + `conditions` + `interventions`; full `ctgov.eligibilities` row) only when no snapshot exists; processing-history panel from `eligibility_study` (every run that touched this NCT_ID); pipeline output rows with row selection + criteria highlighting (click or arrow-key a row to highlight that row's `original_text` in the criteria pane; "Highlight all" wraps every row's `original_text` so operators can see what the LLM didn't capture). |
| Studies | `/Home/Studies` | Filterable / sortable / paginated browse of `eligibility_study`. Filter by NCT ID, status, run ID. The "why didn't this trial produce rows?" surface. |
| Authoring | `/Authoring` | The Authoring feature (section 2.9). Renders in its own focused layout (`_AuthoringLayout`) — no dashboard navbar — and is reached from the dashboard via a button-styled link that opens in a new browser tab. Two-pane shell: a collapsible study list partial on the left, a four-tab editor on the right (Study Overview / Source Eligibility / Analysis / Eligibility Criteria) with tab state persisted in `localStorage`. Create / copy a study is driven by one combined modal. |

**Dashboard-side mutation endpoints** (anti-forgery protected; gated by the `PipelineOps` authorization policy — Owner / Administrator only — instead of the shared-secret check):

- `POST /Home/Trigger` / `/Home/TriggerRecent` — same plumbing as `/trigger`.
- `POST /Home/Rerun` / `/Home/RerunBatch` — single / multi-trial re-run.
- `POST /Home/Cancel` — calls `RunGate.Cancel()`.
- `POST /Home/DeleteStudy` — deletes one `eligibility_study` audit row.

**SignalR**: `RunProgressHub` mounted at `/hubs/progress`. `SignalRPipelineHooks` (an `IPipelineHooks` implementation registered after `AddEligibilityPipeline` so it overrides the `NullPipelineHooks` default) broadcasts `BatchStarted` / `TrialStarted` / `TrialCompleted` / `BatchCompleted` / `BatchCancelled` events to all connected clients. The hub is connection-only — no client-callable methods today.

**Frontend**: Razor Views with Bootstrap 5 + a tiny vanilla-JS SignalR client served from `wwwroot/lib/microsoft-signalr/signalr.min.js` (vendored locally rather than CDN-loaded — see commit history for why). Timestamps render with `Intl.DateTimeFormat` so each visitor sees their own locale.

**Authentication & authorization** (v1.3): the host now requires an authenticated user for every page and action — see section 2.10. A cookie-auth `FallbackPolicy` locks the app down by default; `/health` and the shared-secret `POST /trigger` opt out with `.AllowAnonymous()`. The dashboard navbar gains a top-right user menu (avatar / user-ID + role) with **Manage Accounts** (admins) and **Sign out**; views hide write controls a role can't use, and the per-action `[Authorize(Policy=…)]` attributes are the actual boundary.

### 2.9 Authoring feature (cross-project, v1.2)

An additive capability — full functional spec in **`authoring specification.md`**. It lets a user design a new, **not-yet-registered** study and use the processed-trial corpus to inform its eligibility criteria. It introduces no new project and changes no existing extraction-pipeline behaviour; the pieces are layered onto the existing libraries (flagged inline in sections 2.1–2.3, 2.7, 2.8 above).

**Three phases, served by `AuthoringController` on the `/Authoring` route:**

1. **Study Setup** — create a study (blank, cloned from an AACT trial via its snapshot, or cloned from another authored study), and edit its characteristics + high-level eligibility data. Persisted in `authoring_study` / `authoring_eligibility` (V6) — separate from AACT-extracted data.
2. **Analysis** — embed the proposed study's topic text (`EmbeddingTextBuilder` → `IEmbeddingClient`) and rank processed studies by pgvector cosine similarity (`FindSimilarStudiesAsync`); cluster the common eligibility criteria across the chosen studies by Inclusion/Exclusion + concept identity (`ClusterCommonCriteriaAsync`), with per-cluster record inspection (`GetClusterRecordsAsync`).
3. **Normalization & authoring** — LLM-merge a cluster's `original_text` variants into one canonical statement (`ICriteriaNormalizer`), then copy it into the study's ordered, editable authored-criteria list (`authoring_criterion`, V6). Adding a criterion also snapshots the cluster's source records as that criterion's **lineage** (`authoring_criterion_source`, V10), so the provenance of each authored criterion can be reported later.

**Endpoints** (`AuthoringController`, conventional routing, anti-forgery on all POSTs): `GET /Authoring`, `GET /Authoring/Edit/{id}`, `POST` `Create` / `SaveStudy` / `SaveEligibility` / `SaveCriteria` / `Delete` / `Similar` / `Cluster` / `Normalize`, `GET /Authoring/ClusterRecords`, and `GET /Authoring/ExportCriteria?id=…` (downloads the study's criteria as CSV — every `authoring_criterion` column plus the study id + label — via the shared `Export/AuthoringCriteriaCsv` + `CsvWriter` helpers; a read action open to any authenticated viewer). `Normalize` returns the cluster's `records` (lineage source) alongside the canonical text; `SaveCriteria` binds each criterion's lineage as a JSON `SourcesJson` field, plus the existing `authoring_criterion_id` (hidden `crit-id` input), and writes both via an **upsert** transaction (V12): rows the editor dropped are deleted, surviving rows are `INSERT … ON CONFLICT (authoring_criterion_id) DO UPDATE`, and each row's `authoring_criterion_source` lineage is rewritten. The upsert (replacing the pre-V12 DELETE-all + re-INSERT-all) is what lets per-row `created_by` / `created_at` survive an edit while `last_updated_by` / `updated_at` refresh. The acting user is threaded from the controller via `ICurrentUserAccessor` (section 2.10) into the gateway write methods.

**View layer (v1.1; dashboard-tab integration v1.3).** The Authoring area is a first-class dashboard tab: its views render inside the dashboard `Views/Shared/_Layout.cshtml` under `container-fluid`, as a collapsible master-detail shell (`.authoring-shell`) hosting the study-list aside and the editor pane. `_Layout` highlights the Authoring nav tab on the Authoring controller, and a persistent (localStorage) toggle collapses the aside so the editor uses the full page width; the shell styling lives in `wwwroot/css/site.css`. (The former standalone `_AuthoringLayout.cshtml` was removed in the v1.3 integration.) Three partials carry the shared chrome: `Views/Authoring/_StudyListPanel.cshtml` (renders an `AuthoringListPanelModel` with the list rows + the active-row highlight), `Views/Authoring/_CreateStudyModal.cshtml` (the combined create modal driven by a `mode` dropdown), and `Views/Authoring/Edit.cshtml` (the four-tab editor; tab state persisted in `localStorage`). Inside Analysis, the per-row normalization spinner / mm:ss timer logic is hoisted into a shared `runNormalize(btn)` so the **Normalize All** column-header action reuses the same per-row UX. A `window.authoringSyncAddButtons` helper enforces the Eligibility Criteria dedup contract by walking the cluster Add buttons against the criteria list whenever the latter changes. Criterion lineage (v1.2) round-trips through a hidden `crit-sources` JSON input on each criteria-list row — set from the `Normalize` records on Add, re-serialized server-side on render; alongside it a hidden `crit-id` input round-trips the row's `authoring_criterion_id` so the V12 upsert can preserve each row's identity (and its `created_by` / `created_at`) across edits. A per-row single-character (ⓘ) button reveals the source records inline, rendered from that JSON inside the criterion row so the reorder/remove/submit logic (which walks `.criterion-row` siblings) is untouched. Read-only roles (Viewer) see the criteria list and study/eligibility fields rendered non-editable (textareas `readonly`, fieldsets `disabled`) with the add / remove / reorder / save controls and the cluster **Add** button removed; `window.authoringCanWrite` gates the JS-rendered controls.

**Operational prerequisites:** the `vector` extension must be installed on the output Postgres server (V7 runs `CREATE EXTENSION`); and an OpenAI-compatible `/v1/embeddings` endpoint must be configured under `Embedding:*`. The similarity candidate pool is processed studies only (those with `public.eligibility` rows).

**Embedding production.** The extraction pipeline embeds each study inline — `PipelineOrchestrator` generates and UPSERTs the topic embedding (`EmbeddingTextBuilder` → `IEmbeddingClient` → `UpsertStudyEmbeddingAsync`) immediately after a trial is persisted, so a processed study is searchable without any extra step. This is a deliberate correction to the original v1.2 design, which produced embeddings *only* via the CLI `embed-studies` backfill — meaning every pipeline run left the similarity index stale until an operator remembered to run the backfill. Every processed trial is embedded, **including trials that extracted zero criteria**: the topic embedding is built from the study's metadata snapshot, not its criteria, so a zero-row study is still a useful similarity candidate. Inline embedding is best-effort (a failure logs a warning and the trial still succeeds); the CLI `embed-studies` backfill is retained as the gap-filler for studies whose inline embedding did not land (endpoint down, embedding model changed, corpus processed before this change). Note that `FindSimilarStudiesAsync` still restricts *results* to studies with `public.eligibility` rows — a zero-row study is embedded but not yet returned by similarity search until it has extracted criteria.

### 2.10 Authentication, authorization & auditing (cross-project, v1.3)

A **lightweight custom** auth layer — *not* ASP.NET Core Identity, which would pull in EF Core and clash with the raw-Npgsql data layer. Users, roles, and the audit trail live in our own tables and are read/written through `IPostgresGateway` like everything else.

**Persistence (migrations V11/V12):**

- `public.app_user` (V11) — one row per user: `user_name` + `email` (both case-insensitively unique), `role` text, nullable `password_hash` (BCrypt) and `google_subject`, `picture_url`, `is_active`, `last_login_at`. A row supports password login, Google login, or both (linking by email).
- `created_by` / `last_updated_by` (V12) added to `authoring_study` + `authoring_criterion`, and `public.audit_log` (V12) — append-only `(occurred_at, user_id, user_label, action, entity_type, entity_id, detail)`. **No FKs** to `app_user`: audit history is immutable and must survive a user deletion. Gateway additions: `CountUsersAsync` / `CountOwnersAsync`, `GetUserBy{UserName,Email,GoogleSubject}Async` / `GetUserAsync` / `ListUsersAsync`, `CreateUserAsync` / `UpdateUserRoleAsync` / `UpdateUserPasswordHashAsync` / `LinkGoogleSubjectAsync` / `RecordLoginAsync` / `DeleteUserAsync`, and `InsertAuditAsync`. New Core models: `AppUser`, the `Role` enum + `Roles` helper module (text↔enum + permission helpers), and `AuditEntry`.

**Roles → policies.** Four roles (`Owner`, `Administrator`, `Author`, `Viewer`); Owner and Administrator are equal in permission, the distinct value only exists to *protect* the Owner. Authorization policies registered in `Program.cs`: `Read` (any authenticated user), `AuthorWrite` (Owner/Administrator/Author), `PipelineOps` (Owner/Administrator), `ManageUsers` (Owner/Administrator). A global `FallbackPolicy = RequireAuthenticatedUser` secures every endpoint by default; `/health` and `/trigger` opt out via `.AllowAnonymous()`. Controllers carry `[Authorize(Policy=…)]`; the protected-Owner business rule (can't demote/delete the last Owner; only an Owner may touch another Owner) lives in `UsersController` over the `CountOwnersAsync` primitive.

**Authentication schemes** (`AddAuthentication` in `Program.cs`): a cookie scheme (default; `LoginPath=/Account/Login`, `AccessDeniedPath=/Account/Denied`, sliding expiry from `Auth:CookieExpiryHours`), a transient `External` cookie that the Google handler signs into, and `AddGoogle`. `GoogleCallback` reads the External principal, maps it to a local `app_user` (by `google_subject`, else by email → `LinkGoogleSubjectAsync`), drops the External cookie, then issues the real cookie with our claims (`NameIdentifier`=user id, `Name`, `Email`, `Role`, `picture`). Google client id/secret bind **lazily** via `AddOptions<GoogleOptions>(…).Configure<IOptions<AuthOptions>>` (the same WAF-config-timing pattern as the rate limiter) so test factories — and `.env` overlays — apply; placeholder credentials keep `GoogleOptions.Validate()` happy when Google is unconfigured (the login page hides the button). Because the cookie scheme is the default *challenge* scheme, a forbidden authenticated request falls back to the cookie's forbid handler — its `OnRedirectToLogin`/`OnRedirectToAccessDenied` return **401/403 for XHR/JSON** (so the Authoring `fetch` calls fail cleanly) and redirect browser navigations.

**Web components** (C#, under `EligibilityProcessing.Web/Auth/`): `AuthOptions` (config), `IPasswordHasher`/`BcryptPasswordHasher`, `ICurrentUserAccessor`/`HttpContextCurrentUserAccessor` (reads claims), `IAuditWriter`/`AuditWriter` (best-effort `audit_log` writes pulling the current user), `AuthClaims` (builds the principal), `AuthConstants`. `AccountController` (`[AllowAnonymous]`) handles login / Google / first-run **bootstrap** / denied / logout; `UsersController` (`[Authorize("ManageUsers")]`) is the Manage Accounts JSON API. Views: `_AuthLayout`, `Account/{Login,Bootstrap,Denied}`, the `_UserMenu` partial (rendered in `_Layout`, which now also hosts the Authoring tab), and the `_ManageAccountsModal` (re-parented to `<body>` on load so the sticky-navbar stacking context doesn't trap it behind the Bootstrap backdrop).

**First-run bootstrap.** When `app_user` is empty, `/Account/Login` redirects to a one-time bootstrap form that creates the initial **Owner** (user-ID/password only). The POST re-checks `CountUsersAsync()==0` server-side so it can't become a permanent backdoor once any user exists.

**Unknown Google email.** A Google sign-in whose email isn't an active account is denied (with an explanatory page) and an "access requested" alert is emailed to the admin recipients via `IAccessRequestNotifier` (Core interface; `SmtpAccessRequestNotifier` in `EligibilityProcessing.Notifications`, registered in `CompositionRoot` only when SMTP is configured, else a no-op). This reuses the existing `ISmtpEmailSender` transport rather than coupling to the BatchResult-shaped `INotificationSink`.

**Auditing wiring.** Authoring create/update/delete, pipeline trigger/rerun/cancel/delete-study, user create/role-change/delete, and login / login-denied / bootstrap each write an `audit_log` row (best-effort — a failure is logged, never propagated). `created_by`/`last_updated_by` are populated by threading the acting user id into the authoring gateway write methods. The `audit_log` is browsable by admins via the **Audit Trail** modal in the user menu — `UsersController.AuditLog` (GET, `ManageUsers` policy) returns a paginated, newest-first page via `IPostgresGateway.GetAuditLogAsync(AuditLogFilter, page, pageSize)` with user / action / time-span filters. `UsersController.AuditLogExport` (GET `/Users/AuditLog/Export`) streams **all** rows matching the filter (`GetAuditLogForExportAsync`) as a downloadable CSV using the reusable `Export/CsvWriter` (RFC 4180) + `Export/ExportResults.CsvFile` (UTF-8-BOM `FileContentResult`) helpers and the reusable `window.downloadFile(url, name)` client helper in `site.js` — built to back future export buttons too.

---

## 3. Cross-Cutting Composition

### 3.1 Dependency injection

A single composition root in `EligibilityProcessing.Hosting.CompositionRoot` exposes:

```vbnet
<Extension>
Public Function AddEligibilityPipeline(
        services As IServiceCollection,
        configuration As IConfiguration) As IServiceCollection
    services.Configure(Of PostgresOptions)(configuration.GetSection("Postgres"))
    services.Configure(Of LlmOptions)(configuration.GetSection("Llm"))
    services.Configure(Of UmlsOptions)(configuration.GetSection("Umls"))
    services.Configure(Of OrchestratorOptions)(configuration.GetSection("Pipeline"))

    services.AddKeyedSingleton(Of NpgsqlDataSource)("source", ...)
    services.AddKeyedSingleton(Of NpgsqlDataSource)("output", ...)
    services.AddSingleton(Of IPostgresGateway, PostgresGateway)()

    services.AddHttpClient(Of ILlmClient, LlmClient)("llamacpp")
            .AddResilienceHandler("llm", ...)

    services.AddTransient(Of UmlsLogRedactionHandler)()
    services.AddHttpClient(Of UmlsClient)("umls")
            .AddHttpMessageHandler(Of UmlsLogRedactionHandler)()
            .AddResilienceHandler("umls", ...)
    services.AddScoped(Of IUmlsClient)(... UmlsCache decorator ...)

    services.AddSingleton(Of LlmResponseParser)()
    services.AddSingleton(Of UmlsMatchScorer)()
    services.AddSingleton(Of INotificationSink)(NullNotificationSink.Instance)
    services.AddSingleton(Of IAccessRequestNotifier)(NullAccessRequestNotifier.Instance)
    ' SMTP sink + access-request notifier layered on top when Notifications:Smtp:Host is set:
    services.AddSingleton(Of INotificationSink, SmtpNotificationSink)()
    services.AddSingleton(Of IAccessRequestNotifier, SmtpAccessRequestNotifier)()

    services.AddSingleton(Of IPipelineHooks, NullPipelineHooks)()  ' Web overrides with SignalR
    services.AddScoped(Of PipelineOrchestrator)()
    Return services
End Function
```

The Web host calls `AddEligibilityPipeline()` then adds:

```csharp
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPipelineHooks, SignalRPipelineHooks>();  // last-registration-wins
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhook"));
builder.Services.AddSingleton<RunGate>();
builder.Services.AddSingleton(_ => Channel.CreateBounded<RunRequest>(...));
builder.Services.AddHostedService<BatchRunner>();
builder.Services.AddRateLimiter(...);  // lazy-bound via Options.Configure<TDep>

// Auth (section 2.10): cookie (default) + transient "External" cookie + Google;
// Read / AuthorWrite / PipelineOps / ManageUsers policies + FallbackPolicy.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddAuthentication(...).AddCookie().AddCookie("External").AddGoogle(...);
builder.Services.AddAuthorization(...);  // Google creds lazy-bound via Options.Configure<TDep>
```

**Lifetimes:**

| Service | Lifetime | Why |
|---|---|---|
| `NpgsqlDataSource` (keyed) | Singleton | Manages the connection pool internally. |
| `PostgresGateway` | Singleton | Stateless. |
| `LlmResponseParser`, `UmlsMatchScorer` | Singleton | Pure logic. |
| `LlmClient`, raw `UmlsClient` | Transient | Created via `IHttpClientFactory`. |
| `IUmlsClient` (cache decorator) | **Scoped** | One cache per run; created fresh per batch by `BatchRunner.CreateScope()`. |
| `INotificationSink`, `IPipelineHooks` | Singleton | One per process. |
| `PipelineOrchestrator` | Scoped | Resolves the scoped `IUmlsClient`. |
| `RunGate`, `Channel<RunRequest>` | Singleton | Shared state across the BackgroundService and the trigger endpoint. |

### 3.2 Configuration surface (spec section 6.7)

`appsettings.json` (per-host, non-secrets only):

```json
{
  "Postgres": {
    "ConnectionStringSource": "Host=...;Database=aact;...",
    "ConnectionStringOutput": "Host=...;Database=eligibility;..."
  },
  "Llm": {
    "BaseUrl": "http://...:.../v1",
    "ApiKey": "${LLM_API_KEY}",
    "Model": "gemma-4-26B-A4B-it-Q8_0",
    "Temperature": 0.3,
    "MaxTokens": 8000,
    "TimeoutSeconds": 600,
    "RetryCount": 2,
    "RetryDelaySeconds": 5,
    "ConcurrencyCap": 8,
    "NormalizeMaxTokens": 2000
  },
  "Embedding": {
    "BaseUrl": "",
    "ApiKey": "",
    "Model": "bge-large-en-v1.5",
    "TimeoutSeconds": 30,
    "MaxInputChars": 1500
  },
  "Umls": {
    "BaseUrl": "https://uts-ws.nlm.nih.gov/rest",
    "ApiKey": "${UMLS_API_KEY}",
    "PageSize": 5,
    "TimeoutSeconds": 10,
    "RetryCount": 1,
    "RetryDelaySeconds": 2
  },
  "Pipeline": {
    "LlmConcurrencyCap": 8
  },
  "Webhook": {
    "DefaultStudyCount": 500,
    "RateLimitPermits": 1,
    "RateLimitWindowSeconds": 60
  },
  "Notifications": {
    "Smtp": { "Port": 587, "UseStartTls": true, "FromName": "Eligibility Pipeline" }
  },
  "Auth": {
    "CookieExpiryHours": 8,
    "Google": { "ClientId": "${AUTH_GOOGLE_CLIENT_ID}", "ClientSecret": "${AUTH_GOOGLE_CLIENT_SECRET}" }
  }
}
```

The `Auth` section is Web-only and entirely optional: cookie + password sign-in
work without it; the Google client id/secret enable the OAuth path and come from
`.env` (`Auth__Google__ClientId` / `Auth__Google__ClientSecret`) like the other
secrets.

`.env` at repo root (gitignored, loaded by `DotEnvLoader` before the host builder reads config). Uses the standard double-underscore convention to map nested JSON keys:

| `appsettings` key | `.env` variable |
|---|---|
| `Postgres:ConnectionStringOutput` | `Postgres__ConnectionStringOutput` |
| `Llm:ApiKey` | `Llm__ApiKey` |
| `Umls:ApiKey` | `Umls__ApiKey` |
| `Webhook:Secret` | `Webhook__Secret` |
| `Notifications:Smtp:Password` | `Notifications__Smtp__Password` |

Same `.env` file works across Visual Studio (F5), `dotnet run`, and Docker (`env_file: ../../.env` in `deploy/eligibility-pipeline/docker-compose.yml`). No per-host duplication.

### 3.3 Logging and observability (spec section 6.6)

- **`Microsoft.Extensions.Logging`** with the default console provider. Structured logging via `ILogger<T>`; per-trial diagnostics also persisted to `eligibility_study` (see §2.10 below).
- API keys redacted from UMLS request URLs via `UmlsLogRedactionHandler` (a custom `DelegatingHandler` registered in front of the `umls` HttpClient's resilience pipeline so redaction logs once per caller call, not once per retry).
- Future work: Serilog OTLP sink, `System.Diagnostics.Metrics` instruments, distributed tracing. Deferred until production observability requirements are clearer.

### 3.4 Concurrency model (spec section 7.1)

Trial-level: `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = OrchestratorOptions.LlmConcurrencyCap` (default 8). Criterion-level work for a single trial is sequential — the criterion count per trial is bounded by the LLM token budget rather than a fixed cap (spec section 2.4.2), and the `UmlsCache` decorator deduplicates repeat concept lookups within a run.

### 3.5 Idempotency and resume (spec section 6.1, 6.2)

- **Progress tracking deviates from the spec section 2.2 watermark.** The reference n8n design mirrored `MAX(nct_id)` into an `eligibility_watermark` table; this .NET build derives "already done" directly from the per-trial audit table instead — `GetAttemptedNctIdsAsync` returns every `nct_id` with a row in `eligibility_study`, and `SelectNextTrialsAsync` anti-joins that set. The `eligibility_watermark` table and its `GetWatermark` / `WriteWatermark` gateway methods were removed (migration `V4__drop_watermark.sql`). The `MAX(nct_id)` cutoff was abandoned because it cannot survive mixing Forward and Recent batch directions — a Recent batch jumps `MAX` into the recent NCT_ID range and Forward-mode then skips the gap. The audit-set anti-join is correct under both directions, and the output store remains the single source of truth, so crash-resume still works without a separately-maintained watermark.
- Per-trial DELETE+INSERT inside `PostgresGateway.PersistTrialAsync` uses a single `NpgsqlTransaction`.
- Failed-trial tracking writes to `eligibility_failed` so terminal LLM failures aren't lost across runs (closes spec section 9.1's gap at the audit level; per-NCT-ID retry trigger is still deferred at the UI level).
- The new per-trial audit (`eligibility_study`) makes "did this trial get processed and what happened?" answerable in one query without inferring from absence in `public.eligibility`.

---

## 4. Sequence — Production Run (Webhook Path)

```
External cron       Web host             Core orchestrator     Postgres                LLM pool   UMLS
     │                   │                     │                  │                         │       │
     ├─POST /trigger────►│                     │                  │                         │       │
     │                   ├─validate token──────│                  │                         │       │
     │                   ├─RunGate.TryAcquire──│                  │                         │       │
     │                   ├─enqueue RunRequest─►│ BatchRunner      │                         │       │
     │◄─202 {run_id}─────│                     │                  │                         │       │
     │                   │                     ├─SELECT attempted►│ eligibility_study       │       │
     │                   │                     │◄─[NCT_ID set]────┤                         │       │
     │                   │                     ├─SELECT trials───►│ ctgov.eligibilities     │       │
     │                   │                     │◄─[500 rows]──────┤                         │       │
     │                   │                     ├─OnBatchStarted (→ SignalR via hooks)        │       │
     │                   │                     │                                                    │
     │                   │ ┌─ Parallel.ForEachAsync × LlmConcurrencyCap (default 8) ─┐             │
     │                   │ │   ├─INSERT eligibility_study (status=running) ─────►│   │             │
     │                   │ │   ├─OnTrialStarted (→ SignalR)                       │   │             │
     │                   │ │   ├─LLM call ──────────────────────────────────────►│   │             │
     │                   │ │   │◄─raw JSON + finish_reason + tokens ──────────────┘   │             │
     │                   │ │   ├─ParseWithOutcome → (success | empty_array | invalid_json)│         │
     │                   │ │   ├─UMLS search + best-match scoring ────────────────────────►│         │
     │                   │ │   ├─UMLS semantic types (resolved only) ───────────────────────►│       │
     │                   │ │   │◄─candidates + sem types ────────────────────────────────────│       │
     │                   │ │   ├─per-trial DELETE+INSERT txn ──►│ eligibility              │   │   │
     │                   │ │   ├─UPDATE eligibility_study (terminal status + diagnostics)──►│       │
     │                   │ │   ├─OnTrialCompleted (→ SignalR)                        │   │           │
     │                   │ └────────────────────────────────────────────────────────┘                │
     │                   │                     ├─UPSERT eligibility_run (metrics)──►│                │
     │                   │                     ├─SMTP notification                                  │
     │                   │                     ├─OnBatchCompleted (→ SignalR)                       │
     │                   │                     ├─RunGate.Release                                    │
     │                   │                     │                                                    │
```

Cancellation path: dashboard `POST /Home/Cancel` → `RunGate.Cancel()` → per-run CTS fires → orchestrator unwinds via `ExceptionDispatchInfo` → audit row written with `status=cancelled` → `OnBatchCancelledAsync` broadcast → `RunGate.Release()`.

---

## 5. Deployment

Single Docker image (`Dockerfile.web`) built from `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` with `sdk:8.0-alpine` as the multi-stage builder. The Alpine runtime stages `apk add --no-cache krb5-libs` because Npgsql loads `libgssapi_krb5.so.2` during PostgreSQL connection negotiation and the base image doesn't ship it — without it every DB connection (web, and the CLI `migrate`) fails. The web service mounts a named volume for the ASP.NET Core DataProtection keys so the auth cookie + anti-forgery tokens survive container recreates (otherwise every redeploy silently logs everyone out). CLI runs as a one-shot container or directly on the host.

```yaml
# deploy/eligibility-pipeline/docker-compose.yml (abbreviated)
services:
  eligibility-web:
    build: { context: ../.., dockerfile: deploy/eligibility-pipeline/Dockerfile.web }
    image: eligibility/web:1.0
    restart: unless-stopped
    ports: ["8091:8080"]
    env_file: [../.env]            # single source of truth for secrets

  postgres-output:
    image: pgvector/pgvector:pg18   # Postgres 18 + the vector extension (Authoring feature, section 2.9)
    volumes: [eligibility-data:/var/lib/postgresql/data]
    environment:
      POSTGRES_DB:       ${POSTGRES_DB:-eligibility}
      POSTGRES_USER:     ${POSTGRES_USER:-eligibility}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in ../.env}

volumes:
  eligibility-data:
```

The source AACT database is external and read-only (production points at Duke's public AACT cloud mirror). The nginx LB and llama.cpp servers are unchanged from the n8n reference deployment.

---

## 6. Mapping Back to Specification Sections

| Spec section | .NET realisation |
|---|---|
| §2.1 Invocation modes | CLI (`run`), Web `POST /trigger`, Web dashboard Trigger button (`POST /Home/Trigger`), Library (`PipelineOrchestrator.ExecuteAsync`) |
| §2.2 Watermark | Deviation: no watermark table — `PostgresGateway.GetAttemptedNctIdsAsync` (distinct `nct_id` from `eligibility_study`) anti-joined inside `SelectNextTrialsAsync`. `eligibility_watermark` dropped in `V4__drop_watermark.sql`. See section 3.5. |
| §2.3 Source filtering | Single parameterised query in `PostgresGateway.SelectNextTrialsAsync` (Forward / Recent direction) |
| §2.4 LLM | `LlmClient` + `PromptBuilder` + embedded `Prompts/system.v1.md`; resilience handler via `Microsoft.Extensions.Http.Resilience` |
| §2.5 Parsing | `LlmResponseParser`; `ParseWithOutcome` distinguishes truncation from legitimate empty extraction |
| §2.6 UMLS | `UmlsClient` + `UmlsMatchScorer` + `UmlsCache` decorator (scoped per run) |
| §2.7 Pairing | `CriterionRecord` carries `NctId` end-to-end |
| §2.8 Persistence | `PostgresGateway.PersistTrialAsync` (per-trial `NpgsqlTransaction`) |
| §2.9 Metrics | `RunMetrics` row in `eligibility_run`; per-trial metrics in `eligibility_study` |
| §2.10 Notifications | `INotificationSink` + `SmtpNotificationSink` (MailKit) |
| §2.11 Stop/resume | `RunGate` + per-run `CancellationTokenSource` + dashboard Cancel button + `OnBatchCancelledAsync` SignalR event |
| §6.4 Failure modes — malformed JSON | Audit row records `parse_invalid_json` with finish_reason + completion_tokens diagnostics |
| §6.6 Observability | `eligibility_run` (per-batch) + `eligibility_study` (per-trial audit) + structured `ILogger<T>` |
| §9.1 Failed-trial retry gap | `eligibility_failed` table populated; Studies tab + Analysis processing-history surface the data; per-NCT-ID retry UI deferred |
| §9.7 UMLS caching gap | `UmlsCache` in-memory, scoped per run |
| §9.8 Structured error notification | `BatchResult.FailedNctIds` enumerates terminal failures; SMTP body lists them |
| §2.13 Per-trial study snapshot | `eligibility_study_detail` table (V5 migration); `CaptureStudySnapshotAsync` (orchestrator, best-effort) + `GetStudySnapshotAsync`; snapshot-first Analysis tab; CLI `backfill-details` |
| n/a (re-implementation enhancement) | Per-trial audit table `eligibility_study` (V2 migration), Analysis tab study card, Results tab paged browse, Studies tab filtered browse, live SignalR feed with cancel signalling |
| n/a (Authoring feature) | `authoring specification.md` — V6/V7/V10 migrations, `AuthoringController` + `/Authoring` area (dashboard tab, collapsible two-pane master-detail shell, four-tab editor, combined create modal), pgvector similarity search, criteria clustering with top-N filter and Normalize All, dedup helper for the authored criteria list, per-criterion source-record lineage (`authoring_criterion_source`) with an inline expander, CLI `embed-studies`. See section 2.9. |
| §11 Access control & auditing (spec) | V11/V12 migrations (`app_user`, `audit_log`, attribution columns); cookie + Google auth, four roles → `Read`/`AuthorWrite`/`PipelineOps`/`ManageUsers` policies + `FallbackPolicy`; `AccountController` (login / Google / bootstrap), `UsersController` (Manage Accounts, protected-Owner), `AuditWriter`, `IAccessRequestNotifier`. See section 2.10. |

---

## 7. Acceptance Checklist (§8)

The build is considered complete when:

1. Integration test against a staged AACT mirror and a live UMLS key produces ≥85% resolution on a 50-study sample, within ±15% row count of the Run 75 reference. ☐
2. Crash injection test (kill process mid-batch) followed by re-invocation produces zero duplicates and zero missing trials beyond the crash point. ☐
3. 500-study run completes without OOM (RSS held under 1 GB) and within the projected ~80–100 min envelope on the same hardware as Run 75. ☐
4. Web dashboard shows live progress for a running batch with SignalR latency under 1s. ☑ verified by dashboard end-to-end manual tests
5. Cancel button stops the active run and produces an `eligibility_study.status='cancelled'` row plus a `BatchCancelled` SignalR event. ☑ covered by integration tests
6. No API keys appear in log output across CLI or Web host. ☑ verified by `UmlsLogRedactionHandler` tests
7. All output rows conform to the spec section 2.8.1 schema; `match_score` is numeric end-to-end (not string-coerced). ☑
8. Every trial picked up by a batch leaves an audit row in `eligibility_study`; trials that produced zero output rows can be classified as `parse_empty` (LLM returned `[]`) vs `parse_invalid_json` (truncated/malformed) vs `llm_failed` (transport). ☑

---

*End of architectural specification.*

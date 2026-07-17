# Configuration

How this system is configured: every setting it reads, which file or secret
store owns it, and what a new user has to do to stand the app up locally or in
production.

> **Golden rule:** non-secret defaults live in checked-in JSON; secrets
> (connection strings, API keys, OAuth client secrets, the trigger token) live
> **only** in `.env` / user-secrets / environment variables and are never
> committed. The repo's `.gitignore` blocks `*.env` (but keeps
> [`.env.example`](../../.env.example)).

Related: spec §6.5 ("secrets MUST come from secret storage; secrets MUST NOT
appear in logs"), architecture §3.2 (options binding). For the database side of
configuration (what the connection strings point at) see
[database_schema.md](database_schema.md).

## Mental model: a layered override stack

All three entry points — the Web host, the CLI, and the integration tests —
compose configuration through the same stack. Sources are listed
**lowest precedence first**; anything lower can be overridden by anything
higher:

| # | Source | Holds | Committed? |
|---|--------|-------|------------|
| 1 | `contexts/eligibility/src/Shared/appsettings.Shared.json` | Cross-host non-secret defaults: `Llm`, `LlmNormalize`, `Embedding`, `Umls`, `Pipeline`, `Notifications:Smtp` (structure only) | ✅ yes |
| 2 | per-host `appsettings.json` | Host-specific keys: `Webhook`, `Web`, `AllowedHosts`, `Logging` | ✅ yes |
| 3 | `appsettings.{Environment}.json` | Local-dev / per-environment non-secret overrides | ✅ yes |
| 4 | User secrets (`dotnet user-secrets`) | Secrets, dev only | ❌ no (outside repo) |
| 5 | Environment variables (incl. values loaded from `.env`) | **Secrets and any override** | ❌ no |
| 6 | Command-line args | One-off overrides | n/a |

Two mechanisms make this work and are worth knowing:

- **`appsettings.Shared.json` is one physical file** at `contexts/eligibility/src/Shared/`, linked
  into each host's build output via `<None Include><Link>` in the `.csproj` /
  `.vbproj`. `SharedAppSettings.AddSharedAppSettings` inserts it at index 0 — the
  **lowest** precedence — so a single source of truth for `Llm`/`Umls`/etc. is
  shared across hosts, yet any host can still override one key without copying
  the rest. See [`SharedAppSettings.vb`](../../contexts/eligibility/src/EligibilityProcessing.Hosting/SharedAppSettings.vb).
- **`.env` → environment variables.** .NET's JSON providers do *not* do
  `${VAR}` substitution, so secrets can't be templated into `appsettings.json`.
  Instead, [`DotEnvLoader.LoadDotEnv()`](../../contexts/eligibility/src/EligibilityProcessing.Hosting/DotEnvLoader.vb)
  runs as the first line of each host's entry point, walks up from the working
  directory to find `.env`, and loads its `KEY=VALUE` pairs into the process
  environment **before** the host builds — so the standard environment-variable
  provider (source 5) overlays them onto the JSON. This works identically from
  Visual Studio (F5), `dotnet run`, and Docker.
  - **An environment variable that is already set wins; `.env` never overwrites it**
    (`NoClobber`). So `Postgres__ConnectionStringOutput=<somewhere> dotnet run -- migrate`
    does what it says, even when run from inside the repo where a `.env` exists. This
    matters because a developer `.env` usually points at production: before this was
    pinned, DotNetEnv's clobbering default meant the file silently won and the command
    talked to the wrong database, with no error and no log line. Nothing else changes -
    with nothing exported, `.env` still supplies every value, and a container has no
    `.env` at all.

### The double-underscore convention

A nested JSON key `Section:Sub:Key` maps to the environment variable
`Section__Sub__Key` (colon → double underscore). This is how a flat `.env`
file (or a container's env vars) reaches a nested config section. Examples:

| JSON key | Env / `.env` variable |
|----------|----------------------|
| `Postgres:ConnectionStringOutput` | `Postgres__ConnectionStringOutput` |
| `Umls:ApiKey` | `Umls__ApiKey` |
| `Notifications:Smtp:Password` | `Notifications__Smtp__Password` |
| `Auth:Google:ClientSecret` | `Auth__Google__ClientSecret` |

---

## Quick start (local development)

1. **Copy the template** and fill in real values:
   ```powershell
   Copy-Item .env.example .env
   ```
   Edit `.env` — at minimum you need the two `Postgres__…` connection strings,
   the `Llm__…` endpoint, and `Umls__ApiKey`. Everything else is optional (see
   the table below for what each unlocks).
2. **Leave the JSON files alone** unless you want to change a non-secret default
   (e.g. lower `Pipeline:LlmConcurrencyCap` for a small dev box — add it to
   `appsettings.Development.json`, not to `.env`).
3. **Run:**
   ```powershell
   dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web    # dashboard + trigger
   dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- run --count 10
   ```

If `.env` is missing the app still starts — it just falls back to JSON
defaults, which means blank secrets and a pipeline that can't reach Postgres /
the LLM. Email and Google sign-in degrade gracefully (see their rows below);
the trigger endpoint and the database do not.

> **Alternative to `.env`:** `dotnet user-secrets` works too (the
> `Microsoft.Extensions.Configuration.UserSecrets` package is referenced) and is
> only read in the `Development` environment. `.env` is the repo's primary path
> because it is wired explicitly and works the same in containers; pick one and
> be consistent.

---

## Non-secret settings (checked-in JSON)

These are safe to commit and live in the JSON files. Defaults shown are the
**effective** values from the committed JSON; where the JSON omits a key, the
C#/VB options-class default applies (noted as "code default").

### `Llm` — extraction LLM client · `appsettings.Shared.json`
[`LlmOptions.vb`](../../contexts/eligibility/src/EligibilityProcessing.Llm/LlmOptions.vb)

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | code default `http://localhost:8080/v1` | OpenAI-compatible endpoint. Set per-environment via `Llm__BaseUrl` in `.env`. **Points at the HAProxy pool** (`llm-proxy` compose service, `http://llm-proxy:8080/v1`) so the extraction call fans across the always-on GPU plus any intermittent ones currently up - see below. |
| `Model` | code default `gemma-4-26B-A4B-it-Q8_0` | Chat-completions model name. Usually set via `.env`. |
| `Temperature` | `0.3` | |
| `MaxTokens` | `30000` | Completion-token cap. For **reasoning** models this budget covers *both* the reasoning trace *and* the JSON output — too low and the model is cut off mid-think (`finish_reason=length`, empty content). Must also satisfy `prompt + MaxTokens ≤ per-slot context`, and per-slot context = server `--ctx-size` ÷ `--parallel`. Raising `Pipeline:LlmConcurrencyCap` + server slots for throughput shrinks per-slot context, so size the two together (e.g. 128k ctx ÷ 8 slots = 16k/slot — lower `MaxTokens` accordingly, or quantize the KV cache to afford more ctx). |
| `EnableReasoning` | `true` | **Master on/off for the `reasoning_effort` field.** `false` never sends it (and suppresses escalation) — treat the endpoint as a plain non-reasoning instruct model. `true` defers to `ReasoningEffort` + escalation below. Live-tunable as a checkbox in the Runtime Parameters panel. *(code default `true`.)* |
| `ReasoningEffort` | `low` | First-attempt `reasoning_effort`, sent **only when `EnableReasoning` is true and this is non-empty**. For "thinking" models (gpt-oss, o-series). `low` is fast and handles most trials; long trials bail with `[]` and are rescued by escalation (below). Values: `low` / `medium` / `high`. *(code default `medium` — the safe single-level value when no config is present.)* |
| `EnableReasoningEscalation` | `true` | When on, a trial whose first attempt parses to an empty array is retried once at `EscalateReasoningEffort` before being recorded as `parse_empty`. Lets the common case run at `low` while complex trials transparently get more reasoning. Only `empty_array` escalates — `invalid_json` (truncation) does not. Toggle this; the two effort levels can stay fixed. *(code default `false`.)* |
| `EscalateReasoningEffort` | `medium` | Effort for the escalation retry. No-op (no second call) when equal to `ReasoningEffort` or when escalation is disabled. |
| `NormalizeMaxTokens` | `8192` | Legacy fallback for the normalize call; prefer the `LlmNormalize` section. |
| `ContextLength` | `131072` | The model server's total context window in **tokens** (e.g. `Llm__ContextLength=131000`). Not used by the extraction pipeline. |
| `TimeoutSeconds` | `1200` | **Per-attempt** timeout (retry wraps timeout, each attempt gets the full budget). |
| `RetryCount` | `2` | |
| `RetryDelaySeconds` | `5` | |
| `ConcurrencyCap` | code default `8` | **Vestigial — not wired to anything.** The real parallelism throttle is `Pipeline:LlmConcurrencyCap` (below). Left only so existing config that sets `Llm:ConcurrencyCap` doesn't error; do not rely on it. |
| `ApiKey` | — | **Secret** — see below. |

#### The `llm-proxy` GPU pool (HAProxy)

`Llm__BaseUrl` points at the `llm-proxy` compose service, which pools the always-on GPU
with up to two intermittent ones. The variables below are read **only by
`docker-compose.yml`** (passed into the proxy container), never by the app -
`deploy/eligibility-pipeline/haproxy/haproxy.cfg` is the config.

| Variable | Default | Notes |
|-----|---------|-------|
| `LLM_GPU_PRIMARY_ADDR` | `192.0.2.1:1234` | `host:port` of the always-on GPU. |
| `LLM_GPU_PRIMARY_SLOTS` | `4` | Its concurrent-request capacity (llama.cpp `--parallel` / LM Studio max concurrent). Becomes HAProxy's per-server `maxconn`. |
| `LLM_GPU_AUX1_ADDR` / `_SLOTS` | `192.0.2.2:1234` / `4` | First intermittent GPU. Leave unset to disable. |
| `LLM_GPU_AUX2_ADDR` / `_SLOTS` | `192.0.2.3:1234` / `4` | Second intermittent GPU. |
| `LLM_PROXY_PORT` | `8090` | Host port for the pool. Only needed for a local `dotnet run`; containers use the service name. |
| `LLM_PROXY_STATS_PORT` | `8404` | Stats page (`http://<docker-host>:8404/`) - which GPUs are up and how loaded. No auth; LAN only. |

Things that will bite, all verified rather than assumed:

- **Every GPU must serve the same model with the same parameters.** Which one answers a
  trial is arbitrary and nothing records it, so a mismatch makes extraction quality vary
  by luck.
- **`*_SLOTS` must be a non-empty number.** HAProxy silently accepts an empty `maxconn`
  and treats it as *unlimited*, which would flood one card and defeat the point. Compose
  supplies a default and `haproxy.cfg` refuses to start on an empty value, but a wrong
  *number* is on you.
- **`LlmNormalize__BaseUrl` and `Embedding__BaseUrl` must stay explicitly set.** Both
  inherit `Llm__BaseUrl` when blank, which now means they would silently route through
  the pool. Neither wants it - normalize is not throughput-critical, and the pool's GPUs
  serve a chat model, not an embedding model.
- **Set `Pipeline:LlmConcurrencyCap` for all GPUs up** and let HAProxy queue when the aux
  ones are away. Over-subscription is benign (throughput stays slot-bound; only
  per-request latency inflates), and it self-adjusts with no restart. The cost: `llm_ms`
  then includes queue wait, so the phase-split telemetry gets muddier.
- Unset aux GPUs default to unroutable RFC 5737 addresses: they sit `DOWN` in the stats
  page and take no traffic. Nothing needs restarting when a GPU appears or vanishes -
  active health checks handle it, and `resolvers dns` in the config is what lets a
  *hostname*-configured GPU rejoin after being switched off (without it, HAProxy parks it
  in `MAINT (resolution)` permanently).

### `LlmNormalize` — `normalize-umls` override · `appsettings.Shared.json`
[`LlmNormalizeOptions.vb`](../../contexts/eligibility/src/EligibilityProcessing.Llm/LlmNormalizeOptions.vb)

A short prompt that often suits a smaller/faster model. Any unset key inherits
the matching `Llm` value, so an absent section preserves current behaviour.

| Key | Default | Notes |
|-----|---------|-------|
| `Temperature` | `0.9` | |
| `MaxTokens` | `8000` | Replaces `Llm:NormalizeMaxTokens`; raise if normalize returns empty with `finish_reason=length`. |
| `EnableReasoning` | `true` | **Master on/off for the normalize call's `reasoning_effort` field**, mirroring `Llm:EnableReasoning` but independent. `false` never sends it (non-reasoning normalize model). Live-tunable as a checkbox in the Runtime Parameters panel. |
| `ReasoningEffort` | `low` | OpenAI `reasoning_effort` for the normalize call, mirroring `Llm:ReasoningEffort`. Sent only when `EnableReasoning` is true and this is non-empty; set `""` to inherit `Llm:ReasoningEffort` (blank both to omit). |
| `TimeoutSeconds` | `60` | |
| `RetryCount` | `0` | |
| `RetryDelaySeconds` | `5` | |
| `BaseUrl` / `Model` / `ApiKey` | inherit `Llm:*` | Set via `.env` to point the normalizer at a different host/model. `ApiKey` is a **secret**. |

### `Embedding` — similarity index (`embed-studies`) · (section absent from JSON → all code defaults)
[`EmbeddingOptions.vb`](../../contexts/eligibility/src/EligibilityProcessing.Llm/EmbeddingOptions.vb)

Only needed for the corpus similarity index: the CLI / Tools-tab `embed-studies`
backfill that builds it, and the Authoring Analysis tab's "Find Similar" (which
embeds the authored study at query time to rank the corpus). The core extraction
pipeline does not use it, and the plain CRUD authoring workflow (create study,
edit criteria, export CSV) works without it. Normalize on the Analysis tab uses
the `Llm` endpoint. The shipped seed does not include the embedding index, so a
fresh quickstart must run Tools -> embed-studies (with these keys set) before
Find Similar returns results - or **import** a pre-built index (see below).

An owner can also **export/import** the built index without rebuilding it, from the
account menu's "Database seed & embeddings" dialog (Embeddings tab): export dumps
the index to a downloadable archive named with the model; import (from a file or a
release-asset URL) clears the existing index and loads the archive. Because cosine
similarity is only meaningful within one model, after importing set `Embedding:Model`
to the imported model. This is how a published embeddings release lights up Find
Similar on another instance with no embedding endpoint or backfill run.

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `""` → falls back to `Llm:BaseUrl` | Same server usually serves `/v1/embeddings`. |
| `ApiKey` | `""` → falls back to `Llm:ApiKey` | **Secret** when set explicitly. |
| `Model` | `""` | Embedding model name; set via `.env`. |
| `TimeoutSeconds` | `30` | |
| `RetryCount` | `2` | |
| `RetryDelaySeconds` | `2` | |
| `MaxInputChars` | `1500` | Truncates study text to stay under the model's sequence limit; `0` disables. |

### `Umls` — UTS REST client · `appsettings.Shared.json`
[`UmlsOptions.vb`](../../contexts/eligibility/src/EligibilityProcessing.Umls/UmlsOptions.vb)

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `https://uts-ws.nlm.nih.gov/rest` | |
| `PageSize` | `5` | |
| `TimeoutSeconds` | `10` | |
| `RetryCount` | `1` | |
| `RetryDelaySeconds` | `2` | |
| `Backend` | `rest` | Resolution backend: `rest` (UTS API; needs `ApiKey`) or `postgres` (local `umls.*` schema via `PostgresUmlsClient` — no API key/network at resolution time, sub-ms lookups). Both share the same `UmlsCache` + `UmlsMatchScorer`. Validate `postgres` vs the REST baseline with the CLI `umls-compare` command before switching. |
| `CandidateLimit` | `15` | (postgres backend) max candidate CUIs returned to the scorer per concept. |
| `TrigramThreshold` | `0.3` | (postgres backend) pg_trgm similarity floor for the fuzzy *typo-fallback* arm (FTS ts_rank is the primary ranker). |
| `MinQueryCoverage` | `0.6` | (postgres backend) precision guard: for a multi-word query, drop candidates covering less than this fraction of its significant tokens (stops generic short atoms like "Examination"/"Injection" beating the specific concept — and a 2-token query must match both tokens). `0` disables. |
| `RequireQueryCodeMatch` | `true` | (postgres backend) discriminative-token guard: when the query has numeric/code tokens ("10", "131I", "17p"), drop a candidate carrying a *conflicting* code (stops "4 meter" matching "10 meter"). Set `false` if concepts fold dosage into the name. |
| `MaxAtomLength` | `80` | (postgres backend) the fuzzy arms (FTS + trigram) ignore atoms longer than this and LOINC pipe-panel atoms — long survey questions / IUPAC names that share a token with the query and pollute matching. `0` disables. The exact arm is exempt. |
| `EnableTrigramFallback` | `true` | (postgres backend) score-aware trigram fallback: the expensive pg_trgm fuzzy arm (a full similarity scan of the 3.2M-atom table, ~250 ms/lookup) runs **only** when the cheap exact + FTS pass fails to resolve the concept (no candidate clears the scorer's 0.45 threshold). So the common path stays exact + FTS (~5× faster, beating the REST round-trip) and the fuzzy scan fires only on the would-be-unresolved minority — recovering the fuzzy arm's resolution lift without paying for it on every lookup. `false` disables the fuzzy arm entirely (max speed, lowest resolution). |
| `SourceVocabularies` | `SNOMEDCT_US, MSH, RXNORM, LNC, ICD10CM, MDR` | (code default) Curated SAB filter the `load-umls` command imports; empty loads all English atoms. The runtime query is vocabulary-agnostic. |
| `ApiKey` | — | **Secret** — see below. Required only for the `rest` backend. |

### `Pipeline` — orchestrator · `appsettings.Shared.json`
[`OrchestratorOptions.vb`](../../contexts/eligibility/src/EligibilityProcessing.Core/OrchestratorOptions.vb)

| Key | Default | Notes |
|-----|---------|-------|
| `LlmConcurrencyCap` | `8` | **The real concurrency throttle** — bound to `OrchestratorOptions.LlmConcurrencyCap` and used as `Parallel.ForEachAsync` `MaxDegreeOfParallelism` over trials. Should track the model server's aggregate `--parallel` slot count (spec §2.4.5); raise both together to lift GPU utilization. Because each worker also does UMLS + DB work per trial, a value slightly **above** the slot count keeps the server's decode batch full. (production reference: 8 = 2 backends × 4 slots.) **Live-tunable** by an Owner via the Runtime Parameters panel without a restart — the scoped orchestrator reads it at the start of each run, so an edit applies to the **next** run (not one in flight). The panel edit is transient and reverts to this value on restart. |
| `UseNormalizationCache` | `true` | When a criterion fails to resolve lexically, the orchestrator consults the `umls.concept_normalization` cache (built offline by the `normalize-umls` command) by normalized concept — a cheap batched indexed lookup, **no LLM** — and applies a cached resolution on first pass. So once a residue concept has been normalized once, every later run resolves it for free. `false` disables the inline consult (a harmless miss when the REST backend is active or the cache is empty). |

### `Notifications:Smtp` — email sink (structure only) · `appsettings.Shared.json`
[`SmtpNotificationOptions.vb`](../../contexts/eligibility/src/EligibilityProcessing.Notifications/SmtpNotificationOptions.vb)

Only the **non-secret** shape lives in JSON. The sink is **disabled** unless
`Notifications:Smtp:Host` is set — when blank the host registers a no-op sink
and the pipeline runs without email (notifications are once-per-batch, spec
§2.10).

| Key | Default | Where | Notes |
|-----|---------|-------|-------|
| `Port` | `587` | JSON | `587` = STARTTLS; for `465` set `UseStartTls=false`. |
| `UseStartTls` | `true` | JSON | |
| `FromName` | `Eligibility Pipeline` | JSON | |
| `Host` | `""` | **.env** | Setting this **enables** the SMTP sink. |
| `Username` / `Password` | `""` | **.env** | **Secrets.** |
| `FromAddress` | `""` | **.env** | |
| `ToAddresses` | `""` | **.env** | Comma-separated recipients. |
| `RetriggerUrl` | `""` | **.env** | Optional link embedded in completion mail (spec §2.10.1). |

### `Webhook` — trigger surface (`POST /trigger`) · Web `appsettings.json`
[`WebhookOptions.cs`](../../contexts/eligibility/src/EligibilityProcessing.Web/WebhookOptions.cs)
(section name kept as `Webhook` for back-compat; the host merged into Web)

| Key | Default | Where | Notes |
|-----|---------|-------|-------|
| `DefaultStudyCount` | `500` | JSON | Webhook trigger mode hard-codes 500 (spec §2.1). |
| `RateLimitPermits` | `1` | JSON | 1 trigger / window in production. |
| `RateLimitWindowSeconds` | `60` | JSON | |
| `Secret` | `""` | **.env** | **Secret.** Expected on the `X-Eligibility-Token` header. **When unset, `/trigger` rejects every request** — there is no implicit "no auth" path (spec §6.5). |

### `Web` - dashboard read caching (Web `appsettings.json`)
[`CorpusReadCache.vb`](../../contexts/eligibility/src/EligibilityProcessing.Core/CorpusReadCache.vb)

| Key | Default | Where | Notes |
|-----|---------|-------|-------|
| `CorpusCacheTtlSeconds` | `60` | JSON / .env | In-memory TTL for the two whole-corpus reads behind the Dashboard and Results pages (`GetDashboardMetricsAsync`, `GetEligibilityFilterOptionsAsync`). Both are aggregates that only move when a run persists trials, but were recomputed per page view (~700 ms and ~1150 ms respectively on the production corpus). **Set to `0` to disable caching and always read live.** |

Cache scope is the process, not a shared store: the web host is a singleton
(`container_name` is pinned in `deploy/eligibility-pipeline/docker-compose.yml`,
and SignalR requires the orchestrator in the hub's process), so there is no
second instance to stay coherent with. The CLI's `status` command is
deliberately uncached and always reads live.

### `Auth` — Web sign-in · Web `appsettings.json` + `.env`
[`AuthOptions.cs`](../../contexts/eligibility/src/EligibilityProcessing.Web/Auth/AuthOptions.cs)

| Key | Default | Where | Notes |
|-----|---------|-------|-------|
| `CookieExpiryHours` | `8` | JSON / .env | Sliding cookie lifetime. |
| `Google:ClientId` | `""` | **.env** | Enables the "Sign in with Google" button. |
| `Google:ClientSecret` | `""` | **.env** | **Secret.** Both Google values must be present to enable Google sign-in; password sign-in works without them. Register `<web-base-url>/signin-google` as the authorized redirect URI in Google Cloud Console. |

### `Logging` / `AllowedHosts` · per-host `appsettings.json`

Standard ASP.NET Core logging filters and host filtering. Tune per environment
in `appsettings.{Environment}.json`. No secrets.

#### Seeing SQL and LLM/HTTP calls in the logs

All off by default. Set any of these (env or `.env`) and read `docker compose logs`:

| Variable | Shows |
|-----|-------|
| `Logging__LogLevel__Default=Debug` | **Everything below at once** - the one switch. |
| `Logging__LogLevel__Npgsql=Information` | Every SQL command + duration (`Command execution completed (duration=16ms): SELECT ...`). |
| `Logging__LogLevel__Npgsql=Debug` | Also logs each command as it *starts*, so a hung query is visible. |
| `Logging__LogLevel__System.Net.Http.HttpClient=Information` | LLM / UMLS / embedding calls: URL, status, timing. |
| `Logging__LogLevel__Polly=Information` | Retry attempts. |

Two things make this work, and both are load-bearing:

- **The data sources carry the host's `ILoggerFactory`** (`CompositionRoot.BuildDataSource`).
  `NpgsqlDataSource.Create()` attaches none, so before this the `Npgsql` category was
  dead no matter what level was set - there was nothing to turn on.
- **The code-level pins are defaults, not overrides.** `CompositionRoot` pins `Npgsql`,
  `System.Net.Http.HttpClient` and `Polly` to `Warning` **in code** (not only in
  `appsettings.json`) so the suppression holds even when a host is launched from a
  directory where `appsettings.json` is not found. But a code `AddFilter` **beats
  configuration** for the same category, so pinning unconditionally made those
  categories unreachable. Each pin is now skipped when configuration names that category
  explicitly, or when `Logging:LogLevel:Default` is `Debug`/`Trace`. See
  `DebugLoggingTests` for the matrix.

**Parameter values are never logged**, deliberately: Npgsql's `EnableParameterLogging`
is not enabled, because parameters here carry criteria text, raw LLM responses, user
emails and password hashes. You get command text, not data.

**Making SQL logging usable.** On its own it is unreadable: one entry per command, and
each entry spans as many lines as its query has newlines (the dashboard query alone is
~40). Three settings fix that, and they belong together:

| Variable | Effect |
|-----|-------|
| `Postgres__SlowCommandLogThresholdMs=50` | Drop every command faster than 50ms. This is what turns a firehose into a list of outliers. |
| `Logging__Console__FormatterName=simple` | Required for the next one to bind. |
| `Logging__Console__FormatterOptions__SingleLine=true` | Category and message on one line. Affects all log output, not just SQL. |

Result - one grep-able line per slow query, SQL collapsed and truncated:

```
info: Npgsql.Command[2001] Command execution completed (duration=90ms): SELECT name FROM ctgov.conditions WHERE nct_id = $1 ORDER BY name
```

The filtering reads Npgsql's **structured** `DurationMs` field, never the message text
(`SlowCommandLoggerFactory`), and forwards the original state untouched - a structured
sink still sees `CommandText` / `DurationMs` / `ConnectorId` as fields; only the console
rendering changes.

**This is a firehose.** HttpClient logging is per-request and Npgsql per-command; a real
batch runs 8+ trials in flight and thousands of commands. It is for diagnosis, not for
leaving on.

---

### `Postgres` — data-source tuning (non-secret) · (section absent from JSON → all code defaults)

The connection strings themselves are secrets (see below); these two tunables
shape how the source (AACT) connection behaves during trial selection. Both have
code defaults in `PostgresOptions`; add a `Postgres` section to a host's
`appsettings.json` only to override.

| Key | Default | Notes |
|-----|---------|-------|
| `MaxStudyCount` | `5000` | Upper bound on a single batch's `StudyCount`. A larger request is clamped (with a warning) inside `SelectNextTrialsAsync`, so a fat-fingered value (e.g. `10000`) can't turn the source anti-join into a multi-minute scan. `0` disables the clamp. |
| `SourceCommandTimeoutSeconds` | `300` | Command timeout applied to the **source** data source (the trial-selection scan + exclusion-set COPY). Replaces Npgsql's 30s default, which surfaced a slow selection as a fatal `Exception while reading from stream`. `0` means no timeout. |
| `SlowCommandLogThresholdMs` | `0` | Only log SQL commands taking at least this many ms; `0` logs every command. Only relevant once SQL logging is on. Without it, SQL logging is unusable: one entry per command, thousands per batch. Applies only to commands Npgsql has **timed** - the Debug "Executing command" event has no duration yet and always passes through, because it is the only trace a **hung** query leaves. |
| `InterruptedStudyThresholdHours` | `6` | Age beyond which an `eligibility_study` row still at `status='running'` is assumed orphaned by a killed host and reconciled to `interrupted` at **web-host startup**. `0` or less disables the sweep. **Do not lower this below ~3h without also lowering `Llm:TimeoutSeconds`/`Llm:RetryCount`:** one trial's worst case is ~2h (3 LLM attempts x 1200s, per-attempt, doubled by reasoning escalation), and the CLI can process trials against the same database concurrently with no cross-process lock - the age gate is the only thing keeping the sweep off live rows. A trial wrongly swept self-corrects when it finishes. |

> Fast selection also relies on a partial index, `ix_eligibilities_selectable_nct_id`,
> that the app creates on startup when the source and output databases are
> co-located (same host/port/db). See `PostgresGateway.EnsureSourcePerformanceIndexesAsync`.

---

## Secrets (never committed)

These live in `.env` (or user-secrets / real environment variables) **only**.
The committed template is [`.env.example`](../../.env.example) — copy it to
`.env` and fill in real values. Required vs. optional:

| Variable | Required? | Unlocks |
|----------|-----------|---------|
| `Postgres__ConnectionStringSource` | **Required** | Read-only access to AACT (`ctgov.eligibilities`). |
| `Postgres__ConnectionStringOutput` | **Required** | Read/write access to the eligibility output DB. |

> **Connection pool sizing.** Append `;Maximum Pool Size=N` to each connection string. Npgsql pools default to 100 connections *per data source*, so the web host plus every parallel CLI tool (`run` / `normalize-umls` / `embed-studies`) can collectively exceed the server's `max_connections` and trip `53300: sorry, too many clients already`. Cap each pool so the **sum across all processes** stays under the server limit (default 100, minus a few reserved). `~20` suits a CLI batch at the default concurrency (8); with a cap set, a busy worker waits for a free connection (up to `Timeout`, default 15s) instead of erroring. Raise the cap and `--concurrency` together only if you also raise the server's `max_connections`.
| `Llm__BaseUrl` / `Llm__Model` | **Required** (env-specific) | Points the extraction client at your endpoint/model. |
| `Llm__ApiKey` | If endpoint authenticates | Bearer token for the LLM endpoint (`none` for unauthenticated). |
| `Umls__ApiKey` | **Required** for UMLS resolution | UTS REST API key. |
| `LlmNormalize__BaseUrl/ApiKey/Model` | Optional | Point the `normalize-umls` call at a smaller model; else inherits `Llm__*`. |
| `LlmNormalize__ReasoningEffort` | Optional | Reasoning effort for the normalize call; defaults to `low`, `""` inherits `Llm__ReasoningEffort`. |
| `Embedding__BaseUrl/ApiKey/Model` | Optional | Only for the `embed-studies` backfill that builds the similarity index. |
| `Notifications__Smtp__Host` (+ `Username`/`Password`/`FromAddress`/`ToAddresses`/`RetriggerUrl`) | Optional | Enables batch email notifications; omit `Host` to disable. |
| `Webhook__Secret` | Required to use `POST /trigger` | Shared secret for the `X-Eligibility-Token` header. |
| `Auth__Google__ClientId` / `Auth__Google__ClientSecret` | Optional | "Sign in with Google" button (password sign-in works without). |
| `Auth__CookieExpiryHours` | Optional | Override the 8-hour sliding cookie lifetime. |

Connection-string format (Npgsql):
`Host=…;Port=5432;Database=…;Username=…;Password=…`.

---

## Production deployment

In containers there is usually **no `.env` file inside the image** — set the
same `KEY=VALUE` pairs as real environment variables. `DotEnvLoader` no-ops when
no `.env` is found, and the environment-variable provider picks the values up
directly. With docker-compose, point an `env_file:` at a `.env`-format file
(the same double-underscore convention applies) or list them under
`environment:`. See `deploy/eligibility-pipeline/docker-compose.yml`.

Checklist for a production host:

- All **Required** secrets above are set as environment variables.
- `Webhook__Secret` is a long random string (the trigger is open to the network
  otherwise — and rejects everything if the secret is blank).
- Non-secret production overrides (tighter `AllowedHosts`, production
  `Logging` levels) go in `appsettings.Production.json` — create it only if you
  actually need static overrides; otherwise env vars suffice.
- Secrets never appear in logs (spec §6.5; the UMLS client has a redaction
  handler).

---

## Where each setting is read in code

- Options binding for the pipeline (`Postgres`, `Llm`, `LlmNormalize`,
  `Embedding`, `Umls`, `Pipeline`, `Notifications:Smtp`):
  [`CompositionRoot.vb`](../../contexts/eligibility/src/EligibilityProcessing.Hosting/CompositionRoot.vb).
- Web-only sections (`Webhook`, `Auth`) and `.env` loading order:
  [`Program.cs`](../../contexts/eligibility/src/EligibilityProcessing.Web/Program.cs).
- Shared-file precedence:
  [`SharedAppSettings.vb`](../../contexts/eligibility/src/EligibilityProcessing.Hosting/SharedAppSettings.vb).
- `.env` discovery:
  [`DotEnvLoader.vb`](../../contexts/eligibility/src/EligibilityProcessing.Hosting/DotEnvLoader.vb).

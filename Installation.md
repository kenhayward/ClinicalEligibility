# Installation

How to stand up the ClinicalEligibility pipeline on a fresh machine and populate
it with data, end to end. Follow the steps in order - later steps depend on
earlier ones.

For the conceptual tour see the [README](README.md). For the exhaustive
configuration reference see [`docs/specs/configuration.md`](docs/specs/configuration.md).

---

## What you will end up with

A PostgreSQL database holding every schema the pipeline uses, and two .NET hosts
(a CLI and a web dashboard):

| Schema | Owner | Populated by |
| :--- | :--- | :--- |
| `ctgov.*` | AACT (read-only source) | Step 2 (restore the AACT static copy) |
| `public.*` | eligibility pipeline (output + corpus) | Steps 4, 6, 7 |
| `umls.*` | local UMLS resolver (optional) | Step 5 (optional) |
| `app_user`, `audit_log` | auth / audit | Step 4 (migrate) + Step 8 (bootstrap) |

---

## Prerequisites

- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** 8.0.318 or
  later (pinned in [`global.json`](global.json)).
- **PostgreSQL 16 or 17** with the **`pgvector`** and **`pg_trgm`** extensions
  available. The [`pgvector/pgvector`](https://hub.docker.com/r/pgvector/pgvector)
  Docker image bundles both. The PostgreSQL **client tools** (`psql`,
  `pg_restore`) are needed to load AACT.
- An **OpenAI-compatible LLM endpoint** (chat completions, `/v1/chat/completions`).
  Production reference: llama.cpp serving Gemma behind nginx.
- An **OpenAI-compatible embeddings endpoint** (`/v1/embeddings`) - only needed to
  build the corpus similarity index (Step 7). The same server if it serves an
  embedding model, or a second one.
- A **UMLS UTS API key** ([request one, free with a UMLS license](https://uts.nlm.nih.gov/uts/signup-login)).
- An **AACT** data source - a downloaded static copy (recommended) or network
  access to [Duke's public AACT server](https://aact.ctti-clinicaltrials.org/) (Step 2).
- *(Optional)* **Docker**, for the integration test suite and the container deploy.

Clone and build first:

```powershell
git clone https://github.com/kenhayward/ClinicalEligibility.git
cd ClinicalEligibility
dotnet build contexts/eligibility/Eligibility.sln
```

---

## Step 1 - PostgreSQL and extensions

Stand up a PostgreSQL 16+ server and create the database. Using Docker:

```powershell
docker run -d --name eligibility-pg `
  -e POSTGRES_PASSWORD=postgres `
  -p 5432:5432 `
  pgvector/pgvector:pg16

# Create the database and the extensions (the pipeline migrations also create the
# extensions, but creating them up front keeps the AACT restore self-contained).
psql "host=localhost port=5432 user=postgres password=postgres dbname=postgres" `
  -c "CREATE DATABASE clinical;"
psql "host=localhost port=5432 user=postgres password=postgres dbname=clinical" `
  -c "CREATE EXTENSION IF NOT EXISTS vector; CREATE EXTENSION IF NOT EXISTS pg_trgm;"
```

> **Single-DB recommendation.** Restoring AACT into the same database as the
> output schema lets the app reuse one connection and enables a startup
> performance index on the source. If you only ever run the extraction pipeline,
> AACT may instead be an external server you only read from (Step 2, option B).

---

## Step 2 - Load AACT (the `ctgov` schema)

The pipeline reads trial text from AACT's `ctgov.*` tables. Pick one option.

### Option A (recommended) - restore the AACT static copy

1. Download the latest **"Daily Static Copy of AACT Database"** (a PostgreSQL
   custom-format dump) from the
   [AACT download page](https://aact.ctti-clinicaltrials.org/download). Unzip it
   to get the dump file (e.g. `postgres.dmp`).
2. Restore it into `clinical`. The dump creates the `ctgov` schema and its tables:

   ```powershell
   pg_restore --no-owner --no-privileges --verbose `
     --dbname "host=localhost port=5432 user=postgres password=postgres dbname=clinical" `
     .\postgres.dmp
   ```

   (Exact flags can vary by release; follow the instructions bundled with the
   download if they differ. The goal: the `ctgov.*` tables exist in `clinical`.)
3. Sanity check:

   ```sql
   SELECT count(*) FROM ctgov.studies;          -- ~500k+
   SELECT count(*) FROM ctgov.eligibilities;     -- ~400k+
   ```

The pipeline only reads a handful of `ctgov` tables (`studies`, `eligibilities`,
`brief_summaries`, `conditions`, `interventions`) - see
[`docs/specs/database_schema.md`](docs/specs/database_schema.md).

### Option B - point at Duke's public AACT server

Quicker to start, but slower per query. Set `Postgres__ConnectionStringSource`
(Step 3) to Duke's public read-only host instead of restoring anything. Request
public-database credentials from the [AACT site](https://aact.ctti-clinicaltrials.org/).

---

## Step 3 - Configure `.env`

```powershell
Copy-Item .env.example .env
notepad .env
```

Fill in, at minimum:

- `Postgres__ConnectionStringSource` - the AACT `ctgov` source. For the single-DB
  setup this points at `clinical` (same as below); for Option B it points at
  Duke's server.
- `Postgres__ConnectionStringOutput` - the output database.
- `Llm__BaseUrl` / `Llm__ApiKey` / `Llm__Model` - the chat-completions endpoint.
- `Umls__ApiKey` - your UTS key.

`Embedding__*` is needed only for Step 7. Everything else
(`LlmNormalize__*`, `Notifications__Smtp__*`, `Webhook__Secret`,
`Auth__Google__*`) is optional - the [`.env.example`](.env.example) comments say
what each unlocks.

---

## Step 4 - Create the output schema

Apply the pipeline's migrations. This creates `public.*`, the `app_user` /
`audit_log` tables, and an empty `umls` schema. Idempotent.

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- migrate
```

---

## Step 5 - (Optional) Load the local UMLS store

By default UMLS resolution uses the **UTS REST API** (`Umls__Backend=rest`, just
needs `Umls__ApiKey`) - you can skip this step. For faster, offline, higher-recall
resolution you can load a curated UMLS subset into the local `umls.*` schema and
set `Umls__Backend=postgres`. This needs the unpacked UMLS release files
(`MRCONSO.RRF` + `MRSTY.RRF`):

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- `
  load-umls --rrf-dir D:\umls\2025AB\META
```

Full build/dump/restore runbook:
[`deploy/eligibility-pipeline/umls-loader.md`](deploy/eligibility-pipeline/umls-loader.md).

---

## Step 6 - Populate the output (run the extraction pipeline)

Run the pipeline to extract + UMLS-code eligibility criteria into
`public.eligibility`. Each batch processes N untouched trials; the pipeline is
trial-idempotent and crash-resumable, so just run it repeatedly (or via the
dashboard / `POST /trigger`) until you have the coverage you want.

```powershell
# One batch (default 10). Repeat, or raise --count, to build coverage.
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- run --count 50

# Check progress at any time.
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- status
```

If you processed trials before snapshots existed, backfill their metadata:

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- backfill-details
```

---

## Step 7 - (Optional) Build the similarity index

Embed each processed study's topic text into
`public.eligibility_study_embedding`. Idempotent - only fills gaps; safe to re-run
after each batch in Step 6.

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- embed-studies
```

---

## Step 8 - Run the dashboard and create the first account

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web
```

On first visit, the empty `app_user` table redirects you to a one-time
**bootstrap** form that creates the **Owner** account (user-ID + password). Every
subsequent visitor must sign in; an admin adds further accounts from **Manage
Accounts**.

---

## The sequence at a glance

| # | Step | Command / action | Required? |
| :--- | :--- | :--- | :--- |
| 1 | PostgreSQL + extensions | `CREATE DATABASE` + `vector` / `pg_trgm` | yes |
| 2 | Load AACT (`ctgov`) | `pg_restore` the static copy (or point at Duke's) | yes |
| 3 | Configure `.env` | `Copy-Item .env.example .env` | yes |
| 4 | Output schema | `cli migrate` | yes |
| 5 | Local UMLS store | `cli load-umls` (else use REST backend) | optional |
| 6 | Populate output | `cli run` (repeat) + `cli backfill-details` | yes |
| 7 | Similarity index | `cli embed-studies` | optional |
| 8 | Owner + dashboard | `...Web` (bootstrap) | yes |

---

## Verification

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- status
```

```sql
-- Source loaded?
SELECT count(*) FROM ctgov.eligibilities;
-- Output populated?
SELECT count(*) FROM public.eligibility;            -- extracted rows
SELECT count(*) FROM public.eligibility_study;       -- processed trials (audit)
```

The pipeline's acceptance benchmark (Run 75): 50 studies -> ~374 rows -> ~88%
UMLS resolution. A run within +/-15% rows and +/-3pp resolution on the same input
passes.

---

## Production / container deploy

The runbook above is for local development. For containerised deployment use the
Docker stack (Dockerfiles, a `docker-compose.yml`, and a `deploy.ps1`):
[`deploy/eligibility-pipeline/`](deploy/eligibility-pipeline/).

It expects an **external** PostgreSQL configured via
`Postgres__ConnectionStringOutput` in `.env`; nothing embeds the database. The
same data-population steps (2, 4-7) apply - run the CLI commands inside the
long-lived `eligibility-cli` tools container (`docker exec`).

---

## Troubleshooting

- **"too many clients already" (Postgres 53300).** Bound each pool with
  `Maximum Pool Size=N` in the connection strings (the `.env.example` explains
  the sizing). The sum across all running processes must stay under
  `max_connections`.
- **`migrate` / startup says the database is unavailable.** Check
  `Postgres__ConnectionStringOutput` and that the server is reachable.
- **Low UMLS resolution.** Confirm `Umls__ApiKey` (REST) or that Step 5 loaded the
  local store; use `cli umls-compare` to compare the two backends.

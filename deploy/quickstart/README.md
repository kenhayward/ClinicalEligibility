# Quickstart - try it with one `docker compose up`

This is the "kick the tyres" stack. One command brings up a **self-contained**
ClinicalEligibility - embedded Postgres, the schema, the pre-inferenced seed
corpus, and the dashboard - with no external dependencies. You get a browsable,
already-processed dataset without re-running the LLM pipeline (which is months of
compute and ~1B tokens for the full corpus).

For a real deployment against your **own** external database, use
[`../eligibility-pipeline/docker-compose.yml`](../eligibility-pipeline/docker-compose.yml)
instead - that's the production topology.

## What comes up

```
postgres (pgvector:pg18)            embedded DB, host port 5433
  -> migrate     (elig migrate)     applies the schema (all embedded migrations)
     -> seed-loader                 restores the pre-inferenced seed DATA
        -> web                      the dashboard, http://localhost:8091
```

`depends_on` conditions enforce that order, so the dashboard only starts once the
schema exists and the seed is loaded. The one-shot `migrate` and `seed-loader`
containers run once and exit; `postgres` and `web` stay up.

## Usage

```bash
cd deploy/quickstart
cp .env.example .env
# edit .env: set SEED_URL to the published GitHub Release asset(s),
#            OR SEED_FILE for a local dump (see "Seed source" below)
docker compose up -d
docker compose logs -f seed-loader     # watch the restore (a few minutes)
```

Then open **http://localhost:8091**. On first visit you bootstrap an Owner
account; after that you can browse the seeded corpus (Analysis, History, etc.).

The Postgres is also reachable directly on `localhost:5433` (db `clinical`, user
`postgres`, password from `.env`).

> **Just browsing needs no LLM/UMLS config.** The dashboard validates those
> lazily, only when you actually launch a new extraction. To run *new* inference
> you'd add `Llm__*` / `Umls__*` settings and point
> `Postgres__ConnectionStringSource` at a real AACT database.

> **Authoring demo (`Authoring` in the nav).** Design a new study - optionally
> seeded from an AACT trial's snapshot - hand-build its eligibility criteria, and
> export them as CSV. This works out of the box. The Analysis tab's "Find Similar"
> (mine the seeded corpus for similar trials, cluster their criteria, LLM-normalize
> them) additionally needs the `eligibility_study_embedding` index, which the seed
> does **not** include. Either set `Embedding__*` / `Llm__*` in `.env` and run
> **Tools -> embed-studies** to build it, or import a pre-built index from the owner
> account menu ("Database seed & embeddings" -> Embeddings tab -> Import).

### Seed source (set one in `.env`)

- **`SEED_URL`** - a GitHub Release asset URL. If the dump was byte-split because
  it exceeds GitHub's 2GB per-asset limit, list every part in order (whitespace or
  newline separated); the loader concatenates them before restoring.
- **`SEED_FILE`** - path *inside the loader container* to a bind-mounted local
  dump. Set `SEED_LOCAL_DIR` to the host directory holding it and uncomment the
  `seed-loader` `volumes:` block in `docker-compose.yml`.

`SEED_FILE` wins if both are set. Set `SEED_SHA256` to verify integrity, and
`SEED_FORCE=1` to reload over an already-seeded DB.

## What the seed contains

Six tables, **data only** (the migration framework owns the schema):

| Table | What it is |
|-------|------------|
| `eligibility` | the structured, UMLS-coded criteria (the payload) |
| `eligibility_study` | per-trial audit rows; also the anti-join set that makes the pipeline treat these trials as already-processed |
| `eligibility_study_detail` | AACT source snapshots for the dashboard's Analysis tab |
| `eligibility_run` | run history |
| `eligibility_failed` | trials whose extraction failed (for re-run from the dashboard) |
| `eligibility_umls_retry` | UMLS-retry bookkeeping |

It deliberately **excludes**: `eligibility_study_embedding` (large; rebuild it with
`elig embed-studies`, or import a pre-built index from the owner "Database seed &
embeddings" dialog - see the Authoring note above), the licensed `umls.*`
Metathesaurus (load it yourself with `elig load-umls`), and PII (`app_user`,
`audit_log` - the app bootstraps a fresh Owner on first run).

## Tear down

```bash
docker compose down        # keep the data volume
docker compose down -v     # also wipe the DB (next up re-seeds from scratch)
```

## Producing the seed asset (maintainers)

The seed is a `pg_dump -Fc --data-only` of the six tables above, taken from a
fully-inferenced database. See [`make-seed.sh`](make-seed.sh) for the exact
command. Split large output for GitHub with:

```bash
split -b 1900m seed.dump seed.dump.part-      # -> seed.dump.part-aa, -ab, ...
sha256sum seed.dump                            # publish alongside the asset
```

> **Licensing note (gate before publishing a Release):** the `eligibility` table
> carries UMLS-derived `concept_code` (CUI) and `umls_name` (preferred term).
> Confirm redistribution is permitted under the UMLS Metathesaurus License before
> publishing the asset publicly. Conservative fallback: null `umls_name` and keep
> `concept_code`.

# UMLS local-store loader & yearly refresh runbook

The `Umls:Backend = "postgres"` resolver reads a local copy of a curated UMLS
subset (the `umls` schema, migration V17) instead of the remote UTS REST API.
This makes resolution a sub-millisecond local query and lets us match against
**all** synonym atoms (not just the API's preferred term).

Embedding the corpus (Phase 2) is GPU-bound and one-time; the production target
(pgEdge `clinical`) has no GPU. So the model is **build on a GPU box → dump →
restore to target**. Phase 1 (lexical) needs no GPU anywhere.

UMLS releases twice a year (e.g. `2025AA`, `2025AB`). Repeat this runbook per
release.

---

## 0. Prerequisites (one-time)

- A **UTS / UMLS license** (the same account whose API key backs the `rest`
  backend). Required to download the Metathesaurus.
- Download the **UMLS Full Release** for the target version from the NLM and
  unpack it. You only need two files from the `META` directory:
  `MRCONSO.RRF` (atoms) and `MRSTY.RRF` (semantic types). The loader filters by
  source vocabulary itself (`Umls:SourceVocabularies`), so you can point it at
  the full `MRCONSO.RRF` — no MetamorphoSys subsetting required.
- `pg_dump` / `pg_restore` (Postgres client tools) on the build box.

---

## 1. Build (on the GPU / high-performance box)

Point the CLI at a **local/staging** Postgres that has `pgvector` + `pg_trgm`
(e.g. the `pgvector/pgvector:pg16` image). Set `Postgres__ConnectionStringOutput`
to that staging DB.

```powershell
# Create the umls schema (+ all other migrations; idempotent)
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- migrate

# Parse the release and bulk-load the curated subset. Full rebuild each run.
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- load-umls --rrf-dir D:\umls\2025AB\META

# Phase 2 only: generate concept embeddings (GPU). Resumable; safe to re-run.
# dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- embed-umls
```

`load-umls` prints atom / concept / semantic-type counts **and asserts that every
loaded concept has a semantic type**. It exits `4` if any do not - a non-zero exit
here means the umls schema is not fit to ship, whatever the printed counts say.
(Exit codes: 1 usage, 2 unhandled exception, 3 cancelled, 4 incomplete load.)

Note that `umls.semantic_type` is a **superset** of `umls.concept`: semantic types
are loaded for every CUI in MRSTY, not just the curated vocabularies, because
`public.eligibility` contains CUIs the REST backend resolved from outside the six
configured SABs. Expect roughly 3.9M semantic-type rows against 1.27M concepts.

```sql
SELECT count(*) FROM umls.atom;            -- millions (curated subset)
SELECT count(*) FROM umls.concept;         -- ~ unique CUIs loaded
SELECT count(*) FROM umls.semantic_type;   -- ~3.9M (all of MRSTY)
-- The real check: every concept must have a semantic type. Expect 0.
SELECT count(*) FROM umls.concept c
 WHERE NOT EXISTS (SELECT 1 FROM umls.semantic_type s WHERE s.cui = c.cui);
SELECT cui, pref_name, root_source FROM umls.concept WHERE cui = 'C0020615';  -- Hypoglycemia
```

### Repairing a partial semantic-type load

If `umls.semantic_type` is short but atoms and concepts are healthy, reload
semantic types alone rather than rebuilding everything:

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- `
  load-umls --rrf-dir D:\umls\2025AB\META --semantic-types-only
```

This skips the TRUNCATE and the MRCONSO pass, so `umls.atom` and `umls.concept`
are untouched and resolution keeps working throughout. Only `MRSTY.RRF` needs to
be present, and the load is idempotent - re-running it converges.

Takes ~60s for the full ~3.9M rows, which is why `Postgres:OutputCommandTimeoutSeconds`
exists; Npgsql's 30s default kills it mid-INSERT.

> **This happened.** A May 2026 load left `umls.semantic_type` with 100 rows
> covering 49 CUIs against 1,265,171 concepts and reported success. It went
> unnoticed for two months, during which 3.48M `public.eligibility` rows were
> written with no semantic type. The assertion above exists so that a partial
> load fails loudly instead of shipping.

### Validate before shipping

Run a sample of real concepts through both backends and compare (needs
`Umls__ApiKey` for the REST side):

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- umls-compare --count 200
```

Read the resolution-rate delta and eyeball the "Postgres-only wins" (the lift)
and "REST-only (possible PG regressions)" + "Different CUI" (precision) lists.

---

## 2. Dump (build box)

Dump **only** the `umls` schema, custom-format + compressed:

```powershell
pg_dump -Fc -n umls -d "<staging-connection>" -f umls_2025AB.dump
```

The HNSW index (Phase 2) is intentionally **not** relied upon from the dump — it
is rebuilt on the target (faster and cleaner; see step 3).

---

## 3. Restore (target / production `clinical`)

The target needs the `vector` + `pg_trgm` extensions (already present — V7/V8).
Restore the schema, replacing any previous release:

```powershell
pg_restore --clean --if-exists -n umls -d "<target-connection>" umls_2025AB.dump
```

Phase 2 only — rebuild the HNSW index after restore (skip for Phase 1):

```sql
-- CREATE INDEX IF NOT EXISTS ix_umls_concept_embedding_hnsw
--   ON umls.concept_embedding USING hnsw (embedding vector_cosine_ops);
```

> The pipeline's `cli migrate` also creates an empty `umls` schema (V17) — that
> is harmless; the restore overwrites it. Order doesn't matter.

---

## 4. Switch the backend

Flip the resolver to the local store (transient panel toggle is **not** wired
for this — it is a host config / restart):

```
Umls__Backend=postgres
```

Run a real batch and confirm on the dashboard:
- resolution rate is at or above the REST baseline (no precision regression), and
- the **UMLS** phase in the Runs table's `LLM/UMLS/Pst (s)` column collapses
  toward ~0 (lookups are now local).

Validate against the Run 75 acceptance bar (±15% rows / ±3pp resolution) before
making `postgres` the default.

To roll back: set `Umls__Backend=rest` and restart. No data is lost — the umls
schema just stops being read.

---

## 5. Yearly / per-release refresh

Repeat steps 1–4 with the new release directory. `load-umls` TRUNCATEs and
repopulates, so it is a clean full rebuild; the dump/restore replaces the target
schema in place. Nothing else changes.

`--semantic-types-only` is the exception: it repairs `umls.semantic_type` in
place without touching atoms or concepts, and is not part of a release refresh.

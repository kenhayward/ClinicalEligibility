-- V7: per-study topic embeddings for the Authoring feature's similarity
-- search (authoring specification §3.3, §4.4).
--
-- One row per processed AACT trial: a vector embedding of the study's topic
-- text (title + summary + conditions + interventions), used to rank studies
-- by semantic similarity to a proposed authored study.
--
-- The embedding column is declared as bare `vector` (no fixed dimension) on
-- purpose: it keeps the schema independent of whichever embedding model is
-- configured. Every row is written by one model (recorded in `model`), so the
-- cosine-distance operator always compares same-dimension vectors. At the
-- current corpus size (~22k studies) an exact sequential KNN scan is well
-- under 100 ms, so no HNSW index is created — and a dimensionless column
-- could not be HNSW-indexed anyway. If the corpus grows by orders of
-- magnitude, a later migration can pin the dimension and add an index.
--
-- Idempotent — CREATE IF NOT EXISTS so re-running EnsureSchemaAsync is safe.

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS public.eligibility_study_embedding (
    nct_id      text        NOT NULL PRIMARY KEY,
    embedding   vector      NOT NULL,
    model       text        NOT NULL,
    source_text text,
    embedded_at timestamptz NOT NULL DEFAULT now()
);

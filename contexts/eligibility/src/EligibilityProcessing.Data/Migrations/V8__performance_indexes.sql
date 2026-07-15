-- V8: performance indexes for the 20x-growth horizon.
--
-- Two concerns at scale:
--
--  1. Authoring similarity search. V7 deliberately shipped the embedding
--     table without a vector index — an exact KNN sequential scan is fine at
--     ~20k studies. At an order of magnitude more it is not, so this
--     migration pins the embedding dimension (HNSW requires a fixed
--     dimension) and builds an HNSW index for the cosine-distance operator
--     (<=>) that FindSimilarStudiesAsync orders by.
--
--     The dimension is pinned to 1024, matching every row currently written
--     by the configured embedding model. If a future model emits a different
--     dimension, a later migration must re-pin it and rebuild the index.
--
--  2. Dashboard Results browser. public.eligibility carried only an nct_id
--     index, so SearchEligibilityAsync's default ordering and all of its
--     filter predicates fell back to sequential scans. These indexes cover
--     the default sort, the exact-match filters, and — via pg_trgm — the
--     ILIKE substring filters on criterion and concept.
--
-- Idempotent — the dimension is pinned only when not already pinned, and
-- every index uses CREATE INDEX IF NOT EXISTS, so re-running EnsureSchemaAsync
-- is safe. Plain (non-CONCURRENT) CREATE INDEX is used so the whole file runs
-- inside the migration runner's implicit transaction; on a large pre-existing
-- corpus, building the HNSW index out-of-band with CREATE INDEX CONCURRENTLY
-- and a raised maintenance_work_mem is gentler — after which IF NOT EXISTS
-- makes this migration a no-op.

-- ---- Authoring: pin embedding dimension + HNSW index ----------------------

-- Pin the bare `vector` column to vector(1024) so it can carry an HNSW index.
-- Guarded: atttypmod is 1024 once pinned and -1 while unconstrained, so the
-- table is rewritten at most once. A row whose vector is not 1024-dim makes
-- the ALTER fail loudly — the correct outcome, since a mixed-dimension corpus
-- cannot be HNSW-indexed.
DO $$
BEGIN
    IF (SELECT atttypmod
        FROM pg_attribute
        WHERE attrelid = 'public.eligibility_study_embedding'::regclass
          AND attname  = 'embedding') <> 1024 THEN
        ALTER TABLE public.eligibility_study_embedding
            ALTER COLUMN embedding TYPE vector(1024);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_eligibility_study_embedding_hnsw
    ON public.eligibility_study_embedding
    USING hnsw (embedding vector_cosine_ops);

-- ---- Results browser: public.eligibility indexes --------------------------

CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Default Results ordering is "ORDER BY created_at DESC, id DESC".
CREATE INDEX IF NOT EXISTS ix_eligibility_created_at
    ON public.eligibility (created_at DESC, id DESC);

-- Exact-match filter predicates. domain and semantic_type are low-cardinality
-- — the planner may still prefer a scan for unselective values — but they are
-- cheap to maintain and decisive once a selective value is supplied.
CREATE INDEX IF NOT EXISTS ix_eligibility_domain
    ON public.eligibility (domain);
CREATE INDEX IF NOT EXISTS ix_eligibility_concept_code
    ON public.eligibility (concept_code);
CREATE INDEX IF NOT EXISTS ix_eligibility_semantic_type
    ON public.eligibility (semantic_type);

-- ILIKE '%...%' substring filters on criterion and concept — only a trigram
-- GIN index can serve a leading-wildcard pattern.
CREATE INDEX IF NOT EXISTS ix_eligibility_criterion_trgm
    ON public.eligibility USING gin (criterion gin_trgm_ops);
CREATE INDEX IF NOT EXISTS ix_eligibility_concept_trgm
    ON public.eligibility USING gin (concept gin_trgm_ops);

-- V19: UMLS-only retry bookkeeping.
--
-- The `retry-umls` CLI command re-resolves UMLS gaps (rows in public.eligibility
-- whose concept_code is empty) against the configured backend WITHOUT re-calling
-- the LLM, UPDATING only the five UMLS columns (concept_code, umls_name,
-- match_source, match_score, semantic_type) in place. This per-trial table records
-- which trials have been attempted so consecutive batches advance and
-- already-processed trials are anti-joined out (mirrors how eligibility_study
-- gates the extraction pipeline). A trial is recorded once attempted regardless of
-- outcome, so genuinely-unresolvable rows are not retried forever; `--force`
-- re-attempts them after a corpus refresh.

CREATE TABLE IF NOT EXISTS public.eligibility_umls_retry (
    nct_id         text        PRIMARY KEY,
    retried_at     timestamptz NOT NULL DEFAULT now(),
    rows_attempted integer     NOT NULL DEFAULT 0,
    rows_resolved  integer     NOT NULL DEFAULT 0
);

-- Partial index supporting trial selection: distinct unresolved nct_ids in
-- nct_id order, so SELECT DISTINCT ... ORDER BY nct_id LIMIT N stops early
-- instead of scanning all 600k eligibility rows.
CREATE INDEX IF NOT EXISTS ix_eligibility_unresolved
    ON public.eligibility (nct_id)
    WHERE concept_code IS NULL OR concept_code = '';

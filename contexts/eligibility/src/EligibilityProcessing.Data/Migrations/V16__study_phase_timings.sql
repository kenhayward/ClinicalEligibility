-- V16: per-trial phase timings on the audit table.
--
-- Diagnostic instrumentation for concurrency tuning: how long each trial spends
-- in the LLM call vs. the sequential per-criterion UMLS resolution vs. the
-- persist transaction. Lets the Runs table show the average phase split per run,
-- which reveals whether the LLM is being starved by the UMLS phase (the
-- suspected throughput ceiling) and whether that phase inflates under load.
--
-- Milliseconds, nullable: NULL for the phase on trials that never reached it
-- (e.g. an LLM failure has no persist_ms) and for rows written before V16.
--
-- Idempotent — re-running EnsureSchemaAsync is safe.

ALTER TABLE public.eligibility_study
    ADD COLUMN IF NOT EXISTS llm_ms     integer,
    ADD COLUMN IF NOT EXISTS umls_ms    integer,
    ADD COLUMN IF NOT EXISTS persist_ms integer;

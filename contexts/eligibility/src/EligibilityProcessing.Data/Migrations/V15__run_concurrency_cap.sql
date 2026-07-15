-- V15: record the trial concurrency cap used for each run.
--
-- The pipeline's parallelism (Pipeline:LlmConcurrencyCap -> Parallel.ForEachAsync
-- MaxDegreeOfParallelism) is now tunable at runtime from the Runtime Parameters
-- panel, so it can differ run to run. Persisting the value used makes the Runs
-- (History) table a record of which cap produced which throughput — the key
-- variable when sweeping concurrency against GPU utilization.
--
-- Nullable: runs recorded before this migration stay NULL (cap unknown), and the
-- History tab renders "-" for them.
--
-- Idempotent — re-running EnsureSchemaAsync is safe.

ALTER TABLE public.eligibility_run
    ADD COLUMN IF NOT EXISTS concurrency_cap integer;

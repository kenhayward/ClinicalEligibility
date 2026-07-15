-- V4: drop the eligibility_watermark table.
--
-- The table was a vestige of the original n8n design where the watermark
-- value was mirrored from `MAX(nct_id) FROM eligibility` into a separate
-- store. In practice the orchestrator always read MAX directly (that is what
-- makes crash-recovery work — the output store IS the watermark), so the
-- mirror only ever served as a diagnostic snapshot for the dashboard /
-- CLI `status` cards. Removing the redundant table along with the
-- WriteWatermark / GetWatermark gateway methods that maintained it.
--
-- Forward-only: V1 still creates the table on fresh installs and this
-- migration drops it immediately after. Idempotent (IF EXISTS) so re-runs
-- and never-existed databases are both safe.

DROP TABLE IF EXISTS public.eligibility_watermark;

-- V3: capture the raw LLM response on every audit row.
--
-- Lets operators inspect exactly what the model emitted when a trial lands
-- in parse_invalid_json (truncation? bad escape? non-JSON noise?) or
-- parse_empty (legitimately empty array, or unexpected output the parser
-- couldn't read?). Also useful retrospectively on success rows when a
-- specific extraction looks wrong.
--
-- Text column is TOAST-compressed by Postgres so multi-KB responses don't
-- bloat the row. Idempotent: ALTER ... IF NOT EXISTS means re-running V3
-- after it's applied is a no-op.

ALTER TABLE public.eligibility_study
    ADD COLUMN IF NOT EXISTS llm_raw_response text;

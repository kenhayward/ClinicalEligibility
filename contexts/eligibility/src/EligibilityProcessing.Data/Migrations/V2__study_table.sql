-- V2: per-trial audit table.
--
-- One row per (run_id, nct_id). Inserted just before the LLM call by
-- PostgresGateway.StartStudyAsync (status='running'), updated to the final
-- terminal state by FinishStudyAsync once the trial completes or fails.
--
-- Diagnostic columns capture exactly where a trial went sideways:
--   status:                running / success / llm_failed / parse_empty /
--                          persist_failed / cancelled
--   llm_*:                 transport / model outcome
--   parsed_record_count:   what the parser emitted post-LLM
--   persisted_row_count:   what landed in public.eligibility
--   error_message:         the failure detail
--
-- Idempotent — CREATE IF NOT EXISTS so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.eligibility_study (
    run_id                uuid        NOT NULL,
    nct_id                text        NOT NULL,
    started_at            timestamptz NOT NULL,
    finished_at           timestamptz,
    status                text        NOT NULL,
    llm_succeeded         boolean,
    llm_finish_reason     text,
    llm_prompt_tokens     integer,
    llm_completion_tokens integer,
    parsed_record_count   integer,
    persisted_row_count   integer,
    error_message         text,
    PRIMARY KEY (run_id, nct_id)
);

CREATE INDEX IF NOT EXISTS ix_eligibility_study_nct_id ON public.eligibility_study(nct_id);
CREATE INDEX IF NOT EXISTS ix_eligibility_study_status ON public.eligibility_study(status);
CREATE INDEX IF NOT EXISTS ix_eligibility_study_started_at ON public.eligibility_study(started_at);

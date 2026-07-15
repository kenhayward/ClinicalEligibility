-- V9: llama.cpp vendor stop diagnostics on the per-trial audit table.
--
-- The OpenAI chat-completions schema gives us finish_reason + usage; llama.cpp
-- adds five extra fields at the root of the response (stopped_eos,
-- stopped_limit, stopped_word, stopping_word, truncated). On length-truncated
-- trials those fields are the difference between "max_tokens hit" and "EOS
-- was suppressed and the slot ran out" — distinctions the audit row could not
-- previously surface.
--
-- Columns are nullable: OpenAI-proper deployments leave them NULL; rows
-- written before this migration also remain NULL. The History tab renders
-- whichever fields are present and omits the rest.
--
-- Idempotent — re-running EnsureSchemaAsync is safe.

ALTER TABLE public.eligibility_study
    ADD COLUMN IF NOT EXISTS llm_stopped_eos   boolean,
    ADD COLUMN IF NOT EXISTS llm_stopped_limit boolean,
    ADD COLUMN IF NOT EXISTS llm_stopped_word  boolean,
    ADD COLUMN IF NOT EXISTS llm_stopping_word text,
    ADD COLUMN IF NOT EXISTS llm_truncated     boolean;

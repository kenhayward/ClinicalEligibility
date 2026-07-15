-- V12: Per-record attribution + an append-only audit log.
--
-- Adds created_by / last_updated_by (the acting user_id) to the authored study
-- and authored criterion tables, and a general audit_log capturing every manual
-- create/update/delete plus every login.
--
-- IMPORTANT: this migration must ship alongside the SaveAuthoringCriteriaAsync
-- upsert redesign. The previous DELETE-all + re-INSERT-all save would reset
-- created_by/created_at on every save, defeating per-row attribution.
--
-- No FK from audit_log.user_id or the attribution columns to app_user: audit
-- history is immutable and must survive (and never block) a user deletion.
-- user_label snapshots a human-readable userid/email so deleted users stay
-- legible. entity_id is text so it can later point at composite/text keys (e.g.
-- eligibility_study's run_id+nct_id) without a schema change.
--
-- Idempotent — ADD COLUMN / CREATE ... IF NOT EXISTS so re-running is safe.

ALTER TABLE public.authoring_study     ADD COLUMN IF NOT EXISTS created_by      uuid;
ALTER TABLE public.authoring_study     ADD COLUMN IF NOT EXISTS last_updated_by uuid;
ALTER TABLE public.authoring_criterion ADD COLUMN IF NOT EXISTS created_by      uuid;
ALTER TABLE public.authoring_criterion ADD COLUMN IF NOT EXISTS last_updated_by uuid;

CREATE TABLE IF NOT EXISTS public.audit_log (
    audit_id    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    user_id     uuid,                       -- null for failed/unknown logins
    user_label  text        NOT NULL,       -- snapshot of userid/email (survives deletion)
    action      text        NOT NULL,       -- create|update|delete|login|login_denied|bootstrap|role_change
    entity_type text        NOT NULL,       -- authoring_study|authoring_criterion|app_user|session|eligibility_study
    entity_id   text,                       -- id link target (text, not uuid)
    detail      text
);

CREATE INDEX IF NOT EXISTS ix_audit_log_occurred_at ON public.audit_log (occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_log_entity      ON public.audit_log (entity_type, entity_id);

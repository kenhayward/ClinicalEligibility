-- V1: initial schema for the eligibility output database.
-- Architecture section 2.2. Matches the §4.4 spec shape with the
-- recommended additions (PK, NCT_ID index, created_at timestamp) applied.

CREATE TABLE IF NOT EXISTS public.eligibility (
    id            bigserial PRIMARY KEY,
    nct_id        text         NOT NULL,
    criterion     text         NOT NULL,
    domain        text         NOT NULL,
    concept       text         NOT NULL,
    concept_code  text,
    semantic_type text,
    qualifier     text,
    time_window   text,
    original_text text,
    umls_name     text,
    match_score   numeric(4,3) NOT NULL DEFAULT 0,
    match_source  text,
    created_at    timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_eligibility_nct_id ON public.eligibility(nct_id);

CREATE TABLE IF NOT EXISTS public.eligibility_watermark (
    key        text PRIMARY KEY,
    value      text         NOT NULL,
    updated_at timestamptz  NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.eligibility_run (
    run_id            uuid PRIMARY KEY,
    started_at        timestamptz  NOT NULL,
    ended_at          timestamptz,
    trigger_source    text         NOT NULL,
    study_count       integer      NOT NULL,
    studies_processed integer,
    rows_persisted    integer,
    resolution_rate   numeric(4,3),
    status            text         NOT NULL,
    error_summary     text
);

CREATE TABLE IF NOT EXISTS public.eligibility_failed (
    nct_id          text PRIMARY KEY,
    last_attempted  timestamptz NOT NULL,
    attempt_count   integer     NOT NULL DEFAULT 1,
    last_error      text
);

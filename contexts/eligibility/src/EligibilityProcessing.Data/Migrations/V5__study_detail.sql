-- V5: per-trial snapshot of source study metadata + eligibility detail.
--
-- One row per nct_id (unlike eligibility_study, which is per run+nct). The
-- columns mirror the StudyDetails + SourceEligibilityDetails projections the
-- Analysis tab renders: the study ID card (ctgov.studies + brief_summaries +
-- conditions + interventions) and the raw eligibility block (ctgov.eligibilities).
--
-- Captured from the AACT source DB during processing and refreshed on every
-- run, so the dashboard's Analysis tab can render the study card + eligibility
-- detail without a live AACT connection. Trials processed before this table
-- existed are populated by the CLI 'backfill-details' command.
--
-- conditions  is a text[]  (many condition names per trial).
-- interventions is jsonb   (array of {"type","name"} objects).
--
-- Idempotent — CREATE IF NOT EXISTS so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.eligibility_study_detail (
    nct_id                  text        NOT NULL PRIMARY KEY,
    captured_at             timestamptz NOT NULL DEFAULT now(),
    -- study ID card
    brief_title             text,
    official_title          text,
    overall_status          text,
    phase                   text,
    study_type              text,
    start_date              date,
    completion_date         date,
    primary_completion_date date,
    enrollment              integer,
    enrollment_type         text,
    source                  text,
    why_stopped             text,
    brief_summary           text,
    conditions              text[]      NOT NULL DEFAULT '{}',
    interventions           jsonb       NOT NULL DEFAULT '[]',
    -- raw eligibility detail
    criteria                text,
    gender                  text,
    minimum_age             text,
    maximum_age             text,
    healthy_volunteers      text,
    sampling_method         text,
    population              text,
    adult                   boolean,
    child                   boolean,
    older_adult             boolean
);

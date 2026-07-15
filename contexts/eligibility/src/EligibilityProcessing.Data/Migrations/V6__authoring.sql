-- V6: Authoring feature — user-designed, not-yet-registered studies.
--
-- See docs/specs/authoring specification.md §4. Three tables, all in the
-- public schema, kept separate from the AACT-extracted tables:
--
--   authoring_study       — study characteristics (mirrors the study half of
--                            eligibility_study_detail). Keyed by a surrogate
--                            uuid; an authored study has no NCT_ID.
--   authoring_eligibility — high-level eligibility data, 1:1 with the study
--                            (mirrors the eligibility half of the same table).
--   authoring_criterion   — the ordered list of authored eligibility criteria
--                            the user assembles in the Analysis phase.
--
-- conditions    is a text[] (many condition names per study).
-- interventions is jsonb    (array of {"type","name"} objects), matching the
--                            eligibility_study_detail encoding.
--
-- Idempotent — CREATE IF NOT EXISTS so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.authoring_study (
    authoring_study_id      uuid        NOT NULL PRIMARY KEY,
    label                   text        NOT NULL,
    source_kind             text        NOT NULL,   -- blank | aact | authored
    source_ref              text,                   -- NCT_ID or authoring_study_id of origin
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
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
    interventions           jsonb       NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS ix_authoring_study_updated_at
    ON public.authoring_study(updated_at DESC);

CREATE TABLE IF NOT EXISTS public.authoring_eligibility (
    authoring_study_id  uuid NOT NULL PRIMARY KEY
        REFERENCES public.authoring_study(authoring_study_id) ON DELETE CASCADE,
    criteria            text,
    gender              text,
    minimum_age         text,
    maximum_age         text,
    healthy_volunteers  text,
    sampling_method     text,
    population          text,
    adult               boolean,
    child               boolean,
    older_adult         boolean
);

CREATE TABLE IF NOT EXISTS public.authoring_criterion (
    authoring_criterion_id  uuid        NOT NULL PRIMARY KEY,
    authoring_study_id      uuid        NOT NULL
        REFERENCES public.authoring_study(authoring_study_id) ON DELETE CASCADE,
    ordinal                 integer     NOT NULL,
    criterion               text        NOT NULL,   -- Inclusion | Exclusion
    normalized_text         text        NOT NULL,
    concept                 text,
    concept_code            text,
    semantic_type           text,
    domain                  text,
    source_note             text,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_authoring_criterion_study
    ON public.authoring_criterion(authoring_study_id, ordinal);

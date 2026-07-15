-- V13: user-facing Study ID for authoring studies.
--
-- Adds a human-meaningful identifier (e.g. a protocol number) that the user
-- assigns when creating an authored study, distinct from the surrogate
-- authoring_study_id uuid. Required and unique (case-insensitive) for studies
-- created after this migration.
--
-- The column is nullable so legacy rows (created before this feature) remain
-- valid; uniqueness is enforced case-insensitively among non-null values via a
-- partial unique index on lower(study_id).
--
-- Idempotent — ADD COLUMN IF NOT EXISTS + CREATE UNIQUE INDEX IF NOT EXISTS so
-- re-running EnsureSchemaAsync is safe.

ALTER TABLE public.authoring_study ADD COLUMN IF NOT EXISTS study_id text;

CREATE UNIQUE INDEX IF NOT EXISTS ux_authoring_study_study_id
    ON public.authoring_study (lower(study_id))
    WHERE study_id IS NOT NULL;

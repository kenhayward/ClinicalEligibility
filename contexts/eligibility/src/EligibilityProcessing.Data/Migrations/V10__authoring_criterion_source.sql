-- V10: Authoring criterion lineage — maps each authored criterion back to the
-- public.eligibility source records it was normalized from.
--
-- See docs/specs/authoring specification.md §3.5/§3.6. When an author normalizes
-- a criterion cluster and clicks Add, every public.eligibility row behind that
-- cluster is snapshotted here, FK-linked to the saved authoring_criterion.
--
-- Snapshot, not reference: public.eligibility is rebuilt per-trial with
-- DELETE+INSERT (spec §2.8.2), so eligibility.id is volatile. The source row's
-- content is copied so lineage stays accurate after a trial is re-processed;
-- eligibility_id is kept as a best-effort live link only.
--
-- ON DELETE CASCADE means the replace-all "DELETE FROM authoring_criterion" in
-- SaveAuthoringCriteriaAsync clears stale mappings automatically.
--
-- Idempotent — CREATE IF NOT EXISTS so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.authoring_criterion_source (
    authoring_criterion_source_id uuid        NOT NULL PRIMARY KEY,
    authoring_criterion_id        uuid        NOT NULL
        REFERENCES public.authoring_criterion(authoring_criterion_id) ON DELETE CASCADE,
    eligibility_id                bigint,                  -- best-effort live link (volatile)
    nct_id                        text        NOT NULL,
    criterion                     text,
    domain                        text,
    concept                       text,
    concept_code                  text,
    semantic_type                 text,
    qualifier                     text,
    time_window                   text,
    original_text                 text,
    match_score                   numeric(4,3),
    created_at                    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_authoring_criterion_source_criterion
    ON public.authoring_criterion_source(authoring_criterion_id);

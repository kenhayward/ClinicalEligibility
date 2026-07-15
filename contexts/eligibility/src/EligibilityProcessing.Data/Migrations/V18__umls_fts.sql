-- V18: full-text search ranking for the local UMLS store.
--
-- Trigram fuzzy match (V17) over-resolves: it matches character noise and
-- ignores the distinctive token that matters (a 1-token "Examination" or the
-- wrong number "4 meter" for "10 meter"). The UTS REST API discriminates better
-- because it is a ranked full-text search — distinctive tokens count and weak
-- matches are declined. This adds the same capability locally: a generated
-- tsvector column + GIN index, so SearchCandidatesAsync can rank by ts_rank over
-- a word-level (OR) query and require real word overlap (@@), with trigram kept
-- only as a typo fallback.
--
-- 'english' config: lowercases, tokenises, light stemming; numbers are kept as
-- their own lexemes (so "10"/"131i"/"17p" stay decisive). The column is
-- GENERATED ... STORED, so the existing COPY load (which writes `str`) populates
-- it automatically with no loader change.
--
-- Idempotent — re-running EnsureSchemaAsync is safe.

ALTER TABLE umls.atom
    ADD COLUMN IF NOT EXISTS str_tsv tsvector
        GENERATED ALWAYS AS (to_tsvector('english', str)) STORED;

CREATE INDEX IF NOT EXISTS ix_umls_atom_str_tsv
    ON umls.atom USING gin (str_tsv);

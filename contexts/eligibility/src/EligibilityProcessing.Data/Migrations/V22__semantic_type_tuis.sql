-- V22: semantic types as data, not as a display string.
--
-- public.eligibility.semantic_type is a ", "-joined string. Several UMLS
-- semantic type names contain commas ("Amino Acid, Peptide, or Protein"), so the
-- value cannot be parsed back into its parts, and the Results filter matches the
-- whole string - under-reporting "Pharmacologic Substance" by 68% (6,389 rows
-- matched of 19,674 that carry the type).
--
-- semantic_type_tuis carries the TUIs. TUIs are stable across UMLS releases;
-- names get reworded. semantic_type stays as the display string, derived from
-- the array, so the two cannot drift.
--
-- Idempotent - ADD COLUMN / CREATE ... IF NOT EXISTS, so re-running
-- EnsureSchemaAsync is safe.

ALTER TABLE public.eligibility
    ADD COLUMN IF NOT EXISTS semantic_type_tuis text[];

-- GIN supports the containment queries phase 3 needs
-- (semantic_type_tuis && ARRAY[...]).
CREATE INDEX IF NOT EXISTS ix_eligibility_semantic_type_tuis
    ON public.eligibility USING gin (semantic_type_tuis);

-- umls.semantic_type: key on (cui, tui) rather than (cui, sty).
--
-- Not because the current data is ambiguous - TUI and STY are a perfect 132/132
-- bijection, so nothing is being discarded today. The reason is that
-- load-umls --semantic-types-only (V22-era, added in 0.1.35) is ADDITIVE: if a
-- future UMLS release renames a semantic type, ON CONFLICT (cui, sty) would
-- insert a second row for the same (cui, tui). Keying on TUI makes the additive
-- load idempotent against renames.
--
-- Defensive no-op today: there are zero NULL TUIs, but a future partial load
-- could introduce some and ALTER ... SET NOT NULL would then fail.
DELETE FROM umls.semantic_type WHERE tui IS NULL OR tui = '';

ALTER TABLE umls.semantic_type ALTER COLUMN tui SET NOT NULL;

ALTER TABLE umls.semantic_type DROP CONSTRAINT IF EXISTS semantic_type_pkey;
ALTER TABLE umls.semantic_type ADD PRIMARY KEY (cui, tui);

-- sty is no longer part of the key, so keep it indexed for the dim rebuild and
-- for any name-based lookup.
CREATE INDEX IF NOT EXISTS ix_umls_semantic_type_sty ON umls.semantic_type (sty);

-- ~132 rows. Lets a TUI resolve to a name without touching the 3.9M-row table.
CREATE TABLE IF NOT EXISTS umls.semantic_type_dim (
    tui text PRIMARY KEY,
    sty text NOT NULL
);

-- Populated by the migration from existing data, so no vocabulary reload is
-- needed to make it usable. The loader refreshes it on future loads.
INSERT INTO umls.semantic_type_dim (tui, sty)
SELECT DISTINCT tui, sty FROM umls.semantic_type
ON CONFLICT (tui) DO NOTHING;

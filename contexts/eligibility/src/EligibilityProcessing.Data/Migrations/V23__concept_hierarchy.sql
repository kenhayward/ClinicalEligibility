-- V23: SNOMED-derived concept hierarchy, for rolling criteria clusters up to
-- broader concepts.
--
-- The Authoring Analysis tab clusters criteria on exact concept identity, so
-- "Type 2 Diabetes Mellitus" and "Diabetes Mellitus" fragment into separate
-- clusters. Since the tab drops singleton clusters client-side, fragmentation
-- does not merely add noise - it suppresses signal.
--
-- Deliberately shaped like OMOP's CONCEPT_ANCESTOR (ancestor, descendant,
-- distance). If the OMOP route is adopted later - it covers vocabularies beyond
-- SNOMED, at the cost of an ATHENA download and a CUI-to-OMOP crosswalk - this
-- becomes a load change rather than a rewrite of the rollup SQL and UI.
--
-- Populated by `load-umls --hierarchy-only` from MRREL.RRF, scoped to
-- SAB='SNOMEDCT_US' and REL IN ('PAR','CHD'). Roughly half the corpus's distinct
-- CUIs (66,514 of 132,243, measured 2026-07-20) have SNOMED edges; the rest do
-- not roll up. That partial coverage is expected and surfaced in the UI.
--
-- Idempotent - CREATE ... IF NOT EXISTS, so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS umls.concept_ancestor (
    descendant_cui text    NOT NULL,
    ancestor_cui   text    NOT NULL,
    -- Shortest path length. A DAG gives multiple paths between the same pair;
    -- the minimum is what "roll up at most N levels" is measured against.
    min_distance   integer NOT NULL,
    PRIMARY KEY (descendant_cui, ancestor_cui)
);

-- The PK serves descendant -> ancestors (the rollup direction). This index
-- serves the reverse, ancestor -> descendants, which Part 2 needs to expand a
-- rolled-up cluster back to its member concepts.
CREATE INDEX IF NOT EXISTS ix_umls_concept_ancestor_ancestor
    ON umls.concept_ancestor (ancestor_cui);

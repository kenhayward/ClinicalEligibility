-- V20: LLM concept-normalization cache (the `normalize-umls` command + the
-- pipeline's inline cache consult).
--
-- The residue of UMLS-unresolved concepts is mostly abbreviations, compound
-- phrases, and paraphrase that the lexical store can't match by characters. The
-- `normalize-umls` CLI command sends each DISTINCT unresolved concept to the LLM
-- normalize endpoint to get a canonical clinical term, re-resolves THAT term
-- through the local lexical store (still subject to the 0.45 scorer floor), and
-- caches the outcome here keyed by the normalized concept string.
--
-- This table is both:
--   1. the anti-join that makes `normalize-umls` resumable / batchable (a concept
--      is recorded once attempted, resolved or not), and
--   2. a reusable concept -> CUI map the extraction pipeline consults INLINE: when
--      a criterion fails to resolve lexically, the orchestrator looks it up here
--      (a cheap PK lookup, no LLM) and applies the cached resolution. So the LLM
--      work happens once per distinct concept, offline, and every later run reaps
--      it for free.
--
-- Keyed by concept_norm = UmlsMetathesaurusStore.NormalizeConcept(concept)
-- (lower / trim / collapse-whitespace) so case + spacing variants of the same
-- concept share one row. The five UMLS columns mirror public.eligibility's match
-- columns; match_score is 0 and the rest NULL/empty when resolved = false.
--
-- Idempotent — re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS umls.concept_normalization (
    concept_norm    text          NOT NULL PRIMARY KEY,   -- normalized lookup key
    normalized_term text          NOT NULL DEFAULT '',     -- LLM canonical term ('' / 'NONE' when not a concept)
    concept_code    text,                                  -- UMLS CUI; NULL when unresolved
    umls_name       text,
    match_source    text,
    match_score     numeric(4,3)  NOT NULL DEFAULT 0,
    semantic_type   text,
    resolved        boolean       NOT NULL DEFAULT false,  -- did the normalized term clear the 0.45 floor?
    normalized_at   timestamptz   NOT NULL DEFAULT now()
);

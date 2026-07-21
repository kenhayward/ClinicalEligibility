-- V24: condition -> UMLS CUI dictionary, for slicing corpus analytics by
-- condition.
--
-- public.eligibility_study_detail.conditions is raw AACT free text: 91,600
-- distinct strings over 611,329 mentions, unnormalized (COVID-19 and Covid19 are
-- separate entries), with the top 100 covering only 18 percent of mentions. It
-- cannot back an analytic dimension as-is.
--
-- Keyed on the NORMALIZED string, not on (nct_id, condition). Normalization is
-- study-independent, so a dictionary is ~90,076 rows rather than 611,329, is
-- re-runnable, and lets a new pipeline run reuse every earlier resolution.
--
-- Resolution tiers (see the design spec, section 5):
--   exact           - one CUI from an exact umls.atom.str_norm match; 63.0% of
--                     mentions. The scorer is deliberately NOT consulted here.
--   exact_ambiguous - exact match, several CUIs, tie-broken; 8.4% of mentions.
--   fuzzy           - FTS/trigram + UmlsMatchScorer at >= 0.60; the remainder.
--   unresolved      - attempted and rejected. resolved_at is set, concept_code
--                     is NULL, so a re-run skips it unless forced.
--
-- Idempotent - CREATE ... IF NOT EXISTS, so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.condition_concept (
    -- ConceptKey.Normalize(raw): lower-invariant, internal whitespace collapsed,
    -- trimmed. The SQL mirror is regexp_replace(btrim(lower(x)), '\s+', ' ', 'g'),
    -- which agrees with it on ASCII input only (all the corpus contains); it
    -- does not collapse Unicode whitespace such as a non-breaking space the way
    -- .NET's \s does. ConceptKey.Normalize is authoritative - it also produced
    -- the persisted umls.atom.str_norm values. ConditionConceptStoreTests
    -- cross-checks the ASCII cases only.
    condition_norm text         NOT NULL PRIMARY KEY,
    -- The most frequent ORIGINAL casing of this normalized string, and the
    -- string handed to the matcher. Load-bearing: UmlsMatchScorer's acronym
    -- term only fires on a query matching ^[A-Z0-9]{2,6}$, and the corpus holds
    -- COPD (657 studies), Copd (93), Hiv (258), Nsclc (13). Feeding the
    -- lowercased key would silently disable acronym matching.
    raw_form       text         NOT NULL,
    -- Corpus frequency. Orders backfill work so a cancelled run still leaves the
    -- corpus better off. Recomputed in bulk, NOT incrementally maintained by the
    -- pipeline - see the spec section 6.1.
    study_count    integer      NOT NULL DEFAULT 0,
    concept_code   text         NULL,
    umls_name      text         NULL,
    -- exact | exact_ambiguous | fuzzy | unresolved.
    -- NOT called match_source: public.eligibility.match_source and
    -- UmlsMatch.MatchSource both mean the ROOT SOURCE VOCABULARY (MSH,
    -- SNOMEDCT_US). Reusing that name for a tier label would mislead the
    -- analytics joins this table exists to serve.
    match_tier     text         NOT NULL DEFAULT 'unresolved',
    -- numeric(4,3) to match the criteria pipeline's end-to-end typing of match
    -- scores. 0 when unresolved.
    match_score    numeric(4,3) NOT NULL DEFAULT 0,
    -- NULL means never attempted. Set even on failure, so a re-run skips it
    -- unless --force is passed.
    resolved_at    timestamptz  NULL,
    created_at     timestamptz  NOT NULL DEFAULT now()
);

-- Serves the analytics join: condition_concept -> concept_code -> eligibility.
CREATE INDEX IF NOT EXISTS ix_condition_concept_code
    ON public.condition_concept (concept_code)
    WHERE concept_code IS NOT NULL;

-- Serves the backfill's "highest-value unresolved work first" ordering.
CREATE INDEX IF NOT EXISTS ix_condition_concept_pending
    ON public.condition_concept (study_count DESC)
    WHERE resolved_at IS NULL;

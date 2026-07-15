-- V17: local UMLS Metathesaurus store (curated clinical subset).
--
-- An alternative to the remote UTS REST API behind the same IUmlsClient seam:
-- lexical resolution (exact + trigram synonym match) runs as local SQL instead
-- of per-criterion remote round-trips. pgvector semantic search is layered on
-- later (V18). These tables are populated out-of-band by the CLI `load-umls`
-- command (parsing an unpacked UMLS release), or restored from a pg_dump built
-- on a GPU box — never by the running pipeline.
--
-- "Curated subset" = English atoms from the major clinical vocabularies
-- (SNOMEDCT_US, MSH, RXNORM, LNC, ICD10CM, MDR). The loader does the filtering;
-- the schema is vocabulary-agnostic.
--
-- Idempotent — re-running EnsureSchemaAsync is safe. The loader TRUNCATEs and
-- repopulates these tables per UMLS release (twice-yearly refresh).

CREATE SCHEMA IF NOT EXISTS umls;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- One row per searchable atom (string). A CUI has many atoms — preferred name,
-- synonyms, abbreviations, across source vocabularies. This is the synonym
-- table the trigram/exact lookup searches. str is the raw string (trigram
-- index); str_norm is the lower/trim-normalized form for exact match.
CREATE TABLE IF NOT EXISTS umls.atom (
    cui      text    NOT NULL,
    str      text    NOT NULL,
    str_norm text    NOT NULL,
    sab      text    NOT NULL,   -- source vocabulary (SNOMEDCT_US, MSH, ...)
    tty      text,               -- term type (PT, SY, AB, ...)
    is_pref  boolean NOT NULL DEFAULT false
);

-- One row per concept (CUI) with a chosen preferred name + its source vocab.
-- Supplies UmlsCandidate.Name / RootSource for matched CUIs. Derived from
-- umls.atom by the loader's RebuildConceptTable step (preferred-name priority).
CREATE TABLE IF NOT EXISTS umls.concept (
    cui         text NOT NULL PRIMARY KEY,
    pref_name   text NOT NULL,
    root_source text NOT NULL DEFAULT ''
);

-- CUI -> semantic type name(s), from MRSTY. Backs GetSemanticTypesAsync. The
-- (cui, sty) PK also serves the WHERE cui = @cui lookup (leading-column btree).
CREATE TABLE IF NOT EXISTS umls.semantic_type (
    cui text NOT NULL,
    tui text,
    sty text NOT NULL,
    PRIMARY KEY (cui, sty)
);

-- Fuzzy lookup: trigram GIN over the raw atom string (mirrors
-- ix_eligibility_criterion_trgm in V8). Serves `str % @q` + similarity() rank.
CREATE INDEX IF NOT EXISTS ix_umls_atom_str_trgm
    ON umls.atom USING gin (str gin_trgm_ops);

-- Exact lookup on the normalized form, and the join back to umls.concept.
CREATE INDEX IF NOT EXISTS ix_umls_atom_str_norm ON umls.atom (str_norm);
CREATE INDEX IF NOT EXISTS ix_umls_atom_cui      ON umls.atom (cui);

-- V25: composite indexes for the Analytics tab's cohort queries.
--
-- The cohort profile (all concepts in the trials matching a cohort, with
-- distinct-trial counts) measured 3.6s against the production corpus:
-- 4,439,480 criterion rows, 2,600 MB. Two hypotheses were tested before the
-- plan explained where the time went; both are recorded in the design spec
-- section 2.4 so nobody repeats them.
--
-- ix_eligibility_concept_nct - concept_code FIRST. The cohort predicate filters
-- on concept_code, so with concept_code second it can only scan: measured
-- 4,401,106 of 4,439,480 index entries read and discarded. Leading on it lets
-- the filter seek. It also returns rows already ordered by (concept_code,
-- nct_id), which is exactly the order the group-by needs, making the sort for
-- count(DISTINCT nct_id) nearly free - a seek-based plan that yields rows in
-- nct_id order measured SLOWER overall despite gathering data 3.6x faster.
--
-- ix_eligibility_nct_concept - covers the join back from the cohort, so it
-- never touches the heap (measured Heap Fetches: 0).
--
-- Together: 3,600ms -> 1,225ms. Corpus baseline 4,900ms -> 2,000ms.
-- 162 MB each, 324 MB total against a 2,600 MB table, ~13s each to build.
--
-- Both already exist on the production database, created with CREATE INDEX
-- CONCURRENTLY during that measurement. IF NOT EXISTS makes this a no-op there
-- and correct everywhere else - without the migration the schema would not be
-- reproducible.

CREATE INDEX IF NOT EXISTS ix_eligibility_concept_nct
    ON public.eligibility (concept_code, nct_id);

CREATE INDEX IF NOT EXISTS ix_eligibility_nct_concept
    ON public.eligibility (nct_id, concept_code);

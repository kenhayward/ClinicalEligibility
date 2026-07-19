# Analytics over the extracted eligibility corpus: open-source options

Date: 2026-07-19
Status: research note - advice, not an approved design

**This is not a spec.** Nothing here is agreed or scheduled. It records what was
researched, what was verified, what was refuted, and what was never established,
so a later design decision can start from evidence rather than from memory.

Method: six search angles, 25 sources fetched, 123 candidate claims extracted, 25
put through three-vote adversarial verification. **17 confirmed, 8 refuted.** The
refuted list is kept in full (section 8) because several are things a reasonable
person would assume true and re-derive later.

---

## 1. The question

Four analytics goals over `public.eligibility`:

1. **Criteria prevalence and trends** - concept frequency across the corpus,
   sliced by phase / condition / sponsor / year, with rollup to broader terms so
   synonyms and child concepts aggregate.
2. **Trial similarity and clustering** by eligibility profile, concept-based,
   complementing the existing text embeddings.
3. **Cohort feasibility / patient-trial matching** - needs criteria as logic
   (include/exclude, numeric thresholds), not just concepts.
4. **Eligibility burden / restrictiveness scoring.**

Existing constraints: .NET 8, PostgreSQL 16 + pgvector, AACT as source, per-study
embeddings already in `eligibility_study_embedding`.

## 2. Starting position: the hierarchy gap

`V17__umls_metathesaurus.sql` loads three tables:

- `umls.atom` (from MRCONSO)
- `umls.concept`
- `umls.semantic_type` (from MRSTY)

There is **no MRREL and no MRHIER**, so the database holds concept *identity* and
*semantic type* but no concept *ancestry*. Nothing can currently answer "what is
the broader term for C0011849".

This gates three of the four goals:

- Goal 1 is definitionally hierarchy-dependent. Without ancestry, near-synonyms
  count as distinct concepts and the prevalence long tail is an artifact.
- Goal 2 degrades badly. Two trials recruiting overlapping populations via
  sibling concepts score near-zero on exact-CUI overlap.
- Goal 4 needs a criterion's specificity, which is a function of hierarchy depth.

Goal 3 is separable from hierarchy but has its own blocker - see section 7.

## 3. Question A: the hierarchy route (ANSWERED, high confidence)

**Recommendation: adopt the OHDSI/OMOP Standardized Vocabularies from ATHENA and
use `CONCEPT_ANCESTOR` as the ontology backbone.**

### Why

`CONCEPT_ANCESTOR` ships a **precomputed reflexive transitive closure**. It is
built from `CONCEPT_RELATIONSHIP` by OHDSI, not by the consumer, and includes
"all parent-child relationships, as well as grandparent-grandchild relationships
and those of any other level of lineage", with each concept its own ancestor at
`levels_of_separation = 0`.

Consequences that matter here:

- Rollup is a **flat two-key indexed join**, not a recursive CTE and not a graph
  traversal. Four columns: `ancestor_concept_id`, `descendant_concept_id`,
  `min_levels_of_separation`, `max_levels_of_separation`.
- `min_/max_levels_of_separation` allow **tuned rollup** ("two levels broader")
  rather than all-or-nothing. Caveat: because the hierarchy is a multi-path DAG,
  min != max for many pairs, so a `max_levels_of_separation <= 2` filter is not
  an unambiguous two-levels cut. Choosing min vs max is a query-design decision
  and the results differ.
- **Classification concepts** (`standard_concept = 'C'`) are a ready-made
  grouping tier: non-data-bearing broader terms that exist specifically to be
  queried through `CONCEPT_ANCESTOR`. Standard concepts also have ancestors, so
  these are the non-data-bearing tier of the mechanism, not the whole of it.

OHDSI's own guidance is to trust `CONCEPT_ANCESTOR` rather than hand-traverse
`CONCEPT_RELATIONSHIP`, because lateral and zero-separation edges make naive
recursion diverge from the official closure.

Sources: [Book of OHDSI ch.5](https://ohdsi.github.io/TheBookOfOhdsi/StandardizedVocabularies.html),
[Vocabulary-v5.0 wiki](https://github.com/OHDSI/Vocabulary-v5.0/wiki/General-Structure,-Download-and-Use),
[CDM conventions](https://ohdsi.github.io/CommonDataModel/dataModelConventions.html),
[CONCEPT_ANCESTOR wiki](https://www.ohdsi.org/web/wiki/doku.php?id=documentation:cdm:concept_ancestor).

### Why not load MRREL/MRHIER into the existing mirror

It works, but UMLS asserts **no cross-source hierarchy**. Parent/child edges are
source-attributed via `SAB`, so recursing across all sources mixes incompatible
hierarchies and can produce cycles. Nothing in the schema prevents this - an
unfiltered recursive query simply yields semantically incoherent ancestry.

So the honest framing is that option (i) "load MRREL/MRHIER" and option (iii)
"use one source vocabulary's hierarchy" **are the same exercise**: you must scope
to a single SAB, which means picking SNOMEDCT_US or MSH, and then building the
transitive closure yourself - the expensive part OMOP already ships.

Also: MRHIER, not recursive MRREL traversal, is the better substrate if this
route is taken anyway. MRHIER is the computable representation of complete
hierarchies within a single vocabulary and carries full path-to-root strings.

Source: [NLM UMLS Reference Manual](https://www.ncbi.nlm.nih.gov/books/NBK9685/).

### Why not a graph database

Nothing verified here implies one. The closure is precomputed, so the workload is
an indexed join, which Postgres handles trivially at low millions of rows. A
graph DB would add a second datastore to run, sync, back up and secure in
exchange for solving a problem that is already solved by the data format.

### Tooling note

A dormant R package ([metathesaurus](https://meerapatelmd.github.io/metathesaurus/))
implements a preset loading MRCONSO, MRHIER, MRMAP, MRSMAP, MRSAT and MRREL. Last
release v2.1.0, December 2020, roughly 5.5 years stale. An earlier claim that it
is maintained at v3.0.1 was **refuted 0-3**. Value here is as reference DDL and
load SQL only - it is R against a .NET/Postgres target.

## 4. The blocker: OMOP concept_id is not a UMLS CUI

**This is the single largest integration cost and it must be measured before
committing.**

`public.eligibility.concept_code` holds UMLS CUIs. `CONCEPT_ANCESTOR` is keyed on
OMOP `concept_id`. A **CUI to OMOP standard concept_id crosswalk is mandatory**
before any ancestor rollup returns a single row.

Coverage will be uneven, and unevenly in the worst possible direction for this
project. From the Book of OHDSI: "a high-quality comprehensive hierarchy exists
only for two domains: drug and condition. Procedure, measurement and observation
domains are only partially covered."

Further scope limits on the closure:

- Non-standard concepts are excluded from hierarchies even where their source
  vocabulary has one. CUIs must first resolve via `Maps to` to Standard concepts.
- Only hierarchical `Is a` relationships populate it. Part-of relationships are
  excluded.
- Deprecated concepts are absent by design.
- Ancestry is normally confined within a Domain.

**Practical consequence:** condition and drug criteria will roll up well.
Measurement criteria - lab thresholds - will roll up poorly, and those are
exactly what goals 3 and 4 depend on.

**Therefore the crosswalk should be a first-class, quality-measured artifact**,
reporting unmapped-CUI rate broken down by domain. That rate bounds the validity
of every downstream number, and a prevalence chart built on a silently 40%-
unmapped corpus is worse than no chart.

## 5. Question B: OMOP vocabulary yes, OHDSI applications no (ANSWERED)

### Criteria2Query

[OHDSI/Criteria2Query](https://github.com/OHDSI/Criteria2Query) is the canonical
open-source project for free-text eligibility criteria to OMOP CDM cohort
definition. Verified status as of 2026-07-19:

- Apache-2.0 (verified by fetching LICENSE directly; the JAMIA paper's CC BY-NC
  covers the article, not the code), Java 92.7%, 79 stars, 22 forks, not
  archived, `pushed_at` 2026-03-01.
- Self-described "[In Development]".
- Peer-reviewed lineage: JAMIA 2019, "Criteria2Query: a natural language
  interface to clinical databases for cohort definition".

**Research-grade signals**, all verified: 139 commits on master against 14 open
PRs (a large unmerged backlog for the repo size), no tagged release, a required
negation-scope-detection model **distributed via a Google Drive link outside
version control**, and a commit history with multi-year gaps.

It is **structurally bound to OMOP** - the CDM is its execution target, not a
swappable backend. README prerequisites name OMOP CDM Vocabulary v5 from Athena.
Usagi is not wired in directly; it sits behind a pluggable "Concept Hub" POST API
contract, but any substitute must still return OMOP standard concepts.

### Criteria2Query 3.0 is not deployable

The GPT-4 successor is a real paper - Park J, Fang Y, Ta C, Zhang G, Idnay B,
Weng C, *J Biomed Inform* 2024;154:104649, PMID 38697494 - but **not a usable
system**. The only extant code is
[ashwinn-v/Criteria2Query3.0](https://github.com/ashwinn-v/Criteria2Query3.0):
0 stars, 0 forks, exactly 6 commits, last pushed 2024-01-12, with instructions to
download the upstream repo and "replace the source folder". A commonly cited
alternative URL (`Jimyung6642/Criteria2Query3.0`) returns HTTP 404. A GitHub
search for "Criteria2Query" returns only 5 results total, so no competing 3.0
distribution exists.

OHDSI's 2023 showcase advertises "Criteria2Query 3.0 Powered by Generative Large
Language Models" - do not assume that is what is downloadable.

### The most actionable finding

From the C2Q 3.0 error analysis: **the weak link in LLM-to-cohort-query pipelines
is concept normalisation and boolean include/exclude logic, not entity
extraction.** Of 29 errors across 7 categories, logic errors were the largest
single category, followed by concept omission and incorrect concept mapping.

This maps directly onto goals 3 and 4, and argues for **engineering polarity and
threshold handling explicitly with human review**, rather than delegating them to
the LLM end to end. It also matches the authors' own design choice of a
semi-automatic human-in-the-loop system.

**Honest qualifications, all material** (this claim was graded medium confidence
on effect size despite a 3-0 vote):

- The headline percentage is a **share of errors, not an error rate**. No
  denominator of generated clauses is given.
- The implied comparison to extraction is across different denominators and task
  stages. Concept extraction F1 was 0.891 over 518 concepts, implying roughly 11%
  extraction error - not negligible. The paper does not formally test "extraction
  is not the weak link".
- "Concept omission" is arguably itself an extraction failure, partly undercutting
  the claim's own framing.
- n=29 errors from 5 trials, single site, single evaluation team. Reclassifying
  one error moves a category by about 3.4 percentage points.
- GPT-4 era, published mid-2024. May not hold for current models.

Treat it as **directional, not as an effect size**.

## 6. Licensing (ANSWERED - and one item needs a decision)

### UMLS

Free but gated: a UTS account (Login.gov, approval in about 5 business days) and
acceptance of the UMLS Metathesaurus License are required to download release
files including RRF. No fee; the barrier is registration. An Apache-2.0 loader
package cannot relicense the data it fetches.

### SNOMED CT

**No fee for use inside the United States**, which is a Member Territory via
NLM/NIH. Fees apply only to Non-Member Territory deployment, in bands (Appendix B
2.1; subject to indexation under Clause 1.4, so do not quote as current-year
prices).

"No fee" is not "no obligations". A UMLS Metathesaurus licence must be held, and
US Affiliates must register and file the annual SNOMED CT Affiliate Statement of
Account. Redistribution of SNOMED-derived content is governed by **territory of
deployment, not territory of the developer**.

### OMOP / ATHENA - the item that needs a decision

Two findings:

1. Proprietary vocabularies cannot be selected in ATHENA without proof of an
   active licence. OHDSI does not issue licences.
2. **"The Vocabularies should not be used for purposes of individual patient
   healthcare."** This attaches to the OHDSI Standardized Vocabularies **as a
   whole**, not merely the proprietary subset, and is mirrored in HL7 Terminology
   OMOP metadata through THO v7.2.0.

Goal 3 is patient-trial matching. **Depending on how that feature is framed and
who uses it, restriction (2) is squarely relevant and should be read directly
rather than taken from this summary.**

### Genuinely unresolved

Whether SNOMED CT Affiliate terms permit **redistribution of derived** hierarchy
or ancestor data. The claim that Affiliate terms explicitly permit research and
internal-systems use was **refuted 1-2** - meaning unsettled, not denied.

This directly governs whether any materialised rollup or derived ancestor table
could ever ship inside this repository, which is public and Apache-2.0. **Do not
assume either way.** If derived tables are ever to be committed rather than built
locally from a licensed download, get this answered first.

## 7. What the research did NOT establish

Of the 17 surviving claims, all concern the hierarchy question (A) or the
Criteria2Query family (B, and C only partially).

**Not answered at all:**

- **D, the scaling question.** No evidence was gathered on DuckDB vs Postgres,
  pgvector index behaviour at this corpus size, or where join-based rollup
  degrades. The architecture recommendation in section 9 is **reasoned from
  confirmed facts, not researched**, and is flagged low confidence for that
  reason.
- **E, concept similarity.** Nothing verified on Jaccard vs Lin/Resnik/path
  measures, cui2vec, SapBERT or BioBERT-family embeddings.
- **F, eligibility burden scoring.** Nothing verified. In particular, **do not
  report "burden scoring is bespoke" as a finding** - that was not established
  either way.

**Leads, not findings.** These sources were fetched for the unanswered angles but
their claims never survived into the verification budget. They are recorded so a
second pass has a starting point, with no claim as to quality or maintenance:

| Repo / resource | Relevant to | Note |
|---|---|---|
| [TrialPathfinder](https://github.com/RuishanLiu/TrialPathfinder) | Goal 4 | Appears directly on-point: emulating trials under relaxed criteria to quantify restrictiveness. Most promising lead. |
| [UMLS-Similarity](https://github.com/bmcinnes/UMLS-Similarity) | Goal 2 | Perl; Lin/Resnik/path measures. |
| [PyUMLS_Similarity](https://github.com/victormurcia/PyUMLS_Similarity) | Goal 2 | Python wrapper over the above. |
| [SapBERT](https://github.com/cambridgeltl/sapbert) | Goal 2 | Concept embeddings; would drop into existing pgvector. |
| [Chia](https://www.nature.com/articles/s41597-020-00620-0) | Evaluation | Annotated eligibility-criteria corpus. Could serve as **ground truth for this repo's own LLM extraction stage**, which currently has none. |
| [TrialGPT](https://github.com/ncbi-nlp/TrialGPT) | Goal 3 | NCBI. |

EliIE, LeafAI, the n2c2 2018 cohort selection task and the TREC Clinical Trials
track were named in the brief but never reached.

## 8. Refuted claims (recorded so they are not re-derived)

Eight claims were killed during verification. Several are things a reasonable
person would assume:

| Refuted claim | Vote |
|---|---|
| ATHENA does not freely bundle proprietary vocabularies (in that specific framing) | 0-3 |
| Downloading SNOMED CT triggers a separate Affiliate obligation beyond the UMLS licence | 0-3 |
| SNOMED Affiliate terms explicitly permit research and internal-systems use | 1-2 |
| MRREL is the file carrying concept-to-concept relationships needed for hierarchy | 0-3 |
| MRREL's RELA column must be filtered alongside REL for ancestor queries | 0-3 |
| The `metathesaurus` R package is maintained at v3.0.1 | 0-3 |
| GPT-4 extraction substantially beats C2Q 2.0 (F1 0.891 vs 0.707) | 0-3 |
| C2Q 3.0 depends on the OHDSI stack for its ontology layer (as stated) | 0-3 |

Two citation defects were found and corrected during verification:

- The SNOMED fee-exemption quote is **misattributed** in common secondary
  sources: it comes from the Qualifying Research Project clause (Appendix B 1.9),
  not from any Member Territory statement. The fee-free US outcome rests on
  Clause 7.1 scoping plus the NLM licensing page.
- Criteria2Query repo statistics are widely cited to a dead URL, though the
  figures verify correctly against the real repo.

## 9. Recommended architecture (LOW CONFIDENCE - reasoned, not researched)

Keep Postgres + pgvector. Add:

1. **ATHENA vocabulary tables** (`CONCEPT`, `CONCEPT_RELATIONSHIP`,
   `CONCEPT_ANCESTOR`), loaded into their own schema alongside the existing
   `umls` schema rather than replacing it.
2. **A CUI to OMOP standard concept_id crosswalk table**, treated as a
   first-class artifact with a measured and reported unmapped rate by domain.
3. **Materialised rollup views** for prevalence, rebuilt on vocabulary refresh.

Do **not** adopt ATLAS, Circe or CohortDiagnostics. The OMOP coupling that
matters is at the vocabulary layer; the application layer is Java, in-development
and carries the Google-Drive model dependency noted above.

**Vocabulary versioning is an operational requirement, not a nicety.**
`CONCEPT_ANCESTOR` content changes between releases - an ATHENA correction to ATC
ancestor records shipped in September 2025, and MedDRA ancestors were removed
from the Condition domain in March 2023. Any materialised rollup must be rebuilt
on refresh, and **the vocabulary version must be pinned and recorded alongside
any published figure**, or numbers will silently disagree across time.

**Scaling cliffs are unknown.** No evidence was gathered. The reasoning is only
that a precomputed closure makes rollup an indexed join, which Postgres handles
comfortably at low millions of rows. Treat DuckDB and columnar stores as open,
not rejected.

## 10. A schema gap, independent of any tooling choice

This comes from the repository, not the literature.

`public.eligibility` captures `qualifier` and `time_window` as free text, but
**not include/exclude polarity and not numeric thresholds as logic**. "HbA1c
greater than 9 percent" and "HbA1c less than 7 percent" are opposite eligibility
constraints that currently look nearly identical in the table.

Goals 3 and 4 are unbuildable without this, and it converges exactly with the C2Q
finding in section 5: the hard part is logic, not extraction.

It is a change to **what is extracted**, so every trial processed before it lands
will need reprocessing. That argues for settling it early, ahead of the OMOP
work, because reprocessing debt accrues with every run in the meantime.

## 11. Suggested sequence

1. **Measure the CUI to OMOP mapping yield by domain** against the existing
   corpus. Cheap, and it decides whether the OMOP route is viable for measurement
   criteria specifically. Everything downstream is bounded by this number.
2. **Settle the extraction schema** (polarity, thresholds) before more
   reprocessing debt accrues.
3. **Resolve the SNOMED redistribution question** if derived tables are ever
   intended to ship in this public repository.
4. **Load ATHENA vocabulary plus the crosswalk**, with the unmapped rate reported
   and the vocabulary version pinned.
5. Goals 1 and 2 become straightforward once 1-4 land. **Goals 3 and 4 remain
   research projects**, not scheduled work.

## 12. Open questions

- What is the actual CUI to OMOP standard concept_id mapping yield for the
  existing corpus, by domain?
- What are real ATHENA data volumes and load characteristics in Postgres 16, and
  where does join-based rollup actually degrade?
- Does the SNOMED CT Affiliate agreement permit redistribution of derived
  ancestor or rollup data?
- Is there established methodology or open tooling for eligibility burden
  scoring, and what are the maintained options for concept-based similarity over
  Postgres + pgvector? (E and F were not researched.)
- Beyond Criteria2Query, which of EliIE, Chia, LeafAI, TrialGPT and the
  n2c2/TREC resources are maintained versus papers-only? Chia in particular could
  serve as evaluation data for this repo's own extraction stage.

## 13. Time sensitivity

GitHub metrics (79 stars, 139 commits, 14 open PRs, pushed 2026-03-01) are a
July 2026 snapshot and will drift. SNOMED fee bands are subject to indexation.
The C2Q 3.0 error distribution is GPT-4-era and may not hold for current models.
`CONCEPT_ANCESTOR` content changes between vocabulary releases.

Re-verify anything load-bearing before acting on it more than a few months from
the date at the top of this note.

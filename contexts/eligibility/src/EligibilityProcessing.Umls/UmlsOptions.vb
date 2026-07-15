' Configuration for the UMLS UTS REST client.
'
' Spec section 3.4 / architecture section 3.2. Defaults track the production
' values; ApiKey has no default and MUST be supplied from secret storage per
' spec section 6.5 ("UMLS_API_KEY ... sourced from environment / secret store.
' Secrets MUST NOT appear in logs.").

Public Class UmlsOptions

    Public Property BaseUrl As String = "https://uts-ws.nlm.nih.gov/rest"
    Public Property ApiKey As String = ""
    Public Property PageSize As Integer = 5
    Public Property TimeoutSeconds As Integer = 10

    ' Architecture section 2.4: "Polly retry (1 attempt) for transient errors".
    ' Spec section 2.6.1 does not specify a delay, so 2s is a reasonable default
    ' that gives the upstream service a moment to recover without slowing the batch.
    Public Property RetryCount As Integer = 1
    Public Property RetryDelaySeconds As Integer = 2

    ' --- Local Metathesaurus backend (V17) ---

    ' Resolution backend: "rest" (default — the UTS REST API above) or "postgres"
    ' (the local umls.* schema queried by PostgresUmlsClient). The DI composition
    ' root wraps whichever raw client is chosen in the same UmlsCache decorator,
    ' so this switch is config-only and reversible. Validate "postgres" against
    ' the REST baseline (the CLI `umls-compare` command) before defaulting to it.
    Public Property Backend As String = "rest"

    ' Max candidate CUIs the Postgres backend returns to UmlsMatchScorer per
    ' concept. Larger than the REST PageSize (5) because local lookup is cheap and
    ' the trigram arm benefits from a wider candidate set for the lexical scorer
    ' to rank. Ignored by the REST backend.
    Public Property CandidateLimit As Integer = 15

    ' pg_trgm similarity floor for the Postgres backend's fuzzy (`str % @q`) arm,
    ' applied via set_limit() per lookup. 0.3 is the pg_trgm default; raise to cut
    ' weak fuzzy candidates, lower to widen recall. Ignored by the REST backend.
    Public Property TrigramThreshold As Double = 0.3

    ' Precision guard for the Postgres backend: a candidate must cover at least
    ' this fraction of a MULTI-WORD query's significant tokens, else it is dropped
    ' before scoring. Stops generic short atoms ("Examination", "Injection") from
    ' winning the scorer's containment against a long query. Only applies when the
    ' query has >= 2 significant tokens (single-token / fuzzy lookups are
    ' unaffected). 0 disables the guard. Ignored by the REST backend.
    Public Property MinQueryCoverage As Double = 0.6

    ' Discriminative-token guard for the Postgres backend: when the query contains
    ' numeric/code tokens (digit-bearing — numbers, isotopes, gene loci, drug
    ' codes: "10", "131I", "17p", "177Lu"), a candidate must share at least one of
    ' them, else it is dropped. Stops the fuzzy arm matching the wrong number
    ' ("4 meter" for "10 meter", "1 month" for "12 month") or dropping the
    ' distinguishing code entirely ("MRI scan" for "131I scan"). True by default;
    ' set False if your concepts routinely fold dosage/strength into the concept
    ' name (e.g. "Aspirin 100mg" -> "Aspirin"). Ignored by the REST backend.
    Public Property RequireQueryCodeMatch As Boolean = True

    ' Max atom string length (characters) the Postgres backend's fuzzy arms (FTS +
    ' trigram) will consider. Concept names are short; long atoms are LOINC survey
    ' questions ("During the last 7 days, on how many days...") and IUPAC chemical
    ' names that share a token (often a number) with the query and pollute matching.
    ' The exact arm is exempt (an exact normalized match is always legitimate).
    ' 0 disables the cap. Ignored by the REST backend.
    Public Property MaxAtomLength As Integer = 80

    ' Score-aware trigram fallback for the Postgres backend. The expensive pg_trgm
    ' fuzzy arm (`str % @raw`, a full similarity scan of the 3.2M-atom table —
    ' ~250ms per lookup by EXPLAIN) runs ONLY when the cheap exact + full-text pass
    ' fails to resolve the concept (no candidate clears the scorer's 0.45 match
    ' threshold after the precision guards). So the common path stays exact + FTS
    ' (~5x faster, beating the REST round-trip) and the fuzzy scan fires only on the
    ' would-be-unresolved minority — recovering the resolution the fuzzy arm adds
    ' without paying for it on every lookup. False disables the fuzzy arm entirely
    ' (max speed, lowest resolution). Ignored by the REST backend.
    Public Property EnableTrigramFallback As Boolean = True

    ' Curated source vocabularies the `load-umls` command imports from MRCONSO
    ' (SAB filter). The runtime query is vocabulary-agnostic; this only bounds
    ' what the loader ingests. Empty => load all English atoms.
    Public Property SourceVocabularies As String() =
        {"SNOMEDCT_US", "MSH", "RXNORM", "LNC", "ICD10CM", "MDR"}

End Class

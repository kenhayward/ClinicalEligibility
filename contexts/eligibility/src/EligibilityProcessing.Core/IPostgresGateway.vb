Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks


' Contract for all Postgres access, consumed by the orchestrator in Core.
'
' Lives in Core so the orchestrator does not depend on the transport library
' (EligibilityProcessing.Data). PostgresGateway implements this from Data.
'
' Two underlying databases are involved (architecture section 3.2):
'   - source DB (AACT, read-only): the ctgov.eligibilities table
'   - output DB (read/write): public.eligibility + watermark + run + failed
'
' The gateway hides this split — callers pass NCT_IDs / records and the
' implementation routes to the right connection.

Public Interface IPostgresGateway

    ''' <summary>
    ''' Spec section 2.3: select the next batch of trials, with the canonical
    ''' filters (criteria length, "please contact" exclusions) applied in a
    ''' single query, capped at <paramref name="studyCount"/>.
    '''
    ''' Trials whose nct_id appears in <paramref name="excludedNctIds"/> are
    ''' filtered out via an anti-join (these are the trials already attempted
    ''' in eligibility_study, regardless of status). The implementation streams
    ''' the exclusion set into a temp table via COPY-binary so the join scales
    ''' to 500k+ excluded ids without bloating the parameter payload.
    '''
    ''' <paramref name="direction"/> picks the sort order:
    '''   Forward → ORDER BY nct_id ASC  (earliest unprocessed first)
    '''   Recent  → ORDER BY nct_id DESC (most-recent unprocessed first)
    ''' </summary>
    Function SelectNextTrialsAsync(
            excludedNctIds As IReadOnlyList(Of String),
            direction As TrialSelectionDirection,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial))

    ''' <summary>
    ''' Returns the distinct set of NCT_IDs that have any row in
    ''' public.eligibility_study (any status). This is the "already attempted"
    ''' set passed to <see cref="SelectNextTrialsAsync"/>. Definition includes
    ''' successes, parse_empty, in-flight running, and every failure status —
    ''' a Recent / Forward batch never re-attempts anything already tried.
    ''' Failed trials are surfaced for retry through the History tab's
    ''' selection-mode Re-run workflow instead.
    ''' </summary>
    Function GetAttemptedNctIdsAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

    ''' <summary>
    ''' Fetches a single trial directly by nct_id from ctgov.eligibilities,
    ''' bypassing the watermark + length / "please contact" filters that
    ''' SelectNextTrialsAsync applies. Used by the orchestrator's re-run path
    ''' (RunConfiguration.RerunNctId) — when the operator explicitly asks for
    ''' a specific trial, we trust them and don't second-guess. Returns
    ''' Nothing when no ctgov.eligibilities row exists for that nct_id.
    ''' </summary>
    Function GetSourceTrialAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of Trial)

    ''' <summary>
    ''' Batch form of <see cref="GetSourceTrialAsync"/>: fetches every trial whose
    ''' nct_id is in <paramref name="nctIds"/> from ctgov.eligibilities in a SINGLE
    ''' round-trip (<c>WHERE nct_id = ANY(@ids)</c>), bypassing the length /
    ''' "please contact" filters exactly like the single-trial form. The re-run
    ''' path uses this so a large selection costs one query instead of N
    ''' sequential remote round-trips (the AACT source is typically remote, so the
    ''' per-trial latency dominated and left a multi-second window with no visible
    ''' activity). Result order is not guaranteed; nct_ids with no source row are
    ''' simply absent (callers diff against the request to log skips). Duplicate /
    ''' blank input ids are ignored. Empty input returns an empty list.
    ''' </summary>
    Function GetSourceTrialsAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial))

    ''' <summary>
    ''' Spec section 2.8.2: open a transaction, DELETE existing rows for the
    ''' given nct_id, INSERT the new records, COMMIT. Bounds blast radius to
    ''' one trial — a SQL failure here does not corrupt the rest of the batch.
    ''' Empty <paramref name="records"/> means "delete and leave empty"
    ''' (caller is responsible for filtering the safety-net placeholder).
    ''' </summary>
    Function PersistTrialAsync(
            nctId As String,
            records As IReadOnlyList(Of ResolvedRecord),
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' UMLS-only retry — trial selection. Returns up to <paramref name="count"/>
    ''' distinct nct_ids that still have at least one UMLS-unresolved row
    ''' (concept_code empty) with a non-empty concept to re-resolve. Trials already
    ''' recorded in public.eligibility_umls_retry are anti-joined out so consecutive
    ''' batches advance — unless <paramref name="includeRetried"/> is True (the
    ''' --force path, re-attempting still-unresolved rows after a corpus refresh).
    ''' <paramref name="direction"/>: Forward → nct_id ASC, Recent → nct_id DESC.
    ''' </summary>
    Function SelectTrialsToRetryUmlsAsync(
            direction As TrialSelectionDirection,
            count As Integer,
            includeRetried As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

    ''' <summary>
    ''' UMLS-only retry — the unresolved rows (id + concept) for one trial: rows
    ''' whose concept_code is empty and whose concept is non-empty (re-resolvable).
    ''' </summary>
    Function GetUnresolvedRowsForTrialAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsRetryRow))

    ''' <summary>
    ''' UMLS-only retry — apply one trial's results in a single transaction:
    ''' UPDATE the five UMLS columns (concept_code, umls_name, match_source,
    ''' match_score, semantic_type) on each newly-resolved row in
    ''' <paramref name="results"/> by id, then UPSERT the per-trial bookkeeping row
    ''' in public.eligibility_umls_retry (retried_at, rows_attempted = the count of
    ''' rows tried, rows_resolved = results.Count). NCT_ID membership is unchanged,
    ''' so resume semantics (eligibility_study anti-join) are untouched. An empty
    ''' <paramref name="results"/> still records the attempt (so a trial with no
    ''' newly-resolvable rows isn't retried every batch).
    ''' </summary>
    Function ApplyUmlsRetryAsync(
            nctId As String,
            results As IReadOnlyList(Of UmlsRetryResult),
            rowsAttempted As Integer,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' LLM concept-normalization — concept selection. Returns up to
    ''' <paramref name="count"/> DISTINCT residue concepts (normalized key + a
    ''' representative original phrasing) that still have UMLS-unresolved rows with
    ''' a non-empty concept, most-frequent first. Concepts whose normalized key is
    ''' already in umls.concept_normalization are anti-joined out (so batches
    ''' advance) unless <paramref name="includeAttempted"/> is True (the --force
    ''' path, re-normalizing after a prompt/corpus change).
    ''' </summary>
    Function SelectConceptsToNormalizeAsync(
            count As Integer,
            includeAttempted As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptToNormalize))

    ''' <summary>
    ''' Count of DISTINCT UMLS-unresolved residue concepts still eligible for
    ''' normalization - the same set <see cref="SelectConceptsToNormalizeAsync"/>
    ''' draws from, without the LIMIT. When <paramref name="includeAttempted"/> is
    ''' False, concepts already recorded in umls.concept_normalization are excluded
    ''' (the remaining-work count the dashboard Tools tab shows); True counts every
    ''' unresolved residue concept (the --force universe).
    ''' </summary>
    Function CountConceptsToNormalizeAsync(
            includeAttempted As Boolean,
            cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' EXPENSIVE, ON-DEMAND ONLY. Count of AACT trials that pass the selection filter
    ''' (spec section 2.3) - the trials a batch would actually consider. Nothing when
    ''' there is no reachable AACT source.
    ''' <para>
    ''' ~26 SECONDS against Duke's hosted AACT: the three ILIKEs force a full read of
    ''' the wide `criteria` column across ~593k rows over the internet, and the partial
    ''' index that would make it cheap only exists when the source is co-located with
    ''' the output database (it is not, and the account is read-only). NEVER call this
    ''' from a page load - the dashboard uses the unfiltered count and accepts a 0.29%
    ''' overstatement. This backs the Tools tab's explicit, user-initiated button.
    ''' </para>
    ''' <para>
    ''' Not a true remaining count on its own: no anti-join against the attempted set
    ''' (that would mean COPYing ~280k ids to the source). Callers subtract the
    ''' attempted total, leaving a small drift for trials attempted but no longer
    ''' selectable.
    ''' </para>
    ''' </summary>
    Function CountSelectableSourceTrialsAsync(
            cancellationToken As CancellationToken) As Task(Of Long?)

    ''' <summary>
    ''' Count of SUPERSEDED public.eligibility_study rows: every attempt that is not
    ''' the most recent one for its NCT_ID, regardless of status. Same projection the
    ''' Studies tab's "Hide superseded attempts" toggle uses
    ''' (<c>ROW_NUMBER() OVER (PARTITION BY nct_id ORDER BY started_at DESC)</c>,
    ''' keeping rn = 1), counted from the other side: rn > 1.
    ''' <para>
    ''' A trial attempted once contributes 0. A trial attempted three times
    ''' contributes 2, whether those attempts succeeded or failed.
    ''' </para>
    ''' </summary>
    Function CountSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long)

    ''' <summary>
    ''' DESTRUCTIVE. Deletes every superseded attempt row counted by
    ''' <see cref="CountSupersededStudiesAsync"/>, keeping the most recent attempt
    ''' per NCT_ID. Returns the number of rows deleted.
    ''' <para>
    ''' PROGRESSION IS PRESERVED BY CONSTRUCTION: exactly one row per NCT_ID always
    ''' survives, so the DISTINCT NCT_ID set behind
    ''' <see cref="GetAttemptedNctIdsAsync"/> - the pipeline's only progress marker
    ''' (spec section 2.2) - is bit-for-bit unchanged. This trims audit history, it
    ''' never makes a trial eligible for reprocessing.
    ''' </para>
    ''' <para>
    ''' What it does NOT touch: public.eligibility (the extracted criteria rows are
    ''' per-trial DELETE+INSERT, so they only ever reflect the latest attempt
    ''' anyway), public.eligibility_run, and public.eligibility_failed.
    ''' </para>
    ''' </summary>
    Function DeleteSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long)

    ''' <summary>
    ''' LLM concept-normalization — record one outcome. In a single transaction:
    ''' UPSERT the umls.concept_normalization cache row for
    ''' <paramref name="conceptNorm"/> (the LLM term + the re-lookup result), and
    ''' when <paramref name="match"/> is resolved, UPDATE every public.eligibility
    ''' row whose concept_code is empty and whose normalized concept equals
    ''' <paramref name="conceptNorm"/> with the five UMLS columns (mirrors
    ''' ApplyUmlsRetryAsync's in-place UPDATE; NCT_ID membership unchanged). An
    ''' unresolved <paramref name="match"/> still records the attempt (resolved =
    ''' false) so the concept isn't re-normalized every batch. Returns the number of
    ''' eligibility rows updated (0 when unresolved).
    ''' </summary>
    Function RecordConceptNormalizationAsync(
            conceptNorm As String,
            normalizedTerm As String,
            match As UmlsMatch,
            semanticType As String,
            cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' LLM concept-normalization — inline cache read used by the extraction
    ''' pipeline. Takes the RAW concept strings of lexically-unresolved criteria,
    ''' normalizes each in SQL (the same key the cache is written under), and
    ''' returns the cached RESOLVED mapping for those that have one — keyed by the
    ''' ORIGINAL raw concept the caller passed (so the orchestrator can look up by
    ''' <c>criterion.Concept</c> without depending on the Data-layer normalization).
    ''' Concepts with no resolved cache row are simply absent. A cheap indexed
    ''' lookup, no LLM. Errors are surfaced to the caller, which treats them as a
    ''' miss (the UMLS path is non-fatal).
    ''' </summary>
    Function GetCachedNormalizationsAsync(
            concepts As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, CachedConceptResolution))

    ''' <summary>
    ''' Records or updates the metrics row for one run in public.eligibility_run.
    ''' Idempotent on run_id; partial rows (no EndedAt) are valid for in-flight runs.
    ''' </summary>
    Function RecordRunAsync(
            metrics As RunMetrics,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Records a terminal LLM-call failure into public.eligibility_failed so the
    ''' "retry" CLI command can revisit it. UPSERTs on nct_id, incrementing
    ''' attempt_count. Closes gap section 9.1.
    ''' </summary>
    Function RecordFailedTrialAsync(
            nctId As String,
            errorMessage As String,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Returns the most recent <paramref name="limit"/> rows from
    ''' public.eligibility_run ordered by started_at DESC. Used by the Web
    ''' dashboard's runs history view. The limit is clamped to a sensible
    ''' upper bound by the implementation.
    ''' </summary>
    Function GetRecentRunsAsync(
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics))

    ''' <summary>A page of runs (newest first), for the paginated Runs tab.
    ''' Same shape as GetRecentRunsAsync with an OFFSET.</summary>
    Function GetRunsPageAsync(
            limit As Integer,
            offset As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics))

    ''' <summary>Total row count of public.eligibility_run, for the Runs tab's
    ''' "showing X-Y of N" and pagination bounds.</summary>
    Function CountRunsAsync(cancellationToken As CancellationToken) As Task(Of Long)

    ''' <summary>
    ''' Returns one page of rows from public.eligibility matching
    ''' <paramref name="filter"/>. Empty fields on the filter are ignored.
    ''' Categorical fields (nct_id, domain, concept_code, semantic_type) match
    ''' exactly; free-text fields (criterion, concept) use case-insensitive
    ''' substring match.
    '''
    ''' <paramref name="sortBy"/> picks the result ordering from a hardcoded
    ''' whitelist (see <c>OrderByMap</c> in the implementation). Unknown /
    ''' empty values fall back to <c>created_at DESC</c>.
    '''
    ''' <paramref name="page"/> is 1-based; the returned page also carries the
    ''' total unfiltered match count so the caller can render a pager.
    ''' </summary>
    Function SearchEligibilityAsync(
            filter As EligibilityFilter,
            sortBy As String,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityResultPage)

    ''' <summary>
    ''' Returns distinct values for each filterable column on public.eligibility,
    ''' but ONLY for columns whose cardinality is at or below
    ''' <paramref name="maxDropdownSize"/>. Columns above the threshold get an
    ''' empty list — the dashboard renders a text input for those, dropdown for
    ''' the rest. The query is bounded (LIMIT maxDropdownSize + 1) so high-
    ''' cardinality columns don't trigger a full table scan.
    ''' </summary>
    Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions)

    ''' <summary>
    ''' Returns the high-level study card for the dashboard's Analysis tab —
    ''' a curated projection of ctgov.studies + brief_summaries + conditions +
    ''' interventions for the given <paramref name="nctId"/>. Returns Nothing
    ''' when the trial isn't in the source database.
    ''' </summary>
    Function GetStudyDetailsAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudyDetails)

    ''' <summary>
    ''' Returns the full ctgov.eligibilities row for the given
    ''' <paramref name="nctId"/> — the raw criteria text plus the structured
    ''' fields AACT publishes alongside it. Returns Nothing when the trial
    ''' has no eligibility row in the source database.
    ''' </summary>
    Function GetSourceEligibilityAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of SourceEligibilityDetails)

    ''' <summary>
    ''' Reads the study metadata + eligibility detail for <paramref name="nctId"/>
    ''' from the AACT source DB and UPSERTs it into public.eligibility_study_detail
    ''' on the output DB (keyed by nct_id, refreshing captured_at). This is the
    ''' persisted snapshot the Analysis tab reads so it can render without a live
    ''' AACT connection. A no-op when the trial has no ctgov.studies row.
    ''' Called per-trial by the orchestrator (best-effort) and by the CLI's
    ''' backfill-details command. Requires the source data source to be configured.
    ''' </summary>
    Function CaptureStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Returns the persisted snapshot for <paramref name="nctId"/> from
    ''' public.eligibility_study_detail (output DB), or Nothing when no snapshot
    ''' has been captured for that trial yet. Callers fall back to the live
    ''' AACT lookups (GetStudyDetailsAsync / GetSourceEligibilityAsync) on Nothing.
    ''' </summary>
    Function GetStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudySnapshot)

    ''' <summary>
    ''' Inserts a "running" audit row into public.eligibility_study just before
    ''' the LLM call for a trial. Keyed by (run_id, nct_id); UPSERTs on conflict
    ''' so a re-processed trial within the same run resets cleanly.
    ''' </summary>
    Function StartStudyAsync(
            runId As Guid,
            nctId As String,
            startedAt As DateTimeOffset,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Updates the audit row to its terminal state — sets finished_at and the
    ''' status / diagnostic columns from <paramref name="execution"/>. The
    ''' insert from <see cref="StartStudyAsync"/> must have happened first;
    ''' callers wrap both in try/catch and treat audit-write failures as
    ''' warnings (must not abort the trial).
    ''' </summary>
    Function FinishStudyAsync(
            execution As StudyExecution,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Returns one page of audit rows matching <paramref name="filter"/>.
    ''' nct_id / status / run_id all match exactly when set. <paramref name="sortBy"/>
    ''' picks the ordering from a hardcoded whitelist; unknown values fall back
    ''' to <c>started_at DESC</c>.
    ''' </summary>
    Function GetStudiesAsync(
            filter As StudyFilter,
            sortBy As String,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of StudyExecutionPage)

    ''' <summary>
    ''' Returns every audit row for a single trial, newest first. Used by the
    ''' Analysis tab's processing-history panel; unpaginated because a single
    ''' trial rarely has more than a handful of runs touching it.
    ''' </summary>
    Function GetStudyHistoryAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyExecution))

    ''' <summary>
    ''' Searches the persisted study-detail snapshot (public.eligibility_study_detail)
    ''' by the columns the Analysis-tab Search modal exposes. Every non-empty
    ''' filter field matches case-insensitively as a substring (ILIKE %@value%);
    ''' an empty / Nothing field is ignored. <paramref name="filter"/>.Condition
    ''' matches when ANY element of the conditions text[] contains the value.
    '''
    ''' Results are ordered by nct_id ascending and capped at <paramref name="limit"/>
    ''' (clamped to 1..500 by the implementation). An IsEmpty filter returns an
    ''' empty list — the modal won't issue an unconditional whole-table dump.
    ''' </summary>
    Function SearchStudyDetailsAsync(
            filter As StudySearchFilter,
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudySearchResult))

    ''' <summary>
    ''' Returns the dashboard's headline counters in a single round-trip:
    ''' total runs recorded, total persisted eligibility rows, studies whose
    ''' latest attempt is success, studies whose latest attempt is still in a
    ''' failure status (llm_failed / parse_invalid_json / persist_failed /
    ''' the generic 'failed' — parse_empty and cancelled are treated as
    ''' valid terminal states, not failures), and the UMLS resolution rate
    ''' (share of eligibility rows with non-null concept_code). Empty
    ''' database returns zeros.
    ''' </summary>
    Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics)

    ''' <summary>
    ''' Deletes a single audit row from public.eligibility_study identified by
    ''' its composite primary key (run_id, nct_id). Returns the number of rows
    ''' deleted (0 if no matching row, 1 on success). Used by the Studies tab's
    ''' "Delete" column so operators can clean up the audit hit-list after
    ''' triaging failed runs.
    '''
    ''' Does NOT cascade to public.eligibility — persisted criterion rows for
    ''' the trial (if any) are left in place. Failed parse attempts have no
    ''' persisted rows, so the typical cleanup case is a no-op there anyway.
    ''' </summary>
    Function DeleteStudyAsync(
            runId As Guid,
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of Integer)

    ' ============ Authoring feature (output DB, authoring_* tables) ============

    ''' <summary>
    ''' Returns a summary of every authored study, newest-updated first, for the
    ''' /Authoring landing page. See authoring specification §3.1.1.
    ''' </summary>
    Function ListAuthoringStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuthoringStudySummary))

    ''' <summary>
    ''' Loads one authored study in full — characteristics, the 1:1 eligibility
    ''' data, and the ordered authored-criteria list. Returns Nothing when no
    ''' authoring_study row has that id.
    ''' </summary>
    Function GetAuthoringStudyAsync(
            authoringStudyId As Guid,
            cancellationToken As CancellationToken) As Task(Of AuthoringStudyAggregate)

    ''' <summary>
    ''' True if any authoring_study already uses this Study ID (case-insensitive,
    ''' trimmed). Used to enforce Study ID uniqueness before insert.
    ''' </summary>
    Function StudyIdExistsAsync(
            studyId As String,
            cancellationToken As CancellationToken) As Task(Of Boolean)

    ''' <summary>
    ''' Inserts a new authored study and its 1:1 eligibility row in a single
    ''' transaction. The study's AuthoringStudyId must already be assigned.
    ''' <paramref name="userId"/> is recorded as both created_by and
    ''' last_updated_by.
    ''' </summary>
    Function CreateAuthoringStudyAsync(
            study As AuthoringStudy,
            eligibility As AuthoringEligibility,
            userId As Guid,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Updates the authoring_study row's characteristics columns, sets
    ''' last_updated_by to <paramref name="userId"/>, and refreshes updated_at.
    ''' The criteria list and eligibility row are saved separately.
    ''' </summary>
    Function UpdateAuthoringStudyAsync(
            study As AuthoringStudy,
            userId As Guid,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Sets the user-facing Study ID for a study that does not yet have one
    ''' (legacy studies created before the V13 migration). The update is guarded
    ''' to <c>study_id IS NULL OR study_id = ''</c>, so it can never overwrite an
    ''' already-assigned Study ID — the "fixed once set" invariant holds even
    ''' under a race. Refreshes updated_at / last_updated_by. Returns True if a
    ''' row was updated (the Study ID was empty and is now set), False otherwise.
    ''' Caller is responsible for the case-insensitive uniqueness check
    ''' (<see cref="StudyIdExistsAsync"/>) before calling.
    ''' </summary>
    Function SetAuthoringStudyIdAsync(
            authoringStudyId As Guid,
            studyId As String,
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of Boolean)

    ''' <summary>
    ''' UPSERTs the authoring_eligibility row for one study and refreshes the
    ''' parent study's updated_at + last_updated_by (to <paramref name="userId"/>).
    ''' </summary>
    Function SaveAuthoringEligibilityAsync(
            eligibility As AuthoringEligibility,
            userId As Guid,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Upserts the authored-criteria list for one study within a single
    ''' transaction: rows no longer present are deleted, and each supplied row is
    ''' inserted or updated by id (ON CONFLICT), so a row's created_at/created_by
    ''' survive across edits while last_updated_by/updated_at refresh.
    ''' <paramref name="userId"/> is recorded as created_by on new rows and
    ''' last_updated_by on all rows. Each criterion's
    ''' <see cref="AuthoringCriterion.Sources"/> lineage rows are rewritten, and
    ''' the parent study's updated_at + last_updated_by are refreshed.
    ''' </summary>
    Function SaveAuthoringCriteriaAsync(
            authoringStudyId As Guid,
            criteria As IReadOnlyList(Of AuthoringCriterion),
            userId As Guid,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Deletes an authored study; the 1:1 eligibility row and the criteria list
    ''' cascade. Returns the number of authoring_study rows deleted (0 or 1).
    ''' </summary>
    Function DeleteAuthoringStudyAsync(
            authoringStudyId As Guid,
            cancellationToken As CancellationToken) As Task(Of Integer)

    ' ===== Authoring Analysis phase (similarity + clustering) =====

    ''' <summary>
    ''' Ranks processed studies by cosine similarity of their topic embedding
    ''' to <paramref name="queryVector"/>, most-similar first, capped at
    ''' <paramref name="limit"/>. Restricted to studies that have rows in
    ''' public.eligibility (authoring specification §3.3).
    '''
    ''' Optional exact-match filters: <paramref name="filterPhase"/> and
    ''' <paramref name="filterStudyType"/> restrict results to studies whose
    ''' <c>d.phase</c> / <c>d.study_type</c> equals the supplied value. Empty
    ''' strings disable the corresponding filter (used by the "Match Phase
    ''' and Type" toggle on the Authoring Analysis tab).
    ''' </summary>
    Function FindSimilarStudiesAsync(
            queryVector As IReadOnlyList(Of Single),
            limit As Integer,
            cancellationToken As CancellationToken,
            Optional filterPhase As String = "",
            Optional filterStudyType As String = "") As Task(Of IReadOnlyList(Of SimilarStudy))

    ''' <summary>
    ''' Ranks processed studies by cosine similarity to <paramref name="nctId"/>'s
    ''' own topic embedding (pulled from eligibility_study_embedding), most-similar
    ''' first, capped at <paramref name="limit"/>. The source trial is excluded.
    ''' Restricted to studies that have rows in public.eligibility, matching the
    ''' Authoring similarity search.
    '''
    ''' Optional toggles: <paramref name="matchPhase"/> restricts the result set
    ''' to studies whose <c>d.phase</c> equals the source trial's phase;
    ''' <paramref name="matchStudyType"/> restricts on <c>d.study_type</c>.
    '''
    ''' Returns <c>Nothing</c> when the source trial has no embedding yet —
    ''' callers can distinguish "no similar matches" (empty list) from
    ''' "source not embedded" (Nothing).
    ''' </summary>
    Function FindSimilarTrialsToAsync(
            nctId As String,
            limit As Integer,
            matchPhase As Boolean,
            matchStudyType As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SimilarStudy))

    ''' <summary>
    ''' Clusters public.eligibility rows for the given studies by criterion and
    ''' concept identity, ordered by descending commonality (authoring
    ''' specification §3.4). Inclusion and Exclusion clusters are both returned;
    ''' the caller splits on <see cref="CriterionCluster.Criterion"/>.
    ''' </summary>
    Function ClusterCommonCriteriaAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of CriterionCluster))

    ''' <summary>
    ''' Returns the individual public.eligibility rows behind one cluster — the
    ''' rows for the given studies whose criterion and concept identity match
    ''' <paramref name="criterion"/> / <paramref name="groupKey"/>.
    ''' </summary>
    Function GetClusterRecordsAsync(
            nctIds As IReadOnlyList(Of String),
            criterion As String,
            groupKey As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of EligibilityRow))

    ''' <summary>
    ''' Returns processed studies that still need a topic embedding under
    ''' <paramref name="model"/> — those with public.eligibility rows and an
    ''' eligibility_study_detail snapshot, but no eligibility_study_embedding
    ''' row for that model. Drives the CLI embed-studies backfill.
    ''' </summary>
    Function GetStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyEmbeddingInput))

    ''' <summary>
    ''' Count of processed studies that still need a topic embedding under
    ''' <paramref name="model"/> (the remaining-work count the dashboard Tools tab
    ''' shows for embed-studies) - the same set <see cref="GetStudiesToEmbedAsync"/>
    ''' returns, counted.
    ''' </summary>
    Function CountStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' Returns the topic-embedding input for a single study, read from its
    ''' eligibility_study_detail snapshot, or Nothing when no snapshot exists.
    ''' Lets the pipeline embed a study inline right after persisting its
    ''' eligibility rows, rather than waiting for the embed-studies backfill.
    ''' </summary>
    Function GetStudyEmbeddingInputAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudyEmbeddingInput)

    ''' <summary>
    ''' UPSERTs one study's topic embedding into eligibility_study_embedding.
    ''' </summary>
    Function UpsertStudyEmbeddingAsync(
            nctId As String,
            embedding As IReadOnlyList(Of Single),
            model As String,
            sourceText As String,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Aggregate stats for the eligibility_study_embedding corpus index: total row
    ''' count and the per-model breakdown. Backs the owner-only embeddings
    ''' export/import surface, which surfaces the model so an imported set stays
    ''' comparable for Find Similar.
    ''' </summary>
    Function GetEmbeddingStatsAsync(cancellationToken As CancellationToken) As Task(Of EmbeddingStats)

    ''' <summary>
    ''' Empties eligibility_study_embedding (TRUNCATE), returning the number of rows
    ''' that were present. Used before an embeddings import so the imported set fully
    ''' replaces the existing one.
    ''' </summary>
    Function ClearStudyEmbeddingsAsync(cancellationToken As CancellationToken) As Task(Of Long)

    ' ============ Authentication / users (output DB, app_user) ============

    ''' <summary>Total number of rows in app_user. Zero triggers first-run bootstrap.</summary>
    Function CountUsersAsync(cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' Number of active users with the Owner role. Backs the protected-Owner rule
    ''' (the last Owner cannot be demoted or deleted).
    ''' </summary>
    Function CountOwnersAsync(cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Loads a user by case-insensitive user_name, or Nothing.</summary>
    Function GetUserByUserNameAsync(
            userName As String,
            cancellationToken As CancellationToken) As Task(Of AppUser)

    ''' <summary>Loads a user by case-insensitive email, or Nothing.</summary>
    Function GetUserByEmailAsync(
            email As String,
            cancellationToken As CancellationToken) As Task(Of AppUser)

    ''' <summary>Loads a user by linked Google subject id, or Nothing.</summary>
    Function GetUserByGoogleSubjectAsync(
            googleSubject As String,
            cancellationToken As CancellationToken) As Task(Of AppUser)

    ''' <summary>Loads a user by id, or Nothing.</summary>
    Function GetUserAsync(
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of AppUser)

    ''' <summary>Every user, ordered by user_name, for the Manage Accounts list.</summary>
    Function ListUsersAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AppUser))

    ''' <summary>Inserts a new user. UserId must already be assigned.</summary>
    Function CreateUserAsync(
            user As AppUser,
            cancellationToken As CancellationToken) As Task

    ''' <summary>Updates a user's role and refreshes updated_at.</summary>
    Function UpdateUserRoleAsync(
            userId As Guid,
            role As Role,
            cancellationToken As CancellationToken) As Task

    ''' <summary>Sets a user's password hash (empty clears it) and refreshes updated_at.</summary>
    Function UpdateUserPasswordHashAsync(
            userId As Guid,
            passwordHash As String,
            cancellationToken As CancellationToken) As Task

    ''' <summary>Links a Google subject + picture to a user (account linking by email).</summary>
    Function LinkGoogleSubjectAsync(
            userId As Guid,
            googleSubject As String,
            pictureUrl As String,
            cancellationToken As CancellationToken) As Task

    ''' <summary>Records a successful login (sets last_login_at).</summary>
    Function RecordLoginAsync(
            userId As Guid,
            whenUtc As DateTimeOffset,
            cancellationToken As CancellationToken) As Task

    ''' <summary>Deletes a user. Returns the number of rows deleted (0 or 1).</summary>
    Function DeleteUserAsync(
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of Integer)

    ' ============ Auditing (output DB, audit_log) ============

    ''' <summary>Appends one row to the audit trail.</summary>
    Function InsertAuditAsync(
            entry As AuditEntry,
            cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Returns one page of audit_log rows (newest first) matching the filter,
    ''' plus the unfiltered-by-page total. Backs the Audit Trail view.
    ''' </summary>
    Function GetAuditLogAsync(
            filter As AuditLogFilter,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of AuditLogPage)

    ''' <summary>
    ''' Returns ALL audit_log rows matching the filter (newest first), for export.
    ''' Not paginated; a high safety cap bounds the result.
    ''' </summary>
    Function GetAuditLogForExportAsync(
            filter As AuditLogFilter,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuditEntry))

End Interface

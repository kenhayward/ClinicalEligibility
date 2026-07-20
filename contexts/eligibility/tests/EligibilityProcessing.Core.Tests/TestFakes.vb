Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core

' In-memory test doubles for the four boundary contracts the orchestrator
' depends on. Each one:
'   - records the calls it received (so tests can assert exact invocation count)
'   - serves canned responses configured per-test
'   - is thread-safe enough for parallel orchestrator execution

' ============ Fake IPostgresGateway ============

Friend NotInheritable Class FakeGateway
    Implements IPostgresGateway

    Public Property AttemptedNctIds As IReadOnlyList(Of String) = Array.Empty(Of String)()
    Public Property TrialsToReturn As IReadOnlyList(Of Trial) = Array.Empty(Of Trial)()
    Public Property PersistFailures As New HashSet(Of String)(StringComparer.Ordinal)

    ' --- tool-job knobs (default to the empty behaviour; the tool-job tests set them) ---
    Public Property NormalizeConcepts As IReadOnlyList(Of ConceptToNormalize) = Array.Empty(Of ConceptToNormalize)()
    Public Property RecordNormalizationRows As Integer = 0
    Public Property StudiesToEmbed As IReadOnlyList(Of StudyEmbeddingInput) = Array.Empty(Of StudyEmbeddingInput)()
    Public ReadOnly Property RecordNormalizationCalls As New ConcurrentBag(Of (ConceptNorm As String, Term As String, Resolved As Boolean))

    Public ReadOnly Property PersistTrialCalls As New ConcurrentBag(Of (NctId As String, Records As IReadOnlyList(Of ResolvedRecord)))
    Public ReadOnly Property RecordRunCalls As New List(Of RunMetrics)
    Public ReadOnly Property RecordFailedTrialCalls As New ConcurrentBag(Of (NctId As String, ErrorMessage As String))
    Public ReadOnly Property GetAttemptedNctIdsCalls As New ConcurrentBag(Of Integer)

    ' --- corpus-read knobs (CorpusReadCacheTests). Counters are the point: the
    ' cache is only observable through how often it reaches the gateway. ---
    Public Property MetricsToReturn As DashboardMetrics = DashboardMetrics.Empty
    Public Property FilterOptionsToReturn As EligibilityFilterOptions = EligibilityFilterOptions.Empty
    ' When set, both corpus reads throw this instead of returning. Used to prove
    ' failures are not cached.
    Public Property CorpusReadError As Exception = Nothing
    Private _getDashboardMetricsCalls As Integer
    Private _getFilterOptionsCalls As Integer

    Public ReadOnly Property GetDashboardMetricsCalls As Integer
        Get
            Return Volatile.Read(_getDashboardMetricsCalls)
        End Get
    End Property

    Public ReadOnly Property GetFilterOptionsCalls As Integer
        Get
            Return Volatile.Read(_getFilterOptionsCalls)
        End Get
    End Property

    ' Every maxDropdownSize the cache actually forwarded, in order.
    Public ReadOnly Property FilterOptionsCallArgs As New ConcurrentBag(Of Integer)

    Public Function GetAttemptedNctIdsAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IPostgresGateway.GetAttemptedNctIdsAsync
        cancellationToken.ThrowIfCancellationRequested()
        GetAttemptedNctIdsCalls.Add(AttemptedNctIds.Count)
        Return Task.FromResult(AttemptedNctIds)
    End Function

    Public Function SelectNextTrialsAsync(
            excludedNctIds As IReadOnlyList(Of String),
            direction As TrialSelectionDirection,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial)) _
            Implements IPostgresGateway.SelectNextTrialsAsync
        cancellationToken.ThrowIfCancellationRequested()
        SelectNextTrialsCalls.Add((excludedNctIds, direction, studyCount))
        Return Task.FromResult(TrialsToReturn)
    End Function

    ' Re-run path: orchestrator fetches a single trial by NCT_ID instead of
    ' running the batch select. Tests configure SingleTrials to seed which
    ' NCT_IDs are "in the source DB" and assert what GetSourceTrialAsync gets
    ' called with.
    Public ReadOnly Property SelectNextTrialsCalls As New ConcurrentBag(Of (ExcludedNctIds As IReadOnlyList(Of String), Direction As TrialSelectionDirection, StudyCount As Integer))
    Public ReadOnly Property GetSourceTrialCalls As New ConcurrentBag(Of String)
    Public ReadOnly Property GetSourceTrialsCalls As New ConcurrentBag(Of IReadOnlyList(Of String))
    Public Property SingleTrials As New Dictionary(Of String, Trial)(StringComparer.OrdinalIgnoreCase)

    Public Function GetSourceTrialAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of Trial) _
            Implements IPostgresGateway.GetSourceTrialAsync
        cancellationToken.ThrowIfCancellationRequested()
        GetSourceTrialCalls.Add(nctId)
        Dim trial As Trial = Nothing
        SingleTrials.TryGetValue(nctId, trial)
        Return Task.FromResult(trial)
    End Function

    ' Batch re-run path: resolves each requested NCT_ID against the same
    ' SingleTrials seed, preserving the "missing ids are simply absent" contract.
    Public Function GetSourceTrialsAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial)) _
            Implements IPostgresGateway.GetSourceTrialsAsync
        cancellationToken.ThrowIfCancellationRequested()
        GetSourceTrialsCalls.Add(nctIds)
        Dim found As New List(Of Trial)
        If nctIds IsNot Nothing Then
            For Each nctId In nctIds
                Dim trial As Trial = Nothing
                If Not String.IsNullOrWhiteSpace(nctId) AndAlso SingleTrials.TryGetValue(nctId.Trim(), trial) Then
                    found.Add(trial)
                End If
            Next
        End If
        Return Task.FromResult(Of IReadOnlyList(Of Trial))(found)
    End Function

    Public Function PersistTrialAsync(
            nctId As String,
            records As IReadOnlyList(Of ResolvedRecord),
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.PersistTrialAsync
        cancellationToken.ThrowIfCancellationRequested()
        If PersistFailures.Contains(nctId) Then
            Return Task.FromException(New InvalidOperationException($"Persist failed for {nctId}"))
        End If
        PersistTrialCalls.Add((nctId, records))
        Return Task.CompletedTask
    End Function

    Public Function SelectTrialsToRetryUmlsAsync(
            direction As TrialSelectionDirection,
            count As Integer,
            includeRetried As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IPostgresGateway.SelectTrialsToRetryUmlsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
    End Function

    Public Function GetUnresolvedRowsForTrialAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsRetryRow)) _
            Implements IPostgresGateway.GetUnresolvedRowsForTrialAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of UmlsRetryRow)(), IReadOnlyList(Of UmlsRetryRow)))
    End Function

    Public Function ApplyUmlsRetryAsync(
            nctId As String,
            results As IReadOnlyList(Of UmlsRetryResult),
            rowsAttempted As Integer,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.ApplyUmlsRetryAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.CompletedTask
    End Function

    ' Inline normalization-cache consult: tests seed CachedNormalizations to drive the
    ' orchestrator's hybrid hook; GetCachedNormalizationsCalls records the lookups.
    Public Property CachedNormalizations As New Dictionary(Of String, CachedConceptResolution)(StringComparer.Ordinal)
    Public ReadOnly Property GetCachedNormalizationsCalls As New ConcurrentBag(Of IReadOnlyList(Of String))

    Public Function SelectConceptsToNormalizeAsync(
            count As Integer,
            includeAttempted As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptToNormalize)) _
            Implements IPostgresGateway.SelectConceptsToNormalizeAsync
        cancellationToken.ThrowIfCancellationRequested()
        Dim taken As IReadOnlyList(Of ConceptToNormalize) =
            NormalizeConcepts.Take(Math.Max(1, count)).ToList()
        Return Task.FromResult(taken)
    End Function

    Public Function CountConceptsToNormalizeAsync(
            includeAttempted As Boolean,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountConceptsToNormalizeAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(NormalizeConcepts.Count)
    End Function

    Public Function RecordConceptNormalizationAsync(
            conceptNorm As String,
            normalizedTerm As String,
            match As UmlsMatch,
            semanticType As String,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.RecordConceptNormalizationAsync
        cancellationToken.ThrowIfCancellationRequested()
        RecordNormalizationCalls.Add((conceptNorm, normalizedTerm, match.IsResolved))
        Return Task.FromResult(RecordNormalizationRows)
    End Function

    Public Function GetCachedNormalizationsAsync(
            conceptNorms As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, CachedConceptResolution)) _
            Implements IPostgresGateway.GetCachedNormalizationsAsync
        cancellationToken.ThrowIfCancellationRequested()
        GetCachedNormalizationsCalls.Add(conceptNorms)
        Dim hits As New Dictionary(Of String, CachedConceptResolution)(StringComparer.Ordinal)
        If conceptNorms IsNot Nothing Then
            For Each k In conceptNorms
                Dim v As CachedConceptResolution = Nothing
                If k IsNot Nothing AndAlso CachedNormalizations.TryGetValue(k, v) Then hits(k) = v
            Next
        End If
        Return Task.FromResult(CType(hits, IReadOnlyDictionary(Of String, CachedConceptResolution)))
    End Function

    Public Function RecordRunAsync(metrics As RunMetrics, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordRunAsync
        RecordRunCalls.Add(metrics)
        Return Task.CompletedTask
    End Function

    ' Manual dashboard action - never invoked by the orchestrator, so the fake
    ' reports "nothing was stranded" rather than tracking calls.
    Public Function ResolveInterruptedRunAsync(
            runId As Guid, status As String, reason As String,
            cancellationToken As CancellationToken) As Task(Of (RunUpdated As Boolean, StudiesReconciled As Integer)) _
            Implements IPostgresGateway.ResolveInterruptedRunAsync
        Return Task.FromResult((False, 0))
    End Function

    Public Function RecordFailedTrialAsync(
            nctId As String,
            errorMessage As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordFailedTrialAsync
        RecordFailedTrialCalls.Add((nctId, errorMessage))
        Return Task.CompletedTask
    End Function

    Public Function GetRecentRunsAsync(
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics)) _
            Implements IPostgresGateway.GetRecentRunsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of RunMetrics)(), IReadOnlyList(Of RunMetrics)))
    End Function

    Public Function GetRunsPageAsync(
            limit As Integer,
            offset As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics)) _
            Implements IPostgresGateway.GetRunsPageAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of RunMetrics)(), IReadOnlyList(Of RunMetrics)))
    End Function

    Public Function CountRunsAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.CountRunsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(0L)
    End Function

    Public Function SearchEligibilityAsync(
            filter As EligibilityFilter,
            sortBy As String,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityResultPage) _
            Implements IPostgresGateway.SearchEligibilityAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(EligibilityResultPage.Empty)
    End Function

    Public Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions) _
            Implements IPostgresGateway.GetEligibilityFilterOptionsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Interlocked.Increment(_getFilterOptionsCalls)
        FilterOptionsCallArgs.Add(maxDropdownSize)
        If CorpusReadError IsNot Nothing Then Return Task.FromException(Of EligibilityFilterOptions)(CorpusReadError)
        Return Task.FromResult(FilterOptionsToReturn)
    End Function

    Public Function GetStudyDetailsAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudyDetails) _
            Implements IPostgresGateway.GetStudyDetailsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(Of StudyDetails)(Nothing)
    End Function

    Public Function GetSourceEligibilityAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of SourceEligibilityDetails) _
            Implements IPostgresGateway.GetSourceEligibilityAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(Of SourceEligibilityDetails)(Nothing)
    End Function

    ' Study-snapshot hooks. CaptureStudySnapshotCalls records every per-trial
    ' capture so orchestrator tests can assert invocation count; set
    ' CaptureStudySnapshotThrowFor to drive the best-effort swallow path.
    Public ReadOnly Property CaptureStudySnapshotCalls As New ConcurrentBag(Of String)
    Public Property CaptureStudySnapshotThrowFor As String = Nothing
    Public Property Snapshots As New Dictionary(Of String, StudySnapshot)(StringComparer.OrdinalIgnoreCase)

    Public Function CaptureStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CaptureStudySnapshotAsync
        cancellationToken.ThrowIfCancellationRequested()
        If CaptureStudySnapshotThrowFor IsNot Nothing AndAlso CaptureStudySnapshotThrowFor = nctId Then
            Return Task.FromException(New InvalidOperationException($"CaptureStudySnapshot failed for {nctId}"))
        End If
        CaptureStudySnapshotCalls.Add(nctId)
        Return Task.CompletedTask
    End Function

    Public Function GetStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudySnapshot) _
            Implements IPostgresGateway.GetStudySnapshotAsync
        cancellationToken.ThrowIfCancellationRequested()
        Dim snapshot As StudySnapshot = Nothing
        Snapshots.TryGetValue(nctId, snapshot)
        Return Task.FromResult(snapshot)
    End Function

    ' Per-trial audit hooks. Recording StartStudy calls + FinishStudy calls so
    ' orchestrator tests can assert the audit lifecycle without spinning up
    ' Postgres. ThreadSafe — orchestrator processes trials in parallel.
    Public ReadOnly Property StartStudyCalls As New ConcurrentBag(Of (RunId As Guid, NctId As String, StartedAt As DateTimeOffset))
    Public ReadOnly Property FinishStudyCalls As New ConcurrentBag(Of StudyExecution)
    Public Property StartStudyThrowFor As String = Nothing   ' nct_id to throw on (test the swallow path)

    Public Function StartStudyAsync(
            runId As Guid,
            nctId As String,
            startedAt As DateTimeOffset,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.StartStudyAsync
        cancellationToken.ThrowIfCancellationRequested()
        If StartStudyThrowFor IsNot Nothing AndAlso StartStudyThrowFor = nctId Then
            Return Task.FromException(New InvalidOperationException($"StartStudy failed for {nctId}"))
        End If
        StartStudyCalls.Add((runId, nctId, startedAt))
        Return Task.CompletedTask
    End Function

    Public Function FinishStudyAsync(
            execution As StudyExecution,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.FinishStudyAsync
        FinishStudyCalls.Add(execution)
        Return Task.CompletedTask
    End Function

    Public Function GetStudiesAsync(
            filter As StudyFilter,
            sortBy As String,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of StudyExecutionPage) _
            Implements IPostgresGateway.GetStudiesAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(StudyExecutionPage.Empty)
    End Function

    Public Function GetStudyHistoryAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyExecution)) _
            Implements IPostgresGateway.GetStudyHistoryAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of StudyExecution)(), IReadOnlyList(Of StudyExecution)))
    End Function

    Public Function SearchStudyDetailsAsync(
            filter As StudySearchFilter,
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudySearchResult)) _
            Implements IPostgresGateway.SearchStudyDetailsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of StudySearchResult)(), IReadOnlyList(Of StudySearchResult)))
    End Function

    Public Function FindSimilarTrialsToAsync(
            nctId As String,
            limit As Integer,
            matchPhase As Boolean,
            matchStudyType As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SimilarStudy)) _
            Implements IPostgresGateway.FindSimilarTrialsToAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(CType(Array.Empty(Of SimilarStudy)(), IReadOnlyList(Of SimilarStudy)))
    End Function

    Public Function DeleteStudyAsync(
            runId As Guid,
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteStudyAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(0)
    End Function

    ' --- superseded-attempt housekeeping (Tools tab) ---
    Public Property SupersededCountToReturn As Long = 0
    Public Property SupersededDeletedToReturn As Long = 0
    Private _deleteSupersededCalls As Integer

    Public ReadOnly Property DeleteSupersededCalls As Integer
        Get
            Return Volatile.Read(_deleteSupersededCalls)
        End Get
    End Property

    ' --- exact (expensive) source count, Tools tab on-demand ---
    Public Property SelectableSourceTotalToReturn As Long? = Nothing
    Private _countSelectableSourceCalls As Integer

    Public ReadOnly Property CountSelectableSourceCalls As Integer
        Get
            Return Volatile.Read(_countSelectableSourceCalls)
        End Get
    End Property

    Public Function CountSelectableSourceTrialsAsync(
            cancellationToken As CancellationToken) As Task(Of Long?) _
            Implements IPostgresGateway.CountSelectableSourceTrialsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Interlocked.Increment(_countSelectableSourceCalls)
        Return Task.FromResult(SelectableSourceTotalToReturn)
    End Function

    Public Function CountSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.CountSupersededStudiesAsync
        cancellationToken.ThrowIfCancellationRequested()
        Return Task.FromResult(SupersededCountToReturn)
    End Function

    Public Function DeleteSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.DeleteSupersededStudiesAsync
        cancellationToken.ThrowIfCancellationRequested()
        Interlocked.Increment(_deleteSupersededCalls)
        Return Task.FromResult(SupersededDeletedToReturn)
    End Function

    Public Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics) _
            Implements IPostgresGateway.GetDashboardMetricsAsync
        cancellationToken.ThrowIfCancellationRequested()
        Interlocked.Increment(_getDashboardMetricsCalls)
        If CorpusReadError IsNot Nothing Then Return Task.FromException(Of DashboardMetrics)(CorpusReadError)
        Return Task.FromResult(MetricsToReturn)
    End Function

    ' Authoring CRUD — not exercised by orchestrator tests; minimal stubs.

    Public Function ListAuthoringStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuthoringStudySummary)) _
            Implements IPostgresGateway.ListAuthoringStudiesAsync
        Return Task.FromResult(CType(Array.Empty(Of AuthoringStudySummary)(), IReadOnlyList(Of AuthoringStudySummary)))
    End Function

    Public Function GetAuthoringStudyAsync(
            authoringStudyId As Guid,
            cancellationToken As CancellationToken) As Task(Of AuthoringStudyAggregate) _
            Implements IPostgresGateway.GetAuthoringStudyAsync
        Return Task.FromResult(Of AuthoringStudyAggregate)(Nothing)
    End Function

    Public Function StudyIdExistsAsync(
            studyId As String,
            cancellationToken As CancellationToken) As Task(Of Boolean) _
            Implements IPostgresGateway.StudyIdExistsAsync
        Return Task.FromResult(False)
    End Function

    Public Function CreateAuthoringStudyAsync(
            study As AuthoringStudy,
            eligibility As AuthoringEligibility,
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CreateAuthoringStudyAsync
        Return Task.CompletedTask
    End Function

    Public Function UpdateAuthoringStudyAsync(
            study As AuthoringStudy,
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateAuthoringStudyAsync
        Return Task.CompletedTask
    End Function

    Public Function SetAuthoringStudyIdAsync(
            authoringStudyId As Guid,
            studyId As String,
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of Boolean) _
            Implements IPostgresGateway.SetAuthoringStudyIdAsync
        Return Task.FromResult(False)
    End Function

    Public Function SaveAuthoringEligibilityAsync(
            eligibility As AuthoringEligibility,
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.SaveAuthoringEligibilityAsync
        Return Task.CompletedTask
    End Function

    Public Function SaveAuthoringCriteriaAsync(
            authoringStudyId As Guid,
            criteria As IReadOnlyList(Of AuthoringCriterion),
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.SaveAuthoringCriteriaAsync
        Return Task.CompletedTask
    End Function

    Public Function DeleteAuthoringStudyAsync(
            authoringStudyId As Guid,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteAuthoringStudyAsync
        Return Task.FromResult(0)
    End Function

    Public Function FindSimilarStudiesAsync(
            queryVector As IReadOnlyList(Of Single), limit As Integer,
            cancellationToken As CancellationToken,
            Optional filterPhase As String = "",
            Optional filterStudyType As String = "") As Task(Of IReadOnlyList(Of SimilarStudy)) _
            Implements IPostgresGateway.FindSimilarStudiesAsync
        Return Task.FromResult(CType(Array.Empty(Of SimilarStudy)(), IReadOnlyList(Of SimilarStudy)))
    End Function

    Public Function ClusterCommonCriteriaAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of CriterionCluster)) _
            Implements IPostgresGateway.ClusterCommonCriteriaAsync
        Return Task.FromResult(CType(Array.Empty(Of CriterionCluster)(), IReadOnlyList(Of CriterionCluster)))
    End Function

    Public Function GetClusterRecordsAsync(
            nctIds As IReadOnlyList(Of String), criterion As String, groupKey As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of EligibilityRow)) _
            Implements IPostgresGateway.GetClusterRecordsAsync
        Return Task.FromResult(CType(Array.Empty(Of EligibilityRow)(), IReadOnlyList(Of EligibilityRow)))
    End Function

    Public Function GetStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyEmbeddingInput)) _
            Implements IPostgresGateway.GetStudiesToEmbedAsync
        Return Task.FromResult(StudiesToEmbed)
    End Function

    Public Function CountStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountStudiesToEmbedAsync
        Return Task.FromResult(StudiesToEmbed.Count)
    End Function

    ' Inline-embedding hooks. EmbeddingInputs seeds which NCT_IDs have a
    ' study-detail snapshot the orchestrator can embed; UpsertStudyEmbeddingCalls
    ' records every embedding write so tests can assert the inline step ran.
    Public Property EmbeddingInputs As New Dictionary(Of String, StudyEmbeddingInput)(StringComparer.OrdinalIgnoreCase)
    Public ReadOnly Property GetStudyEmbeddingInputCalls As New ConcurrentBag(Of String)
    Public ReadOnly Property UpsertStudyEmbeddingCalls As New ConcurrentBag(Of (NctId As String, Embedding As IReadOnlyList(Of Single), Model As String, SourceText As String))

    Public Function GetStudyEmbeddingInputAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudyEmbeddingInput) _
            Implements IPostgresGateway.GetStudyEmbeddingInputAsync
        cancellationToken.ThrowIfCancellationRequested()
        GetStudyEmbeddingInputCalls.Add(nctId)
        Dim input As StudyEmbeddingInput = Nothing
        EmbeddingInputs.TryGetValue(nctId, input)
        Return Task.FromResult(input)
    End Function

    Public Function UpsertStudyEmbeddingAsync(
            nctId As String, embedding As IReadOnlyList(Of Single), model As String,
            sourceText As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpsertStudyEmbeddingAsync
        cancellationToken.ThrowIfCancellationRequested()
        UpsertStudyEmbeddingCalls.Add((nctId, embedding, model, sourceText))
        Return Task.CompletedTask
    End Function

    Public Property EmbeddingStatsResult As EmbeddingStats =
        New EmbeddingStats(0, Array.Empty(Of EmbeddingModelCount)())
    Public Property EmbeddingsCleared As Long

    Public Function GetEmbeddingStatsAsync(cancellationToken As CancellationToken) As Task(Of EmbeddingStats) _
            Implements IPostgresGateway.GetEmbeddingStatsAsync
        Return Task.FromResult(EmbeddingStatsResult)
    End Function

    Public Function ClearStudyEmbeddingsAsync(cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.ClearStudyEmbeddingsAsync
        Return Task.FromResult(EmbeddingsCleared)
    End Function

    ' Auth / users / audit — not exercised by orchestrator tests; minimal stubs.

    Public Function CountUsersAsync(cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountUsersAsync
        Return Task.FromResult(0)
    End Function

    Public Function CountOwnersAsync(cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountOwnersAsync
        Return Task.FromResult(0)
    End Function

    Public Function GetUserByUserNameAsync(userName As String, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByUserNameAsync
        Return Task.FromResult(Of AppUser)(Nothing)
    End Function

    Public Function GetUserByEmailAsync(email As String, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByEmailAsync
        Return Task.FromResult(Of AppUser)(Nothing)
    End Function

    Public Function GetUserByGoogleSubjectAsync(googleSubject As String, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByGoogleSubjectAsync
        Return Task.FromResult(Of AppUser)(Nothing)
    End Function

    Public Function GetUserAsync(userId As Guid, cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserAsync
        Return Task.FromResult(Of AppUser)(Nothing)
    End Function

    Public Function ListUsersAsync(cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AppUser)) _
            Implements IPostgresGateway.ListUsersAsync
        Return Task.FromResult(CType(Array.Empty(Of AppUser)(), IReadOnlyList(Of AppUser)))
    End Function

    Public Function CreateUserAsync(user As AppUser, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CreateUserAsync
        Return Task.CompletedTask
    End Function

    Public Function UpdateUserRoleAsync(userId As Guid, role As Role, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateUserRoleAsync
        Return Task.CompletedTask
    End Function

    Public Function UpdateUserPasswordHashAsync(userId As Guid, passwordHash As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateUserPasswordHashAsync
        Return Task.CompletedTask
    End Function

    Public Function LinkGoogleSubjectAsync(userId As Guid, googleSubject As String, pictureUrl As String, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.LinkGoogleSubjectAsync
        Return Task.CompletedTask
    End Function

    Public Function RecordLoginAsync(userId As Guid, whenUtc As DateTimeOffset, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordLoginAsync
        Return Task.CompletedTask
    End Function

    Public Function DeleteUserAsync(userId As Guid, cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteUserAsync
        Return Task.FromResult(0)
    End Function

    Public Function InsertAuditAsync(entry As AuditEntry, cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.InsertAuditAsync
        Return Task.CompletedTask
    End Function

    Public Function GetAuditLogAsync(filter As AuditLogFilter, page As Integer, pageSize As Integer, cancellationToken As CancellationToken) As Task(Of AuditLogPage) _
            Implements IPostgresGateway.GetAuditLogAsync
        Return Task.FromResult(New AuditLogPage(Array.Empty(Of AuditEntry)(), 0, page, pageSize))
    End Function

    Public Function GetAuditLogForExportAsync(filter As AuditLogFilter, cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuditEntry)) _
            Implements IPostgresGateway.GetAuditLogForExportAsync
        Return Task.FromResult(CType(Array.Empty(Of AuditEntry)(), IReadOnlyList(Of AuditEntry)))
    End Function

End Class

' ============ Fake ILlmClient ============

Friend NotInheritable Class FakeLlmClient
    Implements ILlmClient

    Public ReadOnly Property Responses As New Dictionary(Of String, LlmResponse)(StringComparer.Ordinal)
    ' Responses returned only when a reasoning-effort override is supplied
    ' (i.e. the escalation retry). Falls back to Responses / DefaultRawText.
    Public ReadOnly Property EscalatedResponses As New Dictionary(Of String, LlmResponse)(StringComparer.Ordinal)
    Public ReadOnly Property Calls As New ConcurrentBag(Of String)
    Public ReadOnly Property EscalationCalls As New ConcurrentBag(Of String)
    Public Property DefaultRawText As String = "[]"

    ' Drives ILlmClient.EscalationReasoningEffort; "" means "no escalation".
    Public Property EscalationEffort As String = ""

    Public ReadOnly Property EscalationReasoningEffort As String _
            Implements ILlmClient.EscalationReasoningEffort
        Get
            Return EscalationEffort
        End Get
    End Property

    Public Function CompleteAsync(
            request As LlmRequest,
            cancellationToken As CancellationToken,
            Optional reasoningEffortOverride As String = Nothing) As Task(Of LlmResponse) _
            Implements ILlmClient.CompleteAsync
        cancellationToken.ThrowIfCancellationRequested()
        Dim response As LlmResponse = Nothing
        If Not String.IsNullOrEmpty(reasoningEffortOverride) Then
            EscalationCalls.Add(request.NctId)
            If EscalatedResponses.TryGetValue(request.NctId, response) Then
                Return Task.FromResult(response)
            End If
        Else
            Calls.Add(request.NctId)
        End If
        If Responses.TryGetValue(request.NctId, response) Then
            Return Task.FromResult(response)
        End If
        Return Task.FromResult(LlmResponse.Success(request.NctId, DefaultRawText))
    End Function

End Class

' ============ Fake IUmlsClient ============

Friend NotInheritable Class FakeUmlsClient
    Implements IUmlsClient

    Public ReadOnly Property SearchResults As New Dictionary(Of String, IReadOnlyList(Of UmlsCandidate))(StringComparer.OrdinalIgnoreCase)
    Public ReadOnly Property SemanticTypesResults As New Dictionary(Of String, IReadOnlyList(Of SemanticTypeAssignment))(StringComparer.Ordinal)
    Public ReadOnly Property SearchCalls As New ConcurrentBag(Of String)
    Public ReadOnly Property SemanticTypesCalls As New ConcurrentBag(Of String)

    Public Function SearchAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate)) _
            Implements IUmlsClient.SearchAsync
        SearchCalls.Add(concept)
        cancellationToken.ThrowIfCancellationRequested()
        Dim result As IReadOnlyList(Of UmlsCandidate) = Nothing
        If SearchResults.TryGetValue(concept, result) Then
            Return Task.FromResult(result)
        End If
        Return Task.FromResult(CType(Array.Empty(Of UmlsCandidate)(), IReadOnlyList(Of UmlsCandidate)))
    End Function

    Public Function GetSemanticTypeAssignmentsAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) _
            Implements IUmlsClient.GetSemanticTypeAssignmentsAsync
        SemanticTypesCalls.Add(cui)
        cancellationToken.ThrowIfCancellationRequested()
        Dim result As IReadOnlyList(Of SemanticTypeAssignment) = Nothing
        If SemanticTypesResults.TryGetValue(cui, result) Then
            Return Task.FromResult(result)
        End If
        Return Task.FromResult(CType(Array.Empty(Of SemanticTypeAssignment)(), IReadOnlyList(Of SemanticTypeAssignment)))
    End Function

End Class

' ============ Fake IPipelineHooks ============

Friend NotInheritable Class FakePipelineHooks
    Implements IPipelineHooks

    ' ConcurrentQueue (not ConcurrentBag) so tests can verify call ORDER.
    ' Trial-level events from parallel trials interleave but the relative order
    ' of BatchStarted (always first) and BatchCompleted (always last) is
    ' guaranteed by the orchestrator and matters for the contract.
    Public ReadOnly Property Events As New ConcurrentQueue(Of String)

    Public Property ThrowFromOnBatchStarted As Boolean = False

    Public Function OnBatchStartedAsync(
            runId As Guid,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnBatchStartedAsync
        Events.Enqueue($"BatchStarted:{studyCount}")
        If ThrowFromOnBatchStarted Then Throw New InvalidOperationException("hook deliberately failed")
        Return Task.CompletedTask
    End Function

    Public Function OnTrialStartedAsync(
            runId As Guid,
            nctId As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnTrialStartedAsync
        Events.Enqueue($"TrialStarted:{nctId}")
        Return Task.CompletedTask
    End Function

    Public Function OnTrialCompletedAsync(
            runId As Guid,
            nctId As String,
            rowCount As Integer,
            succeeded As Boolean,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnTrialCompletedAsync
        Events.Enqueue($"TrialCompleted:{nctId}:{rowCount}:{succeeded}")
        Return Task.CompletedTask
    End Function

    Public Function OnBatchCompletedAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnBatchCompletedAsync
        Events.Enqueue($"BatchCompleted:{result.Metrics.Status}:{result.Metrics.RowsPersisted}")
        Return Task.CompletedTask
    End Function

    Public Function OnBatchCancelledAsync(
            runId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPipelineHooks.OnBatchCancelledAsync
        Events.Enqueue($"BatchCancelled:{runId}")
        Return Task.CompletedTask
    End Function

End Class

' ============ Fake INotificationSink ============

Friend NotInheritable Class FakeNotificationSink
    Implements INotificationSink

    Public ReadOnly Property CompletionCalls As New List(Of BatchResult)
    Public ReadOnly Property ErrorCalls As New List(Of BatchResult)

    Public Function SendCompletionAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements INotificationSink.SendCompletionAsync
        CompletionCalls.Add(result)
        Return Task.CompletedTask
    End Function

    Public Function SendErrorAsync(
            result As BatchResult,
            cancellationToken As CancellationToken) As Task _
            Implements INotificationSink.SendErrorAsync
        ErrorCalls.Add(result)
        Return Task.CompletedTask
    End Function

End Class

' ============ Fake IEmbeddingClient ============

Friend NotInheritable Class FakeEmbeddingClient
    Implements IEmbeddingClient

    Public Property Model As String = "fake-embed-model" Implements IEmbeddingClient.Model

    ' Canned vector returned for a successful embed; set ForceFailure to drive
    ' the EmbeddingResult.Failure path.
    Public Property Vector As IReadOnlyList(Of Single) = {0.1F, 0.2F, 0.3F}
    Public Property ForceFailure As Boolean = False
    Public ReadOnly Property EmbedCalls As New ConcurrentBag(Of String)

    Public Function EmbedAsync(
            text As String,
            cancellationToken As CancellationToken) As Task(Of EmbeddingResult) _
            Implements IEmbeddingClient.EmbedAsync
        cancellationToken.ThrowIfCancellationRequested()
        EmbedCalls.Add(text)
        If ForceFailure Then
            Return Task.FromResult(EmbeddingResult.Failure("embedding endpoint deliberately failed"))
        End If
        Return Task.FromResult(EmbeddingResult.Success(Vector))
    End Function

End Class

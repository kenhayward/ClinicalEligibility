Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' The `normalize-conditions` maintenance job: seed the dictionary from the
''' corpus, then resolve pending rows highest study_count first.
'''
''' Parallel, like embed-studies, at --concurrency (options.Concurrency, default
''' 8). A production backfill of ~90,000 condition strings measured 4h21m
''' sequential; the tail is dominated by multi-word strings that miss tier 1
''' (single indexed lookup) and fall through to tier 2's FTS/trigram search
''' (ConditionNormalizer.ResolveAsync), which is the slow arm. Each string is
''' independent - a store lookup plus, for the harder ones, a UMLS search - so
''' this parallelises safely; see the collaborator thread-safety notes on
''' RunAsync below. Ordering by study_count DESC still means a cancelled run
''' has done the highest-value strings first, but completion order within the
''' batch is no longer the same as dispatch order.
'''
''' The Npgsql pool is capped independently (Postgres:MaxPoolSize, see
''' PostgresOptions) so raising --concurrency queues for a pooled connection
''' rather than exceeding the Postgres server's own max_connections.
''' </summary>
Public NotInheritable Class ConditionNormalizeJob
    Implements IConditionNormalizeJob

    ' Sentinel for "no explicit --count": resolve everything pending.
    Private Const AllPending As Integer = Integer.MaxValue

    Private ReadOnly _store As IConditionConceptStore
    Private ReadOnly _normalizer As ConditionNormalizer

    Public Sub New(store As IConditionConceptStore, normalizer As ConditionNormalizer)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        If normalizer Is Nothing Then Throw New ArgumentNullException(NameOf(normalizer))
        _store = store
        _normalizer = normalizer
    End Sub

    Public Function CountRemainingAsync(force As Boolean, cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionNormalizeJob.CountRemainingAsync
        Return _store.CountPendingAsync(force, cancellationToken)
    End Function

    Public Async Function RunAsync(options As NormalizeConditionsOptions,
                                   progress As IProgress(Of ToolJobSnapshot),
                                   cancellationToken As CancellationToken) _
            As Task(Of ConditionCounters) Implements IConditionNormalizeJob.RunAsync

        ' Seed first so newly processed trials' conditions are present, and so
        ' raw_form / study_count reflect the current corpus before we order by
        ' them. Skipped entirely on --dry-run: dry-run must not write ANYTHING,
        ' and SeedFromCorpusAsync inserts missing dictionary rows and rewrites
        ' raw_form/study_count on existing ones - that is itself a write, even
        ' though it happens before the per-row DryRun guard further down.
        ' Consequence: on a never-seeded public.condition_concept, a dry run
        ' reports nothing to do (GetPendingAsync sees an empty table) rather than
        ' previewing what a real run would seed and resolve.
        If Not options.DryRun Then
            Await _store.SeedFromCorpusAsync(cancellationToken).ConfigureAwait(False)
        End If

        Dim limit = If(options.Count > 0, options.Count, AllPending)
        Dim pending = Await _store.GetPendingAsync(limit, options.Force, cancellationToken).ConfigureAwait(False)

        Dim counters As New ConditionCounters()
        Dim total = pending.Count
        Dim sw = Stopwatch.StartNew()

        Dim build As Func(Of ToolJobSnapshot) =
            Function() New ToolJobSnapshot(
                ToolJobKind.NormalizeConditions, total, counters.Done, sw.Elapsed,
                New ToolMetric() {
                    New ToolMetric("Resolved", counters.Resolved),
                    New ToolMetric("Unresolved", counters.Unresolved)})

        progress?.Report(build())
        If total = 0 Then Return counters

        Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        Dim progressTask As Task = If(progress Is Nothing,
                                      Task.CompletedTask,
                                      ToolJobProgressPump.PumpAsync(progress, build, progressCts.Token))
        Dim caught As Exception = Nothing

        ' Capture rather than propagate, so the progress reporter is always
        ' stopped and a final snapshot always emitted. VB cannot Await in Finally.
        Try
            Dim parallelOptions As New ParallelOptions With {
                    .MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
                    .CancellationToken = cancellationToken}
            ' Graceful cancellation (see StudyEmbeddingJob / UmlsNormalizeJob): stop
            ' launching new entries on cancel but let in-flight ones finish - pass
            ' CancellationToken.None to the per-entry work so a cancel stops after
            ' the current item(s) rather than abandoning a half-done resolve/upsert.
            Await Parallel.ForEachAsync(pending, parallelOptions,
                    Function(entry As ConditionConceptEntry, innerCt As CancellationToken) As ValueTask
                        Return New ValueTask(ResolveOneAsync(entry, options.DryRun, counters, CancellationToken.None))
                    End Function).ConfigureAwait(False)
        Catch ex As Exception
            caught = ex
        End Try

        progressCts.Cancel()
        Try
            Await progressTask.ConfigureAwait(False)
        Catch ex As OperationCanceledException
        End Try
        progress?.Report(build())

        If caught IsNot Nothing Then Throw caught
        Return counters
    End Function

    ' One pending condition string end-to-end (resolve -> upsert), safe to run
    ' concurrently:
    '   - ConditionNormalizer is stateless (readonly refs to the store, the UMLS
    '     client, and the stateless UmlsMatchScorer) - no shared mutable state.
    '   - IConditionConceptStore's production implementation
    '     (ConditionConceptStore) opens its own Npgsql connection per call from
    '     the pooled data source, so concurrent calls just draw distinct pooled
    '     connections (bounded by Postgres:MaxPoolSize).
    '   - IUmlsClient's decorator (UmlsCache) wraps a ConcurrentDictionary; the
    '     Postgres-backed IUmlsClient (PostgresUmlsClient) likewise opens its own
    '     connection per call.
    ' Counters are updated via Interlocked - the fields are public precisely so
    ' Interlocked can take them ByRef (see ConditionCounters).
    Private Async Function ResolveOneAsync(entry As ConditionConceptEntry,
                                           dryRun As Boolean,
                                           counters As ConditionCounters,
                                           cancellationToken As CancellationToken) As Task

        Dim resolution = Await _normalizer.ResolveAsync(entry.RawForm, cancellationToken).ConfigureAwait(False)

        If Not dryRun Then
            Await _store.UpsertAsync(New ConditionConceptEntry With {
                    .ConditionNorm = entry.ConditionNorm,
                    .RawForm = entry.RawForm,
                    .StudyCount = entry.StudyCount,
                    .ConceptCode = resolution.ConceptCode,
                    .UmlsName = resolution.UmlsName,
                    .MatchTier = resolution.Tier,
                    .MatchScore = resolution.Score
                }, cancellationToken).ConfigureAwait(False)
        End If

        If resolution.IsResolved Then
            Interlocked.Increment(counters.Resolved)
        Else
            Interlocked.Increment(counters.Unresolved)
        End If
        Interlocked.Increment(counters.Done)
    End Function

End Class

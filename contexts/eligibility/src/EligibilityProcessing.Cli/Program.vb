Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Runtime.ExceptionServices
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports EligibilityProcessing.Hosting
Imports EligibilityProcessing.Umls
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Options

' Entry point for the eligibility CLI.
'
' Commands (architecture section 2.7):
'   migrate                              - apply every embedded migration
'                                          (V1 + V2 + V3 ...) to the output DB.
'                                          Idempotent (CREATE / ALTER ... IF NOT
'                                          EXISTS) so safe to re-run.
'   run [--count N] [--recent]           - run one batch of N studies (default 10).
'                                          Default direction is Forward (earliest
'                                          unprocessed first); --recent picks the
'                                          most-recent unprocessed first.
'   status                               - print dashboard counters
'                                          (runs / studies / rows / resolution rate).
'   backfill-details                     - snapshot AACT study metadata +
'                                          eligibility detail into
'                                          eligibility_study_detail for every
'                                          trial already in eligibility_study.
'                                          New runs snapshot themselves; this
'                                          covers trials processed earlier.
'   embed-studies [--concurrency N]      - backfill topic embeddings for the
'                                          Authoring similarity search. Embeds
'                                          processed studies that have a
'                                          snapshot but no embedding yet.
'                                          Idempotent - only fills gaps. Requests
'                                          run in parallel at --concurrency
'                                          (default Pipeline:LlmConcurrencyCap).
'   load-umls --rrf-dir <path>           - load a curated UMLS subset into the
'                                          umls.* schema from an unpacked release
'                                          (MRCONSO.RRF + MRSTY.RRF). Full rebuild
'                                          per release (TRUNCATE first); the SAB
'                                          filter is Umls:SourceVocabularies. Backs
'                                          the Umls:Backend=postgres resolver — run
'                                          on a build box, then pg_dump -Fc -n umls
'                                          and restore to the target.
'   umls-compare [--count N]             - validation harness. Resolve a sample of
'                                          concepts (N unresolved, default 100, +
'                                          50 resolved) through BOTH the REST and
'                                          Postgres backends with the shared scorer
'                                          and report the resolution-rate delta +
'                                          disagreements. Run before flipping
'                                          Umls:Backend.
'   retry-umls [--count N] [--recent]    - re-resolve UMLS gaps (rows whose
'              [--dry-run] [--force]       concept_code is empty) against the
'                                          configured backend WITHOUT re-calling
'                                          the LLM, UPDATING only the UMLS columns
'                                          in place. Batched by trial (default 50);
'                                          a per-trial eligibility_umls_retry row
'                                          anti-joins processed trials so runs
'                                          advance. --dry-run reports counts without
'                                          writing; --force re-attempts already-
'                                          retried trials (e.g. after a corpus
'                                          refresh). Run `migrate` first.
'   normalize-umls [--count N]           - recover UMLS gaps the lexical store can't
'                  [--concurrency N]       match (abbreviations, paraphrase): send
'                  [--dry-run] [--force]   each DISTINCT unresolved concept to the
'                                          LLM normalize endpoint for a canonical
'                                          term, re-resolve THAT term locally, and
'                                          cache the concept->CUI mapping in
'                                          umls.concept_normalization (also UPDATEs
'                                          matching rows in place). The extraction
'                                          pipeline reads that cache inline. Batched
'                                          by distinct concept (default 50), most-
'                                          frequent first; anti-joins processed
'                                          concepts. Requests run in parallel at
'                                          --concurrency (default Pipeline:Llm
'                                          ConcurrencyCap) to fill the model server's
'                                          slots. --dry-run reports without writing;
'                                          --force re-normalizes cached concepts.
'                                          Run `migrate` first.
'   llm-probe <NCT_ID> "<criteria text>" - diagnostic. Send a criteria string
'                                          through the production prompt + LLM +
'                                          parser and print the raw model output
'                                          and the parsed records. Useful for
'                                          investigating trials that land in
'                                          parse_invalid_json / parse_empty.
'   help | --help | -h | (no args)       - print usage.
'
' Exit codes (architecture section 2.7):
'   0 - success
'   1 - configuration error / refused destructive op without --confirm
'   2 - runtime failure
'   3 - cancelled (Ctrl+C)
'
' Ctrl+C is captured and turned into a cooperative CancellationToken so the
' orchestrator can re-throw OperationCanceledException and exit cleanly.
'
' VB.NET notes:
'   - System.Console is fully qualified throughout because the imported
'     namespace Microsoft.Extensions.Logging exposes a child namespace named
'     Console that would otherwise shadow it.
'   - The local IHost variable is named "appHost" rather than "host" to avoid
'     case-insensitive collision with the Microsoft.Extensions.Hosting.Host
'     type used by Host.CreateApplicationBuilder.

Module Program

    Public Function Main(args As String()) As Integer
        ' Load .env into the process environment BEFORE the host builder runs,
        ' so the env-var configuration provider sees the values when it overlays
        ' appsettings.json. No-op in production (no .env in the container).
        DotEnvLoader.LoadDotEnv()

        ' VB's project compiler does not auto-detect an Async Function Main on
        ' a Module — only Sub Main / Function Main are recognised as the entry
        ' point. We bridge to the async pipeline via GetAwaiter().GetResult().
        ' For a CLI with no SynchronizationContext this is safe (no deadlock).
        Return MainAsync(args).GetAwaiter().GetResult()
    End Function

    Private Async Function MainAsync(args As String()) As Task(Of Integer)
        Dim builder = Host.CreateApplicationBuilder(args)

        ' Load cross-host shared config (Llm / Umls / Pipeline / Notifications)
        ' at lowest precedence, so per-host appsettings.json + env vars still
        ' override. The file is linked into bin from src/Shared/ via the
        ' .vbproj — see SharedAppSettings.vb for the loading semantics.
        builder.Configuration.AddSharedAppSettings()

        builder.Services.AddEligibilityPipeline(builder.Configuration)

        Using appHost = builder.Build()
            Dim cts As New CancellationTokenSource()
            AddHandler System.Console.CancelKeyPress,
                Sub(sender, e)
                    e.Cancel = True
                    cts.Cancel()
                End Sub

            Return Await DispatchAsync(appHost, args, cts.Token).ConfigureAwait(False)
        End Using
    End Function

    Private Async Function DispatchAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim command = If(args.Length > 0, args(0).ToLowerInvariant(), "")

        Try
            Select Case command
                Case "migrate"
                    Return Await RunMigrateAsync(appHost, cancellationToken).ConfigureAwait(False)
                Case "run"
                    Dim recent = args.Any(Function(a) String.Equals(a, "--recent", StringComparison.OrdinalIgnoreCase))
                    Dim dir = If(recent, TrialSelectionDirection.Recent, TrialSelectionDirection.Forward)
                    Return Await RunBatchAsync(appHost, ParseStudyCount(args), dir, cancellationToken).ConfigureAwait(False)
                Case "status"
                    Return Await RunStatusAsync(appHost, cancellationToken).ConfigureAwait(False)
                Case "backfill-details"
                    Return Await RunBackfillDetailsAsync(appHost, cancellationToken).ConfigureAwait(False)
                Case "embed-studies"
                    Return Await RunEmbedStudiesAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                Case "load-umls"
                    Return Await RunLoadUmlsAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                Case "umls-compare"
                    Return Await RunUmlsCompareAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                Case "retry-umls"
                    Return Await RunRetryUmlsAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                Case "normalize-umls"
                    Return Await RunNormalizeUmlsAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                Case "llm-probe"
                    Return Await RunLlmProbeAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                'Case "reset"
                '    Return Await RunResetAsync(appHost, args, cancellationToken).ConfigureAwait(False)
                Case "", "help", "--help", "-h"
                    PrintUsage()
                    Return 0
                Case Else
                    System.Console.Error.WriteLine($"Unknown command: {command}")
                    PrintUsage()
                    Return 1
            End Select
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            System.Console.Error.WriteLine("Cancelled.")
            Return 3
        Catch ex As Exception
            System.Console.Error.WriteLine($"Error: {ex.Message}")
            Return 2
        End Try
    End Function

    Private Async Function RunMigrateAsync(
            appHost As IHost,
            cancellationToken As CancellationToken) As Task(Of Integer)
        Dim gateway = appHost.Services.GetRequiredService(Of PostgresGateway)()
        Dim migrations = PostgresGateway.MigrationNames
        Dim latest = If(migrations.Count > 0, migrations(migrations.Count - 1), "(none)")
        System.Console.WriteLine($"Applying {migrations.Count} schema migrations (latest: {latest}) to the output database...")
        Await gateway.EnsureSchemaAsync(cancellationToken).ConfigureAwait(False)
        ' Re-create the source-DB trial-selection index when source + output are
        ' co-located (idempotent; a no-op otherwise). Keeps fast selection working
        ' after an AACT reload drops the index.
        System.Console.WriteLine("Ensuring source trial-selection performance index (if co-located)...")
        Await gateway.EnsureSourcePerformanceIndexesAsync(cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine("Done.")
        Return 0
    End Function

    Private Async Function RunBatchAsync(
            appHost As IHost,
            studyCount As Integer,
            direction As TrialSelectionDirection,
            cancellationToken As CancellationToken) As Task(Of Integer)
        Using scope = appHost.Services.CreateScope()
            Dim orch = scope.ServiceProvider.GetRequiredService(Of PipelineOrchestrator)()
            Dim triggerSource As String = If(direction = TrialSelectionDirection.Recent, "cli-recent", "cli")
            Dim config = New RunConfiguration(studyCount, triggerSource, rerunNctIds:=Nothing, direction:=direction)

            ' Take the cross-process batch lock before doing anything. The web host's
            ' RunGate is an in-process lock this process cannot see, so without this a
            ' `elig run` here and a dashboard-triggered batch there would select
            ' overlapping trials and compete for the same model-server slots. This is
            ' the whole reason the lock exists - the web side alone was already
            ' serialised.
            Dim runLock = scope.ServiceProvider.GetRequiredService(Of PostgresRunLock)()
            If Not Await runLock.TryAcquireAsync(cancellationToken).ConfigureAwait(False) Then
                System.Console.Error.WriteLine(
                        "Another batch is already running (the dashboard, or another CLI run). Nothing was started.")
                Return 2
            End If

            ' VB.NET cannot Await inside Finally, so capture the outcome (including a
            ' Ctrl+C cancellation, which DispatchAsync maps to exit code 3) and release
            ' the lock below, outside the Try.
            Dim exitCode As Integer = 2
            Dim failure As ExceptionDispatchInfo = Nothing
            Try
                exitCode = Await RunBatchInnerAsync(
                        orch, config, studyCount, direction, cancellationToken).ConfigureAwait(False)
            Catch ex As Exception
                failure = ExceptionDispatchInfo.Capture(ex)
            End Try

            ' Released even on failure/Ctrl+C. A killed process ends the session and
            ' Postgres drops the lock with it, so this can never wedge.
            Await runLock.ReleaseAsync().ConfigureAwait(False)

            If failure IsNot Nothing Then failure.Throw()
            Return exitCode
        End Using
    End Function

    ' Split out so RunBatchAsync's lock acquire/release reads as one Try/Finally rather
    ' than wrapping the whole body.
    Private Async Function RunBatchInnerAsync(
            orch As PipelineOrchestrator,
            config As RunConfiguration,
            studyCount As Integer,
            direction As TrialSelectionDirection,
            cancellationToken As CancellationToken) As Task(Of Integer)
        System.Console.WriteLine($"Starting batch run (StudyCount={studyCount}, Direction={direction})...")
        Dim result = Await orch.ExecuteAsync(config, cancellationToken).ConfigureAwait(False)

        Dim m = result.Metrics
        System.Console.WriteLine($"Run {m.RunId} {m.Status}")
        System.Console.WriteLine($"  Studies processed: {m.StudiesProcessed}")
        System.Console.WriteLine($"  Rows persisted:    {m.RowsPersisted}")
        System.Console.WriteLine($"  Resolution rate:   {m.ResolutionRate:P1}")
        Dim duration As TimeSpan = If(m.EndedAt.HasValue, m.EndedAt.Value - m.StartedAt, TimeSpan.Zero)
        System.Console.WriteLine($"  Wall clock:        {duration.TotalSeconds:F1}s")
        If result.FailedNctIds.Count > 0 Then
            System.Console.WriteLine($"  Failed trials:     {result.FailedNctIds.Count}")
            For Each id In result.FailedNctIds
                System.Console.WriteLine($"    - {id}")
            Next
        End If
        Return If(m.Status = "success", 0, 2)
    End Function

    ' Destructive admin command: TRUNCATE every output table so the next batch
    ' starts from scratch (watermark resets to NCT00000000). Requires --confirm
    ' to actually fire; without it we just print what would happen and exit
    ' non-zero. Source DB (ctgov.*) is read-only and never touched.
    Private Async Function RunResetAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim confirmed = args.Any(Function(a) String.Equals(a, "--confirm", StringComparison.OrdinalIgnoreCase))
        If Not confirmed Then
            System.Console.Error.WriteLine("Reset refuses to proceed without --confirm.")
            System.Console.Error.WriteLine()
            System.Console.Error.WriteLine("This will TRUNCATE every output table:")
            System.Console.Error.WriteLine("  public.eligibility")
            System.Console.Error.WriteLine("  public.eligibility_run")
            System.Console.Error.WriteLine("  public.eligibility_failed")
            System.Console.Error.WriteLine("  public.eligibility_study")
            System.Console.Error.WriteLine("  public.eligibility_study_detail")
            System.Console.Error.WriteLine()
            System.Console.Error.WriteLine("All pipeline output (rows, runs, audit, raw responses)")
            System.Console.Error.WriteLine("will be deleted. The source DB (ctgov.*) is read-only and is not touched.")
            System.Console.Error.WriteLine()
            System.Console.Error.WriteLine("Re-run as:  EligibilityProcessing.Cli reset --confirm")
            Return 1
        End If

        Dim gateway = appHost.Services.GetRequiredService(Of PostgresGateway)()
        System.Console.WriteLine("Truncating output tables...")
        Await gateway.ResetOutputAsync(cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine("Done. Next run will start from watermark NCT00000000.")
        Return 0
    End Function

    ' Diagnostic command: send a one-off criteria string through the same prompt +
    ' LLM client + parser the pipeline uses, and print the raw model output plus
    ' the parser's records. Useful for investigating "this trial produced zero
    ' rows" — see whether the LLM returned [] or whether the parser dropped
    ' records.
    Private Async Function RunLlmProbeAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        If args.Length < 3 Then
            System.Console.Error.WriteLine("Usage: llm-probe <NCT_ID> ""<criteria text>""")
            System.Console.Error.WriteLine()
            System.Console.Error.WriteLine("Sends the criteria text through the configured LLM (per .env / appsettings.json)")
            System.Console.Error.WriteLine("using the production prompt, then prints the raw response and the parser output.")
            Return 1
        End If

        Dim nctId = args(1)
        Dim criteria = args(2)

        Dim llmClient = appHost.Services.GetRequiredService(Of ILlmClient)()
        Dim parser = appHost.Services.GetRequiredService(Of LlmResponseParser)()

        System.Console.WriteLine($"=== Probing LLM for {nctId} ===")
        System.Console.WriteLine($"Criteria ({criteria.Length} chars):")
        System.Console.WriteLine(criteria)
        System.Console.WriteLine()

        Dim req = New LlmRequest(nctId, criteria)
        Dim sw = Stopwatch.StartNew()
        Dim resp = Await llmClient.CompleteAsync(req, cancellationToken).ConfigureAwait(False)
        sw.Stop()

        System.Console.WriteLine($"=== LLM call ({sw.ElapsedMilliseconds} ms) ===")
        System.Console.WriteLine($"Succeeded:         {resp.Succeeded}")
        System.Console.WriteLine($"Finish reason:     {resp.FinishReason}")
        System.Console.WriteLine($"Prompt tokens:     {resp.PromptTokens}")
        System.Console.WriteLine($"Completion tokens: {resp.CompletionTokens}")
        If Not resp.Succeeded Then
            System.Console.Error.WriteLine($"Error: {resp.ErrorMessage}")
            Return 2
        End If

        System.Console.WriteLine()
        System.Console.WriteLine("=== Raw model output ===")
        System.Console.WriteLine(resp.RawText)
        System.Console.WriteLine()
        System.Console.WriteLine("=== Parser output ===")
        Dim records = parser.Parse(resp.RawText, nctId)
        System.Console.WriteLine($"Records emitted: {records.Count}")
        For i = 0 To records.Count - 1
            Dim r = records(i)
            System.Console.WriteLine($"  [{i + 1}] {r.Criterion} / {r.Domain} / Concept='{r.Concept}'")
            System.Console.WriteLine($"      Qualifier='{r.Qualifier}', TimeWindow='{r.TimeWindow}'")
            System.Console.WriteLine($"      OriginalText='{r.OriginalText}'")
        Next
        Return 0
    End Function

    Private Async Function RunStatusAsync(
            appHost As IHost,
            cancellationToken As CancellationToken) As Task(Of Integer)
        Dim gateway = appHost.Services.GetRequiredService(Of IPostgresGateway)()
        Dim m = Await gateway.GetDashboardMetricsAsync(cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine($"Eligibility rows persisted: {m.EligibilityRowCount:N0}")
        System.Console.WriteLine($"Studies successful:         {m.StudiesSuccessful:N0}    (latest attempt is success)")
        System.Console.WriteLine($"Studies failed:             {m.StudiesFailedLatest:N0}    (latest attempt is llm_failed / parse_invalid_json / persist_failed / failed)")
        System.Console.WriteLine($"Studies parse_empty:        {m.ParseEmpty:N0}    (latest attempt returned valid JSON with zero criteria — not a failure)")
        System.Console.WriteLine($"UMLS resolution rate:       {m.ResolutionRate:P1}    (non-null concept_code / total rows)")
        System.Console.WriteLine($"Tokens used:                {m.TokensUsed:N0}    (sum of prompt + completion tokens across all attempts)")
        Return 0
    End Function

    ' Backfill command: snapshot AACT study metadata + eligibility detail into
    ' public.eligibility_study_detail for every trial already in
    ' eligibility_study. New runs snapshot themselves during processing; this
    ' populates the detail table for trials processed before it existed.
    ' Per-trial failures are counted and reported but do not abort the loop.
    Private Async Function RunBackfillDetailsAsync(
            appHost As IHost,
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim gateway = appHost.Services.GetRequiredService(Of IPostgresGateway)()
        Dim nctIds = Await gateway.GetAttemptedNctIdsAsync(cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine($"Backfilling study detail for {nctIds.Count} trial(s)...")

        Dim processed As Integer = 0
        Dim failed As Integer = 0
        For Each nctId In nctIds
            Dim captureEx As Exception = Nothing
            Try
                Await gateway.CaptureStudySnapshotAsync(nctId, cancellationToken).ConfigureAwait(False)
                processed += 1
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                captureEx = ex
            End Try
            If captureEx IsNot Nothing Then
                failed += 1
                System.Console.Error.WriteLine($"  {nctId}: {captureEx.Message}")
            End If
            If (processed + failed) Mod 25 = 0 Then
                System.Console.WriteLine($"  ...{processed + failed}/{nctIds.Count}")
            End If
        Next

        System.Console.WriteLine($"Done. Captured {processed}, failed {failed}.")
        Return If(failed > 0, 2, 0)
    End Function

    ' embed-studies - backfill topic embeddings for the Authoring similarity
    ' search. Embeds every processed study that has a snapshot but no embedding
    ' under the configured model; safe to re-run (only the gaps are filled).
    ' Requests run in parallel at --concurrency (default Pipeline:LlmConcurrencyCap)
    ' since embeddings are independent - useful for a full re-embed after a model change.
    Private Async Function RunEmbedStudiesAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim config = appHost.Services.GetRequiredService(Of IConfiguration)()
        Dim model = If(config.GetValue(Of String)("Embedding:Model"), "")
        ' Default request concurrency to the pipeline's LLM cap (override with
        ' --concurrency). The embedding endpoint is usually a separate server, so
        ' tune --concurrency to its slot count for a big re-embed.
        Dim cap = appHost.Services.GetRequiredService(Of IOptions(Of OrchestratorOptions))().Value.LlmConcurrencyCap
        Dim concurrency = Math.Max(1, ParseOptionInt(args, "--concurrency", Math.Max(1, cap)))

        ' The embed loop lives in EligibilityProcessing.Core (StudyEmbeddingJob) so the
        ' CLI and the web Tools tab run identical code. The job is scoped, so resolve it
        ' inside a scope (like the orchestrator). We keep the CLI's own single-line
        ' progress renderer, fed from the job's snapshots.
        Using scope = appHost.Services.CreateScope()
            Dim job = scope.ServiceProvider.GetRequiredService(Of IStudyEmbeddingJob)()
            Dim total = Await job.CountRemainingAsync(model, cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine(
                    $"Embedding {total} study/studies (model: {If(model = "", "(default)", model)}; concurrency {concurrency})...")
            If total = 0 Then Return 0

            Dim latest As ToolJobSnapshot = Nothing
            Dim sink As New SnapshotSink(Sub(s) latest = s)
            Dim sw = Stopwatch.StartNew()
            Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            Dim progressTask = ReportProgressAsync(
                    total,
                    Function() If(latest Is Nothing, 0, latest.Processed),
                    Function() $"studies - {MetricValue(latest, "Embedded")} embedded, {MetricValue(latest, "Failed")} failed",
                    sw, progressCts.Token)
            Dim caught As Exception = Nothing
            Dim counters As EmbedCounters = Nothing

            Try
                counters = Await job.RunAsync(
                        New EmbedStudiesOptions With {.Concurrency = concurrency, .Model = model},
                        sink, cancellationToken).ConfigureAwait(False)
            Catch ex As Exception
                caught = ex
            End Try

            ' Stop + drain the progress reporter, then end the in-place line.
            progressCts.Cancel()
            Try
                Await progressTask.ConfigureAwait(False)
            Catch ex As OperationCanceledException
            End Try
            If Not System.Console.IsOutputRedirected Then System.Console.WriteLine()

            ' Re-throw a captured cancellation so DispatchAsync maps it to exit code 3.
            If caught IsNot Nothing Then Throw caught

            System.Console.WriteLine($"Done. Embedded {counters.Processed}, failed {counters.Failed} in {FormatHms(sw.Elapsed)}.")
            Return If(counters.Failed > 0, 2, 0)
        End Using
    End Function

    ' Loads a curated UMLS subset into the umls.* schema from an unpacked release.
    ' Full rebuild per release (TRUNCATE first); idempotent. The SAB filter comes
    ' from Umls:SourceVocabularies. Run on a build box, then pg_dump -Fc -n umls
    ' and restore to the target (see deploy/eligibility-pipeline/umls-loader.md).
    Private Async Function RunLoadUmlsAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim semanticTypesOnly = IsSemanticTypesOnly(args)

        Dim rrfDir = GetOptionValue(args, "--rrf-dir")
        If String.IsNullOrWhiteSpace(rrfDir) Then
            System.Console.Error.WriteLine("load-umls requires --rrf-dir <path> (unpacked UMLS release with MRCONSO.RRF + MRSTY.RRF).")
            Return 1
        End If
        Dim mrconso = Path.Combine(rrfDir, "MRCONSO.RRF")
        Dim mrsty = Path.Combine(rrfDir, "MRSTY.RRF")
        ' --semantic-types-only never reads MRCONSO, so do not demand it: the
        ' repair must work from an MRSTY-only directory.
        If Not semanticTypesOnly AndAlso Not File.Exists(mrconso) Then
            System.Console.Error.WriteLine($"Not found: {mrconso}") : Return 1
        End If
        If Not File.Exists(mrsty) Then System.Console.Error.WriteLine($"Not found: {mrsty}") : Return 1

        Dim store = appHost.Services.GetRequiredService(Of UmlsMetathesaurusStore)()
        Dim opts = appHost.Services.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
        Dim sabs = If(opts.SourceVocabularies, Array.Empty(Of String)())

        Dim atoms As Long = 0
        Dim concepts As Long = 0

        System.Console.WriteLine($"Loading UMLS from {rrfDir}")
        If semanticTypesOnly Then
            ' Repair path: leave umls.atom and umls.concept untouched. Safe to run
            ' against a live system - LoadSemanticTypesAsync stages into a temp
            ' table and inserts ON CONFLICT DO NOTHING, touching only
            ' umls.semantic_type.
            System.Console.WriteLine("  --semantic-types-only: skipping truncate, atoms and concepts")
            atoms = Await store.CountAsync("umls.atom", cancellationToken).ConfigureAwait(False)
            concepts = Await store.CountAsync("umls.concept", cancellationToken).ConfigureAwait(False)
        Else
            System.Console.WriteLine($"  vocabularies: {If(sabs.Length = 0, "(all English)", String.Join(", ", sabs))}")
            System.Console.WriteLine("  truncating umls.* ...")
            Await store.TruncateAsync(cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine("  COPY atoms from MRCONSO.RRF ...")
            atoms = Await store.BulkLoadAtomsAsync(UmlsRrfReader.ReadAtoms(mrconso, sabs), cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine($"    {atoms:N0} atoms")
            System.Console.WriteLine("  building umls.concept (preferred names) ...")
            concepts = Await store.RebuildConceptTableAsync(cancellationToken).ConfigureAwait(False)
            System.Console.WriteLine($"    {concepts:N0} concepts")
        End If

        System.Console.WriteLine("  loading semantic types from MRSTY.RRF ...")
        Dim stys = Await store.LoadSemanticTypesAsync(UmlsRrfReader.ReadSemanticTypes(mrsty), cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine($"    {stys:N0} semantic-type rows")

        ' The load reporting a row count is not evidence it worked. In May 2026 a
        ' load left umls.semantic_type with 100 rows covering 49 CUIs against
        ' 1,265,171 concepts, returned success, and nothing noticed for two
        ' months - by which point 3.48M eligibility rows had been written with no
        ' semantic type.
        '
        ' Exit 4, not 2. DispatchAsync already uses 1 = usage, 2 = unhandled
        ' exception, 3 = cancelled, so "ran to completion but produced a bad
        ' result" needs its own code - otherwise a wrapper script cannot tell an
        ' incomplete load from a crash.
        Dim completeness = Await store.GetLoadCompletenessAsync(cancellationToken).ConfigureAwait(False)
        If Not completeness.IsComplete Then
            System.Console.Error.WriteLine("LOAD INCOMPLETE - " & completeness.Describe())
            System.Console.Error.WriteLine(
                "Every UMLS concept has at least one semantic type, so coverage should be total. " &
                "Re-run with --semantic-types-only against a complete MRSTY.RRF.")
            Return 4
        End If

        System.Console.WriteLine($"Done. atoms={atoms:N0} concepts={concepts:N0} semantic_types={stys:N0}.")
        System.Console.WriteLine("  " & completeness.Describe())
        Return 0
    End Function

    ' Validation harness: resolve a sample of concepts from public.eligibility
    ' through BOTH the REST and Postgres backends (same scorer) and report the
    ' resolution-rate delta + disagreements. Run after load-umls, before flipping
    ' Umls:Backend. REST calls need Umls:ApiKey; missing/failed REST → counts as
    ' unresolved (so Postgres-only wins still surface).
    Private Async Function RunUmlsCompareAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim store = appHost.Services.GetRequiredService(Of UmlsMetathesaurusStore)()
        Dim restClient = appHost.Services.GetRequiredService(Of UmlsClient)()
        Dim scorer = appHost.Services.GetRequiredService(Of UmlsMatchScorer)()
        Dim opts = appHost.Services.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
        Dim pgClient = New PostgresUmlsClient(store, opts.CandidateLimit, opts.TrigramThreshold,
                                              opts.MinQueryCoverage, opts.RequireQueryCodeMatch, opts.MaxAtomLength,
                                              opts.EnableTrigramFallback, scorer)

        Dim unresolvedN = ParseOptionInt(args, "--count", 100)
        Dim resolvedN = 50
        Dim concepts = Await store.GetSampleConceptsAsync(unresolvedN, resolvedN, cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine($"Comparing REST vs Postgres UMLS resolution on {concepts.Count} sample concepts...")

        Dim bothN = 0, neitherN = 0, restOnlyN = 0, pgOnlyN = 0, diffN = 0, i = 0
        Dim wins As New List(Of String)()
        Dim regressions As New List(Of String)()
        Dim diffs As New List(Of String)()

        For Each concept In concepts
            cancellationToken.ThrowIfCancellationRequested()
            i += 1
            Dim restMatch = scorer.PickBestMatch(concept, Await restClient.SearchAsync(concept, cancellationToken).ConfigureAwait(False))
            Dim pgMatch = scorer.PickBestMatch(concept, Await pgClient.SearchAsync(concept, cancellationToken).ConfigureAwait(False))
            If restMatch.IsResolved AndAlso pgMatch.IsResolved Then
                bothN += 1
                If Not String.Equals(restMatch.ConceptCode, pgMatch.ConceptCode, StringComparison.Ordinal) Then
                    diffN += 1
                    If diffs.Count < 15 Then diffs.Add($"  '{concept}': REST {restMatch.ConceptCode} ({restMatch.UmlsName}) vs PG {pgMatch.ConceptCode} ({pgMatch.UmlsName})")
                End If
            ElseIf restMatch.IsResolved Then
                restOnlyN += 1
                If regressions.Count < 15 Then regressions.Add($"  '{concept}': REST {restMatch.ConceptCode} ({restMatch.UmlsName}); PG unresolved")
            ElseIf pgMatch.IsResolved Then
                pgOnlyN += 1
                If wins.Count < 15 Then wins.Add($"  '{concept}': PG {pgMatch.ConceptCode} ({pgMatch.UmlsName}); REST unresolved")
            Else
                neitherN += 1
            End If
            If i Mod 25 = 0 Then System.Console.WriteLine($"  ...{i}/{concepts.Count}")
        Next

        Dim total = concepts.Count
        System.Console.WriteLine()
        System.Console.WriteLine($"Sample: {total} concepts")
        System.Console.WriteLine($"  REST resolved:        {bothN + restOnlyN} ({Pct(bothN + restOnlyN, total)})")
        System.Console.WriteLine($"  Postgres resolved:    {bothN + pgOnlyN} ({Pct(bothN + pgOnlyN, total)})")
        System.Console.WriteLine($"  both resolved:        {bothN}   (different CUI: {diffN})")
        System.Console.WriteLine($"  Postgres-only wins:   {pgOnlyN}")
        System.Console.WriteLine($"  REST-only (PG missed):{restOnlyN}")
        System.Console.WriteLine($"  neither:              {neitherN}")
        PrintSample("Postgres-only wins (REST unresolved):", wins)
        PrintSample("REST-only (possible PG regressions):", regressions)
        PrintSample("Different CUI (precision check):", diffs)
        Return 0
    End Function

    ' UMLS-only retry: re-resolve the UMLS columns of existing public.eligibility
    ' rows whose concept_code is empty, against the configured backend (postgres),
    ' WITHOUT re-calling the LLM. Re-runs each stored Concept through the same
    ' IUmlsClient + UmlsMatchScorer the pipeline uses, then UPDATEs only the five
    ' UMLS columns in place (per-trial transaction). Batched by trial; a per-trial
    ' bookkeeping row (eligibility_umls_retry) anti-joins processed trials so
    ' consecutive runs advance. Run `migrate` first (creates the V19 table).
    Private Async Function RunRetryUmlsAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim count = ParseOptionInt(args, "--count", 50)
        Dim dryRun = args.Any(Function(a) String.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))
        Dim force = args.Any(Function(a) String.Equals(a, "--force", StringComparison.OrdinalIgnoreCase))
        Dim recent = args.Any(Function(a) String.Equals(a, "--recent", StringComparison.OrdinalIgnoreCase))
        Dim direction = If(recent, TrialSelectionDirection.Recent, TrialSelectionDirection.Forward)

        Dim gateway = appHost.Services.GetRequiredService(Of IPostgresGateway)()
        Dim opts = appHost.Services.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
        If Not String.Equals(If(opts.Backend, ""), "postgres", StringComparison.OrdinalIgnoreCase) Then
            System.Console.WriteLine($"Note: Umls:Backend is '{If(opts.Backend, "rest")}', not 'postgres' — retry uses the configured backend.")
        End If

        Dim trials = Await gateway.SelectTrialsToRetryUmlsAsync(direction, count, force, cancellationToken).ConfigureAwait(False)
        System.Console.WriteLine(
                $"retry-umls: {trials.Count} trial(s) with UMLS gaps" &
                If(dryRun, " (dry-run — no writes)", "") &
                If(force, " (force — includes already-retried trials)", "") & ".")
        If trials.Count = 0 Then Return 0

        Dim totalAttempted As Integer = 0, totalResolved As Integer = 0
        Dim trialsDone As Integer = 0, errors As Integer = 0

        ' IUmlsClient is scoped (the UmlsCache decorator) — resolve inside a scope.
        Using scope = appHost.Services.CreateScope()
            Dim umlsClient = scope.ServiceProvider.GetRequiredService(Of IUmlsClient)()
            Dim scorer = scope.ServiceProvider.GetRequiredService(Of UmlsMatchScorer)()

            For Each nctId In trials
                cancellationToken.ThrowIfCancellationRequested()
                Try
                    Dim rows = Await gateway.GetUnresolvedRowsForTrialAsync(nctId, cancellationToken).ConfigureAwait(False)
                    Dim updates As New List(Of UmlsRetryResult)(rows.Count)
                    For Each row In rows
                        Dim candidates = Await umlsClient.SearchAsync(row.Concept, cancellationToken).ConfigureAwait(False)
                        Dim match = scorer.PickBestMatch(row.Concept, candidates)
                        If match.IsResolved Then
                            Dim semanticTypes = Await umlsClient.GetSemanticTypesAsync(
                                    match.ConceptCode, cancellationToken).ConfigureAwait(False)
                            ' Same reduction as ResolvedRecord: comma-join the list.
                            Dim semantic = If(semanticTypes Is Nothing OrElse semanticTypes.Count = 0,
                                              "", String.Join(", ", semanticTypes))
                            updates.Add(New UmlsRetryResult(
                                    row.Id, match.ConceptCode, match.UmlsName, match.MatchSource, match.MatchScore, semantic))
                        End If
                    Next
                    totalAttempted += rows.Count
                    totalResolved += updates.Count
                    If Not dryRun Then
                        Await gateway.ApplyUmlsRetryAsync(nctId, updates, rows.Count, cancellationToken).ConfigureAwait(False)
                    End If
                    trialsDone += 1
                Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                    Throw
                Catch ex As Exception
                    errors += 1
                    System.Console.Error.WriteLine($"  {nctId}: {ex.Message}")
                End Try
                If trialsDone Mod 25 = 0 AndAlso trialsDone > 0 Then
                    System.Console.WriteLine($"  ...{trialsDone}/{trials.Count} trials; {totalResolved}/{totalAttempted} rows resolved")
                End If
            Next
        End Using

        Dim pct = If(totalAttempted > 0, 100.0 * totalResolved / totalAttempted, 0.0)
        System.Console.WriteLine(
                $"Done. {trialsDone} trial(s); {totalResolved}/{totalAttempted} unresolved rows now resolved ({pct:F1}%)." &
                If(dryRun, " (dry-run — nothing written)", ""))
        Return If(errors > 0, 2, 0)
    End Function

    ' LLM concept-normalization: for each DISTINCT unresolved concept, ask the LLM
    ' normalize endpoint for a canonical clinical term, re-resolve THAT term through
    ' the configured UMLS backend + scorer (0.45 floor), and cache the outcome in
    ' umls.concept_normalization (the gateway also UPDATEs every matching eligibility
    ' row in place on a hit). No extraction LLM call. Batched by distinct concept; a
    ' per-concept cache row anti-joins processed concepts so consecutive runs advance.
    ' Run `migrate` first (creates the V20 table).
    Private Async Function RunNormalizeUmlsAsync(
            appHost As IHost,
            args As String(),
            cancellationToken As CancellationToken) As Task(Of Integer)

        Dim count = ParseOptionInt(args, "--count", 50)
        Dim dryRun = args.Any(Function(a) String.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))
        Dim force = args.Any(Function(a) String.Equals(a, "--force", StringComparison.OrdinalIgnoreCase))

        Dim opts = appHost.Services.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
        ' Default the request concurrency to the pipeline's LLM cap so normalize-umls
        ' loads the model server's slots the way extraction does - one in-flight LLM
        ' call leaves most GPU slots idle. --concurrency overrides it.
        Dim cap = appHost.Services.GetRequiredService(Of IOptions(Of OrchestratorOptions))().Value.LlmConcurrencyCap
        Dim concurrency = Math.Max(1, ParseOptionInt(args, "--concurrency", Math.Max(1, cap)))
        If Not String.Equals(If(opts.Backend, ""), "postgres", StringComparison.OrdinalIgnoreCase) Then
            System.Console.WriteLine($"Note: Umls:Backend is '{If(opts.Backend, "rest")}', not 'postgres' - re-lookup uses the configured backend.")
        End If

        ' The normalize loop lives in EligibilityProcessing.Core (UmlsNormalizeJob) so
        ' the CLI and the web Tools tab run identical code. Scoped (the per-run UMLS
        ' cache), so resolve it inside a scope. The CLI keeps its own single-line
        ' progress renderer, fed from the job's snapshots.
        Using scope = appHost.Services.CreateScope()
            Dim job = scope.ServiceProvider.GetRequiredService(Of IUmlsNormalizeJob)()
            Dim remaining = Await job.CountRemainingAsync(force, cancellationToken).ConfigureAwait(False)
            Dim total = Math.Min(remaining, Math.Max(1, count))
            System.Console.WriteLine(
                    $"normalize-umls: {total} distinct unresolved concept(s) to process; concurrency {concurrency}" &
                    If(dryRun, " (dry-run - no writes)", "") &
                    If(force, " (force - includes already-normalized)", "") & ".")
            If total = 0 Then Return 0

            Dim latest As ToolJobSnapshot = Nothing
            Dim sink As New SnapshotSink(Sub(s) latest = s)
            Dim sw = Stopwatch.StartNew()
            Dim progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            Dim progressTask = ReportProgressAsync(
                    total,
                    Function() If(latest Is Nothing, 0, latest.Processed),
                    Function() $"concepts - {MetricValue(latest, "Resolved")} resolved, {MetricValue(latest, "Not a concept")} none, {MetricValue(latest, "Rows updated")} rows",
                    sw, progressCts.Token)
            Dim caught As Exception = Nothing
            Dim counters As NormalizeCounters = Nothing

            Try
                counters = Await job.RunAsync(
                        New NormalizeUmlsOptions With {.Count = count, .Concurrency = concurrency, .DryRun = dryRun, .Force = force},
                        sink, cancellationToken).ConfigureAwait(False)
            Catch ex As Exception
                caught = ex
            End Try

            ' Stop + drain the progress reporter, then end the in-place line.
            progressCts.Cancel()
            Try
                Await progressTask.ConfigureAwait(False)
            Catch ex As OperationCanceledException
            End Try
            If Not System.Console.IsOutputRedirected Then System.Console.WriteLine()

            ' Re-throw a captured cancellation so DispatchAsync maps it to exit code 3.
            If caught IsNot Nothing Then Throw caught

            Dim done = counters.Done
            Dim pct = If(done > 0, 100.0 * counters.Resolved / done, 0.0)
            System.Console.WriteLine(
                    $"Done. {done} concept(s); {counters.Resolved} resolved ({pct:F1}%), {counters.NoneCount} not-a-concept, {counters.RowsUpdated} eligibility row(s) updated" &
                    If(counters.Errors > 0, $", {counters.Errors} error(s)", "") &
                    $" in {FormatHms(sw.Elapsed)}" &
                    If(dryRun, " (dry-run - nothing written)", "") & ".")
            Return If(counters.Errors > 0, 2, 0)
        End Using
    End Function

    ' Generic background progress reporter for long CLI batches. Periodically reads
    ' the current count (getProcessed) + a caller-built detail string (getDetail) and
    ' renders one line with elapsed wall-clock + a rate-based ETA + projected finish.
    ' Rewrites a single line in place on a TTY (every ~0.75s); emits a periodic line
    ' when output is redirected/piped (every ~5s) so captured logs aren't flooded with
    ' carriage returns. Stops when the caller cancels the token after the batch ends.
    ' The getters are read from a background thread while the workers update them;
    ' int reads are atomic and this is display-only, so no locking is needed.
    Private Async Function ReportProgressAsync(
            total As Integer,
            getProcessed As Func(Of Integer),
            getDetail As Func(Of String),
            sw As Stopwatch,
            cancellationToken As CancellationToken) As Task
        Dim redirected = System.Console.IsOutputRedirected
        Try
            Do
                Await Task.Delay(If(redirected, 5000, 750), cancellationToken).ConfigureAwait(False)
                WriteProgressLine(getProcessed(), total, getDetail(), sw, redirected)
            Loop
        Catch ex As OperationCanceledException
            ' Stopped by the caller once the batch completes.
        End Try
    End Function

    Private Sub WriteProgressLine(processed As Integer, total As Integer, detail As String, sw As Stopwatch, redirected As Boolean)
        Dim elapsed = sw.Elapsed
        ' Rate-based ETA: remaining = (elapsed / processed) * (total - processed).
        Dim eta As String
        If processed > 0 AndAlso processed < total Then
            Dim remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (total - processed) / processed)
            eta = $"ETA {FormatHms(remaining)} (finish ~{DateTime.Now.Add(remaining):HH:mm})"
        Else
            eta = "ETA --:--"
        End If
        Dim line = $"{processed}/{total} {detail} | {FormatHms(elapsed)} elapsed, {eta}"
        If redirected Then
            System.Console.WriteLine($"  {line}")
        Else
            ' Carriage return + trailing pad rewrites the line in place and clears any
            ' leftover characters from a longer previous render.
            System.Console.Write($"{vbCr}  {line}            ")
        End If
    End Sub

    ' Formats a TimeSpan as H:MM:SS (handles spans over 24h, unlike "hh\:mm\:ss").
    Private Function FormatHms(ts As TimeSpan) As String
        If ts < TimeSpan.Zero Then ts = TimeSpan.Zero
        Return $"{CInt(Math.Floor(ts.TotalHours))}:{ts.Minutes:D2}:{ts.Seconds:D2}"
    End Function

    ' Reads a named metric value out of the latest tool-job snapshot (0 when the
    ' snapshot or that metric is absent), for rendering the CLI progress line. The
    ' tool jobs (normalize-umls / embed-studies) now live in Core; the CLI renders
    ' their snapshots through its existing single-line progress reporter.
    Private Function MetricValue(snapshot As ToolJobSnapshot, label As String) As Long
        If snapshot Is Nothing OrElse snapshot.Metrics Is Nothing Then Return 0
        For Each m In snapshot.Metrics
            If String.Equals(m.Label, label, StringComparison.Ordinal) Then Return m.Value
        Next
        Return 0
    End Function

    Private Sub PrintSample(header As String, lines As List(Of String))
        If lines.Count = 0 Then Return
        System.Console.WriteLine()
        System.Console.WriteLine(header)
        For Each l In lines
            System.Console.WriteLine(l)
        Next
    End Sub

    ' Returns the value following --name (or after --name=) in args, or "".
    Private Function GetOptionValue(args As String(), name As String) As String
        For i = 1 To args.Length - 1
            If String.Equals(args(i), name, StringComparison.OrdinalIgnoreCase) AndAlso i + 1 < args.Length Then
                Return args(i + 1)
            ElseIf args(i).StartsWith(name & "=", StringComparison.OrdinalIgnoreCase) Then
                Return args(i).Substring(name.Length + 1)
            End If
        Next
        Return ""
    End Function

    Private Function ParseOptionInt(args As String(), name As String, fallback As Integer) As Integer
        Dim n As Integer
        If Integer.TryParse(GetOptionValue(args, name), n) AndAlso n > 0 Then Return n
        Return fallback
    End Function

    Private Function Pct(n As Integer, total As Integer) As String
        If total <= 0 Then Return "0.0%"
        Return (100.0 * n / total).ToString("F1") & "%"
    End Function

    ''' <summary>
    ''' True when --semantic-types-only is present. Exact match (not a prefix),
    ''' so an unrelated flag beginning with the same text does not trigger it.
    ''' Friend rather than Private so CliCompositionTests can exercise it without
    ''' a database (the Cli vbproj declares InternalsVisibleTo for that project).
    ''' </summary>
    Friend Function IsSemanticTypesOnly(args As String()) As Boolean
        Return args.Any(Function(a) String.Equals(a, "--semantic-types-only", StringComparison.OrdinalIgnoreCase))
    End Function

    Friend Function ParseStudyCount(args As String()) As Integer
        ' Look for --count N or --count=N. Default 10 per spec section 2.1.
        For i = 1 To args.Length - 1
            Dim a = args(i)
            If a = "--count" AndAlso i + 1 < args.Length Then
                Dim n As Integer
                If Integer.TryParse(args(i + 1), n) AndAlso n > 0 Then Return n
            ElseIf a.StartsWith("--count=") Then
                Dim n As Integer
                If Integer.TryParse(a.Substring("--count=".Length), n) AndAlso n > 0 Then Return n
            End If
        Next
        Return 10
    End Function

    Private Sub PrintUsage()
        System.Console.WriteLine("Eligibility processing CLI")
        System.Console.WriteLine()
        System.Console.WriteLine("Usage:")
        System.Console.WriteLine("  EligibilityProcessing.Cli migrate")
        System.Console.WriteLine("      Apply all embedded schema migrations to the output database.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli run [--count N] [--recent]")
        System.Console.WriteLine("      Run one batch of N studies (default 10). Default direction is")
        System.Console.WriteLine("      Forward (earliest unprocessed first); --recent walks the catalogue")
        System.Console.WriteLine("      DESC by nct_id and picks the most-recent unprocessed first.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli status")
        System.Console.WriteLine("      Print dashboard counters: runs recorded, studies successful / failed,")
        System.Console.WriteLine("      eligibility rows persisted, UMLS resolution rate.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli backfill-details")
        System.Console.WriteLine("      Snapshot AACT study metadata + eligibility detail into")
        System.Console.WriteLine("      eligibility_study_detail for every trial already processed.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli embed-studies [--concurrency N]")
        System.Console.WriteLine("      Backfill topic embeddings for the Authoring similarity search.")
        System.Console.WriteLine("      Embeds every processed study with a snapshot but no embedding")
        System.Console.WriteLine("      under the configured model. Safe to re-run; requests run in parallel")
        System.Console.WriteLine("      at --concurrency (default Pipeline:LlmConcurrencyCap).")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli load-umls --rrf-dir <path> [--semantic-types-only]")
        System.Console.WriteLine("      Load a curated UMLS subset into the umls.* schema from an unpacked")
        System.Console.WriteLine("      release (MRCONSO.RRF + MRSTY.RRF). Full rebuild per release. Backs the")
        System.Console.WriteLine("      Umls:Backend=postgres resolver. Run on a build box, then pg_dump/restore.")
        System.Console.WriteLine("      --semantic-types-only reloads umls.semantic_type alone from MRSTY.RRF,")
        System.Console.WriteLine("      leaving atoms and concepts untouched. Use to repair a partial load")
        System.Console.WriteLine("      without rebuilding healthy tables; safe against a running system.")
        System.Console.WriteLine("      Exits 4 if semantic types do not cover every loaded concept.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli umls-compare [--count N]")
        System.Console.WriteLine("      Resolve a sample of concepts through both the REST and Postgres")
        System.Console.WriteLine("      backends and report the resolution-rate delta + disagreements.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli retry-umls [--count N] [--recent] [--dry-run] [--force]")
        System.Console.WriteLine("      Re-resolve UMLS gaps (eligibility rows with empty concept_code) against")
        System.Console.WriteLine("      the configured backend without re-running the LLM, updating only the UMLS")
        System.Console.WriteLine("      columns in place. Batched by trial (default 50); processed trials are")
        System.Console.WriteLine("      tracked in eligibility_umls_retry so runs advance. --dry-run reports counts")
        System.Console.WriteLine("      without writing; --force re-attempts already-retried trials.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli normalize-umls [--count N] [--concurrency N] [--dry-run] [--force]")
        System.Console.WriteLine("      Recover gaps the lexical store can't match (abbreviations, paraphrase):")
        System.Console.WriteLine("      LLM-normalize each distinct unresolved concept to a canonical term, re-")
        System.Console.WriteLine("      resolve it locally, and cache the concept->CUI mapping in")
        System.Console.WriteLine("      umls.concept_normalization (also updates matching rows). Batched by distinct")
        System.Console.WriteLine("      concept (default 50); requests run in parallel at --concurrency (default")
        System.Console.WriteLine("      Pipeline:LlmConcurrencyCap). --dry-run reports without writing; --force re-normalizes.")
        System.Console.WriteLine()
        System.Console.WriteLine("  EligibilityProcessing.Cli llm-probe <NCT_ID> ""<criteria text>""")
        System.Console.WriteLine("      Send a criteria string through the production prompt + LLM + parser")
        System.Console.WriteLine("      and print the raw model output and the parsed records. Diagnostic.")
        System.Console.WriteLine()
        System.Console.WriteLine("Configuration is loaded from appsettings.json + environment variables.")
        System.Console.WriteLine("Required keys: Postgres:ConnectionStringSource, Postgres:ConnectionStringOutput,")
        System.Console.WriteLine("Umls:ApiKey, Llm:ApiKey, Llm:BaseUrl.")
    End Sub

End Module

' Minimal IProgress sink that just stores the most recent tool-job snapshot, so the
' CLI's existing single-line progress reporter (ReportProgressAsync) can render it.
' Ordering does not matter - only the latest snapshot is ever read. (The
' NormalizeCounters / EmbedCounters tallies now live in EligibilityProcessing.Core
' alongside the shared job logic.)
Friend NotInheritable Class SnapshotSink
    Implements IProgress(Of ToolJobSnapshot)

    Private ReadOnly _onReport As Action(Of ToolJobSnapshot)

    Public Sub New(onReport As Action(Of ToolJobSnapshot))
        _onReport = onReport
    End Sub

    Public Sub Report(value As ToolJobSnapshot) Implements IProgress(Of ToolJobSnapshot).Report
        _onReport(value)
    End Sub
End Class

Imports System.Net.Http
Imports System.Runtime.CompilerServices
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports EligibilityProcessing.Llm
Imports EligibilityProcessing.Notifications
Imports EligibilityProcessing.Umls
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.DependencyInjection.Extensions
Imports Microsoft.Extensions.Http
Imports Microsoft.Extensions.Http.Resilience
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports Npgsql
Imports Polly

' Single composition root for every host (CLI, Webhook, Web). Architecture
' section 3.1. Wires configuration -> options -> services -> orchestrator.
'
' Lifetimes:
'   Singleton   - NpgsqlDataSource (manages connection pool), PostgresGateway,
'                 the pure-logic helpers (parser, scorer), notification sink,
'                 IPipelineHooks (NullPipelineHooks by default — Web replaces
'                 it with the SignalR-broadcasting sink).
'   Transient   - LlmClient and UmlsClient via IHttpClientFactory.
'   Scoped      - UmlsCache (one cache per run, dies with the scope) and the
'                 orchestrator itself. Hosts MUST create a scope per run.
'
' Resilience: each typed HttpClient has an AddResilienceHandler with a retry
' strategy whose attempts/delay come from options (LlmOptions.RetryCount/
' RetryDelaySeconds and the matching UmlsOptions fields). The retry strategy
' wraps each attempt and counts both transient HTTP responses (5xx, 408, 429)
' and HttpRequestException / TimeoutRejectedException as retryable — that is
' the default ShouldHandle predicate on HttpRetryStrategyOptions and it
' matches spec section 2.4.4 verbatim.
'
' The LLM client enforces TimeoutSeconds as a *per-attempt* timeout strategy
' inside the resilience pipeline (retry wraps timeout), so every retry gets the
' full budget. HttpClient.Timeout is left infinite — if it bounded the whole
' SendAsync it would also bound all retries together, and one slow attempt
' would cancel the pipeline before any retry ran. The UMLS client keeps the
' simpler HttpClient.Timeout (fast remote API, single retry).
'
' Handler order (outer -> inner):
'   HttpClient -> UmlsLogRedactionHandler -> ResilienceHandler -> Transport
' This means redaction logs the request once per /caller call, NOT once per
' retry — which matches typical telemetry expectations.

Public Module CompositionRoot

    Private Const SourceDataSourceKey As String = "source"
    Private Const OutputDataSourceKey As String = "output"

    <Extension>
    Public Function AddEligibilityPipeline(
            services As IServiceCollection,
            configuration As IConfiguration) As IServiceCollection

        ' --- logging noise filters ---
        ' These categories log at Information by default and are pure noise in
        ' normal operation: per-attempt Polly resilience telemetry
        ' ("Execution attempt ... Result: '200'"), per-request HttpClient, and
        ' Npgsql command logging. Filter them to Warning in CODE (not only in
        ' appsettings.json) so the suppression holds no matter which directory a
        ' host is launched from - Host.CreateApplicationBuilder resolves
        ' appsettings.json relative to the current directory, so a CLI run from
        ' the repo root never loads the file-based filters. Warning+ still lets a
        ' real retry / failure / handled fault through.
        services.AddLogging(
            Sub(logging)
                logging.AddFilter("Polly", LogLevel.Warning)
                logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning)
                logging.AddFilter("Npgsql", LogLevel.Warning)
            End Sub)

        ' --- options binding ---
        services.Configure(Of PostgresOptions)(configuration.GetSection("Postgres"))
        services.Configure(Of LlmOptions)(configuration.GetSection("Llm"))
        services.Configure(Of LlmNormalizeOptions)(configuration.GetSection("LlmNormalize"))
        services.Configure(Of EmbeddingOptions)(configuration.GetSection("Embedding"))
        services.Configure(Of UmlsOptions)(configuration.GetSection("Umls"))
        services.Configure(Of OrchestratorOptions)(configuration.GetSection("Pipeline"))

        ' --- Postgres data sources (two databases, keyed) ---
        services.AddKeyedSingleton(Of NpgsqlDataSource)(SourceDataSourceKey,
                Function(sp As IServiceProvider, key As Object) As NpgsqlDataSource
                    Dim opts = sp.GetRequiredService(Of IOptions(Of PostgresOptions)).Value
                    ' Apply a larger command timeout to the SOURCE connection: the
                    ' trial-selection scan + exclusion-set COPY can run longer than
                    ' Npgsql's 30s default, and the default surfaces as a fatal
                    ' "Exception while reading from stream". 0 => no timeout.
                    Dim sourceConn As New NpgsqlConnectionStringBuilder(opts.ConnectionStringSource) With {
                            .CommandTimeout = Math.Max(0, opts.SourceCommandTimeoutSeconds)}
                    Return NpgsqlDataSource.Create(sourceConn.ConnectionString)
                End Function)
        services.AddKeyedSingleton(Of NpgsqlDataSource)(OutputDataSourceKey,
                Function(sp As IServiceProvider, key As Object) As NpgsqlDataSource
                    Dim opts = sp.GetRequiredService(Of IOptions(Of PostgresOptions)).Value
                    Return NpgsqlDataSource.Create(opts.ConnectionStringOutput)
                End Function)

        services.AddSingleton(Of IPostgresGateway)(
                Function(sp As IServiceProvider) As IPostgresGateway
                    Dim outputDs = sp.GetRequiredKeyedService(Of NpgsqlDataSource)(OutputDataSourceKey)
                    Dim sourceDs = sp.GetRequiredKeyedService(Of NpgsqlDataSource)(SourceDataSourceKey)
                    Dim logger = sp.GetRequiredService(Of ILogger(Of PostgresGateway))()
                    Dim opts = sp.GetRequiredService(Of IOptions(Of PostgresOptions)).Value
                    Return New PostgresGateway(outputDs, sourceDs, logger, opts.MaxStudyCount)
                End Function)

        ' Concrete gateway also resolvable (CLI migrate command uses it directly).
        services.AddSingleton(Of PostgresGateway)(
                Function(sp As IServiceProvider) As PostgresGateway
                    Return CType(sp.GetRequiredService(Of IPostgresGateway)(), PostgresGateway)
                End Function)

        ' --- LLM typed HttpClient with retry pipeline ---
        services.AddHttpClient(Of ILlmClient, LlmClient)("llamacpp",
                Sub(client As HttpClient)
                    ' Timeout is owned by the per-attempt timeout strategy below,
                    ' not by HttpClient.Timeout (which would bound all retries
                    ' together). See the resilience note above.
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan
                End Sub).AddResilienceHandler("llm",
                Sub(builder As ResiliencePipelineBuilder(Of HttpResponseMessage), context As ResilienceHandlerContext)
                    Dim opts = context.ServiceProvider.GetRequiredService(Of IOptions(Of LlmOptions))().Value
                    ' Retry added first => outermost; timeout added next => innermost,
                    ' so the timeout applies per attempt and a TimeoutRejectedException
                    ' from a slow attempt is retried.
                    builder.AddRetry(New HttpRetryStrategyOptions With {
                            .MaxRetryAttempts = opts.RetryCount,
                            .Delay = TimeSpan.FromSeconds(opts.RetryDelaySeconds),
                            .BackoffType = DelayBackoffType.Constant,
                            .UseJitter = False})
                    builder.AddTimeout(TimeSpan.FromSeconds(opts.TimeoutSeconds))
                End Sub)

        ' --- Criterion normalizer typed HttpClient (Authoring §3.5) ---
        ' Endpoint / model / API key, retry, and per-attempt timeout each come
        ' from LlmNormalizeOptions when set, otherwise fall back to LlmOptions.
        ' Lets a deployment point the normalize call at a smaller, faster model
        ' (often with shorter timeouts and fewer retries) without changing the
        ' extraction LLM config.
        services.AddHttpClient(Of ICriteriaNormalizer, CriteriaNormalizer)("normalizer",
                Sub(client As HttpClient)
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan
                End Sub).AddResilienceHandler("normalizer",
                Sub(builder As ResiliencePipelineBuilder(Of HttpResponseMessage), context As ResilienceHandlerContext)
                    Dim opts = context.ServiceProvider.GetRequiredService(Of IOptions(Of LlmOptions))().Value
                    Dim normOpts = context.ServiceProvider.GetRequiredService(Of IOptions(Of LlmNormalizeOptions))().Value
                    Dim retryCount = If(normOpts.RetryCount, opts.RetryCount)
                    Dim retryDelay = If(normOpts.RetryDelaySeconds, opts.RetryDelaySeconds)
                    Dim timeoutSeconds = If(normOpts.TimeoutSeconds, opts.TimeoutSeconds)
                    ' Polly's HttpRetryStrategyOptions validates MaxRetryAttempts >= 1.
                    ' A fast small model often wants "no retries" — skip the retry
                    ' strategy entirely when RetryCount is 0 (the natural intent),
                    ' otherwise validation throws at first request.
                    If retryCount > 0 Then
                        builder.AddRetry(New HttpRetryStrategyOptions With {
                                .MaxRetryAttempts = retryCount,
                                .Delay = TimeSpan.FromSeconds(retryDelay),
                                .BackoffType = DelayBackoffType.Constant,
                                .UseJitter = False})
                    End If
                    builder.AddTimeout(TimeSpan.FromSeconds(timeoutSeconds))
                End Sub)

        ' --- Embeddings typed HttpClient (Authoring similarity search) ---
        services.AddHttpClient(Of IEmbeddingClient, EmbeddingClient)("embedding",
                Sub(sp As IServiceProvider, client As HttpClient)
                    Dim opts = sp.GetRequiredService(Of IOptions(Of EmbeddingOptions)).Value
                    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds)
                End Sub).AddResilienceHandler("embedding",
                Sub(builder As ResiliencePipelineBuilder(Of HttpResponseMessage), context As ResilienceHandlerContext)
                    Dim opts = context.ServiceProvider.GetRequiredService(Of IOptions(Of EmbeddingOptions))().Value
                    builder.AddRetry(New HttpRetryStrategyOptions With {
                            .MaxRetryAttempts = opts.RetryCount,
                            .Delay = TimeSpan.FromSeconds(opts.RetryDelaySeconds),
                            .BackoffType = DelayBackoffType.Constant,
                            .UseJitter = False})
                End Sub)

        ' --- UMLS typed HttpClient (raw client) + cache decorator (public IUmlsClient) ---
        services.AddTransient(Of UmlsLogRedactionHandler)()
        services.AddHttpClient(Of UmlsClient)("umls",
                Sub(sp As IServiceProvider, client As HttpClient)
                    Dim opts = sp.GetRequiredService(Of IOptions(Of UmlsOptions)).Value
                    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds)
                End Sub).AddHttpMessageHandler(Of UmlsLogRedactionHandler)() _
                  .AddResilienceHandler("umls",
                Sub(builder As ResiliencePipelineBuilder(Of HttpResponseMessage), context As ResilienceHandlerContext)
                    Dim opts = context.ServiceProvider.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
                    builder.AddRetry(New HttpRetryStrategyOptions With {
                            .MaxRetryAttempts = opts.RetryCount,
                            .Delay = TimeSpan.FromSeconds(opts.RetryDelaySeconds),
                            .BackoffType = DelayBackoffType.Constant,
                            .UseJitter = False})
                End Sub)

        ' Local UMLS Metathesaurus store (umls.* schema, V17) — backs the
        ' "postgres" resolution backend and the CLI load-umls command. Uses the
        ' output data source. Stateless/thread-safe → singleton.
        services.AddSingleton(Of UmlsMetathesaurusStore)(
                Function(sp As IServiceProvider) As UmlsMetathesaurusStore
                    Dim outputDs = sp.GetRequiredKeyedService(Of NpgsqlDataSource)(OutputDataSourceKey)
                    Return New UmlsMetathesaurusStore(outputDs)
                End Function)

        ' Public IUmlsClient = UmlsCache wrapping whichever raw client the
        ' Umls:Backend selects. "postgres" → local PostgresUmlsClient (no network);
        ' anything else → the REST UmlsClient. Config-only, reversible switch.
        services.AddScoped(Of IUmlsClient)(
                Function(sp As IServiceProvider) As IUmlsClient
                    Dim opts = sp.GetRequiredService(Of IOptions(Of UmlsOptions))().Value
                    Dim cacheLogger = sp.GetRequiredService(Of ILogger(Of UmlsCache))()
                    Dim inner As IUmlsClient
                    If String.Equals(If(opts.Backend, ""), "postgres", StringComparison.OrdinalIgnoreCase) Then
                        inner = New PostgresUmlsClient(
                                sp.GetRequiredService(Of UmlsMetathesaurusStore)(),
                                opts.CandidateLimit,
                                opts.TrigramThreshold,
                                opts.MinQueryCoverage,
                                opts.RequireQueryCodeMatch,
                                opts.MaxAtomLength,
                                opts.EnableTrigramFallback,
                                sp.GetRequiredService(Of UmlsMatchScorer)(),
                                sp.GetRequiredService(Of ILogger(Of PostgresUmlsClient))())
                    Else
                        inner = sp.GetRequiredService(Of UmlsClient)()
                    End If
                    Return New UmlsCache(inner, cacheLogger)
                End Function)

        ' --- pure-logic helpers (stateless, safe as singletons) ---
        services.AddSingleton(Of LlmResponseParser)()
        services.AddSingleton(Of UmlsMatchScorer)()

        ' --- notifications ---
        ' Default to NullNotificationSink. If Notifications:Smtp:Host is set in
        ' configuration we layer SmtpNotificationSink ON TOP via a second
        ' AddSingleton — the last registration wins for single-resolution, so
        ' the orchestrator sees the SMTP sink. Settings come from
        ' Notifications:Smtp:* and use the standard IOptions(Of T) pattern.
        services.AddSingleton(Of INotificationSink)(NullNotificationSink.Instance)
        ' Access-request alerts (unrecognised login attempts) reuse the same SMTP
        ' transport; default to a no-op when mail is unconfigured.
        services.AddSingleton(Of IAccessRequestNotifier)(NullAccessRequestNotifier.Instance)
        Dim smtpHost = configuration.GetSection("Notifications:Smtp:Host").Value
        If Not String.IsNullOrWhiteSpace(smtpHost) Then
            services.Configure(Of SmtpNotificationOptions)(configuration.GetSection("Notifications:Smtp"))
            services.AddSingleton(Of ISmtpEmailSender, MailKitSmtpEmailSender)()
            services.AddSingleton(Of INotificationSink, SmtpNotificationSink)()
            services.AddSingleton(Of IAccessRequestNotifier, SmtpAccessRequestNotifier)()
        End If

        ' --- pipeline observability hooks (no-op default; Web overrides with a
        '     SignalR-broadcasting implementation after calling this extension). ---
        services.AddSingleton(Of IPipelineHooks, NullPipelineHooks)()

        ' --- orchestrator ---
        services.AddScoped(Of PipelineOrchestrator)(
                Function(sp As IServiceProvider) As PipelineOrchestrator
                    Return New PipelineOrchestrator(
                            gateway:=sp.GetRequiredService(Of IPostgresGateway)(),
                            llmClient:=sp.GetRequiredService(Of ILlmClient)(),
                            umlsClient:=sp.GetRequiredService(Of IUmlsClient)(),
                            parser:=sp.GetRequiredService(Of LlmResponseParser)(),
                            scorer:=sp.GetRequiredService(Of UmlsMatchScorer)(),
                            notificationSink:=sp.GetRequiredService(Of INotificationSink)(),
                            hooks:=sp.GetRequiredService(Of IPipelineHooks)(),
                            embeddingClient:=sp.GetRequiredService(Of IEmbeddingClient)(),
                            options:=sp.GetRequiredService(Of IOptions(Of OrchestratorOptions)).Value,
                            logger:=sp.GetRequiredService(Of ILogger(Of PipelineOrchestrator))())
                End Function)

        ' --- maintenance tool jobs (CLI commands + web Tools tab share these) ---
        ' Scoped, like the orchestrator: they depend on the scoped ICriteriaNormalizer
        ' / IUmlsClient (per-run UMLS cache), so a scope MUST be created per run.
        services.AddScoped(Of IUmlsNormalizeJob)(
                Function(sp As IServiceProvider) As IUmlsNormalizeJob
                    Return New UmlsNormalizeJob(
                            gateway:=sp.GetRequiredService(Of IPostgresGateway)(),
                            normalizer:=sp.GetRequiredService(Of ICriteriaNormalizer)(),
                            umlsClient:=sp.GetRequiredService(Of IUmlsClient)(),
                            scorer:=sp.GetRequiredService(Of UmlsMatchScorer)())
                End Function)
        services.AddScoped(Of IStudyEmbeddingJob)(
                Function(sp As IServiceProvider) As IStudyEmbeddingJob
                    Return New StudyEmbeddingJob(
                            gateway:=sp.GetRequiredService(Of IPostgresGateway)(),
                            embeddingClient:=sp.GetRequiredService(Of IEmbeddingClient)())
                End Function)

        ' Disable the framework's per-request HttpClient logging (the
        ' "Start/Send/Received/End processing HTTP request" lines that AddHttpClient
        ' wires at Information level on every LLM/UMLS call). It floods CLI output
        ' (esp. parallel normalize-umls) and the app already logs its own meaningful
        ' events + failures. Removing the builder filter is the canonical way to turn
        ' it off in code, so it doesn't depend on a deployed appsettings log-level.
        ' MUST run after all AddHttpClient calls (a later one would re-add the filter).
        services.RemoveAll(Of IHttpMessageHandlerBuilderFilter)()

        Return services
    End Function

End Module

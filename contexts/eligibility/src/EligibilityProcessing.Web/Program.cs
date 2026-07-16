using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EligibilityProcessing.Core;
using EligibilityProcessing.Data;      // PostgresGateway (startup perf-index ensure)
using EligibilityProcessing.Hosting;   // AddEligibilityPipeline (VB extension)
using EligibilityProcessing.Web;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Seeding;
using EligibilityProcessing.Web.Embeddings;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

// Load .env into the process environment before WebApplication.CreateBuilder
// runs, so the env-var configuration provider can overlay JSON values.
DotEnvLoader.LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

// Load cross-host shared config (Llm / Umls / Pipeline / Notifications) at
// lowest precedence, so per-host appsettings.json + env vars still override.
// The file is linked into bin from src/Shared/ via the .csproj — see
// SharedAppSettings.vb for the loading semantics.
builder.Configuration.AddSharedAppSettings();

// Pipeline (gateway, LLM, UMLS, orchestrator, etc.).
builder.Services.AddEligibilityPipeline(builder.Configuration);

// Cached corpus reads for the dashboard and the Results filter dropdowns.
// Both are whole-corpus aggregates that only change when a run persists new
// trials, yet were recomputed on every page view - measured at ~700 ms
// (dashboard) and ~1150 ms (filter options) against the production corpus.
//
// Singleton: IPostgresGateway and IMemoryCache are both singletons, and the
// cache holds no per-request state. The TTL is read inside the factory rather
// than here so it resolves LAZILY, after WebApplicationFactory's configuration
// overrides have been applied - same reasoning as RateLimiterOptions below.
// Set Web:CorpusCacheTtlSeconds to 0 to disable caching and always read live.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICorpusReadCache>(sp =>
{
    var ttlSeconds = sp.GetRequiredService<IConfiguration>()
        .GetValue<int?>("Web:CorpusCacheTtlSeconds") ?? CorpusReadCache.DefaultTtlSeconds;
    return new CorpusReadCache(
        sp.GetRequiredService<IPostgresGateway>(),
        sp.GetRequiredService<IMemoryCache>(),
        TimeSpan.FromSeconds(ttlSeconds));
});

// SignalR live progress: register the hub infrastructure and override the
// no-op IPipelineHooks default with the broadcasting impl. Co-locating the
// orchestrator (drained from the trigger channel below) with the hub is what
// makes the dashboard's live feed work — previously the orchestrator ran in a
// separate "Webhook" host that had no SignalR plumbing.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPipelineHooks, SignalRPipelineHooks>();

// Trigger surface — POST /trigger plumbing folded in from the old Webhook host.
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhook"));
builder.Services.AddSingleton<RunGate>();
builder.Services.AddSingleton(_ => Channel.CreateBounded<RunRequest>(new BoundedChannelOptions(1)
{
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleReader = true,
    SingleWriter = false
}));
builder.Services.AddHostedService<BatchRunner>();

// Maintenance tool jobs (Tools tab): normalize-umls / embed-studies run as
// background jobs through their own channel + runner, but share the RunGate above
// so they are mutually exclusive with the main pipeline and each other. ToolJobState
// holds the live metrics so a reloaded/reconnected Tools tab renders the running job.
builder.Services.AddSingleton<ToolJobState>();
builder.Services.AddSingleton(_ => Channel.CreateBounded<ToolJobRequest>(new BoundedChannelOptions(1)
{
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleReader = true,
    SingleWriter = false
}));
builder.Services.AddHostedService<ToolJobRunner>();

// Owner-only "Create database seed" job: its own single-slot channel + background
// runner + state, sharing the RunGate above so it is mutually exclusive with the
// pipeline and the maintenance tools. The runner shells out to pg_dump.
builder.Services.AddSingleton<SeedJobState>();
builder.Services.AddSingleton(_ => Channel.CreateBounded<SeedJobRequest>(new BoundedChannelOptions(1)
{
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleReader = true,
    SingleWriter = false
}));
builder.Services.AddHostedService<SeedJobRunner>();

// Owner-only embeddings export/import job (pg_dump / pg_restore of the corpus
// similarity index): its own single-slot channel + background runner + state,
// sharing the RunGate so it is mutually exclusive with everything else. The named
// HttpClient (no timeout) is for URL imports of large release-asset archives.
builder.Services.AddSingleton<EmbeddingsJobState>();
builder.Services.AddSingleton(_ => Channel.CreateBounded<EmbeddingsJobRequest>(new BoundedChannelOptions(1)
{
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleReader = true,
    SingleWriter = false
}));
builder.Services.AddHostedService<EmbeddingsJobRunner>();
builder.Services.AddHttpClient(EmbeddingsJobRunner.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);

// Rate limit: 1 trigger per 60 seconds in production. Values are read from
// WebhookOptions via Options.Configure<TDep> so they resolve LAZILY — at the
// moment RateLimiterOptions is first requested — rather than eagerly here,
// before WebApplicationFactory's configuration overrides have been applied.
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<IOptions<WebhookOptions>>((rateLimitOpts, webhookOpts) =>
    {
        var w = webhookOpts.Value;
        rateLimitOpts.AddFixedWindowLimiter("trigger", opt =>
        {
            opt.PermitLimit = w.RateLimitPermits;
            opt.Window = TimeSpan.FromSeconds(w.RateLimitWindowSeconds);
            opt.QueueLimit = 0;
            opt.AutoReplenishment = true;
        });
        rateLimitOpts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

builder.Services.AddControllersWithViews();

// --- Authentication & authorization (lightweight custom: cookie + Google) ---
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Denied";
        options.SlidingExpiration = true;
        // Fail JSON/XHR requests with a status code instead of a 302 to the login
        // page, so the Authoring tab's fetch() calls handle an expired session
        // cleanly rather than parsing an HTML login page as JSON.
        options.Events.OnRedirectToLogin = context =>
        {
            if (IsApiRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (IsApiRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    // Transient external-login cookie: the Google handler signs into THIS, then
    // GoogleCallback maps the Google identity to a local app_user and issues the
    // real cookie with our claims/role. Without the separate scheme the cookie
    // would carry Google's claims (no role), not ours.
    .AddCookie(AuthConstants.ExternalScheme)
    .AddGoogle(options =>
    {
        // Placeholders keep GoogleOptions.Validate() happy when Google isn't
        // configured; real values are layered in lazily below (the login page
        // hides the Google button unless AuthOptions.Google.Enabled).
        options.ClientId = "not-configured";
        options.ClientSecret = "not-configured";
        options.SignInScheme = AuthConstants.ExternalScheme;
    });

// Bind cookie lifetime + Google credentials LAZILY (mirrors the RateLimiterOptions
// pattern above) so WebApplicationFactory's in-memory "Auth:*" overrides apply —
// eager reads at composition time miss the test host's config source.
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((cookieOpts, authOpts) =>
    {
        var hours = authOpts.Value.CookieExpiryHours;
        if (hours > 0) cookieOpts.ExpireTimeSpan = TimeSpan.FromHours(hours);
    });
builder.Services.AddOptions<GoogleOptions>(GoogleDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((googleOpts, authOpts) =>
    {
        var g = authOpts.Value.Google;
        if (g.Enabled)
        {
            googleOpts.ClientId = g.ClientId;
            googleOpts.ClientSecret = g.ClientSecret;
        }
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Read", p => p.RequireAuthenticatedUser());
    options.AddPolicy("AuthorWrite", p => p.RequireRole(Roles.Owner, Roles.Administrator, Roles.Author));
    options.AddPolicy("PipelineOps", p => p.RequireRole(Roles.Owner, Roles.Administrator));
    options.AddPolicy("ManageUsers", p => p.RequireRole(Roles.Owner, Roles.Administrator));
    options.AddPolicy("OwnerOnly", p => p.RequireRole(Roles.Owner));
    // Everything requires an authenticated user unless an action opts out with
    // [AllowAnonymous] (login/bootstrap) or an endpoint calls .AllowAnonymous().
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

// Best-effort startup step: ensure the source-DB trial-selection performance
// index exists when the source (AACT) and output databases are co-located.
// Idempotent and self-skipping when not co-located; failures are logged, never
// fatal — see PostgresGateway.EnsureSourcePerformanceIndexesAsync.
using (var startupScope = app.Services.CreateScope())
{
    try
    {
        await startupScope.ServiceProvider
            .GetRequiredService<PostgresGateway>()
            .EnsureSourcePerformanceIndexesAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "Source performance-index startup step failed; trial selection will still work but may be slower.");
    }

    // Best-effort startup step: reconcile study rows stranded at status='running' by
    // a host that was killed mid-trial. Left alone they are invisible (counted as
    // neither success nor failure) AND permanently skipped (the batch anti-join has
    // no status filter), so the trial is silently lost. Marking them 'interrupted'
    // surfaces them on the dashboard and makes them re-runnable from History.
    //
    // Age-gated by Postgres:InterruptedStudyThresholdHours (default 6h) because the
    // CLI can be processing trials against this same database right now and RunGate
    // cannot see it - see ReconcileInterruptedStudiesAsync.
    //
    // Its own try/catch rather than sharing the one above, so a failure of the index
    // step does not skip this, and vice versa. Own scope reused deliberately.
    try
    {
        var thresholdHours = startupScope.ServiceProvider
            .GetRequiredService<IOptions<PostgresOptions>>().Value.InterruptedStudyThresholdHours;
        await startupScope.ServiceProvider
            .GetRequiredService<PostgresGateway>()
            .ReconcileInterruptedStudiesAsync(TimeSpan.FromHours(thresholdHours), CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "Interrupted-study reconcile startup step failed; rows stranded at 'running' stay hidden until the next start.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Machine-to-machine + liveness endpoints stay anonymous: /trigger has its own
// shared-secret check, and /health must answer before a user logs in. Without
// AllowAnonymous the FallbackPolicy would gate both.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapPost("/trigger", (
        HttpContext httpContext,
        IOptions<WebhookOptions> options,
        RunGate gate,
        Channel<RunRequest> channel,
        int? count) =>
    {
        var webhookOptions = options.Value;

        if (!IsAuthorized(httpContext, webhookOptions.Secret))
        {
            return Results.Unauthorized();
        }

        var runId = Guid.NewGuid();
        if (!gate.TryAcquire(runId))
        {
            return Results.Conflict(new { current_run_id = gate.CurrentRunId });
        }

        var studyCount = count.GetValueOrDefault(webhookOptions.DefaultStudyCount);
        if (studyCount <= 0)
        {
            gate.Release();
            return Results.BadRequest(new { error = "count must be a positive integer" });
        }

        var startedAt = DateTimeOffset.UtcNow;
        if (!channel.Writer.TryWrite(new RunRequest(runId, studyCount, startedAt)))
        {
            gate.Release();
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Accepted(
            uri: $"/runs/{runId}",
            value: new { run_id = runId, started_at = startedAt, study_count = studyCount });
    })
    .RequireRateLimiting("trigger")
    .AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapHub<RunProgressHub>("/hubs/progress");

app.Run();

static bool IsAuthorized(HttpContext httpContext, string secret)
{
    if (string.IsNullOrEmpty(secret))
    {
        // Refusing to fall through to "no auth required" when the secret is
        // misconfigured is intentional — silently disabling auth is the
        // textbook footgun (spec section 6.5).
        return false;
    }
    if (!httpContext.Request.Headers.TryGetValue("X-Eligibility-Token", out var values))
    {
        return false;
    }
    var providedBytes = Encoding.UTF8.GetBytes(values.ToString());
    var secretBytes = Encoding.UTF8.GetBytes(secret);
    return CryptographicOperations.FixedTimeEquals(providedBytes, secretBytes);
}

// True for fetch()/XHR/JSON requests, which should get a 401/403 status on an
// auth failure rather than a 302 redirect to an HTML login page. Browser page
// navigations send "Accept: text/html"; fetch defaults to "*/*".
static bool IsApiRequest(HttpRequest request)
{
    if (string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }
    var accept = request.Headers.Accept.ToString();
    return !string.IsNullOrEmpty(accept)
        && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
}

// WebApplicationFactory uses EligibilityProcessing.Web.WebMarker for its
// TEntryPoint generic argument; see WebMarker.cs for rationale.

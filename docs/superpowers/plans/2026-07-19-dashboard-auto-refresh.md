# Dashboard Auto-Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an on/off switch and interval select to the dashboard toolbar that poll `GET /Home/Metrics` while a batch is in flight, so the run card advances without the operator touching Reload.

**Architecture:** No server change and no new endpoint - this is entirely client-side, reusing the existing `loadMetrics` fetch path. `loadMetrics` gains two options (`silent`, `includeToolCounts`) so a tick can be cheap and invisible; a small self-contained IIFE owns the timer lifecycle. The timer starts from the server-rendered run status or SignalR `BatchStarted`, and stops on SignalR completion or when a poll's own payload shows the run ended.

**Tech Stack:** ASP.NET Core 8 Razor views, vanilla JS (no framework, no build step), Bootstrap 5, SignalR client, xUnit + `WebApplicationFactory`.

**Spec:** `docs/superpowers/specs/2026-07-19-dashboard-auto-refresh-design.md`

## Global Constraints

- Branch `feat/dashboard-auto-refresh` (already created off `origin/main`). Never commit to `main`.
- **ASCII only** in every authored file - no em dashes, en dashes, curly quotes, or ellipsis characters. Use a plain hyphen `-`. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8. Existing lines in these files contain em dashes; leave untouched lines alone, but do not add new ones.
- **Never write files with PowerShell `Set-Content`/`Out-File`** - PS 5.1 adds a UTF-8 BOM. Use the Edit/Write tools.
- Platform is Windows/PowerShell. Use `$env:VAR` syntax.
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`, never `dotnet build` alone.
- **No migration, no schema change, no controller change.** Version bump is **build only**.
- There is **no JavaScript test infrastructure** in this repo. Do not add one. The timer logic gets no automated coverage by design - see Task 4.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Views/Home/Index.cshtml` | `loadMetrics` options; the auto-refresh IIFE; SignalR start/stop hooks | 1, 3 |
| `Views/Home/_DashboardToolbar.cshtml` | The two controls + status suffix markup | 2 |
| `tests/.../WebTests.cs` | Render tests for the controls | 2 |
| `contexts/eligibility/version.json` | Build bump + release note | 4 |

`Index.cshtml` is already ~560 lines and does a lot. The auto-refresh logic is deliberately one self-contained IIFE with a single exported hook (`window.__dashAutoRefresh`) rather than functions scattered through the existing script, so it can be read and reasoned about as one unit.

---

### Task 1: `loadMetrics` options

Pure refactor - no behaviour change. Every existing caller keeps its current semantics via defaults. Lands green on its own.

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Index.cshtml:83-137` (the `loadMetrics` function)

**Interfaces:**
- Consumes: nothing.
- Produces: `loadMetrics(fresh, opts)` where `opts` is `{ silent?: boolean, includeToolCounts?: boolean }`, both defaulting to the current behaviour (`silent: false`, `includeToolCounts: true`). Returns a Promise, as today.

- [ ] **Step 1: Change the signature and guard the two new options**

In `Views/Home/Index.cshtml`, replace lines 83-99 (from `async function loadMetrics(fresh) {` down to and including the `const res = await fetch(...)` call) with:

```javascript
            // opts.silent      - skip the skeleton. Auto-refresh ticks pass this:
            //                    flashing the whole dashboard into skeleton state
            //                    every 10-30s is unusable, and a tick is not a
            //                    user action that needs acknowledging.
            // opts.includeToolCounts - the maintenance backlog counts do not move
            //                    during a batch, and they are three UNCACHED
            //                    queries. Ticks skip them.
            async function loadMetrics(fresh, opts) {
                const silent = !!(opts && opts.silent);
                const includeToolCounts = !opts || opts.includeToolCounts !== false;

                if (inFlight) { inFlight.abort(); }
                const ctl = new AbortController();
                inFlight = ctl;
                if (!silent) { dash.setAttribute("data-loading", "1"); }
                try {
                    // The maintenance-backlog counts on the failures card come from
                    // the same endpoint the Tools tab uses. Fetched in parallel and
                    // best-effort - it must not block or fail the main figures - so
                    // hydrate the counts here, before awaiting the metrics below.
                    if (includeToolCounts) {
                        fetch("/Home/ToolCounts", { signal: ctl.signal, headers: { "Accept": "application/json" } })
                            .then((r) => r.json())
                            .then((tc) => { if (!ctl.signal.aborted) { hydrateToolCounts(tc); } })
                            .catch(() => { /* leave the counts at their last value */ });
                    }

                    const res = await fetch(fresh ? "/Home/Metrics?fresh=true" : "/Home/Metrics",
                                            { signal: ctl.signal, headers: { "Accept": "application/json" } });
```

- [ ] **Step 2: Make the `finally` block respect `silent`**

Still in `loadMetrics`, the `finally` block currently reads:

```javascript
                } finally {
                    // Always clear, even on failure: a skeleton that outlives its
                    // request tells the user to keep waiting for something that is
                    // never coming.
                    if (inFlight === ctl) {
                        dash.removeAttribute("data-loading");
                        inFlight = null;
                    }
                }
```

Replace with:

```javascript
                } finally {
                    // Always clear, even on failure: a skeleton that outlives its
                    // request tells the user to keep waiting for something that is
                    // never coming. A silent call never set the attribute, but
                    // clearing it unconditionally is still correct - it may have
                    // been set by a non-silent call this one superseded.
                    if (inFlight === ctl) {
                        dash.removeAttribute("data-loading");
                        inFlight = null;
                    }
                }
```

Only the comment changes - the code is deliberately identical. A silent tick that supersedes a visible Reload must still clear that Reload's skeleton, or it sticks forever.

- [ ] **Step 3: Return the payload so callers can inspect it**

The auto-refresh timer needs to know whether the run is still in flight. Inside the `try`, the success path currently ends:

```javascript
                    hydrate(payload);
                    errBox.hidden = true;
```

Replace with:

```javascript
                    hydrate(payload);
                    errBox.hidden = true;
                    return payload;
```

Every existing caller ignores the return value, so this is additive. All other exits (abort, error, non-JSON) fall through to an implicit `undefined`, which the timer treats as "no information, keep polling" - see Task 3.

- [ ] **Step 4: Verify nothing regressed**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS. Razor compile errors in `Index.cshtml` surface here. The existing dashboard render tests in `WebTests.cs` exercise this view.

- [ ] **Step 5: Manual smoke check of the refactor**

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web
```

Load `/`, confirm the dashboard still paints figures and the Reload button still shows the skeleton and updates numbers. This is a no-behaviour-change refactor, so anything different is a bug introduced here, and it is far cheaper to catch now than after Task 3 adds a timer on top.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Index.cshtml
git commit -m "Give loadMetrics silent and includeToolCounts options"
```

---

### Task 2: Toolbar controls

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/_DashboardToolbar.cshtml` (add controls after the Reload button block, which ends at line 42)
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/WebTests.cs` (append tests before the `Factory` class at line ~272)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: DOM contract consumed by Task 3 -
  - `#auto-refresh-toggle` - `<input type="checkbox">`
  - `#auto-refresh-interval` - `<select>` with values `10000`, `30000`, `60000`, `300000`; `30000` selected
  - `#auto-refresh-state` - `<span>` for the status suffix text
  - `#auto-refresh` - wrapper `<div>` carrying `data-run-active="true"|"false"` (the server-rendered seed)

- [ ] **Step 1: Write the failing tests**

In `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/WebTests.cs`, insert immediately before `public sealed class Factory : WebApplicationFactory<WebMarker>` (line ~272):

```csharp
    // Auto-refresh controls. The timer logic itself has no automated coverage
    // (there is no JS test harness in this repo), so these assert the contract
    // the script depends on: the elements exist, the default is 30s, and the
    // controls are NOT behind the pipeline-ops gate.
    [Fact]
    public async Task Dashboard_renders_auto_refresh_controls()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("auto-refresh-toggle", body);
        Assert.Contains("auto-refresh-interval", body);
        Assert.Contains("auto-refresh-state", body);
    }

    [Fact]
    public async Task Auto_refresh_offers_four_intervals_defaulting_to_30_seconds()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("value=\"10000\"", body);
        Assert.Contains("value=\"30000\"", body);
        Assert.Contains("value=\"60000\"", body);
        Assert.Contains("value=\"300000\"", body);
        // The 30s option is the one carrying `selected`.
        Assert.Contains("value=\"30000\" selected", body);
    }

    // Watching a run is a read. The Reload button is deliberately outside the
    // PipelineOps gate for the same reason - a read-only user watching a batch
    // has more reason to refresh than anyone.
    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Auto_refresh_controls_render_for_read_only_roles(string role)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("auto-refresh-toggle", body);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Integration.Tests --filter "FullyQualifiedName~auto_refresh|FullyQualifiedName~Auto_refresh"
```

Expected: FAIL - `Assert.Contains() Failure: Sub-string not found`.

- [ ] **Step 3: Add the markup**

In `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/_DashboardToolbar.cshtml`, insert immediately after the Reload button's closing `</button>` (line 42) and before the `<div>` holding the title (line 43):

```html
        @* Auto-refresh. Outside the PipelineOps gate for the same reason as
           Reload above: watching a run is a read.

           data-run-active seeds the timer from the server so a batch already in
           flight at page load starts polling immediately - including one started
           by the CLI on another host, which never fires a SignalR BatchStarted in
           this browser. Deliberately NOT runInProgress: that is also true for
           tool jobs, which do not drive the run card. *@
        <div id="auto-refresh" class="d-flex align-items-center gap-2 border rounded-3 px-2 py-1"
             data-run-active="@((Model.MostRecentRun?.Status == "running").ToString().ToLowerInvariant())">
            <div class="form-check form-switch mb-0">
                <input class="form-check-input" type="checkbox" role="switch"
                       id="auto-refresh-toggle" checked
                       aria-label="Automatically refresh the dashboard while a run is in progress" />
            </div>
            <label for="auto-refresh-interval" class="epk mb-0" style="white-space: nowrap;">
                Auto-refresh: <span id="auto-refresh-state">idle</span>
            </label>
            <select id="auto-refresh-interval" class="form-select form-select-sm"
                    style="width: auto;"
                    aria-label="Auto-refresh interval">
                <option value="10000">10s</option>
                <option value="30000" selected>30s</option>
                <option value="60000">60s</option>
                <option value="300000">5 min</option>
            </select>
        </div>
```

Note `checked` on the toggle: the switch defaults to on, per the spec. The persisted preference overrides this on load (Task 3).

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Integration.Tests --filter "FullyQualifiedName~Auto_refresh|FullyQualifiedName~auto_refresh"
```

Expected: PASS, 4 tests (two `[Fact]` plus the two-case `[Theory]`).

- [ ] **Step 5: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/_DashboardToolbar.cshtml contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/WebTests.cs
git commit -m "Add auto-refresh controls to the dashboard toolbar"
```

---

### Task 3: The timer

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Index.cshtml` (new IIFE after the Reload handler at line ~516; SignalR hook edits at lines 454, 467, 473)

**Interfaces:**
- Consumes: `loadMetrics(fresh, opts)` returning the payload (Task 1); the DOM ids from Task 2.
- Produces: `window.__dashAutoRefresh` with `{ start(), stop() }`, called from the SignalR handlers.

- [ ] **Step 1: Add the auto-refresh IIFE**

In `Views/Home/Index.cshtml`, insert after the Reload button handler's closing `}` (line ~516) and before the `// First paint has no figures in it` comment (line ~518):

```javascript
            // ===================== Auto-refresh =====================
            //
            // Polls ONLY while a batch is in flight. Idle costs nothing.
            //
            // Three things here are load-bearing and easy to "simplify" wrongly:
            //
            //  1. A tick SKIPS ITSELF if a request is already running. loadMetrics
            //     aborts whatever is in flight (inFlight is a single slot), so a
            //     tick landing during a user's Reload would kill that Reload. The
            //     user's explicit action always wins; a skipped tick costs nothing
            //     because another follows in 10-300s.
            //
            //  2. The poll STOPS ITSELF when its own payload says the run ended.
            //     Not redundant with the SignalR BatchCompleted hook: if the
            //     connection has dropped, that event never arrives and the timer
            //     would otherwise spin forever.
            //
            //  3. Ticks pass fresh=false. fresh=true invalidates the SHARED 60s
            //     cache, so polling with it would force every connected client's
            //     aggregate uncached at once - the opposite of what a poll is for.
            (function () {
                const root = document.getElementById("auto-refresh");
                const toggleEl = document.getElementById("auto-refresh-toggle");
                const intervalEl = document.getElementById("auto-refresh-interval");
                const stateEl = document.getElementById("auto-refresh-state");
                if (!root || !toggleEl || !intervalEl || !stateEl) { return; }

                const KEY_ON = "dashboard.autoRefresh";
                const KEY_MS = "dashboard.autoRefreshIntervalMs";
                const DEFAULT_MS = 30000;
                const ALLOWED_MS = ["10000", "30000", "60000", "300000"];

                let timer = null;        // the single interval handle
                let runActive = root.dataset.runActive === "true";

                // localStorage throws in some privacy modes; a dead preference
                // must not take the dashboard down with it.
                function readPref(key, fallback) {
                    try {
                        const v = localStorage.getItem(key);
                        return v === null ? fallback : v;
                    } catch (e) { return fallback; }
                }
                function writePref(key, value) {
                    try { localStorage.setItem(key, value); } catch (e) { }
                }

                function chosenMs() {
                    const v = parseInt(intervalEl.value, 10);
                    return isNaN(v) ? DEFAULT_MS : v;
                }
                function label(ms) {
                    return ms >= 60000 ? `${Math.round(ms / 60000)}min` : `${Math.round(ms / 1000)}s`;
                }
                function setState(text) { stateEl.textContent = text; }

                function pulse() {
                    stateEl.classList.add("ep-tick");
                    setTimeout(() => stateEl.classList.remove("ep-tick"), 400);
                }

                async function tick() {
                    // (1) above - never cancel a request already in progress.
                    if (inFlight) { return; }
                    pulse();
                    const payload = await loadMetrics(false, { silent: true, includeToolCounts: false });
                    // (2) above. An undefined payload means abort/error/expired
                    // session - no information about the run, so keep polling and
                    // let the visible error box speak for itself.
                    if (payload && payload.mostRecentRun && payload.mostRecentRun.isRunning === false) {
                        runActive = false;
                        stop();
                    }
                }

                // Always clears before setting: the only defence against leaking a
                // second interval when this is called twice (interval change,
                // visibility resume, a SignalR reconnect re-firing BatchStarted).
                function start() {
                    stopTimerOnly();
                    if (!toggleEl.checked) { setState("off"); return; }
                    if (!runActive) { setState("idle"); return; }
                    if (document.visibilityState === "hidden") { setState("paused"); return; }
                    const ms = chosenMs();
                    timer = setInterval(tick, ms);
                    setState(label(ms));
                }
                function stopTimerOnly() {
                    if (timer !== null) { clearInterval(timer); timer = null; }
                }
                function stop() {
                    stopTimerOnly();
                    setState(toggleEl.checked ? "idle" : "off");
                }

                toggleEl.addEventListener("change", () => {
                    writePref(KEY_ON, toggleEl.checked ? "1" : "0");
                    if (toggleEl.checked) { start(); } else { stop(); }
                });

                intervalEl.addEventListener("change", () => {
                    writePref(KEY_MS, intervalEl.value);
                    start();   // clears the old interval first
                });

                // A hidden tab polling every 10s for hours is pure waste. Resume
                // with an immediate tick so returning to the tab shows current
                // figures rather than up-to-one-interval-old ones.
                document.addEventListener("visibilitychange", () => {
                    if (document.visibilityState === "hidden") {
                        stopTimerOnly();
                        if (toggleEl.checked && runActive) { setState("paused"); }
                    } else {
                        start();
                        if (toggleEl.checked && runActive) { tick(); }
                    }
                });

                // Restore preferences. An unrecognised stored interval is NOT
                // trusted - a corrupt value would otherwise become a real
                // setInterval delay.
                const storedMs = readPref(KEY_MS, String(DEFAULT_MS));
                intervalEl.value = ALLOWED_MS.indexOf(storedMs) >= 0 ? storedMs : String(DEFAULT_MS);
                toggleEl.checked = readPref(KEY_ON, "1") === "1";

                window.__dashAutoRefresh = {
                    start: function () { runActive = true; start(); },
                    stop: function () { runActive = false; stop(); }
                };

                start();
            })();
```

- [ ] **Step 2: Hook the SignalR batch events**

Three edits in the SignalR handlers in the same file.

`BatchStarted` (line ~454) - add the `start()` call:

```javascript
            connection.on("BatchStarted", (e) => {
                setTriggerButtonsDisabled(true);
                appendEvent("Batch", "bg-info", `Started run ${e.runId.substring(0, 8)} (${e.studyCount} studies)`);
                if (window.__dashAutoRefresh) { window.__dashAutoRefresh.start(); }
            });
```

`BatchCompleted` (line ~467) - stop before the final read, so the timer cannot fire alongside it:

```javascript
            connection.on("BatchCompleted", (e) => {
                setTriggerButtonsDisabled(false);
                appendEvent("Batch", e.status === "success" ? "bg-success" : "bg-danger",
                    `Completed run ${e.runId.substring(0, 8)} (${e.rowsPersisted} rows, ${(e.resolutionRate * 100).toFixed(1)}% resolved)`);
                if (window.__dashAutoRefresh) { window.__dashAutoRefresh.stop(); }
                loadMetrics(false);
            });
```

`BatchCancelled` (line ~473) - same:

```javascript
            connection.on("BatchCancelled", (e) => {
                setTriggerButtonsDisabled(false);
                appendEvent("Batch", "bg-warning", `Cancelled run ${e.runId.substring(0, 8)}`);
                if (window.__dashAutoRefresh) { window.__dashAutoRefresh.stop(); }
                // Cancelled still persisted whatever finished first.
                loadMetrics(false);
            });
```

Do **not** hook `ToolJobStarted` / `ToolJobCompleted`. Tool jobs do not drive the run card, and starting the poll for them would spend queries showing nothing new.

- [ ] **Step 3: Add the tick-pulse style**

In `Views/Home/Index.cshtml`, the `@section Scripts` block holds only script. Add the style to the existing site stylesheet instead - `contexts/eligibility/src/EligibilityProcessing.Web/wwwroot/css/site.css`, appended at the end:

```css
/* Auto-refresh tick indicator: a brief fade on the interval label each time the
   dashboard polls. The only visible proof the timer is alive - without it, "on"
   and "silently broken" look identical. */
.ep-tick {
    opacity: 0.35;
    transition: opacity 120ms ease-in-out;
}
```

This is the correct file: it is referenced by `Views/Shared/_Layout.cshtml:16`
and already holds every other `ep-` class (`ep-dash`, `ep-reload-btn`). Check its
tail first for house conventions:

```powershell
Get-Content contexts/eligibility/src/EligibilityProcessing.Web/wwwroot/css/site.css -Tail 15
```

- [ ] **Step 4: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS. This catches Razor/JS syntax errors that break the view render, but **not** timer behaviour.

- [ ] **Step 5: Manual verification - this is where the feature is actually proven**

The automated tests cannot exercise any of this. Run the app:

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web
```

Work through all seven checks. Each maps to a specific failure mode in the design:

1. **Idle state.** Load `/` with no run active. Status reads `idle`. Open DevTools Network - confirm **no** repeating `/Home/Metrics` requests.
2. **Starts on a run.** Trigger a batch. Status changes to `30s`, and the run card's rows/ring advance on their own.
3. **No skeleton flash.** During polling, the cards must not grey out on each tick. (Regression check for `silent`.)
4. **No tool-count requests.** In the Network tab during polling, `/Home/Metrics` should appear each tick and `/Home/ToolCounts` should **not**.
5. **Reload is never cancelled.** While polling, click Reload. It must complete - not show as `(canceled)` in the Network tab. (Regression check for the in-flight skip.)
6. **Stops on completion.** When the batch finishes, status returns to `idle` and requests stop.
7. **Interval change and persistence.** Switch to 10s mid-run - ticks speed up and there is exactly one request per tick, not two (a leaked interval shows as doubled requests). Reload the page; the switch and interval come back as you left them.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Index.cshtml contexts/eligibility/src/EligibilityProcessing.Web/wwwroot/css/site.css
git commit -m "Poll the dashboard while a batch is in flight"
```

---

### Task 4: Version bump and PR

**Files:**
- Modify: `contexts/eligibility/version.json`

- [ ] **Step 1: Bump the version**

Build-only bump - no migration, no schema change. Read the file first to confirm the current build number (it was 33 at the time of writing; if `main` has moved, use `current.build + 1` and keep `releases[0]` matching `current`).

Set `current` to:

```json
  "current": { "major": 0, "minor": 1, "build": 34, "releaseDate": "2026-07-19" },
```

and prepend to `releases`:

```json
    {
      "version": "0.1.34",
      "releaseDate": "2026-07-19",
      "enhancements": [
        "Dashboard: an auto-refresh switch and interval (10s / 30s / 60s / 5 min, default 30s) next to Reload. While a batch is running the figures and the run card advance on their own; nothing polls when the pipeline is idle. The setting is remembered per browser."
      ],
      "fixes": []
    },
```

Keep the file ASCII-only.

- [ ] **Step 2: Update the version assertions**

`VersionWebTests.cs` hard-codes the current version and release date, so a bump always breaks it. Update every occurrence:

```powershell
dotnet test contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~VersionWebTests"
```

Read the failures and update `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/VersionWebTests.cs` - there are four version literals (including one `v`-prefixed, in the footer test) and **one `ReleaseDate` assertion**. The release date is easy to miss because only one of the four tests asserts it.

Use the Edit tool, not PowerShell string replacement - PS 5.1 writes a UTF-8 BOM, which shows up as a spurious first-line diff.

- [ ] **Step 3: Verify the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, 0 skipped if Docker is running. The count should be up 4 from baseline (Task 2's render tests).

- [ ] **Step 4: Commit, push, open the PR**

```powershell
git add contexts/eligibility/version.json contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/VersionWebTests.cs
git commit -m "Bump version to 0.1.34"
git push -u origin feat/dashboard-auto-refresh
```

Open the PR with `gh pr create`. The body must state plainly:

- What the dashboard already did (event-driven refresh on batch/tool completion) and the three gaps this fills.
- That polling is in-flight-only, and the accepted blind spot: a CLI-started run does not wake an idle tab until the page is reloaded.
- That the timer logic has **no automated coverage** because there is no JS test harness, which manual checks were run instead, and that this is the app's first background poller.

---

## Deferred (not in this plan)

- **`/Home/ToolCounts` is uncached** - three count queries on every dashboard load and every completion event. Out of scope here (ticks simply skip it), but it is the obvious next caching target.
- **Waking an idle tab for a CLI-started run** would need either a server-push heartbeat or an idle low-frequency poll. Explicitly rejected during design on query cost.

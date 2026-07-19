# Dashboard auto-refresh

Date: 2026-07-19
Status: approved

## Problem

The dashboard's figures are fetch-driven. They update on page load, on the
Reload button, and on three SignalR events - `BatchCompleted`, `BatchCancelled`,
`ToolJobCompleted` - each of which calls `loadMetrics()`
(`Views/Home/Index.cshtml:467,473,487`). The server invalidates the cached
aggregate *before* announcing those events
(`SignalRPipelineHooks.cs:54-59`), so those reads come back fresh.

So "numbers are stale after a run finishes" is already solved. The gaps are
narrower, and all three are cases where the dashboard silently stops reflecting
reality:

1. **Nothing advances during a run.** `TrialCompleted` only appends a line to
   the activity feed (`Index.cshtml:460`). The run card's ring, rows, resolution
   and estimated finish do not move until the batch ends. The data exists - the
   orchestrator upserts the run row after every trial
   (`PipelineOrchestrator.vb:604`), and `GetRecentRunsAsync` bypasses the cache -
   so a poll would show live progress.
2. **CLI-driven runs emit no SignalR events.** A batch started by `elig run` on
   another host updates the database but never notifies a browser.
3. **A dropped SignalR connection** stops all updates with no visible sign.

## Decision

Two controls in the dashboard toolbar - an on/off switch and an interval select
(10s / 30s / 60s / 5min, default 30s) - driving a poll that runs **only while a
batch is in flight**.

### Why in-flight only

Rejected: a poll that runs whenever the switch is on. At 10s that is four
uncached queries per tick, per open tab, indefinitely. `/Home/Metrics` caches
only the big aggregate (60s TTL); `GetRecentRunsAsync` bypasses the cache by
design, and `/Home/ToolCounts` (`HomeController.cs:449`) is three uncached counts
with no caching at all.

### Accepted blind spot

With in-flight-only polling, a CLI-started run does not wake an idle tab. Page
load catches it - the server already computes `MostRecentRun?.Status ==
"running"` for the toolbar - but a tab left idle stays asleep until reloaded.
This is a deliberate trade for the query cost, not an oversight.

### Tool counts excluded from ticks

The maintenance backlog counts (normalize / embed / superseded) do not move
during a batch, so ticks skip `/Home/ToolCounts` entirely. That removes three of
the four uncached queries per tick. Those counts continue to refresh on page load
and on job-completion events.

## Design

### Lifecycle

The timer starts from either of two sources:

- **Page load**, when the server-rendered most-recent-run status is `running`.
  This is the path that catches a CLI-started run.
- **SignalR `BatchStarted`.**

It stops on `BatchCompleted` / `BatchCancelled`, **and also stops itself when a
poll response shows the run is no longer `running`**. The second path is not
redundant: it means a dead SignalR connection cannot leave the timer spinning
forever, and makes the poll's own data the authority on when to stop.

Tool jobs do not start the timer. They do not drive the run card.

### What a tick does

`loadMetrics` gains two options:

    loadMetrics(fresh, { silent = false, includeToolCounts = true } = {})

- **`silent`** skips `dash.setAttribute("data-loading", "1")`
  (`Index.cshtml:87`). Without it every tick flashes the whole dashboard into
  skeleton state; at 10s that is unusable.
- **`includeToolCounts: false`** drops the second endpoint, per the decision
  above.
- Ticks always pass **`fresh = false`**. `fresh=true` invalidates the shared
  cache (`HomeController.cs:429`), so polling with it would force every connected
  client's aggregate uncached at once - the opposite of what a poll should do.

**In-flight skip.** `loadMetrics` aborts any request already running
(`Index.cshtml:83-85`), because `inFlight` is a single slot. A tick landing
during a user's Reload would kill that Reload. So a tick **skips itself entirely
when `inFlight` is set**. The user's explicit action always wins, and a skipped
tick costs nothing - another follows in 10-300s.

**Visibility pausing.** The timer pauses on
`document.visibilityState === "hidden"` and resumes with an immediate tick when
the tab becomes visible. A background tab polling every 10s for hours is waste.

### Controls and state

The status suffix distinguishes two states that would otherwise be conflated:

- `Auto-refresh: idle` - switch on, no batch running, nothing polling.
- `Auto-refresh: 30s` - actively polling, with a brief pulse on each tick.

Without this, a switch reading "on" with nothing visibly happening is
indistinguishable from a broken feature, and the natural response is to reload
the page - which is what the feature exists to avoid.

Both settings persist in `localStorage` under `dashboard.autoRefresh` and
`dashboard.autoRefreshIntervalMs`, following the dotted-key convention and the
defensive `try { } catch (e) { }` wrapper at `_StudyListPanel.cshtml:111`.
localStorage throws in some privacy modes and must not take the dashboard down.

An unrecognised or corrupt stored interval falls back to the 30s default rather
than being trusted.

The switch **defaults to on**. It costs nothing while idle.

### Placement and authorization

The controls sit in `_DashboardToolbar.cshtml` next to Reload, **outside** the
`canRunPipeline` gate. Watching a run is a read. The existing comment at
`_DashboardToolbar.cshtml:31-37` makes this argument for Reload already: a
read-only user watching a batch has more reason to refresh than anyone.

No new endpoint and no controller change - the poll reuses `GET /Home/Metrics`,
which is already `[Authorize]` with no tighter policy.

## Risk

This is the first background poller in the application. That is a category of
bug that does not announce itself: intervals leaked across SignalR reconnects,
ticks racing user actions, timers surviving navigation.

Mitigations, all in the design above: a single interval handle, `clearInterval`
before every set, the in-flight skip, self-termination from poll data, and
visibility pausing.

## Testing

**There is no JavaScript test infrastructure in this repository** - all five test
projects are .NET. The timer logic itself (start/stop transitions, interval
changes, visibility pausing, the in-flight skip) therefore gets **no automated
coverage**, and standing up a JS harness is not justified by this feature.

Automated coverage, following the render-test pattern used for the Resolve modal:

- The controls render, with the switch and all four interval options.
- The default selected interval is 30s.
- The controls render for a Viewer, confirming they are outside the pipeline-ops
  gate.
- `GET /Home/Metrics?fresh=false` returns a valid payload - the path the poll
  depends on.

Manual verification, which is where the behaviour is actually confirmed:

- Start a batch; the run card advances without touching Reload.
- No skeleton flash on ticks.
- The timer stops when the batch completes.
- Switching the interval mid-run takes effect without duplicate timers.
- The preference survives a page reload.

## Acceptance

- While a batch runs, the dashboard advances on its own at the chosen interval.
- Nothing polls while idle.
- The controls state plainly whether they are idle or polling.
- A user's Reload is never cancelled by a tick.

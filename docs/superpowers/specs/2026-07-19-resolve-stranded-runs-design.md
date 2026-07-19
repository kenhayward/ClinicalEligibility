# Manually resolving stranded runs

Date: 2026-07-19
Status: approved

## Problem

An unclean shutdown of the web host (server reboot, container kill, power loss)
leaves the in-flight `public.eligibility_run` row permanently at
`status = 'running'`. Nothing reconciles it. Several such rows have accumulated.

The operator-visible symptom is that the dashboard's trigger controls
(Earliest / Latest / Run Trial) will not enable, so no new run can be started.

### Actual mechanism

Worth recording precisely, because the obvious diagnosis is wrong.

Neither run-exclusivity gate reads `eligibility_run.status`:

- `RunGate` (`EligibilityProcessing.Web/RunGate.cs:62`) is an in-process CLR flag.
  A reboot clears it.
- `PostgresRunLock` (`EligibilityProcessing.Data/PostgresRunLock.vb`) is a
  session-level `pg_try_advisory_lock`. It is released when the dead session's
  connection drops.

Both are therefore free after a reboot. The block is purely presentational:
`Views/Home/_DashboardToolbar.cshtml:25` computes

    var runInProgress = Model.MostRecentRun?.Status == "running"
        || !string.IsNullOrEmpty(Model.BusyActivity);

and disables the trigger buttons on that basis. So a stale row is cosmetic to
the pipeline but load-bearing to the dashboard, and correcting the row is
sufficient to unblock triggering.

### Existing precedent

Stranded *study* rows already self-heal: `ReconcileInterruptedStudiesAsync`
(`PostgresGateway.vb:2135`) sweeps `eligibility_study` rows at `status='running'`
older than `InterruptedStudyThresholdHours` to `'interrupted'` at web-host
startup. The *run* table has no equivalent, which is why these rows accumulate.

## Decision

Add a manual, per-row "Resolve" action to the Runs list. Explicitly **not** an
automatic startup sweep for runs.

Rationale for rejecting the automatic sweep: a developer running a local host
against the shared database would have that host's startup sweep interrupt
genuinely running production pipelines. The existing study-row sweep carries the
same hazard and only mitigates it with an age threshold. A manual action avoids
the class of problem rather than approximating a solution to it.

## Scope

The action is available only on runs currently at `status = 'running'`. Runs that
have reached a terminal status cannot be edited.

This is the central constraint. `running` is the only status that can be *wrong*
rather than merely unflattering; a `success` or `failed` run is a real historical
result and must stay immutable. Restricting to `running` is what keeps
`eligibility_run` a trustworthy record while still permitting the correction.

Target status is chosen by the operator from `failed`, `cancelled`,
`interrupted`. A free-text reason is required and is written to `error_summary`.

Offering the choice of target matters: a reboot is closer to `interrupted` than
to `failed`, and collapsing them loses information when the Runs list is read
later.

### Accepted trade-off

This makes run history manually editable for the first time. It is constrained to
`running`-only, gated on `PipelineOps`, and audited. A permanently-wrong
`running` row is judged worse than an audited correction.

### Out of scope

This ships a broom, not a fix. Stranded rows will continue to accumulate on every
unclean shutdown. The durable remedies - a startup sweep for runs, or a heartbeat
column allowing staleness to be inferred rather than guessed - are deferred to a
follow-up.

## Design

### 1. Core: run status constants

New `EligibilityProcessing.Core/RunStatus.vb`, following the shape of the
existing per-study constants in `StudyExecution.vb:23-30`:

    Running, Success, Failed, Cancelled, Interrupted

plus a `ManualResolvable` collection containing `Failed`, `Cancelled`,
`Interrupted`. The controller validates the submitted target against this set, so
the whitelist lives in Core rather than in a controller conditional.

`Interrupted` is new to runs; it currently exists only for study rows.

Replaces the bare string literals at `PipelineOrchestrator.vb:100,204,215,606`
and `RunCardView.cs:106,109`. The comment at `RunMetrics.vb:50` is stale (it
omits `running`, which is written) and is corrected to list all five.

### 2. Data: one narrow gateway method

On `IPostgresGateway`:

    ResolveInterruptedRunAsync(runId, status, reason, ct)
        As Task(Of (RunUpdated As Boolean, StudiesReconciled As Integer))

Deliberately not `RecordRunAsync`: that is a full `RunMetrics` upsert and would
require the caller to reconstruct every metric field in order to change two of
them.

Two statements, one transaction:

    UPDATE public.eligibility_run
       SET status = @status, error_summary = @reason,
           ended_at = COALESCE(ended_at, @now)
     WHERE run_id = @run_id AND status = 'running';

    UPDATE public.eligibility_study
       SET status = 'interrupted', finished_at = @now,
           error_message = COALESCE(NULLIF(error_message, ''), @reason)
     WHERE run_id = @run_id AND status = 'running';

`AND status = 'running'` on the first statement is load-bearing. If the run
genuinely completes between the page rendering and the operator submitting, the
update matches zero rows, the transaction commits as a no-op, and the UI reports
that the run had already finished rather than overwriting a real result. This is
what makes the action safe on a shared database.

`COALESCE(ended_at, @now)` because a stranded row always has `ended_at IS NULL`;
the coalesce prevents the method inventing a second ending.

The study cascade is scoped to the single `run_id` and applies **no age
threshold**. The threshold in `ReconcileInterruptedStudiesAsync` exists to
approximate the judgement that a run is dead; here the operator has asserted it
directly for one specific run, so the cascade inherits that authority.

Cascading is not optional. Marking a run `interrupted` while its child study rows
still claim `running` is an inconsistent record and leaves the Studies tab
showing phantom in-flight trials.

### 3. Web: controller and UI

Controller action on `HomeController`, modelled on `RemoveSuperseded`
(`HomeController.cs:537`):

    [HttpPost] [Authorize(Policy = "PipelineOps")] [ValidateAntiForgeryToken]
    ResolveRun(Guid runId, string status, string reason)

- Validates `status` against `RunStatus.ManualResolvable`; 400 otherwise.
- Requires a non-empty, trimmed `reason`; 400 otherwise.
- Returns 409 when `RunGate.CurrentRunId == runId`. If this host is actively
  running that run it is by definition not stranded, and the operator has hit
  the button in error. This closes the one case the SQL guard alone would allow.
- Returns `Json` describing what changed, including the no-op case.

`PipelineOps` (Owner / Administrator) is the policy, matching every other run
control.

Audit row via the existing `IAuditWriter`:

    _audit.WriteAsync("update", "eligibility_run", runId.ToString(),
        $"manually resolved to '{status}': {reason}")

`entity_id` is indexed, so the run id alone goes there and the detail carries the
reason (see the note at `HomeController.cs:317-320`).

UI: a "Resolve" control in a new actions column on `Views/Home/Runs.cshtml`,
rendered only for rows at `running` and only when the `PipelineOps` check in the
view succeeds. It opens a small modal - status `select`, reason `textarea`,
submit disabled while the reason is empty - posting via `fetch` with the
anti-forgery token read from a hidden input, the pattern already used by
`Views/Shared/_CreateSeedModal.cshtml:154`. On success the page reloads so the
toolbar's `runInProgress` recomputes and the trigger buttons re-enable.

Note that `Runs.cshtml` currently has no per-row POST action and no authorization
check; both are introduced here, following `History.cshtml:4,312-319`.

### 4. Schema and versioning

No migration. `status` is plain `text` with no CHECK constraint, so
`interrupted` is a new value in an existing column. Therefore no
`database_schema.md` change, and the version bump is **build only**, not MINOR.

## Testing

Per the project rule, every new function ships with tests.

`Core.Tests`
- `RunStatus` constant values.
- `ManualResolvable` contains exactly `failed`, `cancelled`, `interrupted`, and
  excludes `running` and `success`.

`Integration.Tests` (real Postgres)
- Resolves a stranded run and cascades its `running` study rows.
- No-ops when the run has already reached a terminal status, and leaves the
  existing status and `error_summary` untouched. This is the concurrency guard
  and the most important test here.
- Leaves other runs' study rows untouched.
- Preserves an existing non-empty `error_message` on a study row.
- Policy denies Viewer and Author; permits Owner and Administrator.
- Rejects an invalid target status and an empty reason.
- Returns 409 when the gate holds that run id.

## Acceptance

- A run stranded at `running` can be resolved from the Runs list, and the
  dashboard trigger buttons re-enable without a restart.
- A completed run cannot be edited through this path.
- An audit row records who resolved which run, to what, and why.

# Resolve Stranded Runs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an Owner/Administrator mark a run stranded at `status='running'` (left by an unclean host shutdown) as failed/cancelled/interrupted with a required reason, cascading to that run's stranded study rows, so the dashboard trigger buttons re-enable.

**Architecture:** Validation is a pure function in Core (`RunStatus.ValidateResolution`) so it is unit-testable with no fakes; the controller is a thin shim over it. Persistence is one narrow gateway method running two guarded UPDATEs in a single transaction. The `AND status = 'running'` guard is what makes the action a safe no-op if the run genuinely finishes between page render and submit.

**Tech Stack:** .NET 8, VB.NET (Core/Data), C# (Web), Npgsql, xUnit, Testcontainers (Postgres), Bootstrap 5.

**Spec:** `docs/superpowers/specs/2026-07-19-resolve-stranded-runs-design.md`

## Global Constraints

- Branch `feat/resolve-stranded-runs` (already created off `origin/main`). Never commit to `main`.
- **ASCII only** in every authored file - no em dashes, en dashes, curly quotes, or ellipsis characters. Use a plain hyphen `-`. Windows PowerShell 5.1 mangles non-ASCII in BOM-less UTF-8.
- Platform is Windows/PowerShell. Use `$env:VAR` syntax.
- `Option Strict On` / `Option Infer On` for VB, `Nullable enable` - inherited from `Directory.Build.props`, do not restate per project.
- **No migration in this change.** `eligibility_run.status` is plain `text` with no CHECK constraint. Therefore **no** `docs/specs/database_schema.md` edit, and the version bump is **build only** (not MINOR).
- Verification is `dotnet test contexts/eligibility/Eligibility.sln`, never `dotnet build` alone.
- Existing files use `—` (em dash) in prose comments. Do not copy that style into new lines; new content is ASCII. Do not mass-rewrite existing lines either - leave untouched lines alone.

---

### Task 1: `RunStatus` constants and resolution validation

Introduces the Core vocabulary for run status plus the pure validator the controller will call. Nothing depends on this task's output yet, so it lands standalone and green.

**Files:**
- Create: `contexts/eligibility/src/EligibilityProcessing.Core/RunStatus.vb`
- Create: `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/RunStatusTests.vb`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/RunMetrics.vb:50` (stale comment)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/PipelineOrchestrator.vb` (lines 100, 204, 215, 606 - bare literals)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `RunStatus.Running` / `.Success` / `.Failed` / `.Cancelled` / `.Interrupted` - `Public Const String`
  - `RunStatus.ManualResolvable As IReadOnlyList(Of String)` - shared readonly property
  - `RunStatus.ValidateResolution(status As String, reason As String) As RunResolutionValidation`
  - `RunResolutionValidation` with `IsValid As Boolean`, `ErrorMessage As String`, `Status As String`, `Reason As String`

- [ ] **Step 1: Write the failing test**

Create `contexts/eligibility/tests/EligibilityProcessing.Core.Tests/RunStatusTests.vb`:

```vb
Imports System.Linq
Imports EligibilityProcessing.Core
Imports Xunit

' Unit tests for RunStatus: the run-level status vocabulary and the pure
' validator behind the dashboard's "Resolve" action. Validation lives in Core
' rather than the controller so it is testable without faking IPostgresGateway.

Public Class RunStatusTests

    <Fact>
    Public Sub Constants_have_their_persisted_values()
        Assert.Equal("running", RunStatus.Running)
        Assert.Equal("success", RunStatus.Success)
        Assert.Equal("failed", RunStatus.Failed)
        Assert.Equal("cancelled", RunStatus.Cancelled)
        Assert.Equal("interrupted", RunStatus.Interrupted)
    End Sub

    <Fact>
    Public Sub ManualResolvable_is_exactly_the_three_terminal_targets()
        Assert.Equal(New String() {"failed", "cancelled", "interrupted"},
                     RunStatus.ManualResolvable.ToArray())
    End Sub

    ' The load-bearing constraint: a completed run must never be rewritable
    ' through this path, and "running" is not a legal *target*.
    <Theory>
    <InlineData("running")>
    <InlineData("success")>
    Public Sub ValidateResolution_rejects_non_target_statuses(status As String)
        Dim result = RunStatus.ValidateResolution(status, "server rebooted")
        Assert.False(result.IsValid)
        Assert.Contains("status", result.ErrorMessage, StringComparison.OrdinalIgnoreCase)
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData(Nothing)>
    <InlineData("archived")>
    Public Sub ValidateResolution_rejects_unknown_status(status As String)
        Dim result = RunStatus.ValidateResolution(status, "server rebooted")
        Assert.False(result.IsValid)
    End Sub

    <Theory>
    <InlineData("FAILED")>
    <InlineData("  interrupted  ")>
    Public Sub ValidateResolution_normalizes_case_and_whitespace(status As String)
        Dim result = RunStatus.ValidateResolution(status, "server rebooted")
        Assert.True(result.IsValid)
        Assert.Equal(status.Trim().ToLowerInvariant(), result.Status)
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData(Nothing)>
    <InlineData("   ")>
    Public Sub ValidateResolution_requires_a_reason(reason As String)
        Dim result = RunStatus.ValidateResolution("failed", reason)
        Assert.False(result.IsValid)
        Assert.Contains("reason", result.ErrorMessage, StringComparison.OrdinalIgnoreCase)
    End Sub

    <Fact>
    Public Sub ValidateResolution_trims_the_reason()
        Dim result = RunStatus.ValidateResolution("failed", "  server rebooted  ")
        Assert.True(result.IsValid)
        Assert.Equal("server rebooted", result.Reason)
    End Sub

    ' error_summary is read in a table cell; an unbounded paste would wreck it.
    <Fact>
    Public Sub ValidateResolution_rejects_an_over_long_reason()
        Dim result = RunStatus.ValidateResolution("failed", New String("x"c, RunStatus.MaxReasonLength + 1))
        Assert.False(result.IsValid)
        Assert.Contains("500", result.ErrorMessage)
    End Sub

    <Fact>
    Public Sub ValidateResolution_accepts_a_reason_at_the_limit()
        Dim result = RunStatus.ValidateResolution("failed", New String("x"c, RunStatus.MaxReasonLength))
        Assert.True(result.IsValid)
    End Sub

    <Fact>
    Public Sub Valid_result_has_no_error_message()
        Dim result = RunStatus.ValidateResolution("interrupted", "server rebooted")
        Assert.True(result.IsValid)
        Assert.Equal("", result.ErrorMessage)
        Assert.Equal("interrupted", result.Status)
    End Sub

End Class
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~RunStatusTests"
```

Expected: BUILD FAILURE - `'RunStatus' is not declared`.

- [ ] **Step 3: Write the implementation**

Create `contexts/eligibility/src/EligibilityProcessing.Core/RunStatus.vb`:

```vb
' Status values for one row in public.eligibility_run - the per-batch record.
' Free text in the table (no CHECK constraint), enumerated here so callers stop
' scattering bare literals. These literals are PERSISTED: renaming one is a data
' migration.
'
'   running     - in flight; ended_at = Nothing
'   success     - the batch completed
'   failed      - the batch threw and did not complete
'   cancelled   - a user cancelled the batch mid-flight
'   interrupted - the host process stopped before the run reached a terminal
'                 status, leaving the row stranded at 'running'. NOT written by
'                 the pipeline. Unlike the per-study equivalent there is no
'                 automatic sweep: an operator resolves the row by hand from the
'                 Runs tab. See PostgresGateway.ResolveInterruptedRunAsync.

Imports System.Collections.Generic
Imports System.Linq

Public NotInheritable Class RunStatus

    Public Const Running As String = "running"
    Public Const Success As String = "success"
    Public Const Failed As String = "failed"
    Public Const Cancelled As String = "cancelled"
    Public Const Interrupted As String = "interrupted"

    ''' <summary>
    ''' Longest accepted manual reason. error_summary is rendered in a table
    ''' cell, so an unbounded paste would wreck the Runs tab.
    ''' </summary>
    Public Const MaxReasonLength As Integer = 500

    Private Shared ReadOnly _manualResolvable As String() = {Failed, Cancelled, Interrupted}

    ''' <summary>
    ''' The statuses an operator may manually resolve a stranded run TO.
    ''' Deliberately excludes Running (not a terminal state) and Success (a real
    ''' historical result that must never be manufactured by hand).
    ''' </summary>
    Public Shared ReadOnly Property ManualResolvable As IReadOnlyList(Of String)
        Get
            Return _manualResolvable
        End Get
    End Property

    ''' <summary>
    ''' Validates a manual resolution request. Pure - no I/O - so the controller
    ''' stays a thin shim and this logic is unit-testable without faking the
    ''' gateway. Trims and lower-cases the status, trims the reason.
    ''' </summary>
    Public Shared Function ValidateResolution(status As String, reason As String) As RunResolutionValidation
        Dim normalizedStatus = If(status, "").Trim().ToLowerInvariant()
        If Not _manualResolvable.Contains(normalizedStatus) Then
            Return RunResolutionValidation.Invalid(
                    $"status must be one of: {String.Join(", ", _manualResolvable)}")
        End If

        Dim normalizedReason = If(reason, "").Trim()
        If normalizedReason.Length = 0 Then
            Return RunResolutionValidation.Invalid("A reason is required.")
        End If
        If normalizedReason.Length > MaxReasonLength Then
            Return RunResolutionValidation.Invalid(
                    $"The reason must be {MaxReasonLength} characters or fewer.")
        End If

        Return RunResolutionValidation.Valid(normalizedStatus, normalizedReason)
    End Function

End Class

''' <summary>
''' Outcome of <see cref="RunStatus.ValidateResolution"/>. On success carries the
''' normalized status and reason ready to persist; on failure carries a message
''' safe to show the operator.
''' </summary>
Public NotInheritable Class RunResolutionValidation

    Private Sub New(isValid As Boolean, errorMessage As String, status As String, reason As String)
        Me.IsValid = isValid
        Me.ErrorMessage = errorMessage
        Me.Status = status
        Me.Reason = reason
    End Sub

    Public ReadOnly Property IsValid As Boolean
    Public ReadOnly Property ErrorMessage As String   ' "" when valid
    Public ReadOnly Property Status As String         ' canonical; "" when invalid
    Public ReadOnly Property Reason As String         ' trimmed; "" when invalid

    Friend Shared Function Valid(status As String, reason As String) As RunResolutionValidation
        Return New RunResolutionValidation(True, "", status, reason)
    End Function

    Friend Shared Function Invalid(errorMessage As String) As RunResolutionValidation
        Return New RunResolutionValidation(False, errorMessage, "", "")
    End Function

End Class
```

- [ ] **Step 4: Run the test to verify it passes**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Core.Tests --filter "FullyQualifiedName~RunStatusTests"
```

Expected: PASS, 11 tests.

- [ ] **Step 5: Replace the bare literals in the orchestrator**

In `contexts/eligibility/src/EligibilityProcessing.Core/PipelineOrchestrator.vb`, replace the string literal with the constant on each of these lines. Do not change surrounding logic.

- Line 100: `status:="running"` becomes `status:=RunStatus.Running`
- Line 204: `"cancelled"` becomes `RunStatus.Cancelled`
- Line 215: the `"success"` / `"failed"` pair becomes `RunStatus.Success` / `RunStatus.Failed`
- Line 606: `status:="running"` becomes `status:=RunStatus.Running`

`PipelineOrchestrator` is already in the `EligibilityProcessing.Core` namespace, so no import is needed.

- [ ] **Step 6: Fix the stale comment on RunMetrics**

In `contexts/eligibility/src/EligibilityProcessing.Core/RunMetrics.vb`, line 50 currently reads:

```vb
    Public ReadOnly Property Status As String            ' "success" | "failed" | "cancelled"
```

It omits `running`, which the orchestrator does write. Replace with:

```vb
    ' See RunStatus for the vocabulary. "running" is written first (in-flight,
    ' EndedAt = Nothing) and overwritten on completion; "interrupted" is only
    ' ever written by a manual resolve from the Runs tab.
    Public ReadOnly Property Status As String            ' running | success | failed | cancelled | interrupted
```

- [ ] **Step 7: Run the full suite to confirm nothing regressed**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS. The orchestrator tests in `PipelineOrchestratorTests.vb` assert on the same literal values, so they must stay green unchanged - if any fail, a constant was mistyped.

- [ ] **Step 8: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/RunStatus.vb contexts/eligibility/tests/EligibilityProcessing.Core.Tests/RunStatusTests.vb contexts/eligibility/src/EligibilityProcessing.Core/RunMetrics.vb contexts/eligibility/src/EligibilityProcessing.Core/PipelineOrchestrator.vb
git commit -m "Add RunStatus constants and manual-resolution validation"
```

---

### Task 2: `ResolveInterruptedRunAsync` gateway method

The persistence half. Two guarded UPDATEs in one transaction.

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Core/IPostgresGateway.vb` (add after `RecordRunAsync`, ~line 255)
- Modify: `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb` (add after `RecordRunAsync`, ~line 981)
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb` (append to the `RecordRunAsync` section, ~line 1645)

**Interfaces:**
- Consumes: `RunStatus.*` from Task 1; `StudyExecution.StatusInterrupted` (already exists).
- Produces: `IPostgresGateway.ResolveInterruptedRunAsync(runId As Guid, status As String, reason As String, cancellationToken As CancellationToken) As Task(Of (RunUpdated As Boolean, StudiesReconciled As Integer))`

- [ ] **Step 1: Write the failing tests**

Append to `contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb`, immediately before the `' ============ GetRecentRunsAsync ============` header at line 1647:

```vb
    ' ============ ResolveInterruptedRunAsync (manual resolve of a stranded run) ============
    '
    ' An unclean host shutdown leaves eligibility_run at 'running' forever, which
    ' disables the dashboard trigger buttons. There is no automatic sweep for runs
    ' (unlike study rows), so an operator resolves the row by hand. The
    ' "already terminal" test below is the load-bearing one: it is the guard that
    ' stops a stale page rewriting a run that finished in the meantime.

    Private Async Function SeedRunAsync(runId As Guid, status As String) As Task
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(runId, DateTimeOffset.UtcNow, Nothing, "form", 50, 0, 0, 0, status, ""),
                CancellationToken.None)
    End Function

    Private Async Function GetRunStatusAsync(runId As Guid) As Task(Of (Status As String, ErrorSummary As String, HasEnded As Boolean))
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT status, COALESCE(error_summary, ''), ended_at IS NOT NULL
                                     FROM public.eligibility_run WHERE run_id = @id"
                cmd.Parameters.AddWithValue("id", runId)
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Return (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))
                End Using
            End Using
        End Using
    End Function

    Private Async Function GetStudyStatusAsync(nctId As String) As Task(Of (Status As String, ErrorMessage As String))
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT status, COALESCE(error_message, '')
                                     FROM public.eligibility_study WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Using reader = Await cmd.ExecuteReaderAsync()
                    Await reader.ReadAsync()
                    Return (reader.GetString(0), reader.GetString(1))
                End Using
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function ResolveInterruptedRun_sets_status_reason_and_ended_at() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Await SeedRunAsync(runId, RunStatus.Running)

        Dim result = Await _fixture.Gateway.ResolveInterruptedRunAsync(
                runId, RunStatus.Interrupted, "server rebooted", CancellationToken.None)

        Assert.True(result.RunUpdated)
        Dim row = Await GetRunStatusAsync(runId)
        Assert.Equal(RunStatus.Interrupted, row.Status)
        Assert.Equal("server rebooted", row.ErrorSummary)
        Assert.True(row.HasEnded)   ' a stranded row has no ended_at; we stamp one
    End Function

    ' THE guard. If the run genuinely completed between the page rendering and the
    ' operator submitting, this must be a no-op rather than overwriting a real result.
    <SkippableFact>
    Public Async Function ResolveInterruptedRun_is_a_no_op_when_the_run_already_finished() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Await _fixture.Gateway.RecordRunAsync(
                New RunMetrics(runId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "form",
                               50, 50, 374, 0.882, RunStatus.Success, ""),
                CancellationToken.None)

        Dim result = Await _fixture.Gateway.ResolveInterruptedRunAsync(
                runId, RunStatus.Failed, "should not apply", CancellationToken.None)

        Assert.False(result.RunUpdated)
        Dim row = Await GetRunStatusAsync(runId)
        Assert.Equal(RunStatus.Success, row.Status)
        Assert.Equal("", row.ErrorSummary)
    End Function

    <SkippableFact>
    Public Async Function ResolveInterruptedRun_returns_false_for_an_unknown_run() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim result = Await _fixture.Gateway.ResolveInterruptedRunAsync(
                Guid.NewGuid(), RunStatus.Failed, "nobody home", CancellationToken.None)

        Assert.False(result.RunUpdated)
        Assert.Equal(0, result.StudiesReconciled)
    End Function

    <SkippableFact>
    Public Async Function ResolveInterruptedRun_cascades_to_stranded_study_rows() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Await SeedRunAsync(runId, RunStatus.Running)
        ' Two rows left at 'running' by the same dead host, no age threshold applied.
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000001", DateTimeOffset.UtcNow, CancellationToken.None)
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000002", DateTimeOffset.UtcNow, CancellationToken.None)

        Dim result = Await _fixture.Gateway.ResolveInterruptedRunAsync(
                runId, RunStatus.Interrupted, "server rebooted", CancellationToken.None)

        Assert.Equal(2, result.StudiesReconciled)
        Dim study = Await GetStudyStatusAsync("NCT00000001")
        Assert.Equal(StudyExecution.StatusInterrupted, study.Status)
        Assert.Equal("server rebooted", study.ErrorMessage)
    End Function

    <SkippableFact>
    Public Async Function ResolveInterruptedRun_leaves_other_runs_study_rows_alone() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim deadRunId = Guid.NewGuid()
        Dim liveRunId = Guid.NewGuid()
        Await SeedRunAsync(deadRunId, RunStatus.Running)
        Await SeedRunAsync(liveRunId, RunStatus.Running)
        Await _fixture.Gateway.StartStudyAsync(deadRunId, "NCT00000001", DateTimeOffset.UtcNow, CancellationToken.None)
        Await _fixture.Gateway.StartStudyAsync(liveRunId, "NCT00000002", DateTimeOffset.UtcNow, CancellationToken.None)

        Dim result = Await _fixture.Gateway.ResolveInterruptedRunAsync(
                deadRunId, RunStatus.Interrupted, "server rebooted", CancellationToken.None)

        Assert.Equal(1, result.StudiesReconciled)
        Dim untouched = Await GetStudyStatusAsync("NCT00000002")
        Assert.Equal(StudyExecution.StatusRunning, untouched.Status)
        Dim liveRun = Await GetRunStatusAsync(liveRunId)
        Assert.Equal(RunStatus.Running, liveRun.Status)
    End Function

    ' A study row that recorded its own failure knows more than the blanket run
    ' reason does, so the existing message wins.
    <SkippableFact>
    Public Async Function ResolveInterruptedRun_preserves_an_existing_study_error_message() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim runId = Guid.NewGuid()
        Await SeedRunAsync(runId, RunStatus.Running)
        Await _fixture.Gateway.StartStudyAsync(runId, "NCT00000001", DateTimeOffset.UtcNow, CancellationToken.None)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "UPDATE public.eligibility_study SET error_message = 'llm timeout' WHERE nct_id = 'NCT00000001'"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using

        Await _fixture.Gateway.ResolveInterruptedRunAsync(
                runId, RunStatus.Interrupted, "server rebooted", CancellationToken.None)

        Dim study = Await GetStudyStatusAsync("NCT00000001")
        Assert.Equal("llm timeout", study.ErrorMessage)
    End Function
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~ResolveInterruptedRun"
```

Expected: BUILD FAILURE - `'ResolveInterruptedRunAsync' is not a member of ...`.

Note: if Docker is unavailable these will report Skipped once they compile, not Passed. That is expected and acceptable locally; they run in CI. Confirm they at least **compile and skip** rather than fail.

- [ ] **Step 3: Declare the method on the interface**

In `contexts/eligibility/src/EligibilityProcessing.Core/IPostgresGateway.vb`, insert immediately after the `RecordRunAsync` declaration (which ends at line 255, before the `RecordFailedTrialAsync` doc comment):

```vb
    ''' <summary>
    ''' Manually resolves a run stranded at 'running' by an unclean host shutdown,
    ''' setting its status and error_summary and cascading its still-'running'
    ''' study rows to 'interrupted'. Both statements run in one transaction.
    ''' </summary>
    ''' <remarks>
    ''' Guarded on <c>status = 'running'</c>: if the run genuinely completed
    ''' between the operator loading the page and submitting, the update matches
    ''' nothing, the transaction commits as a no-op, and RunUpdated is False. This
    ''' is what makes the action safe against a second host sharing the database.
    '''
    ''' The study cascade applies NO age threshold, unlike
    ''' ReconcileInterruptedStudiesAsync: that threshold exists to guess that a run
    ''' is dead, and here an operator has asserted it directly for this one run.
    ''' </remarks>
    ''' <returns>
    ''' RunUpdated: False when the run does not exist or already reached a terminal
    ''' status. StudiesReconciled: how many study rows the cascade moved.
    ''' </returns>
    Function ResolveInterruptedRunAsync(
            runId As Guid,
            status As String,
            reason As String,
            cancellationToken As CancellationToken) As Task(Of (RunUpdated As Boolean, StudiesReconciled As Integer))
```

- [ ] **Step 4: Implement it on the gateway**

In `contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb`, insert after `RecordRunAsync` ends (line 981) and before the `' ============ RecordFailedTrialAsync ...` header:

```vb
    ' ============ ResolveInterruptedRunAsync (output DB, guarded, transactional) ============

    Public Async Function ResolveInterruptedRunAsync(
            runId As Guid,
            status As String,
            reason As String,
            cancellationToken As CancellationToken) As Task(Of (RunUpdated As Boolean, StudiesReconciled As Integer)) _
            Implements IPostgresGateway.ResolveInterruptedRunAsync
        If String.IsNullOrWhiteSpace(status) Then
            Throw New ArgumentException("status must be non-empty", NameOf(status))
        End If
        If String.IsNullOrWhiteSpace(reason) Then
            Throw New ArgumentException("reason must be non-empty", NameOf(reason))
        End If

        ' AND status = 'running' is the concurrency guard - see the interface
        ' remarks. COALESCE on ended_at because a stranded row always has NULL
        ' there; the coalesce stops this inventing a second ending if it ever
        ' does not.
        Const RunSql As String = "
UPDATE public.eligibility_run
   SET status        = @status,
       error_summary = @reason,
       ended_at      = COALESCE(ended_at, @now)
 WHERE run_id = @run_id
   AND status = 'running'"

        ' Scoped to the one run_id and NOT age-filtered. The existing
        ' error_message wins when present: a row that recorded its own failure
        ' knows more than the blanket run reason does.
        Const StudySql As String = "
UPDATE public.eligibility_study
   SET status        = 'interrupted',
       finished_at   = @now,
       error_message = COALESCE(NULLIF(error_message, ''), @reason)
 WHERE run_id = @run_id
   AND status = 'running'"

        Dim now = DateTimeOffset.UtcNow

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                Dim runUpdated As Integer
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = RunSql
                    cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = runId})
                    cmd.Parameters.Add(New NpgsqlParameter("status", NpgsqlDbType.Text) With {.Value = status})
                    cmd.Parameters.Add(New NpgsqlParameter("reason", NpgsqlDbType.Text) With {.Value = reason})
                    cmd.Parameters.Add(New NpgsqlParameter("now", NpgsqlDbType.TimestampTz) With {.Value = now})
                    runUpdated = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                ' No run was resolved, so there is nothing to cascade to. Commit
                ' the empty transaction and report the no-op.
                If runUpdated = 0 Then
                    Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
                    Return (False, 0)
                End If

                Dim studiesReconciled As Integer
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = StudySql
                    cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = runId})
                    cmd.Parameters.Add(New NpgsqlParameter("reason", NpgsqlDbType.Text) With {.Value = reason})
                    cmd.Parameters.Add(New NpgsqlParameter("now", NpgsqlDbType.TimestampTz) With {.Value = now})
                    studiesReconciled = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
                _logger.LogInformation(
                        "Run {RunId} manually resolved to '{Status}'; {Count} stranded study row(s) reconciled to 'interrupted'.",
                        runId, status, studiesReconciled)
                Return (True, studiesReconciled)
            End Using
        End Using
    End Function
```

The `'interrupted'` and `'running'` literals inside the SQL stay literal - the Data project's other SQL does the same (see `ReconcileInterruptedStudiesAsync` at line 2135), and a parameterized status column value would defeat index use on the guard.

- [ ] **Step 5: Run the tests to verify they pass**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Data.Tests --filter "FullyQualifiedName~ResolveInterruptedRun"
```

Expected: 6 tests PASS with Docker available, or 6 Skipped without it. If Skipped, note it - the task is not fully verified until CI runs them.

- [ ] **Step 6: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS. Any other `IPostgresGateway` implementer (test fakes in `TestFakes.vb`) must also implement the new member - if the build breaks there, add the member to the fake returning `(False, 0)`.

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Core/IPostgresGateway.vb contexts/eligibility/src/EligibilityProcessing.Data/PostgresGateway.vb contexts/eligibility/tests/EligibilityProcessing.Data.Tests/PostgresGatewayIntegrationTests.vb
git commit -m "Add ResolveInterruptedRunAsync for manually resolving stranded runs"
```

---

### Task 3: `ResolveRun` controller action

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/HomeController.cs` (add after `RemoveSuperseded`, which ends line 569)
- Modify: `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/AuthTests.cs` (append role-gating tests before the `AnonFactory` class at line 185)

**Interfaces:**
- Consumes: `RunStatus.ValidateResolution` (Task 1), `IPostgresGateway.ResolveInterruptedRunAsync` (Task 2), existing `RunGate`, existing `IAuditWriter`.
- Produces: `POST /Home/ResolveRun` taking form fields `runId` (Guid), `status` (string), `reason` (string). Returns `Json(new { ok, runUpdated, studiesReconciled, message })` on 200; 400 on validation failure; 409 when the gate holds that run id.

- [ ] **Step 1: Write the failing tests**

Append to `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/AuthTests.cs`, immediately before `private class AnonFactory` at line 185:

```csharp
    // The Resolve action edits run history, so it sits behind the same
    // PipelineOps policy as every other run control. Authorization runs before
    // the antiforgery filter, so a forbidden role gets 403 without a token.
    [Theory]
    [InlineData("Author")]
    [InlineData("Viewer")]
    public async Task Resolve_run_requires_pipeline_ops(string role)
    {
        using var factory = new AuthedFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/Home/ResolveRun");
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

**Known test gap - read before proceeding.** The spec listed four more integration
tests (invalid status, empty reason, missing run id, and the 409 when the gate
holds that run id). All four sit *behind* the antiforgery filter, and this repo
has no mocking library and no fake `IPostgresGateway` in `Integration.Tests`, so
driving them over HTTP would mean hand-rolling a fake for a ~90-member interface
or fetching a real token first. The validation cases are covered instead as pure
unit tests in Task 1 (`RunStatusTests`), which is the better home for them. That
leaves exactly one branch untested by automation: the `gate.CurrentRunId == runId`
409. It is three lines with no logic, and Task 4 Step 6 exercises the surrounding
flow by hand. Do not silently skip this - if you find a cheap way to cover it,
add it; otherwise note it in the PR body.

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Integration.Tests --filter "FullyQualifiedName~Resolve_run_requires_pipeline_ops"
```

Expected: FAIL - `Assert.Equal() Failure: Expected Forbidden, Actual NotFound` (the route does not exist yet).

- [ ] **Step 3: Implement the action**

In `contexts/eligibility/src/EligibilityProcessing.Web/Controllers/HomeController.cs`, insert after `RemoveSuperseded` ends (line 569), before `RunNormalizeUmls`:

```csharp
    /// <summary>
    /// Manually resolves a run stranded at 'running' by an unclean host shutdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: neither exclusivity gate reads eligibility_run.status
    /// (RunGate is in-process, PostgresRunLock is a session advisory lock), so a
    /// stranded row blocks nothing server-side - but _DashboardToolbar disables the
    /// trigger buttons while the most recent run reads as "running", so the
    /// operator cannot start a batch until the row is corrected.
    /// </para>
    /// <para>
    /// Restricted to runs at 'running' by the gateway's SQL guard, so a completed
    /// run's history can never be rewritten through this path. Validation lives in
    /// Core (RunStatus.ValidateResolution) rather than here, which keeps it
    /// unit-testable without faking the gateway.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "PipelineOps")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveRun(
        [FromServices] RunGate gate,
        Guid runId,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            return BadRequest(new { error = "A run id is required." });
        }

        // If THIS host is actively running that run it is by definition not
        // stranded, and the operator has hit the button in error. The SQL guard
        // alone would let this through, since a live run is legitimately
        // 'running'. Deliberately does not take the gate - this is a targeted
        // row correction, not a job, and blocking on an unrelated tool job would
        // just make an incident harder to clear.
        if (gate.CurrentRunId == runId)
        {
            return Conflict(new
            {
                error = "That run is currently in flight on this host. Cancel it instead.",
                current_run_id = gate.CurrentRunId,
                activity = gate.CurrentActivity
            });
        }

        var validation = RunStatus.ValidateResolution(status, reason);
        if (!validation.IsValid)
        {
            return BadRequest(new { error = validation.ErrorMessage });
        }

        try
        {
            var (runUpdated, studiesReconciled) = await _gateway.ResolveInterruptedRunAsync(
                runId, validation.Status, validation.Reason, cancellationToken);

            if (!runUpdated)
            {
                // Not an error: the run finished on its own between the page
                // rendering and this submit, which is exactly what the guard is
                // for. Say so plainly rather than 500ing or claiming success.
                return Json(new
                {
                    ok = true,
                    runUpdated = false,
                    studiesReconciled = 0,
                    message = "That run is no longer stranded - it has already reached a terminal status. Nothing was changed."
                });
            }

            await _audit.WriteAsync("update", "eligibility_run", runId.ToString(),
                $"manually resolved to '{validation.Status}': {validation.Reason}",
                HttpContext.RequestAborted);

            return Json(new
            {
                ok = true,
                runUpdated = true,
                studiesReconciled,
                message = $"Run resolved to '{validation.Status}'."
                          + (studiesReconciled > 0
                              ? $" {studiesReconciled} stranded study row(s) marked interrupted."
                              : string.Empty)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve run {RunId}", runId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }
```

`RunStatus` is in `EligibilityProcessing.Core`, already imported at the top of the file (line 3).

- [ ] **Step 4: Run the test to verify it passes**

```powershell
dotnet test contexts/eligibility/tests/EligibilityProcessing.Integration.Tests --filter "FullyQualifiedName~Resolve_run_requires_pipeline_ops"
```

Expected: PASS, 2 tests.

- [ ] **Step 5: Run the full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/Controllers/HomeController.cs contexts/eligibility/tests/EligibilityProcessing.Integration.Tests/AuthTests.cs
git commit -m "Add ResolveRun action for manually resolving stranded runs"
```

---

### Task 4: Runs list UI

**Files:**
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Runs.cshtml`
- Modify: `contexts/eligibility/src/EligibilityProcessing.Web/Models/RunCardView.cs:106` (status badge switch)

**Interfaces:**
- Consumes: `POST /Home/ResolveRun` (Task 3), `RunStatus` constants (Task 1).
- Produces: no code interface; UI only.

- [ ] **Step 1: Add the `interrupted` badge to both status switches**

In `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Runs.cshtml`, the switch at lines 72-79 becomes:

```csharp
                    var statusBadge = run.Status switch
                    {
                        "success" => "bg-success",
                        "failed" => "bg-danger",
                        "running" => "bg-info",
                        "cancelled" => "bg-warning text-dark",
                        "interrupted" => "bg-warning text-dark",
                        _ => "bg-secondary"
                    };
```

In `contexts/eligibility/src/EligibilityProcessing.Web/Models/RunCardView.cs`, add the same `"interrupted" => "bg-warning text-dark",` arm to the switch at line 106, above the `_ =>` default.

- [ ] **Step 2: Add the authorization check and the actions column**

At the top of `Runs.cshtml`, inside the existing `@{ ... }` block that ends at line 29, append:

```csharp
    // Cosmetic gate only - HomeController.ResolveRun re-checks the policy. Matches
    // History.cshtml:4.
    var canRunPipeline = (await AuthorizationService.AuthorizeAsync(User, "PipelineOps")).Succeeded;
```

Add a header cell after the `Run ID` header (line 66):

```html
                    @if (canRunPipeline)
                    {
                        <th></th>
                    }
```

Add the matching row cell after the Run ID cell (closing `</td>` at line 135):

```html
                        @if (canRunPipeline)
                        {
                            <td>
                                @if (run.Status == "running")
                                {
                                    <button type="button" class="btn btn-link btn-sm p-0 text-decoration-none"
                                            data-resolve-run="@run.RunId"
                                            data-resolve-started="@run.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")"
                                            title="Mark this run as no longer in flight (it was stranded by a host restart)">Resolve</button>
                                }
                            </td>
                        }
```

- [ ] **Step 3: Add the modal**

Insert immediately before the `@section Scripts {` line (line 164), inside the file but outside the `else` block:

```html
@if (canRunPipeline)
{
    <div class="modal fade" id="resolveRunModal" tabindex="-1" aria-hidden="true">
        <div class="modal-dialog modal-dialog-centered">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">Resolve stranded run</h5>
                    <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
                </div>
                <div class="modal-body">
                    @* Anti-forgery token for the fetch below. The form tag helper
                       only injects one into a real <form>, and this modal posts
                       via fetch - same approach as _CreateSeedModal. *@
                    <form id="rr-token-form" method="post"></form>

                    <p class="text-muted small">
                        Only a run still showing as <em>running</em> can be resolved. This records why the run
                        ended and marks any of its trials still stranded at <em>running</em> as
                        <em>interrupted</em>. It does not re-run anything.
                    </p>
                    <p class="small mb-3">Run started <span class="font-monospace" id="rr-started"></span></p>

                    <div class="mb-3">
                        <label for="rr-status" class="form-label">Mark the run as</label>
                        <select class="form-select form-select-sm" id="rr-status">
                            <option value="interrupted" selected>interrupted - the host stopped mid-run</option>
                            <option value="failed">failed - the run errored</option>
                            <option value="cancelled">cancelled - it was stopped deliberately</option>
                        </select>
                    </div>

                    <div class="mb-2">
                        <label for="rr-reason" class="form-label">Reason (required)</label>
                        <textarea class="form-control form-control-sm" id="rr-reason" rows="3"
                                  maxlength="500" placeholder="e.g. unscheduled server reboot on 2026-07-18"></textarea>
                        <div class="form-text">Stored as the run's error summary and shown on this page.</div>
                    </div>

                    <div id="rr-error" class="alert alert-danger py-2 mb-0 d-none"></div>
                </div>
                <div class="modal-footer">
                    <button type="button" class="btn btn-secondary btn-sm" data-bs-dismiss="modal">Cancel</button>
                    <button type="button" class="btn btn-primary btn-sm" id="rr-submit" disabled>Resolve run</button>
                </div>
            </div>
        </div>
    </div>
}
```

Note: the empty `<form id="rr-token-form" method="post"></form>` is what makes ASP.NET Core emit a `__RequestVerificationToken` hidden input on this page.

- [ ] **Step 4: Wire the modal**

Append inside the existing `@section Scripts { ... }` block in `Runs.cshtml`, after the closing `})();` of the ticker IIFE (line 212) and before the `</script>` tag:

```javascript
        // Resolve-stranded-run modal. Posts to /Home/ResolveRun and reloads on
        // success so the dashboard toolbar's runInProgress check re-evaluates and
        // the trigger buttons come back.
        (function () {
            const modalEl = document.getElementById("resolveRunModal");
            if (!modalEl || typeof bootstrap === "undefined") return;
            const modal = new bootstrap.Modal(modalEl);

            const startedEl = document.getElementById("rr-started");
            const statusEl = document.getElementById("rr-status");
            const reasonEl = document.getElementById("rr-reason");
            const errorEl = document.getElementById("rr-error");
            const submitEl = document.getElementById("rr-submit");
            let runId = null;

            function token() {
                const el = document.querySelector('#rr-token-form input[name="__RequestVerificationToken"]');
                return el ? el.value : "";
            }
            function showError(message) {
                errorEl.textContent = message;
                errorEl.classList.remove("d-none");
            }

            document.querySelectorAll("button[data-resolve-run]").forEach(function (btn) {
                btn.addEventListener("click", function () {
                    runId = btn.dataset.resolveRun;
                    startedEl.textContent = btn.dataset.resolveStarted || "";
                    reasonEl.value = "";
                    statusEl.value = "interrupted";
                    errorEl.classList.add("d-none");
                    submitEl.disabled = true;
                    submitEl.textContent = "Resolve run";
                    modal.show();
                });
            });

            reasonEl.addEventListener("input", function () {
                submitEl.disabled = reasonEl.value.trim().length === 0;
            });

            submitEl.addEventListener("click", async function () {
                if (!runId) return;
                submitEl.disabled = true;
                submitEl.textContent = "Resolving...";
                errorEl.classList.add("d-none");

                const body = new FormData();
                body.set("runId", runId);
                body.set("status", statusEl.value);
                body.set("reason", reasonEl.value.trim());
                body.set("__RequestVerificationToken", token());

                try {
                    const response = await fetch("@Url.Action("ResolveRun", "Home")", {
                        method: "POST",
                        body: body,
                        headers: { "Accept": "application/json", "X-Requested-With": "XMLHttpRequest" }
                    });
                    const payload = await response.json().catch(() => ({}));
                    if (!response.ok) {
                        showError(payload.error || ("Request failed (" + response.status + ")."));
                        submitEl.disabled = false;
                        submitEl.textContent = "Resolve run";
                        return;
                    }
                    // Reload either way: on a real resolve to pick up the new
                    // status, and on the already-finished no-op because the page
                    // is by definition stale.
                    window.location.reload();
                } catch (err) {
                    showError("Could not reach the server.");
                    submitEl.disabled = false;
                    submitEl.textContent = "Resolve run";
                }
            });
        })();
```

- [ ] **Step 5: Verify the build and full suite**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS. Razor compile errors surface here - `AuthorizationService` must be available in the view (it is injected globally via `_ViewImports.cshtml`; if the build complains, confirm against `History.cshtml:4` which uses it the same way).

- [ ] **Step 6: Manual verification**

Run the dashboard and confirm the flow end to end:

```powershell
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web
```

Check, on `/Home/Runs`:
1. A run at `running` shows a "Resolve" link; runs at other statuses show nothing.
2. The submit button stays disabled until a reason is typed.
3. Resolving sets the status badge and the trigger buttons on `/` re-enable.
4. Signed in as a Viewer, the column is absent.

- [ ] **Step 7: Commit**

```powershell
git add contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Runs.cshtml contexts/eligibility/src/EligibilityProcessing.Web/Models/RunCardView.cs
git commit -m "Add Resolve action to the Runs list for stranded runs"
```

---

### Task 5: Version bump and PR

**Files:**
- Modify: `contexts/eligibility/version.json`

- [ ] **Step 1: Bump the version**

Build-only bump (no migration in this change). Set `current` to:

```json
  "current": { "major": 0, "minor": 1, "build": 33, "releaseDate": "2026-07-19" },
```

and prepend to `releases`:

```json
    {
      "version": "0.1.33",
      "releaseDate": "2026-07-19",
      "enhancements": [
        "Runs tab: a run left showing as 'running' by a server restart can now be resolved by hand - pick failed, cancelled or interrupted, give a reason, and any of its trials still stranded at 'running' are marked interrupted too. This clears the stale run that was keeping the dashboard's trigger buttons disabled."
      ],
      "fixes": []
    },
```

Keep the file ASCII-only. `releases[0]` must match `current`.

- [ ] **Step 2: Verify the full suite one final time**

```powershell
dotnet test contexts/eligibility/Eligibility.sln
```

Expected: PASS, with the run count up by roughly 19 from baseline (11 Core + 6 Data + 2 Integration).

- [ ] **Step 3: Commit and open the PR**

```powershell
git add contexts/eligibility/version.json
git commit -m "Bump version to 0.1.33"
git push -u origin feat/resolve-stranded-runs
```

Then open the PR with `gh pr create`, titled "Resolve stranded runs from the Runs list", body describing the problem (unclean shutdown strands `eligibility_run` at `running`; the dashboard toolbar disables triggering on that basis), the fix, and the deferred follow-up (no automatic sweep - stranded rows still accumulate).

---

## Deferred (not in this plan)

The spec records these deliberately out of scope: an automatic startup sweep for run rows (rejected - a second host pointed at the same database would interrupt live pipelines), and a heartbeat column allowing staleness to be inferred rather than guessed. Worth a follow-up issue after this merges.

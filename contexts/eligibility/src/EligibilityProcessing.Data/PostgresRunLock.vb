Imports System.Runtime.ExceptionServices
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports Npgsql
Imports NpgsqlTypes

' Cross-process mutual exclusion for pipeline batches, using a Postgres SESSION-level
' advisory lock on the output database.
'
' WHY THIS EXISTS: RunGate (Web) is an in-process CLR lock. It correctly serialises
' the web host's own runs and tools, but it is invisible to any other process. The CLI
' (`elig run`) drives the same orchestrator against the same database and takes no lock
' at all, so today a CLI batch and a web batch can run simultaneously: both anti-join
' the attempted set independently, both can select the same trials, and both compete
' for the same model-server slots. That is not corruption - the per-trial DELETE+INSERT
' is idempotent and the (run_id, nct_id) key keeps the audit rows distinct - but it
' wastes the one genuinely scarce resource (LLM time) and oversubscribes the slots.
'
' This lock is deliberately NOT a replacement for RunGate. It sits at the two places
' real work starts (BatchRunner and the CLI's run command), so the fast in-process 409
' the dashboard depends on is unchanged, and the web host's startup path never touches
' the database on a request thread.
'
' SESSION-level, not transaction-level: a batch runs for hours, far outside any
' transaction. That means the lock lives on a dedicated connection held open for the
' whole run. Two consequences worth knowing:
'
'   1. Process death releases it automatically. The session ends, Postgres drops the
'      lock, and the next run can start. There is nothing to wedge and nothing to
'      reconcile - unlike the 'running' rows the same crash leaves behind (see
'      PostgresGateway.ReconcileInterruptedStudiesAsync).
'
'   2. A dropped connection ALSO releases it silently. The lock connection is idle for
'      the whole run, so an aggressive firewall/NAT idle timeout could cut it and let a
'      second process start. Set `Keepalive=60` in the output connection string if that
'      is a real risk on your network. The failure mode is bounded: a silently released
'      lock only matters if someone starts a run inside that window, and the result is
'      exactly today's unguarded behaviour - so this is strictly an improvement, never
'      a regression.
Public NotInheritable Class PostgresRunLock
    Implements IAsyncDisposable
    ' IDisposable as well as IAsyncDisposable, and NOT optional: this type is registered
    ' Scoped, and both hosts dispose their run scope synchronously (VB.NET has no
    ' `Await Using`, so the CLI cannot use CreateAsyncScope). An IAsyncDisposable-only
    ' service in a sync-disposed scope makes the container throw
    ' "type only implements IAsyncDisposable. Use DisposeAsync to dispose the container."
    ' at scope teardown - which it did, until this existed.
    Implements IDisposable

    ' Arbitrary but STABLE application-wide key. Any process wanting to run a batch
    ' against this database must use this exact number, so it must never change - a
    ' new value would silently stop excluding the old one. Not derived from hashtext():
    ' that function is documented as an internal detail with no cross-version stability
    ' guarantee, and a key that shifts under a major upgrade would fail open.
    Friend Const RunLockKey As Long = 4242000001L

    Private ReadOnly _dataSource As NpgsqlDataSource
    Private ReadOnly _logger As ILogger(Of PostgresRunLock)

    ' Non-Nothing only while the lock is held. The session owning this connection owns
    ' the lock, so it must stay open for the run's duration and must not be returned to
    ' the pool early: Npgsql sends DISCARD ALL on return, which releases session
    ' advisory locks.
    Private _connection As NpgsqlConnection

    Public Sub New(outputDataSource As NpgsqlDataSource,
                   Optional logger As ILogger(Of PostgresRunLock) = Nothing)
        If outputDataSource Is Nothing Then Throw New ArgumentNullException(NameOf(outputDataSource))
        _dataSource = outputDataSource
        _logger = If(logger, CType(NullLogger(Of PostgresRunLock).Instance, ILogger(Of PostgresRunLock)))
    End Sub

    Public ReadOnly Property IsHeld As Boolean
        Get
            Return _connection IsNot Nothing
        End Get
    End Property

    ''' <summary>
    ''' Tries to take the batch lock. Returns True when acquired, False when another
    ''' process (a CLI run, or another host) already holds it. Never blocks - callers
    ''' report the clash rather than queueing behind it, matching the 409 the dashboard
    ''' already returns for an in-process clash.
    ''' <para>
    ''' Throws if the database is unreachable. Callers must treat that as a failure to
    ''' start rather than as "nobody holds it": failing open here would reintroduce the
    ''' exact race this class removes.
    ''' </para>
    ''' </summary>
    Public Async Function TryAcquireAsync(cancellationToken As CancellationToken) As Task(Of Boolean)
        If _connection IsNot Nothing Then
            Throw New InvalidOperationException("The run lock is already held by this instance.")
        End If

        Dim conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)

        ' VB.NET cannot Await inside Catch/Finally, so capture the failure and do the
        ' connection cleanup below, outside the Try.
        Dim acquired As Boolean = False
        Dim failure As ExceptionDispatchInfo = Nothing
        Try
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT pg_try_advisory_lock(@key)"
                cmd.Parameters.Add(New NpgsqlParameter("key", NpgsqlDbType.Bigint) With {.Value = RunLockKey})
                acquired = CBool(Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False))
            End Using
        Catch ex As Exception
            failure = ExceptionDispatchInfo.Capture(ex)
        End Try

        If failure IsNot Nothing Then
            Await conn.DisposeAsync().ConfigureAwait(False)
            failure.Throw()
        End If

        If Not acquired Then
            ' Someone else is running. Hand the connection straight back - holding it
            ' would pin a pool slot for nothing.
            Await conn.DisposeAsync().ConfigureAwait(False)
            _logger.LogInformation("Batch run lock is held by another process; not starting.")
            Return False
        End If

        ' Only now take ownership: every path above either handed the connection back or
        ' rethrew, so _connection is set if and only if the lock is genuinely held.
        _connection = conn
        _logger.LogDebug("Acquired the batch run lock.")
        Return True
    End Function

    ''' <summary>
    ''' Releases the lock and returns the connection to the pool. Safe to call when the
    ''' lock is not held (no-op) and safe to call twice.
    ''' </summary>
    Public Async Function ReleaseAsync() As Task
        Dim conn = _connection
        If conn Is Nothing Then Return
        _connection = Nothing

        ' VB.NET cannot Await inside Catch/Finally, so capture and log below.
        Dim unlockError As Exception = Nothing
        Try
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT pg_advisory_unlock(@key)"
                cmd.Parameters.Add(New NpgsqlParameter("key", NpgsqlDbType.Bigint) With {.Value = RunLockKey})
                Await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(False)
            End Using
        Catch ex As Exception
            unlockError = ex
        End Try

        If unlockError IsNot Nothing Then
            ' Non-fatal by construction: disposing the connection ends the session, and
            ' Postgres drops every session advisory lock with it. The explicit unlock is
            ' just tidier (it returns a clean connection to the pool).
            _logger.LogDebug(unlockError,
                    "Explicit advisory unlock failed; closing the connection releases it anyway.")
        End If

        ' Unconditional: _connection was already cleared above, so the lock is considered
        ' released either way, and the connection must not leak.
        Await conn.DisposeAsync().ConfigureAwait(False)
        _logger.LogDebug("Released the batch run lock.")
    End Function

    ' Not Async: VB.NET cannot declare an Async Function returning ValueTask, so wrap
    ' the Task instead (same workaround PipelineOrchestrator uses for
    ' Parallel.ForEachAsync's ValueTask body).
    Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
        Return New ValueTask(ReleaseAsync())
    End Function

    ''' <summary>
    ''' Synchronous safety net for container-driven scope teardown. Both callers release
    ''' explicitly, so in the normal path the lock is already gone and this is a no-op.
    ''' </summary>
    ''' <remarks>
    ''' Fully synchronous by design - no ReleaseAsync().GetAwaiter().GetResult(), which
    ''' risks deadlock. Disposing the connection ends the session, and Postgres drops
    ''' every session advisory lock with it, so closing alone is sufficient; the explicit
    ''' unlock just returns a clean connection to the pool.
    ''' </remarks>
    Public Sub Dispose() Implements IDisposable.Dispose
        Dim conn = _connection
        If conn Is Nothing Then Return
        _connection = Nothing

        Try
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT pg_advisory_unlock(@key)"
                cmd.Parameters.Add(New NpgsqlParameter("key", NpgsqlDbType.Bigint) With {.Value = RunLockKey})
                cmd.ExecuteScalar()
            End Using
        Catch ex As Exception
            _logger.LogDebug(ex, "Explicit advisory unlock failed during Dispose; closing the connection releases it anyway.")
        Finally
            conn.Dispose()
            _logger.LogDebug("Released the batch run lock (synchronous dispose).")
        End Try
    End Sub

End Class

Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Xunit

' Cross-process batch exclusion via a Postgres session advisory lock.
'
' These tests exercise the property the class exists for: two INDEPENDENT holders -
' standing in for the web host and a `elig run` CLI process - cannot both hold it.
' Each PostgresRunLock takes its own connection from the pool, so two instances are
' two Postgres sessions, which is exactly the situation being modelled. In-process
' faking would prove nothing here: the whole point is that the exclusion lives in the
' database, not in the CLR.
'
' STYLE NOTE: every test releases BEFORE asserting. VB.NET cannot Await inside a
' Finally, so a Try/Finally cannot do the cleanup; releasing first means a failed
' assertion still cannot strand the lock on a pooled connection and cascade into every
' later test in the class.
Public Class PostgresRunLockTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    Private Function NewLock() As PostgresRunLock
        Return New PostgresRunLock(_fixture.DataSource)
    End Function

    <SkippableFact>
    Public Async Function TryAcquire_succeeds_when_the_lock_is_free() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        Dim heldBefore = runLock.IsHeld
        Dim acquired = Await runLock.TryAcquireAsync(CancellationToken.None)
        Dim heldAfter = runLock.IsHeld
        Await runLock.ReleaseAsync()

        Assert.False(heldBefore)
        Assert.True(acquired)
        Assert.True(heldAfter)
    End Function

    ' THE test. webHost stands in for the web host mid-batch; cliRun for someone running
    ' `elig run` at the same time. Two instances = two pooled connections = two Postgres
    ' sessions, which is the cross-process case. The second must be refused.
    <SkippableFact>
    Public Async Function A_second_independent_holder_is_refused_while_the_first_holds_it() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim webHost = NewLock()
        Dim cliRun = NewLock()

        Dim firstAcquired = Await webHost.TryAcquireAsync(CancellationToken.None)
        Dim secondAcquired = Await cliRun.TryAcquireAsync(CancellationToken.None)
        Dim secondHeld = cliRun.IsHeld
        Await cliRun.ReleaseAsync()
        Await webHost.ReleaseAsync()

        Assert.True(firstAcquired)
        Assert.False(secondAcquired)
        Assert.False(secondHeld)
    End Function

    ' A refused acquire must return the connection it opened to probe, or a handful of
    ' rejected runs would drain the pool. Then: the handover the design depends on.
    <SkippableFact>
    Public Async Function A_refused_acquire_can_be_retried_and_succeeds_once_released() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim first = NewLock()
        Dim second = NewLock()

        Dim firstAcquired = Await first.TryAcquireAsync(CancellationToken.None)
        Dim refusals As New List(Of Boolean)
        For i = 1 To 5
            refusals.Add(Await second.TryAcquireAsync(CancellationToken.None))
        Next
        Await first.ReleaseAsync()
        Dim acquiredAfterHandover = Await second.TryAcquireAsync(CancellationToken.None)
        Await second.ReleaseAsync()

        Assert.True(firstAcquired)
        Assert.All(refusals, Sub(r) Assert.False(r))
        Assert.True(acquiredAfterHandover)
    End Function

    <SkippableFact>
    Public Async Function Release_is_idempotent_and_safe_when_never_acquired() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        ' Never acquired - must be a no-op, not a throw.
        Await runLock.ReleaseAsync()
        Dim heldAfterBareRelease = runLock.IsHeld

        Dim acquired = Await runLock.TryAcquireAsync(CancellationToken.None)
        Await runLock.ReleaseAsync()
        Dim heldAfterFirstRelease = runLock.IsHeld
        ' Second release must not throw or double-unlock someone else's lock.
        Await runLock.ReleaseAsync()

        ' ...and the lock really is free for another holder.
        Dim other = NewLock()
        Dim otherAcquired = Await other.TryAcquireAsync(CancellationToken.None)
        Await other.ReleaseAsync()

        Assert.False(heldAfterBareRelease)
        Assert.True(acquired)
        Assert.False(heldAfterFirstRelease)
        Assert.True(otherAcquired)
    End Function

    ' The IAsyncDisposable path (VB wraps the Task in a ValueTask by hand).
    <SkippableFact>
    Public Async Function DisposeAsync_releases_the_lock() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        Dim acquired = Await runLock.TryAcquireAsync(CancellationToken.None)
        Await runLock.DisposeAsync()
        Dim heldAfterDispose = runLock.IsHeld

        Dim other = NewLock()
        Dim otherAcquired = Await other.TryAcquireAsync(CancellationToken.None)
        Await other.ReleaseAsync()

        Assert.True(acquired)
        Assert.False(heldAfterDispose)
        Assert.True(otherAcquired)
    End Function

    ' Guards against acquiring twice on one instance: the second connection would
    ' overwrite the first in the field, leaking it AND leaving its lock held for ever.
    <SkippableFact>
    Public Async Function TryAcquire_twice_on_the_same_instance_throws() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        Dim acquired = Await runLock.TryAcquireAsync(CancellationToken.None)
        Dim threw As Exception = Nothing
        Try
            Await runLock.TryAcquireAsync(CancellationToken.None)
        Catch ex As InvalidOperationException
            threw = ex
        End Try
        Await runLock.ReleaseAsync()

        Assert.True(acquired)
        Assert.NotNull(threw)
    End Function

    ' An unreachable database must NOT read as "nobody holds it". Failing open would
    ' reintroduce the exact race this class removes, so callers must see a throw.
    <SkippableFact>
    Public Async Function TryAcquire_throws_when_the_database_is_unreachable() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Using deadSource = NpgsqlDataSource.Create(
                "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=1")
            Dim runLock As New PostgresRunLock(deadSource)
            Await Assert.ThrowsAnyAsync(Of Exception)(
                    Function() runLock.TryAcquireAsync(CancellationToken.None))
            Assert.False(runLock.IsHeld)
        End Using
    End Function

    ' Regression: the type is registered Scoped and BOTH hosts dispose their run scope
    ' synchronously (VB has no `Await Using`, so the CLI cannot use CreateAsyncScope).
    ' An IAsyncDisposable-only service in a sync-disposed scope makes the container throw
    ' at teardown - it did, and only showed up when running the real CLI. Sync Dispose
    ' must release the lock too.
    <SkippableFact>
    Public Async Function Sync_Dispose_releases_the_lock() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        Dim acquired = Await runLock.TryAcquireAsync(CancellationToken.None)
        ' The container's sync path, not DisposeAsync.
        CType(runLock, IDisposable).Dispose()
        Dim heldAfterDispose = runLock.IsHeld

        Dim other = NewLock()
        Dim otherAcquired = Await other.TryAcquireAsync(CancellationToken.None)
        Await other.ReleaseAsync()

        Assert.True(acquired)
        Assert.False(heldAfterDispose)
        Assert.True(otherAcquired, "sync Dispose must release the advisory lock, not just close the object")
    End Function

    ' Sync Dispose after an explicit release (the normal path) must be a harmless no-op.
    <SkippableFact>
    Public Async Function Sync_Dispose_after_ReleaseAsync_is_a_no_op() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        Assert.True(Await runLock.TryAcquireAsync(CancellationToken.None))
        Await runLock.ReleaseAsync()
        CType(runLock, IDisposable).Dispose()
        Assert.False(runLock.IsHeld)
    End Function

    ' Advisory means advisory: it must not take row/table locks or otherwise interfere
    ' with the run it guards, which writes to these tables continuously while held.
    <SkippableFact>
    Public Async Function Lock_does_not_block_unrelated_database_work() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim runLock = NewLock()
        Dim acquired = Await runLock.TryAcquireAsync(CancellationToken.None)
        Dim queryRan As Boolean
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM public.eligibility_study"
                Await cmd.ExecuteScalarAsync()
                queryRan = True
            End Using
        End Using
        Await runLock.ReleaseAsync()

        Assert.True(acquired)
        Assert.True(queryRan)
    End Function

End Class

Imports System.Collections.Concurrent
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports Npgsql
Imports Xunit

' Pins the wiring that makes SQL visible in `docker compose logs`.
'
' NpgsqlDataSource.Create() attaches NO logger, so the Npgsql log category is dead no
' matter what level is configured - the app looked like it "had SQL logging off" when in
' fact it had none to turn on. CompositionRoot.BuildDataSource now attaches the host's
' ILoggerFactory; this asserts that actually produces command logs, so a future refactor
' back to NpgsqlDataSource.Create() fails here instead of silently going quiet.
Public Class NpgsqlLoggingTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function DataSource_with_a_logger_factory_logs_the_command_text() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim captured As New ConcurrentQueue(Of String)
        Using lf = LoggerFactory.Create(
                Sub(b)
                    b.SetMinimumLevel(LogLevel.Trace)
                    b.AddProvider(New CapturingLoggerProvider(captured))
                End Sub)

            Dim builder As New NpgsqlDataSourceBuilder(_fixture.ConnectionString)
            builder.UseLoggerFactory(lf)
            Using ds = builder.Build()
                Using conn = Await ds.OpenConnectionAsync()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT 42"
                        Await cmd.ExecuteScalarAsync()
                    End Using
                End Using
            End Using
        End Using

        Dim lines = captured.ToArray()
        Assert.True(lines.Any(Function(l) l.Contains("SELECT 42")),
                    "Npgsql logged no command text. Captured: " & String.Join(" | ", lines.Take(10)))
    End Function

    ' The counterpart: no logger factory => nothing, which is what the app used to do.
    <SkippableFact>
    Public Async Function DataSource_without_a_logger_factory_logs_nothing() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim captured As New ConcurrentQueue(Of String)
        Using lf = LoggerFactory.Create(
                Sub(b)
                    b.SetMinimumLevel(LogLevel.Trace)
                    b.AddProvider(New CapturingLoggerProvider(captured))
                End Sub)

            ' Deliberately NOT calling UseLoggerFactory - the old NpgsqlDataSource.Create
            ' shape. The factory exists but Npgsql never sees it.
            Using ds = NpgsqlDataSource.Create(_fixture.ConnectionString)
                Using conn = Await ds.OpenConnectionAsync()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT 43"
                        Await cmd.ExecuteScalarAsync()
                    End Using
                End Using
            End Using
        End Using

        Assert.DoesNotContain(captured.ToArray(), Function(l) l.Contains("SELECT 43"))
    End Function

    Private NotInheritable Class CapturingLoggerProvider
        Implements ILoggerProvider

        Private ReadOnly _sink As ConcurrentQueue(Of String)

        Public Sub New(sink As ConcurrentQueue(Of String))
            _sink = sink
        End Sub

        Public Function CreateLogger(categoryName As String) As ILogger Implements ILoggerProvider.CreateLogger
            Return New CapturingLogger(categoryName, _sink)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
        End Sub
    End Class

    Private NotInheritable Class CapturingLogger
        Implements ILogger

        Private ReadOnly _category As String
        Private ReadOnly _sink As ConcurrentQueue(Of String)

        Public Sub New(category As String, sink As ConcurrentQueue(Of String))
            _category = category
            _sink = sink
        End Sub

        Public Function BeginScope(Of TState)(state As TState) As IDisposable Implements ILogger.BeginScope
            Return Nothing
        End Function

        Public Function IsEnabled(logLevel As LogLevel) As Boolean Implements ILogger.IsEnabled
            Return True
        End Function

        Public Sub Log(Of TState)(
                logLevel As LogLevel, eventId As EventId, state As TState,
                exception As Exception, formatter As Func(Of TState, Exception, String)) Implements ILogger.Log
            _sink.Enqueue($"{logLevel} {_category}: {formatter(state, exception)}")
        End Sub
    End Class

End Class

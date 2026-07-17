Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports EligibilityProcessing.Data
Imports Microsoft.Extensions.Logging
Imports Npgsql
Imports Xunit

' The slow-command threshold that makes SQL logging usable: without it, turning SQL
' logging on produces one entry per command (thousands per batch), each spanning as many
' lines as its query has newlines, and an outlier cannot be spotted.
Public Class SlowCommandLoggerFactoryTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    ' ============ pure logic: the filter + the one-line rendering ============

    ' Mimics Npgsql's structured state for the "command execution completed" event.
    Private Shared Function CompletedState(durationMs As Object, sql As String) As List(Of KeyValuePair(Of String, Object))
        Return New List(Of KeyValuePair(Of String, Object)) From {
            New KeyValuePair(Of String, Object)("CommandText", sql),
            New KeyValuePair(Of String, Object)("DurationMs", durationMs),
            New KeyValuePair(Of String, Object)("ConnectorId", 7),
            New KeyValuePair(Of String, Object)("{OriginalFormat}",
                "Command execution completed (duration={DurationMs}ms): {CommandText}")}
    End Function

    ' The Debug "Executing command" event: has CommandText but NO DurationMs.
    Private Shared Function StartedState(sql As String) As List(Of KeyValuePair(Of String, Object))
        Return New List(Of KeyValuePair(Of String, Object)) From {
            New KeyValuePair(Of String, Object)("CommandText", sql),
            New KeyValuePair(Of String, Object)("ConnectorId", 7),
            New KeyValuePair(Of String, Object)("{OriginalFormat}", "Executing command: {CommandText}")}
    End Function

    Private Shared Function LogVia(thresholdMs As Integer, category As String,
                                   state As Object) As String()
        Dim sink As New ConcurrentQueue(Of String)
        Using inner = LoggerFactory.Create(
                Sub(b)
                    b.SetMinimumLevel(LogLevel.Trace)
                    b.AddProvider(New CapturingProvider(sink))
                End Sub)
            Dim wrapped As New SlowCommandLoggerFactory(inner, thresholdMs)
            Dim logger = wrapped.CreateLogger(category)
            logger.Log(LogLevel.Information, New EventId(2001), state, Nothing,
                       Function(s, e) "ORIGINAL-TEMPLATE-OUTPUT")
        End Using
        Return sink.ToArray()
    End Function

    <Fact>
    Public Sub A_command_faster_than_the_threshold_is_dropped_entirely()
        Dim lines = LogVia(50, "Npgsql.Command", CompletedState(13, "SELECT 1"))
        Assert.Empty(lines)
    End Sub

    <Fact>
    Public Sub A_command_at_or_over_the_threshold_is_logged()
        Assert.Single(LogVia(50, "Npgsql.Command", CompletedState(50, "SELECT 1")))   ' boundary: >= keeps
        Assert.Single(LogVia(50, "Npgsql.Command", CompletedState(90, "SELECT 1")))
    End Sub

    ' The whole point: one grep-able line, not 40.
    <Fact>
    Public Sub A_logged_command_is_rendered_on_one_line_with_the_sql()
        Dim sql = "
SELECT intervention_type, name
FROM ctgov.interventions
WHERE nct_id = $1
ORDER BY intervention_type, name"
        Dim lines = LogVia(50, "Npgsql.Command", CompletedState(87, sql))

        Dim line = Assert.Single(lines)
        Assert.DoesNotContain(vbLf, line)
        Assert.DoesNotContain(vbCr, line)
        Assert.Contains("(duration=87ms)", line)
        Assert.Contains("SELECT intervention_type, name FROM ctgov.interventions WHERE nct_id = $1", line)
    End Sub

    ' A hung query never completes, so it never reports a duration - the only trace it
    ' leaves is the Debug "Executing command" event. Thresholding must not eat it.
    <Fact>
    Public Sub An_event_without_a_duration_passes_through_untouched()
        Dim lines = LogVia(50, "Npgsql.Command", StartedState("SELECT pg_sleep(600)"))

        Dim line = Assert.Single(lines)
        ' Untouched => the ORIGINAL formatter's output, not our rewritten line.
        Assert.Contains("ORIGINAL-TEMPLATE-OUTPUT", line)
    End Sub

    ' Only the command category is wrapped; connection chatter must be unaffected.
    <Fact>
    Public Sub Other_npgsql_categories_are_not_filtered()
        Dim lines = LogVia(50, "Npgsql.Connection", CompletedState(1, "SELECT 1"))
        Assert.Single(lines)
    End Sub

    <Fact>
    Public Sub A_threshold_of_zero_or_less_disables_filtering_entirely()
        Assert.Single(LogVia(0, "Npgsql.Command", CompletedState(1, "SELECT 1")))
        Assert.Single(LogVia(-1, "Npgsql.Command", CompletedState(1, "SELECT 1")))
    End Sub

    <Fact>
    Public Sub Long_sql_is_truncated_so_one_query_cannot_produce_a_giant_line()
        Dim huge = "SELECT " & String.Join(", ", Enumerable.Range(1, 500).Select(Function(i) $"column_{i}"))
        Dim line = Assert.Single(LogVia(1, "Npgsql.Command", CompletedState(10, huge)))

        Assert.EndsWith("...", line)
        Assert.True(line.Length < 700, $"line should be capped, was {line.Length}")
    End Sub

    <Fact>
    Public Sub CollapseSql_squashes_whitespace_and_handles_edges()
        Assert.Equal("SELECT a FROM b", SlowCommandLoggerFactory.CollapseSql("  SELECT   a" & vbCrLf & vbTab & " FROM b  "))
        Assert.Equal("", SlowCommandLoggerFactory.CollapseSql(""))
        Assert.Equal("", SlowCommandLoggerFactory.CollapseSql(Nothing))
    End Sub

    ' Disposing a data source must not tear down the host's logging.
    <Fact>
    Public Sub Disposing_the_wrapper_does_not_dispose_the_hosts_factory()
        Dim sink As New ConcurrentQueue(Of String)
        Dim inner = LoggerFactory.Create(
            Sub(b)
                b.SetMinimumLevel(LogLevel.Trace)
                b.AddProvider(New CapturingProvider(sink))
            End Sub)

        Dim wrapped As New SlowCommandLoggerFactory(inner, 50)
        wrapped.Dispose()

        ' The borrowed factory must still work.
        inner.CreateLogger("after").LogInformation("still alive")
        Assert.Contains(sink.ToArray(), Function(l) l.Contains("still alive"))
        inner.Dispose()
    End Sub

    ' ============ end to end against real Npgsql ============

    ' Proves the field names (DurationMs / CommandText) still match what Npgsql actually
    ' emits. If Npgsql renames them, the filter would silently stop matching and every
    ' command would be logged again - this catches that.
    <SkippableFact>
    Public Async Function A_fast_real_command_is_filtered_out_by_a_high_threshold() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim sink As New ConcurrentQueue(Of String)
        Using inner = LoggerFactory.Create(
                Sub(b)
                    b.SetMinimumLevel(LogLevel.Trace)
                    b.AddProvider(New CapturingProvider(sink))
                End Sub)
            ' 10 seconds: no trivial SELECT will ever reach it.
            Dim wrapped As New SlowCommandLoggerFactory(inner, 10000)
            Dim builder As New NpgsqlDataSourceBuilder(_fixture.ConnectionString)
            builder.UseLoggerFactory(wrapped)
            Using ds = builder.Build()
                Using conn = Await ds.OpenConnectionAsync()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT 4242"
                        Await cmd.ExecuteScalarAsync()
                    End Using
                End Using
            End Using
        End Using

        ' The completed event is gone...
        Assert.DoesNotContain(sink.ToArray(), Function(l) l.Contains("Command execution completed") AndAlso l.Contains("4242"))
    End Function

    <SkippableFact>
    Public Async Function A_real_command_over_the_threshold_is_logged_on_one_line() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim sink As New ConcurrentQueue(Of String)
        Using inner = LoggerFactory.Create(
                Sub(b)
                    b.SetMinimumLevel(LogLevel.Information)
                    b.AddProvider(New CapturingProvider(sink))
                End Sub)
            Dim wrapped As New SlowCommandLoggerFactory(inner, 100)
            Dim builder As New NpgsqlDataSourceBuilder(_fixture.ConnectionString)
            builder.UseLoggerFactory(wrapped)
            Using ds = builder.Build()
                Using conn = Await ds.OpenConnectionAsync()
                    Using cmd = conn.CreateCommand()
                        ' Genuinely slow, and written across several lines like the real
                        ' queries that produced the firehose.
                        cmd.CommandText = "
SELECT pg_sleep(0.3),
       'marker-query' AS tag"
                        Await cmd.ExecuteScalarAsync()
                    End Using
                End Using
            End Using
        End Using

        Dim hit = sink.ToArray().FirstOrDefault(Function(l) l.Contains("marker-query"))
        Assert.NotNull(hit)
        Assert.DoesNotContain(vbLf, hit)
        Assert.Contains("Command execution completed (duration=", hit)
        Assert.Contains("SELECT pg_sleep(0.3), 'marker-query' AS tag", hit)
    End Function

    Private NotInheritable Class CapturingProvider
        Implements ILoggerProvider

        Private ReadOnly _sink As ConcurrentQueue(Of String)

        Public Sub New(sink As ConcurrentQueue(Of String))
            _sink = sink
        End Sub

        Public Function CreateLogger(categoryName As String) As ILogger Implements ILoggerProvider.CreateLogger
            Return New CapturingLogger(_sink)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
        End Sub
    End Class

    Private NotInheritable Class CapturingLogger
        Implements ILogger

        Private ReadOnly _sink As ConcurrentQueue(Of String)

        Public Sub New(sink As ConcurrentQueue(Of String))
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
            _sink.Enqueue(formatter(state, exception))
        End Sub
    End Class

End Class

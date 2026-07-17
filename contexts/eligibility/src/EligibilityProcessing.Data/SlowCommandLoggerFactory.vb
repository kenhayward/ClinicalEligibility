Imports System.Collections.Generic
Imports System.Text
Imports Microsoft.Extensions.Logging

' Makes Npgsql's SQL logging usable: drops every command faster than a threshold, and
' renders the survivors as ONE grep-able line.
'
' The problem it solves: Npgsql logs every command at Information, and its message
' template embeds the raw command text - so a query written across 40 lines of SQL logs
' as 40 lines. A real batch runs thousands of commands, and the result is a firehose in
' which an outlier is impossible to spot.
'
' This wraps ONLY the ILoggerFactory handed to the Npgsql data sources (see
' CompositionRoot.BuildDataSource), so nothing else in the app is affected and the host's
' own factory is untouched.
'
' HOW IT FILTERS: Npgsql's log state is structured -
'   CommandText=SELECT 42; DurationMs=13; ConnectorId=82;
'   {OriginalFormat}=Command execution completed (duration={DurationMs}ms): {CommandText}
' so the duration is read from the DurationMs field, never parsed out of the message.
'
' Events WITHOUT a DurationMs (the Debug-level "Executing command", connector chatter)
' are passed through untouched: a threshold can only apply to something that has been
' measured, and dropping the "Executing command" line would remove the only signal a
' HUNG query ever produces - it never completes, so it never gets a duration.
'
' The original structured state is forwarded unchanged; only the rendered TEXT is
' replaced. A structured sink still sees CommandText / DurationMs / ConnectorId as
' fields, while the console gets one readable line.
Public NotInheritable Class SlowCommandLoggerFactory
    Implements ILoggerFactory

    ' Npgsql's category for command events. Matched exactly: Npgsql.Connection and the
    ' rest must pass through untouched.
    Friend Const NpgsqlCommandCategory As String = "Npgsql.Command"

    ' Structured field names from Npgsql's own LoggerMessage definitions.
    Friend Const DurationField As String = "DurationMs"
    Friend Const CommandTextField As String = "CommandText"

    ' Long enough for any real query to be identifiable, short enough that a line stays
    ' scannable. The dashboard metrics query alone is ~2k chars on one line.
    Friend Const MaxCommandTextLength As Integer = 500

    Private ReadOnly _inner As ILoggerFactory
    Private ReadOnly _thresholdMs As Integer

    Public Sub New(inner As ILoggerFactory, thresholdMs As Integer)
        If inner Is Nothing Then Throw New ArgumentNullException(NameOf(inner))
        _inner = inner
        _thresholdMs = thresholdMs
    End Sub

    Public Function CreateLogger(categoryName As String) As ILogger Implements ILoggerFactory.CreateLogger
        Dim logger = _inner.CreateLogger(categoryName)
        If _thresholdMs <= 0 Then Return logger
        If Not String.Equals(categoryName, NpgsqlCommandCategory, StringComparison.Ordinal) Then Return logger
        Return New SlowCommandLogger(logger, _thresholdMs)
    End Function

    Public Sub AddProvider(provider As ILoggerProvider) Implements ILoggerFactory.AddProvider
        _inner.AddProvider(provider)
    End Sub

    ''' <summary>
    ''' Deliberately does NOT dispose the wrapped factory: this type borrows the host's
    ''' factory, it does not own it. Disposing it here would tear down logging for the
    ''' whole application when a data source is disposed.
    ''' </summary>
    Public Sub Dispose() Implements IDisposable.Dispose
    End Sub

    ''' <summary>
    ''' Collapses SQL onto a single line: runs of whitespace (including the newlines that
    ''' make a formatted query log as 40 lines) become one space, and the result is capped
    ''' so one slow query cannot produce a 2,000-character log line.
    ''' </summary>
    Friend Shared Function CollapseSql(sql As String) As String
        If String.IsNullOrEmpty(sql) Then Return ""
        Dim sb As New StringBuilder(Math.Min(sql.Length, MaxCommandTextLength + 3))
        Dim lastWasSpace = False
        For Each ch In sql
            If Char.IsWhiteSpace(ch) Then
                If Not lastWasSpace AndAlso sb.Length > 0 Then
                    sb.Append(" "c)
                    lastWasSpace = True
                End If
            Else
                sb.Append(ch)
                lastWasSpace = False
                ' +1 so we can tell "exactly at the cap" from "over it" below.
                If sb.Length > MaxCommandTextLength Then Exit For
            End If
        Next

        Dim text = sb.ToString().TrimEnd()
        If text.Length > MaxCommandTextLength Then
            Return text.Substring(0, MaxCommandTextLength) & "..."
        End If
        Return text
    End Function

    Private NotInheritable Class SlowCommandLogger
        Implements ILogger

        Private ReadOnly _inner As ILogger
        Private ReadOnly _thresholdMs As Integer

        Public Sub New(inner As ILogger, thresholdMs As Integer)
            _inner = inner
            _thresholdMs = thresholdMs
        End Sub

        Public Function BeginScope(Of TState)(state As TState) As IDisposable Implements ILogger.BeginScope
            Return _inner.BeginScope(state)
        End Function

        Public Function IsEnabled(logLevel As LogLevel) As Boolean Implements ILogger.IsEnabled
            Return _inner.IsEnabled(logLevel)
        End Function

        Public Sub Log(Of TState)(
                logLevel As LogLevel,
                eventId As EventId,
                state As TState,
                exception As Exception,
                formatter As Func(Of TState, Exception, String)) Implements ILogger.Log

            Dim durationMs As Integer? = Nothing
            Dim commandText As String = Nothing

            Dim fields = TryCast(state, IReadOnlyList(Of KeyValuePair(Of String, Object)))
            If fields IsNot Nothing Then
                For Each field In fields
                    Select Case field.Key
                        Case DurationField
                            durationMs = TryToInt(field.Value)
                        Case CommandTextField
                            commandText = TryCast(field.Value, String)
                    End Select
                Next
            End If

            ' Nothing measured => nothing to threshold. Covers the Debug "Executing
            ' command" event, which is the ONLY trace a hung query leaves.
            If Not durationMs.HasValue Then
                _inner.Log(logLevel, eventId, state, exception, formatter)
                Return
            End If

            If durationMs.Value < _thresholdMs Then Return

            ' Forward the ORIGINAL state (so structured sinks keep every field) and swap
            ' only the rendering, to one line.
            Dim line = $"Command execution completed (duration={durationMs.Value}ms): {CollapseSql(commandText)}"
            _inner.Log(logLevel, eventId, state, exception, Function(s, e) line)
        End Sub

        ' Npgsql boxes DurationMs as an integer today, but the field is its message
        ' template's, not our contract - accept anything numeric rather than assume.
        Private Shared Function TryToInt(value As Object) As Integer?
            If value Is Nothing Then Return Nothing
            If TypeOf value Is Integer Then Return CInt(value)
            If TypeOf value Is Long Then Return CInt(CLng(value))
            If TypeOf value Is Double Then Return CInt(Math.Round(CDbl(value)))
            Dim parsed As Integer
            If Integer.TryParse(value.ToString(), parsed) Then Return parsed
            Return Nothing
        End Function
    End Class

End Class

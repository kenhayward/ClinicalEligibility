Imports System
Imports EligibilityProcessing.Core
Imports Xunit

' Unit coverage for RunMetrics.CompletionTokensPerSecond — the Runs (History)
' table's aggregate decode throughput: total completion tokens ÷ run wall clock.
Public Class RunMetricsTests

    Private Shared Function Make(
            startedAt As DateTimeOffset,
            endedAt As DateTimeOffset?,
            completionTokens As Long) As RunMetrics
        Return New RunMetrics(
                runId:=Guid.NewGuid(),
                startedAt:=startedAt,
                endedAt:=endedAt,
                triggerSource:="form",
                studyCount:=10,
                studiesProcessed:=10,
                rowsPersisted:=100,
                resolutionRate:=0.9,
                status:="success",
                errorSummary:="",
                completionTokens:=completionTokens)
    End Function

    <Fact>
    Public Sub CompletionTokensPerSecond_divides_total_tokens_by_wall_clock()
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        ' 12,000 completion tokens over a 60-second run = 200.0 tok/s aggregate.
        Dim m = Make(start, start.AddSeconds(60), 12000)

        Assert.True(m.CompletionTokensPerSecond.HasValue)
        Assert.Equal(200.0, m.CompletionTokensPerSecond.Value, precision:=3)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_rises_with_shorter_wall_clock_at_fixed_tokens()
        ' Concurrency intuition: same total work, less wall clock -> higher tok/s.
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        Dim slow = Make(start, start.AddSeconds(120), 60000)  ' 500 tok/s
        Dim fast = Make(start, start.AddSeconds(60), 60000)   ' 1000 tok/s

        Assert.Equal(500.0, slow.CompletionTokensPerSecond.Value, precision:=3)
        Assert.Equal(1000.0, fast.CompletionTokensPerSecond.Value, precision:=3)
        Assert.True(fast.CompletionTokensPerSecond.Value > slow.CompletionTokensPerSecond.Value)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_is_nothing_while_in_flight()
        ' No EndedAt -> no final wall clock -> "—" on the table.
        Dim m = Make(DateTimeOffset.UtcNow, Nothing, 5000)
        Assert.False(m.CompletionTokensPerSecond.HasValue)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_is_nothing_when_wall_clock_non_positive()
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        Dim m = Make(start, start, 5000)   ' ended_at == started_at
        Assert.False(m.CompletionTokensPerSecond.HasValue)
    End Sub

    <Fact>
    Public Sub CompletionTokens_defaults_to_zero_on_the_write_path_constructor()
        ' The orchestrator builds RunMetrics without a token total; it stays 0.
        Dim m = New RunMetrics(
                runId:=Guid.NewGuid(), startedAt:=DateTimeOffset.UtcNow, endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="form", studyCount:=1, studiesProcessed:=1, rowsPersisted:=1,
                resolutionRate:=1.0, status:="success", errorSummary:="")
        Assert.Equal(0L, m.CompletionTokens)
    End Sub

    <Fact>
    Public Sub ConcurrencyCap_round_trips_through_the_constructor()
        Dim withCap = New RunMetrics(
                runId:=Guid.NewGuid(), startedAt:=DateTimeOffset.UtcNow, endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="form", studyCount:=1, studiesProcessed:=1, rowsPersisted:=1,
                resolutionRate:=1.0, status:="success", errorSummary:="",
                completionTokens:=0, concurrencyCap:=12)
        Assert.Equal(12, withCap.ConcurrencyCap)

        ' Unset (legacy / pre-V15 rows) reads as Nothing -> "—" in the table.
        Dim noCap = New RunMetrics(
                runId:=Guid.NewGuid(), startedAt:=DateTimeOffset.UtcNow, endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="form", studyCount:=1, studiesProcessed:=1, rowsPersisted:=1,
                resolutionRate:=1.0, status:="success", errorSummary:="")
        Assert.False(noCap.ConcurrencyCap.HasValue)
    End Sub

    <Fact>
    Public Sub Phase_averages_round_trip_through_the_constructor()
        Dim withAvgs = New RunMetrics(
                runId:=Guid.NewGuid(), startedAt:=DateTimeOffset.UtcNow, endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="form", studyCount:=1, studiesProcessed:=1, rowsPersisted:=1,
                resolutionRate:=1.0, status:="success", errorSummary:="",
                completionTokens:=0, concurrencyCap:=8,
                avgLlmMs:=4200.0, avgUmlsMs:=2100.0, avgPersistMs:=80.0)
        Assert.Equal(4200.0, withAvgs.AvgLlmMs.Value, precision:=3)
        Assert.Equal(2100.0, withAvgs.AvgUmlsMs.Value, precision:=3)
        Assert.Equal(80.0, withAvgs.AvgPersistMs.Value, precision:=3)

        ' Pre-V16 runs read as Nothing -> "—" in the table.
        Dim noAvgs = New RunMetrics(
                runId:=Guid.NewGuid(), startedAt:=DateTimeOffset.UtcNow, endedAt:=DateTimeOffset.UtcNow,
                triggerSource:="form", studyCount:=1, studiesProcessed:=1, rowsPersisted:=1,
                resolutionRate:=1.0, status:="success", errorSummary:="")
        Assert.False(noAvgs.AvgLlmMs.HasValue)
        Assert.False(noAvgs.AvgUmlsMs.HasValue)
        Assert.False(noAvgs.AvgPersistMs.HasValue)
    End Sub

End Class

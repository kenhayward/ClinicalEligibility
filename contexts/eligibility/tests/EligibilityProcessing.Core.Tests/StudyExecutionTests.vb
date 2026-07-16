Imports System
Imports EligibilityProcessing.Core
Imports Xunit

' Unit coverage for StudyExecution's computed properties — specifically
' CompletionTokensPerSecond, the History tab's throughput guide
' (completion tokens ÷ trial Duration).
Public Class StudyExecutionTests

    Private Shared Function Make(
            startedAt As DateTimeOffset,
            finishedAt As DateTimeOffset?,
            completionTokens As Integer?) As StudyExecution
        Return New StudyExecution(
                runId:=Guid.NewGuid(),
                nctId:="NCT00000001",
                startedAt:=startedAt,
                finishedAt:=finishedAt,
                status:=StudyExecution.StatusSuccess,
                llmSucceeded:=True,
                llmFinishReason:="stop",
                llmPromptTokens:=Nothing,
                llmCompletionTokens:=completionTokens,
                parsedRecordCount:=0,
                persistedRowCount:=0,
                errorMessage:="")
    End Function

    <Fact>
    Public Sub CompletionTokensPerSecond_divides_completion_tokens_by_duration_seconds()
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        ' 900 completion tokens over 20 seconds = 45.0 tok/s.
        Dim ex = Make(start, start.AddSeconds(20), 900)

        Assert.True(ex.CompletionTokensPerSecond.HasValue)
        Assert.Equal(45.0, ex.CompletionTokensPerSecond.Value, precision:=3)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_handles_fractional_seconds_to_one_decimal()
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        ' 939 tokens over 17.2 s ≈ 54.6 tok/s (matches the screenshot's first row).
        Dim ex = Make(start, start.AddMilliseconds(17200), 939)

        Assert.Equal("54.6", ex.CompletionTokensPerSecond.Value.ToString("F1"))
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_is_nothing_while_running()
        ' No finished_at -> no Duration -> no throughput (running row shows "—").
        Dim ex = Make(DateTimeOffset.UtcNow, Nothing, 500)
        Assert.False(ex.CompletionTokensPerSecond.HasValue)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_is_nothing_when_no_completion_tokens_captured()
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        Dim ex = Make(start, start.AddSeconds(10), Nothing)
        Assert.False(ex.CompletionTokensPerSecond.HasValue)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_is_nothing_when_duration_non_positive()
        ' finished_at == started_at (or earlier, via clock skew) -> guard against
        ' divide-by-zero / negative rates.
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        Dim zero = Make(start, start, 500)
        Dim negative = Make(start, start.AddSeconds(-1), 500)

        Assert.False(zero.CompletionTokensPerSecond.HasValue)
        Assert.False(negative.CompletionTokensPerSecond.HasValue)
    End Sub

    <Fact>
    Public Sub Phase_timings_round_trip_through_the_constructor()
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        Dim ex = New StudyExecution(
                runId:=Guid.NewGuid(), nctId:="NCT00000001", startedAt:=start, finishedAt:=start.AddSeconds(10),
                status:=StudyExecution.StatusSuccess, llmSucceeded:=True, llmFinishReason:="stop",
                llmPromptTokens:=100, llmCompletionTokens:=900, parsedRecordCount:=5, persistedRowCount:=5,
                errorMessage:="", llmMs:=4200, umlsMs:=2100, persistMs:=80)

        Assert.Equal(4200, ex.LlmMs)
        Assert.Equal(2100, ex.UmlsMs)
        Assert.Equal(80, ex.PersistMs)
    End Sub

    <Fact>
    Public Sub Phase_timings_default_to_nothing_when_unset()
        Dim ex = Make(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 500)
        Assert.False(ex.LlmMs.HasValue)
        Assert.False(ex.UmlsMs.HasValue)
        Assert.False(ex.PersistMs.HasValue)
    End Sub

    <Fact>
    Public Sub CompletionTokensPerSecond_is_zero_for_zero_tokens_over_real_duration()
        ' Distinct from "—": a row that finished with 0 completion tokens has a
        ' legitimate 0.0 tok/s, not an unknown.
        Dim start = New DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)
        Dim ex = Make(start, start.AddSeconds(5), 0)

        Assert.True(ex.CompletionTokensPerSecond.HasValue)
        Assert.Equal(0.0, ex.CompletionTokensPerSecond.Value, precision:=3)
    End Sub

    ' These literals are PERSISTED in public.eligibility_study.status and are matched
    ' verbatim by SQL (the dashboard's status buckets) and by the History filter.
    ' Renaming one is a data migration, not a refactor - so pin the wire values.
    <Fact>
    Public Sub StatusInterrupted_has_the_expected_wire_value()
        Assert.Equal("interrupted", StudyExecution.StatusInterrupted)
    End Sub

    <Fact>
    Public Sub Status_constants_are_distinct()
        Dim all = {StudyExecution.StatusRunning, StudyExecution.StatusSuccess,
                   StudyExecution.StatusLlmFailed, StudyExecution.StatusParseEmpty,
                   StudyExecution.StatusParseInvalidJson, StudyExecution.StatusPersistFailed,
                   StudyExecution.StatusFailed, StudyExecution.StatusCancelled,
                   StudyExecution.StatusInterrupted}
        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count())
    End Sub

End Class

Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

' Unit tests for the shared maintenance-tool jobs (UmlsNormalizeJob /
' StudyEmbeddingJob in Core) that back both the CLI commands and the web Tools tab.
Public Class ToolJobsTests

    ' ---------------- normalize-umls ----------------

    <Fact>
    Public Async Function Normalize_counts_resolved_none_and_errors() As Task
        Dim gateway As New FakeGateway() With {
            .RecordNormalizationRows = 5,
            .NormalizeConcepts = New ConceptToNormalize() {
                New ConceptToNormalize("diabetes mellitus", "Diabetes Mellitus"),
                New ConceptToNormalize("not a concept", "blah blah"),
                New ConceptToNormalize("boom key", "explode")}}

        Dim normalizer As New FakeNormalizer()
        normalizer.Map("Diabetes Mellitus") = NormalizationResult.Success("Diabetes Mellitus")
        normalizer.Map("blah blah") = NormalizationResult.Success("NONE")
        normalizer.Map("explode") = NormalizationResult.Failure("transport down")

        Dim umls As New FakeUmlsClient()
        umls.SearchResults("Diabetes Mellitus") = New UmlsCandidate() {New UmlsCandidate("C0011849", "Diabetes Mellitus", "MSH")}
        umls.SemanticTypesResults("C0011849") = New SemanticTypeAssignment() {New SemanticTypeAssignment("T047", "Disease or Syndrome")}

        Dim job As New UmlsNormalizeJob(gateway, normalizer, umls, New UmlsMatchScorer())
        Dim counters = Await job.RunAsync(
            New NormalizeUmlsOptions With {.Count = 10, .Concurrency = 2},
            Nothing, CancellationToken.None)

        Assert.Equal(1, counters.Resolved)
        Assert.Equal(1, counters.NoneCount)
        Assert.Equal(1, counters.Errors)
        Assert.Equal(2, counters.Done)               ' resolved + none reach Done; the error returns first
        ' Record is called for the resolved + the none concept (each returns 5 rows); the transport
        ' failure is NOT recorded so it retries next run.
        Assert.Equal(2, gateway.RecordNormalizationCalls.Count)
        Assert.Equal(10, counters.RowsUpdated)
        Assert.Contains(gateway.RecordNormalizationCalls, Function(c) c.Resolved)
    End Function

    <Fact>
    Public Async Function Normalize_dry_run_records_nothing_but_still_counts() As Task
        Dim gateway As New FakeGateway() With {
            .RecordNormalizationRows = 3,
            .NormalizeConcepts = New ConceptToNormalize() {New ConceptToNormalize("diabetes mellitus", "Diabetes Mellitus")}}
        Dim normalizer As New FakeNormalizer()
        normalizer.Map("Diabetes Mellitus") = NormalizationResult.Success("Diabetes Mellitus")
        Dim umls As New FakeUmlsClient()
        umls.SearchResults("Diabetes Mellitus") = New UmlsCandidate() {New UmlsCandidate("C0011849", "Diabetes Mellitus", "MSH")}

        Dim job As New UmlsNormalizeJob(gateway, normalizer, umls, New UmlsMatchScorer())
        Dim counters = Await job.RunAsync(
            New NormalizeUmlsOptions With {.Count = 10, .Concurrency = 1, .DryRun = True},
            Nothing, CancellationToken.None)

        Assert.Equal(1, counters.Resolved)
        Assert.Equal(0, counters.RowsUpdated)
        Assert.Empty(gateway.RecordNormalizationCalls)
    End Function

    <Fact>
    Public Async Function Normalize_reports_a_final_snapshot_with_totals_and_metrics() As Task
        Dim gateway As New FakeGateway() With {
            .NormalizeConcepts = New ConceptToNormalize() {
                New ConceptToNormalize("a", "alpha"),
                New ConceptToNormalize("b", "beta")}}
        Dim normalizer As New FakeNormalizer()   ' both default to NONE

        Dim job As New UmlsNormalizeJob(gateway, normalizer, New FakeUmlsClient(), New UmlsMatchScorer())
        Dim progress As New CapturingProgress()
        Await job.RunAsync(New NormalizeUmlsOptions With {.Count = 10, .Concurrency = 1}, progress, CancellationToken.None)

        Assert.NotEmpty(progress.Snapshots)
        Dim final = progress.Snapshots.Last()
        Assert.Equal(ToolJobKind.NormalizeUmls, final.Kind)
        Assert.Equal(2, final.Total)
        Assert.Equal(2, final.Processed)
        Assert.Contains(final.Metrics, Function(m) m.Label = "Resolved")
        Assert.Contains(final.Metrics, Function(m) m.Label = "Not a concept")
    End Function

    <Fact>
    Public Async Function Normalize_count_remaining_delegates_to_the_gateway() As Task
        Dim gateway As New FakeGateway() With {
            .NormalizeConcepts = New ConceptToNormalize() {
                New ConceptToNormalize("a", "alpha"), New ConceptToNormalize("b", "beta"), New ConceptToNormalize("c", "gamma")}}
        Dim job As New UmlsNormalizeJob(gateway, New FakeNormalizer(), New FakeUmlsClient(), New UmlsMatchScorer())
        Assert.Equal(3, Await job.CountRemainingAsync(False, CancellationToken.None))
    End Function

    <Fact>
    Public Async Function Normalize_propagates_cancellation() As Task
        Dim gateway As New FakeGateway() With {
            .NormalizeConcepts = New ConceptToNormalize() {New ConceptToNormalize("a", "alpha")}}
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Dim job As New UmlsNormalizeJob(gateway, New FakeNormalizer(), New FakeUmlsClient(), New UmlsMatchScorer())
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(
                Function() job.RunAsync(New NormalizeUmlsOptions With {.Count = 10, .Concurrency = 1}, Nothing, cts.Token))
        End Using
    End Function

    ' ---------------- embed-studies ----------------

    <Fact>
    Public Async Function Embed_upserts_each_study_and_counts_processed() As Task
        Dim gateway As New FakeGateway() With {.StudiesToEmbed = New StudyEmbeddingInput() {Study("NCT1"), Study("NCT2")}}
        Dim embed As New FakeEmbeddingClient()
        Dim job As New StudyEmbeddingJob(gateway, embed)

        Dim counters = Await job.RunAsync(New EmbedStudiesOptions With {.Concurrency = 2, .Model = "m"}, Nothing, CancellationToken.None)

        Assert.Equal(2, counters.Processed)
        Assert.Equal(0, counters.Failed)
        Assert.Equal(2, gateway.UpsertStudyEmbeddingCalls.Count)
    End Function

    <Fact>
    Public Async Function Embed_counts_failures_and_does_not_upsert() As Task
        Dim gateway As New FakeGateway() With {.StudiesToEmbed = New StudyEmbeddingInput() {Study("NCT1"), Study("NCT2")}}
        Dim embed As New FakeEmbeddingClient() With {.ForceFailure = True}
        Dim job As New StudyEmbeddingJob(gateway, embed)

        Dim counters = Await job.RunAsync(New EmbedStudiesOptions With {.Concurrency = 2, .Model = "m"}, Nothing, CancellationToken.None)

        Assert.Equal(0, counters.Processed)
        Assert.Equal(2, counters.Failed)
        Assert.Empty(gateway.UpsertStudyEmbeddingCalls)
    End Function

    <Fact>
    Public Async Function Embed_count_remaining_delegates_to_the_gateway() As Task
        Dim gateway As New FakeGateway() With {.StudiesToEmbed = New StudyEmbeddingInput() {Study("NCT1")}}
        Dim job As New StudyEmbeddingJob(gateway, New FakeEmbeddingClient())
        Assert.Equal(1, Await job.CountRemainingAsync("m", CancellationToken.None))
    End Function

    Private Shared Function Study(nctId As String) As StudyEmbeddingInput
        Return New StudyEmbeddingInput(
            nctId:=nctId,
            briefTitle:="Title " & nctId,
            officialTitle:="",
            briefSummary:="A study.",
            conditions:=Array.Empty(Of String)(),
            interventions:=Array.Empty(Of Intervention)())
    End Function
End Class

' A normalizer fake mapping the ORIGINAL concept phrasing to a canned result
' (defaults to a successful "NONE" - "not a concept").
Friend NotInheritable Class FakeNormalizer
    Implements ICriteriaNormalizer

    Public ReadOnly Property Map As New Dictionary(Of String, NormalizationResult)(StringComparer.Ordinal)
    Public Property [Default] As NormalizationResult = NormalizationResult.Success("NONE")

    Public Function NormalizeAsync(originalTexts As IReadOnlyList(Of String), cancellationToken As CancellationToken) As Task(Of NormalizationResult) _
            Implements ICriteriaNormalizer.NormalizeAsync
        Return Task.FromResult(NormalizationResult.Failure("not used in these tests"))
    End Function

    Public Function NormalizeConceptAsync(concept As String, cancellationToken As CancellationToken) As Task(Of NormalizationResult) _
            Implements ICriteriaNormalizer.NormalizeConceptAsync
        cancellationToken.ThrowIfCancellationRequested()
        Dim r As NormalizationResult = Nothing
        If Map.TryGetValue(concept, r) Then Return Task.FromResult(r)
        Return Task.FromResult([Default])
    End Function
End Class

' Captures every progress snapshot a job reports.
Friend NotInheritable Class CapturingProgress
    Implements IProgress(Of ToolJobSnapshot)

    Public ReadOnly Property Snapshots As New List(Of ToolJobSnapshot)

    Public Sub Report(value As ToolJobSnapshot) Implements IProgress(Of ToolJobSnapshot).Report
        SyncLock Snapshots
            Snapshots.Add(value)
        End SyncLock
    End Sub
End Class

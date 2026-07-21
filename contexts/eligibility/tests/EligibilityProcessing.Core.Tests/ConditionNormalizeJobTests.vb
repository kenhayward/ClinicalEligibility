Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

Public Class ConditionNormalizeJobTests

    ' Minimal store that serves a fixed pending list and records upserts.
    Private NotInheritable Class JobStore
        Implements IConditionConceptStore

        Public Property Pending As New List(Of ConditionConceptEntry)
        Public Property Upserted As New List(Of ConditionConceptEntry)
        Public Property SeedCalls As Integer
        Public Property ExactByNorm As New Dictionary(Of String, IReadOnlyList(Of ConditionCandidate))
        Public Property LastForce As Boolean?

        Public Function LookupExactAsync(conditionNorm As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of ConditionCandidate)) Implements IConditionConceptStore.LookupExactAsync
            Dim hit As IReadOnlyList(Of ConditionCandidate) = Nothing
            If ExactByNorm.TryGetValue(conditionNorm, hit) Then Return Task.FromResult(hit)
            Return Task.FromResult(Of IReadOnlyList(Of ConditionCandidate))(Array.Empty(Of ConditionCandidate)())
        End Function

        Public Function UpsertAsync(entry As ConditionConceptEntry, cancellationToken As CancellationToken) _
                As Task Implements IConditionConceptStore.UpsertAsync
            Upserted.Add(entry)
            Return Task.CompletedTask
        End Function

        Public Function GetUnseenConditionsForStudyAsync(nctId As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of String)) Implements IConditionConceptStore.GetUnseenConditionsForStudyAsync
            Return Task.FromResult(Of IReadOnlyList(Of String))(Array.Empty(Of String)())
        End Function

        Public Function SeedFromCorpusAsync(cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.SeedFromCorpusAsync
            SeedCalls += 1
            Return Task.FromResult(0)
        End Function

        Public Function GetPendingAsync(limit As Integer, force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of ConditionConceptEntry)) Implements IConditionConceptStore.GetPendingAsync
            Return Task.FromResult(Of IReadOnlyList(Of ConditionConceptEntry))(Pending.Take(limit).ToList())
        End Function

        Public Function CountPendingAsync(force As Boolean, cancellationToken As CancellationToken) _
                As Task(Of Integer) Implements IConditionConceptStore.CountPendingAsync
            LastForce = force
            Return Task.FromResult(Pending.Count)
        End Function
    End Class

    Private NotInheritable Class NullUmlsClient
        Implements IUmlsClient

        Public Function SearchAsync(concept As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IUmlsClient.SearchAsync
            Return Task.FromResult(Of IReadOnlyList(Of UmlsCandidate))(Array.Empty(Of UmlsCandidate)())
        End Function

        Public Function GetSemanticTypeAssignmentsAsync(cui As String, cancellationToken As CancellationToken) _
                As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) Implements IUmlsClient.GetSemanticTypeAssignmentsAsync
            Return Task.FromResult(Of IReadOnlyList(Of SemanticTypeAssignment))(Array.Empty(Of SemanticTypeAssignment)())
        End Function
    End Class

    Private Shared Function NewJob(store As JobStore) As ConditionNormalizeJob
        Return New ConditionNormalizeJob(
                store,
                New ConditionNormalizer(store, New NullUmlsClient(), New UmlsMatchScorer()))
    End Function

    <Fact>
    Public Async Function Run_seeds_then_resolves_and_counts_outcomes() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 9})
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "zzqq", .RawForm = "Zzqq", .StudyCount = 1})
        store.ExactByNorm("stroke") = {New ConditionCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US", hasHierarchy:=False)}

        Dim counters = Await NewJob(store).RunAsync(
                New NormalizeConditionsOptions(), Nothing, CancellationToken.None)

        Assert.Equal(1, store.SeedCalls)
        Assert.Equal(2, counters.Done)
        Assert.Equal(1, counters.Resolved)
        Assert.Equal(1, counters.Unresolved)
        Assert.Equal(2, store.Upserted.Count)
        Assert.Equal("C0038454", store.Upserted.First(Function(e) e.ConditionNorm = "stroke").ConceptCode)
    End Function

    <Fact>
    Public Async Function DryRun_resolves_but_writes_nothing() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 9})
        store.ExactByNorm("stroke") = {New ConditionCandidate("C0038454", "CVA - Cerebrovascular accident", "SNOMEDCT_US", hasHierarchy:=False)}

        Dim counters = Await NewJob(store).RunAsync(
                New NormalizeConditionsOptions With {.DryRun = True}, Nothing, CancellationToken.None)

        Assert.Equal(1, counters.Resolved)
        Assert.Empty(store.Upserted)
        ' Dry-run must not write ANYTHING, including the seed. SeedFromCorpusAsync
        ' inserts missing rows and rewrites raw_form/study_count on existing ones,
        ' so it counts as a write and must be skipped entirely, not just gated at
        ' the per-row upsert.
        Assert.Equal(0, store.SeedCalls)
    End Function

    <Fact>
    Public Async Function Run_preserves_study_count_on_the_upserted_row() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 42})
        store.ExactByNorm("stroke") = {New ConditionCandidate("C0038454", "CVA", "SNOMEDCT_US", hasHierarchy:=False)}

        Await NewJob(store).RunAsync(New NormalizeConditionsOptions(), Nothing, CancellationToken.None)

        Assert.Equal(42, store.Upserted.Single().StudyCount)
    End Function

    <Fact>
    Public Async Function CountRemaining_delegates_to_store_and_passes_force() As Task
        Dim store As New JobStore()
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "stroke", .RawForm = "Stroke", .StudyCount = 9})
        store.Pending.Add(New ConditionConceptEntry With {.ConditionNorm = "diabetes", .RawForm = "Diabetes", .StudyCount = 5})

        Dim job = NewJob(store)

        ' Test with force = True
        Dim result = Await job.CountRemainingAsync(True, CancellationToken.None)
        Assert.Equal(2, result)
        Assert.True(store.LastForce.HasValue)
        Assert.True(store.LastForce.Value)

        ' Test with force = False
        result = Await job.CountRemainingAsync(False, CancellationToken.None)
        Assert.Equal(2, result)
        Assert.True(store.LastForce.HasValue)
        Assert.False(store.LastForce.Value)
    End Function
End Class

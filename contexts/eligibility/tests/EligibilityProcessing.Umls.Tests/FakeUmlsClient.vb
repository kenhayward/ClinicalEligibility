Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core

' In-memory IUmlsClient stand-in. Records every call and serves canned results.
' Used by UmlsCacheTests to verify the cache delegates exactly once per unique
' key and never on cache hits.

Friend NotInheritable Class FakeUmlsClient
    Implements IUmlsClient

    Public ReadOnly Property SearchCalls As New List(Of String)
    Public ReadOnly Property SemanticTypesCalls As New List(Of String)

    ' Keyed by lower-cased trimmed concept (mirrors how the real UMLS endpoint
    ' is queried case-insensitively).
    Public ReadOnly Property SearchResults As New Dictionary(Of String, IReadOnlyList(Of UmlsCandidate))(
            StringComparer.OrdinalIgnoreCase)

    ' Keyed by exact CUI (CUIs are case-sensitive on the UMLS API).
    Public ReadOnly Property SemanticTypesResults As New Dictionary(Of String, IReadOnlyList(Of SemanticTypeAssignment))(
            StringComparer.Ordinal)

    Public Property ExceptionToThrow As Exception

    Public Function SearchAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate)) _
            Implements IUmlsClient.SearchAsync
        SearchCalls.Add(concept)
        cancellationToken.ThrowIfCancellationRequested()
        If ExceptionToThrow IsNot Nothing Then Throw ExceptionToThrow

        Dim key = If(concept, "").Trim().ToLowerInvariant()
        Dim result As IReadOnlyList(Of UmlsCandidate) = Nothing
        If SearchResults.TryGetValue(key, result) Then
            Return Task.FromResult(result)
        End If
        Return Task.FromResult(CType(Array.Empty(Of UmlsCandidate)(), IReadOnlyList(Of UmlsCandidate)))
    End Function

    Public Function GetSemanticTypeAssignmentsAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SemanticTypeAssignment)) _
            Implements IUmlsClient.GetSemanticTypeAssignmentsAsync
        SemanticTypesCalls.Add(cui)
        cancellationToken.ThrowIfCancellationRequested()
        If ExceptionToThrow IsNot Nothing Then Throw ExceptionToThrow

        Dim result As IReadOnlyList(Of SemanticTypeAssignment) = Nothing
        If SemanticTypesResults.TryGetValue(cui, result) Then
            Return Task.FromResult(result)
        End If
        Return Task.FromResult(CType(Array.Empty(Of SemanticTypeAssignment)(), IReadOnlyList(Of SemanticTypeAssignment)))
    End Function

End Class

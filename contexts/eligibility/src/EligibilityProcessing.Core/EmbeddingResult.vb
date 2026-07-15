Imports System.Collections.Generic

' Outcome of one embedding call. Mirrors the LlmResponse success/failure
' factory pattern: transport failures surface as Failure rather than throwing,
' so callers (the Authoring similarity action, the CLI backfill) can degrade
' gracefully when the embedding endpoint is unreachable.

Public NotInheritable Class EmbeddingResult

    Private Sub New(succeeded As Boolean, vector As IReadOnlyList(Of Single), errorMessage As String)
        Me.Succeeded = succeeded
        Me.Vector = If(vector, CType(Array.Empty(Of Single)(), IReadOnlyList(Of Single)))
        Me.ErrorMessage = If(errorMessage, "")
    End Sub

    Public ReadOnly Property Succeeded As Boolean
    Public ReadOnly Property Vector As IReadOnlyList(Of Single)   ' empty when not succeeded
    Public ReadOnly Property ErrorMessage As String               ' empty when succeeded

    Public Shared Function Success(vector As IReadOnlyList(Of Single)) As EmbeddingResult
        Return New EmbeddingResult(True, vector, "")
    End Function

    Public Shared Function Failure(errorMessage As String) As EmbeddingResult
        Return New EmbeddingResult(False, Nothing, errorMessage)
    End Function

End Class

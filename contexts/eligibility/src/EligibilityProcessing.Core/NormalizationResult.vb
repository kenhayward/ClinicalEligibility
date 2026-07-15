' Outcome of one criterion-normalization LLM call (authoring specification
' §3.5). Mirrors LlmResponse / EmbeddingResult: transport failures surface as
' Failure rather than throwing, so the Authoring UI can show a clear message
' instead of a 500.

Public NotInheritable Class NormalizationResult

    Private Sub New(succeeded As Boolean, normalizedText As String, errorMessage As String)
        Me.Succeeded = succeeded
        Me.NormalizedText = If(normalizedText, "")
        Me.ErrorMessage = If(errorMessage, "")
    End Sub

    Public ReadOnly Property Succeeded As Boolean
    Public ReadOnly Property NormalizedText As String   ' empty when not succeeded
    Public ReadOnly Property ErrorMessage As String      ' empty when succeeded

    Public Shared Function Success(normalizedText As String) As NormalizationResult
        Return New NormalizationResult(True, normalizedText, "")
    End Function

    Public Shared Function Failure(errorMessage As String) As NormalizationResult
        Return New NormalizationResult(False, "", errorMessage)
    End Function

End Class

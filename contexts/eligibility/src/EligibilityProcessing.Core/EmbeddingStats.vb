Imports System.Collections.Generic

' Aggregate stats for the eligibility_study_embedding corpus index: the total row
' count plus the per-model breakdown. Backs the owner-only embeddings export/import
' surface - the export must note the embedding model(s) so that whoever imports the
' set knows which model to configure for Find Similar (cosine similarity is only
' meaningful between vectors from the same model).

Public NotInheritable Class EmbeddingStats

    Public Sub New(totalRows As Long, models As IReadOnlyList(Of EmbeddingModelCount))
        Me.TotalRows = totalRows
        Me.Models = If(models, CType(Array.Empty(Of EmbeddingModelCount)(), IReadOnlyList(Of EmbeddingModelCount)))
    End Sub

    ''' <summary>Total rows in eligibility_study_embedding.</summary>
    Public ReadOnly Property TotalRows As Long

    ''' <summary>Per-model row counts, most-common first. Empty when the index is empty.</summary>
    Public ReadOnly Property Models As IReadOnlyList(Of EmbeddingModelCount)

End Class

Public NotInheritable Class EmbeddingModelCount

    Public Sub New(model As String, count As Long)
        Me.Model = If(model, "")
        Me.Count = count
    End Sub

    ''' <summary>The embedding model name recorded on the rows (may be empty).</summary>
    Public ReadOnly Property Model As String

    ''' <summary>How many embedding rows carry this model.</summary>
    Public ReadOnly Property Count As Long

End Class

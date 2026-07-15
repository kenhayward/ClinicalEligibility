Imports System.Collections.Generic

' The fields of a study that feed its topic embedding (authoring specification
' §3.3.2). Built from eligibility_study_detail for processed AACT trials, or
' from an AuthoringStudy for the proposed study. EmbeddingTextBuilder turns
' this into the single text block sent to the embedding model.

Public NotInheritable Class StudyEmbeddingInput

    Public Sub New(
            nctId As String,
            briefTitle As String,
            officialTitle As String,
            briefSummary As String,
            conditions As IReadOnlyList(Of String),
            interventions As IReadOnlyList(Of Intervention))
        Me.NctId = If(nctId, "")
        Me.BriefTitle = If(briefTitle, "")
        Me.OfficialTitle = If(officialTitle, "")
        Me.BriefSummary = If(briefSummary, "")
        Me.Conditions = If(conditions, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.Interventions = If(interventions, CType(Array.Empty(Of Intervention)(), IReadOnlyList(Of Intervention)))
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property BriefTitle As String
    Public ReadOnly Property OfficialTitle As String
    Public ReadOnly Property BriefSummary As String
    Public ReadOnly Property Conditions As IReadOnlyList(Of String)
    Public ReadOnly Property Interventions As IReadOnlyList(Of Intervention)

End Class

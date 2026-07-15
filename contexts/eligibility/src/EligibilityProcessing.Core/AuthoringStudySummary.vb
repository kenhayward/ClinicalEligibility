' One row of the /Authoring landing-page list (spec §3.1.1). A lightweight
' projection of authoring_study with the authored-criterion count, so the list
' renders without loading every study's full detail.

Public NotInheritable Class AuthoringStudySummary

    Public Sub New(
            authoringStudyId As Guid,
            studyId As String,
            label As String,
            sourceKind As String,
            sourceRef As String,
            phase As String,
            createdAt As DateTimeOffset,
            updatedAt As DateTimeOffset,
            criterionCount As Integer)
        Me.AuthoringStudyId = authoringStudyId
        Me.StudyId = If(studyId, "")
        Me.Label = If(label, "")
        Me.SourceKind = If(sourceKind, "")
        Me.SourceRef = If(sourceRef, "")
        Me.Phase = If(phase, "")
        Me.CreatedAt = createdAt
        Me.UpdatedAt = updatedAt
        Me.CriterionCount = criterionCount
    End Sub

    Public ReadOnly Property AuthoringStudyId As Guid
    Public ReadOnly Property StudyId As String
    Public ReadOnly Property Label As String
    Public ReadOnly Property SourceKind As String
    Public ReadOnly Property SourceRef As String
    Public ReadOnly Property Phase As String
    Public ReadOnly Property CreatedAt As DateTimeOffset
    Public ReadOnly Property UpdatedAt As DateTimeOffset
    Public ReadOnly Property CriterionCount As Integer

End Class

' One row in the Analysis-tab Search modal's result list. A thin projection
' over public.eligibility_study_detail — just the columns the modal renders
' in its result table (NCT_ID, headline metadata, status / phase badges).
'
' Returned by <see cref="IPostgresGateway.SearchStudyDetailsAsync"/>, ordered
' by NCT_ID ascending.

Public NotInheritable Class StudySearchResult

    Public Sub New(
            nctId As String,
            briefTitle As String,
            overallStatus As String,
            phase As String,
            studyType As String,
            source As String,
            conditions As IReadOnlyList(Of String))
        Me.NctId = nctId
        Me.BriefTitle = If(briefTitle, "")
        Me.OverallStatus = If(overallStatus, "")
        Me.Phase = If(phase, "")
        Me.StudyType = If(studyType, "")
        Me.Source = If(source, "")
        Me.Conditions = If(conditions, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property BriefTitle As String
    Public ReadOnly Property OverallStatus As String
    Public ReadOnly Property Phase As String
    Public ReadOnly Property StudyType As String
    Public ReadOnly Property Source As String
    Public ReadOnly Property Conditions As IReadOnlyList(Of String)

End Class

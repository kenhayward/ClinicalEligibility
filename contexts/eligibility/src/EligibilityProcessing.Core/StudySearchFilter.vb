' Query parameters for the dashboard's Analysis-tab Search modal. All fields
' are optional; an empty / Nothing field means "do not filter on this column."
'
' Searches public.eligibility_study_detail — the persisted snapshot of AACT
' metadata captured during a pipeline run. Every text column is matched with
' case-insensitive substring (ILIKE %@value%). Conditions are stored as a
' text[] on the snapshot row and any-element-contains is the natural match.
'
' Used by <see cref="IPostgresGateway.SearchStudyDetailsAsync"/> behind the
' Analysis Search button.

Public NotInheritable Class StudySearchFilter

    Public Sub New(
            Optional nctId As String = Nothing,
            Optional briefTitle As String = Nothing,
            Optional officialTitle As String = Nothing,
            Optional overallStatus As String = Nothing,
            Optional phase As String = Nothing,
            Optional studyType As String = Nothing,
            Optional source As String = Nothing,
            Optional briefSummary As String = Nothing,
            Optional condition As String = Nothing,
            Optional gender As String = Nothing,
            Optional healthyVolunteers As String = Nothing)
        Me.NctId = NullIfBlank(nctId)
        Me.BriefTitle = NullIfBlank(briefTitle)
        Me.OfficialTitle = NullIfBlank(officialTitle)
        Me.OverallStatus = NullIfBlank(overallStatus)
        Me.Phase = NullIfBlank(phase)
        Me.StudyType = NullIfBlank(studyType)
        Me.Source = NullIfBlank(source)
        Me.BriefSummary = NullIfBlank(briefSummary)
        Me.Condition = NullIfBlank(condition)
        Me.Gender = NullIfBlank(gender)
        Me.HealthyVolunteers = NullIfBlank(healthyVolunteers)
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property BriefTitle As String
    Public ReadOnly Property OfficialTitle As String
    Public ReadOnly Property OverallStatus As String
    Public ReadOnly Property Phase As String
    Public ReadOnly Property StudyType As String
    Public ReadOnly Property Source As String
    Public ReadOnly Property BriefSummary As String
    Public ReadOnly Property Condition As String
    Public ReadOnly Property Gender As String
    Public ReadOnly Property HealthyVolunteers As String

    Public ReadOnly Property IsEmpty As Boolean
        Get
            Return NctId Is Nothing AndAlso BriefTitle Is Nothing AndAlso OfficialTitle Is Nothing _
                   AndAlso OverallStatus Is Nothing AndAlso Phase Is Nothing AndAlso StudyType Is Nothing _
                   AndAlso Source Is Nothing AndAlso BriefSummary Is Nothing AndAlso Condition Is Nothing _
                   AndAlso Gender Is Nothing AndAlso HealthyVolunteers Is Nothing
        End Get
    End Property

    Private Shared Function NullIfBlank(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return Nothing
        Return value.Trim()
    End Function

End Class

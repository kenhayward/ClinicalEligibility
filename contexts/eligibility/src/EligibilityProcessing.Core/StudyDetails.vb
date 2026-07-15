' High-level study metadata for the dashboard's Analysis tab. Composed from
' the AACT ctgov schema:
'   * ctgov.studies         — title, status, phase, dates, enrollment, source
'   * ctgov.brief_summaries — narrative abstract (1:1 with studies)
'   * ctgov.conditions      — names (many per study)
'   * ctgov.interventions   — interventions (many per study)
'
' Lookup is by NCT ID; returns Nothing from the gateway when the study isn't
' in the source database.

Public NotInheritable Class StudyDetails

    Public Sub New(
            nctId As String,
            briefTitle As String,
            officialTitle As String,
            overallStatus As String,
            phase As String,
            studyType As String,
            startDate As Date?,
            completionDate As Date?,
            primaryCompletionDate As Date?,
            enrollment As Integer?,
            enrollmentType As String,
            source As String,
            whyStopped As String,
            briefSummary As String,
            conditions As IReadOnlyList(Of String),
            interventions As IReadOnlyList(Of Intervention))
        Me.NctId = nctId
        Me.BriefTitle = If(briefTitle, "")
        Me.OfficialTitle = If(officialTitle, "")
        Me.OverallStatus = If(overallStatus, "")
        Me.Phase = If(phase, "")
        Me.StudyType = If(studyType, "")
        Me.StartDate = startDate
        Me.CompletionDate = completionDate
        Me.PrimaryCompletionDate = primaryCompletionDate
        Me.Enrollment = enrollment
        Me.EnrollmentType = If(enrollmentType, "")
        Me.Source = If(source, "")
        Me.WhyStopped = If(whyStopped, "")
        Me.BriefSummary = If(briefSummary, "")
        Me.Conditions = If(conditions, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.Interventions = If(interventions, CType(Array.Empty(Of Intervention)(), IReadOnlyList(Of Intervention)))
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property BriefTitle As String
    Public ReadOnly Property OfficialTitle As String
    Public ReadOnly Property OverallStatus As String
    Public ReadOnly Property Phase As String
    Public ReadOnly Property StudyType As String
    Public ReadOnly Property StartDate As Date?
    Public ReadOnly Property CompletionDate As Date?
    Public ReadOnly Property PrimaryCompletionDate As Date?
    Public ReadOnly Property Enrollment As Integer?
    Public ReadOnly Property EnrollmentType As String   ' "Actual" | "Anticipated" | ""
    Public ReadOnly Property Source As String           ' lead sponsor (denormalised on studies row)
    Public ReadOnly Property WhyStopped As String       ' non-empty only for terminated/withdrawn
    Public ReadOnly Property BriefSummary As String
    Public ReadOnly Property Conditions As IReadOnlyList(Of String)
    Public ReadOnly Property Interventions As IReadOnlyList(Of Intervention)

End Class

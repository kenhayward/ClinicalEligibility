' A persisted snapshot of one trial's source data, as seen by the Analysis tab.
' Composes the two AACT-derived projections — the study ID card (StudyDetails)
' and the raw eligibility block (SourceEligibilityDetails) — plus the timestamp
' the snapshot was captured.
'
' Stored one row per nct_id in public.eligibility_study_detail; written by the
' orchestrator during processing (refreshed each run) and read by the Web
' host's Analysis tab so the page renders without a live AACT connection.

Public NotInheritable Class StudySnapshot

    Public Sub New(
            details As StudyDetails,
            eligibility As SourceEligibilityDetails,
            capturedAt As DateTimeOffset)
        If details Is Nothing Then Throw New ArgumentNullException(NameOf(details))
        If eligibility Is Nothing Then Throw New ArgumentNullException(NameOf(eligibility))
        Me.Details = details
        Me.Eligibility = eligibility
        Me.CapturedAt = capturedAt
    End Sub

    Public ReadOnly Property Details As StudyDetails
    Public ReadOnly Property Eligibility As SourceEligibilityDetails
    Public ReadOnly Property CapturedAt As DateTimeOffset

    Public ReadOnly Property NctId As String
        Get
            Return Details.NctId
        End Get
    End Property

End Class

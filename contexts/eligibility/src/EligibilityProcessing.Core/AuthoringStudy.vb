Imports System.Collections.Generic

' A user-designed, not-yet-registered study — the study characteristics half
' of an authored study. See docs/specs/authoring specification.md §4.1.
'
' Mirrors the study ID-card columns of eligibility_study_detail, but keyed by
' a surrogate uuid (an authored study has no NCT_ID) and kept in its own
' table, separate from AACT-extracted data.
'
' Mutable by design — unlike the immutable pipeline DTOs, an authored study is
' edited field-by-field through the Authoring UI.

Public NotInheritable Class AuthoringStudy

    Public Property AuthoringStudyId As Guid

    ' User-facing, human-meaningful Study ID (e.g. a protocol number), distinct
    ' from the surrogate AuthoringStudyId. Required and unique (case-insensitive)
    ' for studies created after migration V13; fixed once set. May be empty for
    ' legacy studies created before the feature existed.
    Public Property StudyId As String = ""

    Public Property Label As String = ""
    Public Property SourceKind As String = "blank"   ' blank | aact | authored
    Public Property SourceRef As String = ""          ' NCT_ID or authoring_study_id of origin
    Public Property CreatedAt As DateTimeOffset
    Public Property UpdatedAt As DateTimeOffset

    ' Audit attribution (app_user.user_id). Populated on read for display; the
    ' acting user is passed to the gateway write methods, not taken from here.
    Public Property CreatedBy As Guid?
    Public Property LastUpdatedBy As Guid?

    ' Study ID card — mirrors StudyDetails / eligibility_study_detail.
    Public Property BriefTitle As String = ""
    Public Property OfficialTitle As String = ""
    Public Property OverallStatus As String = ""
    Public Property Phase As String = ""
    Public Property StudyType As String = ""
    Public Property StartDate As Date?
    Public Property CompletionDate As Date?
    Public Property PrimaryCompletionDate As Date?
    Public Property Enrollment As Integer?
    Public Property EnrollmentType As String = ""
    Public Property Source As String = ""             ' lead sponsor
    Public Property WhyStopped As String = ""
    Public Property BriefSummary As String = ""
    Public Property Conditions As List(Of String) = New List(Of String)()
    Public Property Interventions As List(Of Intervention) = New List(Of Intervention)()

End Class

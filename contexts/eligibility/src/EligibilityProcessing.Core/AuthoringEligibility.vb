' High-level eligibility data for an authored study — 1:1 with AuthoringStudy.
' See docs/specs/authoring specification.md §4.2.
'
' Mirrors the eligibility half of eligibility_study_detail (the structured
' fields AACT publishes alongside the free-text criteria). Mutable: edited
' through the Authoring UI's Setup form.

Public NotInheritable Class AuthoringEligibility

    Public Property AuthoringStudyId As Guid
    Public Property Criteria As String = ""
    Public Property Gender As String = ""
    Public Property MinimumAge As String = ""
    Public Property MaximumAge As String = ""
    Public Property HealthyVolunteers As String = ""
    Public Property SamplingMethod As String = ""
    Public Property Population As String = ""
    Public Property Adult As Boolean?
    Public Property Child As Boolean?
    Public Property OlderAdult As Boolean?

End Class

Imports System.Collections.Generic

' The full authored study loaded for the /Authoring/Edit page: the study
' characteristics, the 1:1 eligibility data, and the ordered authored-criteria
' list. Returned by IPostgresGateway.GetAuthoringStudyAsync.

Public NotInheritable Class AuthoringStudyAggregate

    Public Sub New(
            study As AuthoringStudy,
            eligibility As AuthoringEligibility,
            criteria As IReadOnlyList(Of AuthoringCriterion))
        Me.Study = study
        Me.Eligibility = eligibility
        Me.Criteria = If(criteria, CType(Array.Empty(Of AuthoringCriterion)(), IReadOnlyList(Of AuthoringCriterion)))
    End Sub

    Public ReadOnly Property Study As AuthoringStudy
    Public ReadOnly Property Eligibility As AuthoringEligibility
    Public ReadOnly Property Criteria As IReadOnlyList(Of AuthoringCriterion)

End Class

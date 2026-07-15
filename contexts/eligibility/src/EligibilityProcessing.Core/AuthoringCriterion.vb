Imports System.Collections.Generic

' One entry in an authored study's eligibility criteria list. See
' docs/specs/authoring specification.md §4.3 / §3.6.
'
' Each entry has an Inclusion/Exclusion type and editable normalized text, and
' may carry concept metadata + a provenance note recording the criterion
' cluster it was copied from. Ordered within a study by Ordinal.

Public NotInheritable Class AuthoringCriterion

    Public Property AuthoringCriterionId As Guid
    Public Property AuthoringStudyId As Guid
    Public Property Ordinal As Integer
    Public Property Criterion As String = ""          ' Inclusion | Exclusion
    Public Property NormalizedText As String = ""
    Public Property Concept As String = ""
    Public Property ConceptCode As String = ""
    Public Property SemanticType As String = ""
    Public Property Domain As String = ""
    Public Property SourceNote As String = ""         ' provenance: originating cluster
    Public Property ManualReason As String = ""       ' free-text rationale for a manual addition
    Public Property CreatedAt As DateTimeOffset
    Public Property UpdatedAt As DateTimeOffset

    ' Audit attribution (app_user.user_id). Populated on read for display; the
    ' acting user is passed to the gateway write methods, not taken from here.
    Public Property CreatedBy As Guid?
    Public Property LastUpdatedBy As Guid?

    ' The public.eligibility source records this criterion was normalized from
    ' (lineage). Persisted in authoring_criterion_source; see AuthoringCriterionSource.
    Public Property Sources As List(Of AuthoringCriterionSource) = New List(Of AuthoringCriterionSource)()

End Class

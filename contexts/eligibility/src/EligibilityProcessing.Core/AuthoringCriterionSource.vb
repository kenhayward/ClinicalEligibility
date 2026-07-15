' One source record behind an authored criterion — a snapshot of the
' public.eligibility row the criterion was normalized from. See
' docs/specs/authoring specification.md §3.5/§3.6.
'
' Snapshot, not reference: public.eligibility is rebuilt per-trial with
' DELETE+INSERT (spec §2.8.2), so EligibilityId is volatile and kept only as a
' best-effort live link; the row's content is copied so lineage survives a
' re-process. Persisted in authoring_criterion_source (migration V10).

Public NotInheritable Class AuthoringCriterionSource

    Public Property AuthoringCriterionSourceId As Guid
    Public Property AuthoringCriterionId As Guid
    Public Property EligibilityId As Long?            ' best-effort link to public.eligibility.id
    Public Property NctId As String = ""
    Public Property Criterion As String = ""          ' Inclusion | Exclusion (source row's)
    Public Property Domain As String = ""
    Public Property Concept As String = ""
    Public Property ConceptCode As String = ""
    Public Property SemanticType As String = ""
    Public Property Qualifier As String = ""
    Public Property TimeWindow As String = ""
    Public Property OriginalText As String = ""
    Public Property MatchScore As Decimal
    Public Property CreatedAt As DateTimeOffset

End Class

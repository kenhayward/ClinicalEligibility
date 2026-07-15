' A cluster of eligibility criteria shared across the similar studies — rows
' of public.eligibility grouped by criterion (Inclusion/Exclusion) and concept
' identity (authoring specification §3.4). Returned by
' IPostgresGateway.ClusterCommonCriteriaAsync, ordered by descending StudyCount.

Public NotInheritable Class CriterionCluster

    Public Sub New(
            criterion As String,
            groupKey As String,
            resolved As Boolean,
            concept As String,
            conceptCode As String,
            semanticType As String,
            studyCount As Integer,
            recordCount As Integer)
        Me.Criterion = If(criterion, "")
        Me.GroupKey = If(groupKey, "")
        Me.Resolved = resolved
        Me.Concept = If(concept, "")
        Me.ConceptCode = If(conceptCode, "")
        Me.SemanticType = If(semanticType, "")
        Me.StudyCount = studyCount
        Me.RecordCount = recordCount
    End Sub

    Public ReadOnly Property Criterion As String       ' Inclusion | Exclusion
    ' Opaque identity used to fetch the cluster's individual records — the
    ' concept_code when resolved, else 'concept:<lowercased concept text>'.
    Public ReadOnly Property GroupKey As String
    Public ReadOnly Property Resolved As Boolean       ' False = no UMLS concept code
    Public ReadOnly Property Concept As String
    Public ReadOnly Property ConceptCode As String     ' empty when unresolved
    Public ReadOnly Property SemanticType As String
    Public ReadOnly Property StudyCount As Integer     ' commonality — distinct studies
    Public ReadOnly Property RecordCount As Integer    ' total eligibility rows

End Class

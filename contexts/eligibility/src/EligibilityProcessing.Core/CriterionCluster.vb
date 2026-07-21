Imports System.Collections.Generic

' A cluster of eligibility criteria shared across the similar studies — rows
' of public.eligibility grouped by criterion (Inclusion/Exclusion) and, at
' rollup level 0, concept identity (authoring specification §3.4). Above level 0
' the grouping is by a shared broader concept from umls.concept_ancestor.
' Returned by IPostgresGateway.ClusterCommonCriteriaAsync, ordered by descending
' StudyCount.

Public NotInheritable Class CriterionCluster

    Public Sub New(
            criterion As String,
            groupKey As String,
            resolved As Boolean,
            concept As String,
            conceptCode As String,
            semanticType As String,
            studyCount As Integer,
            recordCount As Integer,
            ancestorCode As String,
            ancestorConcept As String,
            memberCodes As IReadOnlyList(Of String),
            rollupLevel As Integer)
        Me.Criterion = If(criterion, "")
        Me.GroupKey = If(groupKey, "")
        Me.Resolved = resolved
        Me.Concept = If(concept, "")
        Me.ConceptCode = If(conceptCode, "")
        Me.SemanticType = If(semanticType, "")
        Me.StudyCount = studyCount
        Me.RecordCount = recordCount
        Me.AncestorCode = If(ancestorCode, "")
        Me.AncestorConcept = If(ancestorConcept, "")
        Me.MemberCodes = If(memberCodes, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.RollupLevel = rollupLevel
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

    ''' <summary>
    ''' The broader concept this cluster was rolled up to, or empty when it was
    ''' not rolled up - either because rollupLevel was 0, or because no ancestor
    ''' covered more than this one concept.
    ''' </summary>
    Public ReadOnly Property AncestorCode As String

    ''' <summary>Display name for <see cref="AncestorCode"/>. Empty when not rolled up.</summary>
    Public ReadOnly Property AncestorConcept As String

    ''' <summary>
    ''' The concept codes merged into this cluster. Needed to fetch the cluster's
    ''' records: the ancestor CUI is not any row's concept_code, so a lookup by
    ''' group key alone would match nothing.
    ''' </summary>
    Public ReadOnly Property MemberCodes As IReadOnlyList(Of String)

    ''' <summary>The level this cluster was produced at. 0 = exact concept identity.</summary>
    Public ReadOnly Property RollupLevel As Integer

End Class

' One row from public.eligibility — the structured criterion records produced
' by the LLM extraction + UMLS resolution pipeline. Backing model for the
' dashboard's Results table browser.

Public NotInheritable Class EligibilityRow

    Public Sub New(
            id As Long,
            nctId As String,
            criterion As String,
            domain As String,
            concept As String,
            conceptCode As String,
            semanticType As String,
            qualifier As String,
            timeWindow As String,
            originalText As String,
            umlsName As String,
            matchScore As Double,
            matchSource As String,
            createdAt As DateTimeOffset)
        Me.Id = id
        Me.NctId = nctId
        Me.Criterion = criterion
        Me.Domain = domain
        Me.Concept = concept
        Me.ConceptCode = conceptCode
        Me.SemanticType = semanticType
        Me.Qualifier = qualifier
        Me.TimeWindow = timeWindow
        Me.OriginalText = originalText
        Me.UmlsName = umlsName
        Me.MatchScore = matchScore
        Me.MatchSource = matchSource
        Me.CreatedAt = createdAt
    End Sub

    Public ReadOnly Property Id As Long
    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criterion As String
    Public ReadOnly Property Domain As String
    Public ReadOnly Property Concept As String
    Public ReadOnly Property ConceptCode As String       ' empty when below match threshold
    Public ReadOnly Property SemanticType As String      ' empty when below match threshold
    Public ReadOnly Property Qualifier As String
    Public ReadOnly Property TimeWindow As String
    Public ReadOnly Property OriginalText As String
    Public ReadOnly Property UmlsName As String          ' empty when below match threshold
    Public ReadOnly Property MatchScore As Double        ' [0, 1] rounded to 3 dp
    Public ReadOnly Property MatchSource As String       ' empty when below match threshold
    Public ReadOnly Property CreatedAt As DateTimeOffset

End Class

' The result of UMLS best-match scoring for a single criterion concept.
'
' When `IsResolved` is False, all string fields are empty and `MatchScore`
' is 0 per spec section 2.8.1 / section 4.4. This is what gets persisted
' for criteria the scorer could not confidently link to a UMLS concept.

Public NotInheritable Class UmlsMatch

    Public Sub New(
            conceptCode As String,
            umlsName As String,
            matchSource As String,
            matchScore As Double)
        Me.ConceptCode = conceptCode
        Me.UmlsName = umlsName
        Me.MatchSource = matchSource
        Me.MatchScore = matchScore
    End Sub

    Public ReadOnly Property ConceptCode As String   ' UMLS CUI; empty when unresolved
    Public ReadOnly Property UmlsName As String      ' preferred name; empty when unresolved
    Public ReadOnly Property MatchSource As String   ' root source vocabulary; empty when unresolved
    Public ReadOnly Property MatchScore As Double    ' [0, 1] rounded to 3 dp; 0 when unresolved

    Public ReadOnly Property IsResolved As Boolean
        Get
            Return Not String.IsNullOrEmpty(ConceptCode)
        End Get
    End Property

    Public Shared ReadOnly Unresolved As New UmlsMatch(
            conceptCode:="",
            umlsName:="",
            matchSource:="",
            matchScore:=0.0)

End Class

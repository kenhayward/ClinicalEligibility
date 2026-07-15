Imports System.Collections.Generic

' A criterion after UMLS resolution — the final shape written to public.eligibility.
'
' Spec section 4.3 / 4.4 / 2.8.1. Composed from:
'   - a <see cref="CriterionRecord"/> (LLM-extracted)
'   - a <see cref="UmlsMatch"/> (best-match scoring result; Unresolved when below threshold)
'   - a list of UMLS semantic-type names for the matched CUI (empty when unresolved)
'
' When the match is unresolved (IsResolved = False), the UMLS-derived fields
' all hold the unresolved sentinels: empty strings for ConceptCode/UmlsName/
' MatchSource/SemanticType and 0.0 for MatchScore (spec section 2.8.1).

Public NotInheritable Class ResolvedRecord

    Public Sub New(
            criterion As CriterionRecord,
            umlsMatch As UmlsMatch,
            semanticTypes As IReadOnlyList(Of String))
        If criterion Is Nothing Then Throw New ArgumentNullException(NameOf(criterion))
        If umlsMatch Is Nothing Then Throw New ArgumentNullException(NameOf(umlsMatch))

        Me.NctId = criterion.NctId
        Me.Criterion = criterion.Criterion
        Me.Domain = criterion.Domain
        Me.Concept = criterion.Concept
        Me.Qualifier = criterion.Qualifier
        Me.TimeWindow = criterion.TimeWindow
        Me.OriginalText = criterion.OriginalText

        Me.ConceptCode = umlsMatch.ConceptCode
        Me.UmlsName = umlsMatch.UmlsName
        Me.MatchSource = umlsMatch.MatchSource
        Me.MatchScore = umlsMatch.MatchScore
        Me.SemanticType = If(semanticTypes Is Nothing OrElse semanticTypes.Count = 0,
                              "",
                              String.Join(", ", semanticTypes))
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criterion As String
    Public ReadOnly Property Domain As String
    Public ReadOnly Property Concept As String
    Public ReadOnly Property ConceptCode As String     ' UMLS CUI; empty when unresolved
    Public ReadOnly Property SemanticType As String    ' comma-joined names; empty when unresolved
    Public ReadOnly Property Qualifier As String
    Public ReadOnly Property TimeWindow As String
    Public ReadOnly Property OriginalText As String
    Public ReadOnly Property UmlsName As String        ' preferred UMLS name; empty when unresolved
    Public ReadOnly Property MatchScore As Double      ' [0, 1] rounded to 3 dp; 0 when unresolved
    Public ReadOnly Property MatchSource As String     ' root source vocabulary; empty when unresolved

End Class

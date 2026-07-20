' Query parameters for the dashboard's Results table browser. All fields are
' optional; an empty / Nothing field means "do not filter on this column."
'
' Match semantics applied by <see cref="IPostgresGateway.SearchEligibilityAsync"/>:
'   - NctId, Domain, ConceptCode : exact match (column = @value)
'   - Criterion, Concept         : substring match (column ILIKE %@value%)
'   - SemanticTypeTuis           : array overlap (semantic_type_tuis && @value)
'
' The exact-match columns are the ones likely to appear as dropdowns in the UI
' (categorical IDs / enum-shaped values). The substring columns hold human-
' readable text that's useful to grep.

Imports System.Collections.Generic
Imports System.Linq

Public NotInheritable Class EligibilityFilter

    Public Sub New(
            Optional nctId As String = Nothing,
            Optional criterion As String = Nothing,
            Optional domain As String = Nothing,
            Optional concept As String = Nothing,
            Optional conceptCode As String = Nothing,
            Optional semanticTypeTuis As IReadOnlyList(Of String) = Nothing)
        Me.NctId = NullIfBlank(nctId)
        Me.Criterion = NullIfBlank(criterion)
        Me.Domain = NullIfBlank(domain)
        Me.Concept = NullIfBlank(concept)
        Me.ConceptCode = NullIfBlank(conceptCode)
        ' Blank entries dropped and the list deduped, so a stray "" from a form
        ' post cannot turn "no filter" into "match nothing".
        Me.SemanticTypeTuis =
            If(semanticTypeTuis, CType(Array.Empty(Of String)(), IReadOnlyList(Of String))) _
                .Select(Function(t) If(t, "").Trim()) _
                .Where(Function(t) t.Length > 0) _
                .Distinct(StringComparer.OrdinalIgnoreCase) _
                .ToArray()
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criterion As String
    Public ReadOnly Property Domain As String
    Public ReadOnly Property Concept As String
    Public ReadOnly Property ConceptCode As String

    ''' <summary>
    ''' Semantic type ids to match. Multi-value and OR-combined: a row matches if
    ''' it carries ANY of them. Empty means "no filter", NOT "match nothing".
    ''' </summary>
    ''' <remarks>
    ''' TUIs rather than names because they are stable across UMLS releases, and
    ''' because the display string cannot be matched reliably - several semantic
    ''' type names contain commas, so the joined form is ambiguous. Matching the
    ''' whole joined string under-reported by 62% on the production corpus.
    ''' </remarks>
    Public ReadOnly Property SemanticTypeTuis As IReadOnlyList(Of String)

    Public ReadOnly Property IsEmpty As Boolean
        Get
            Return NctId Is Nothing AndAlso Criterion Is Nothing AndAlso Domain Is Nothing _
                   AndAlso Concept Is Nothing AndAlso ConceptCode Is Nothing _
                   AndAlso SemanticTypeTuis.Count = 0
        End Get
    End Property

    Private Shared Function NullIfBlank(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return Nothing
        Return value.Trim()
    End Function

End Class

' Query parameters for the dashboard's Results table browser. All fields are
' optional; an empty / Nothing field means "do not filter on this column."
'
' Match semantics applied by <see cref="IPostgresGateway.SearchEligibilityAsync"/>:
'   - NctId, Domain, ConceptCode, SemanticType : exact match (column = @value)
'   - Criterion, Concept                       : substring match (column ILIKE %@value%)
'
' The exact-match columns are the ones likely to appear as dropdowns in the UI
' (categorical IDs / enum-shaped values). The substring columns hold human-
' readable text that's useful to grep.

Public NotInheritable Class EligibilityFilter

    Public Sub New(
            Optional nctId As String = Nothing,
            Optional criterion As String = Nothing,
            Optional domain As String = Nothing,
            Optional concept As String = Nothing,
            Optional conceptCode As String = Nothing,
            Optional semanticType As String = Nothing)
        Me.NctId = NullIfBlank(nctId)
        Me.Criterion = NullIfBlank(criterion)
        Me.Domain = NullIfBlank(domain)
        Me.Concept = NullIfBlank(concept)
        Me.ConceptCode = NullIfBlank(conceptCode)
        Me.SemanticType = NullIfBlank(semanticType)
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criterion As String
    Public ReadOnly Property Domain As String
    Public ReadOnly Property Concept As String
    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property SemanticType As String

    Public ReadOnly Property IsEmpty As Boolean
        Get
            Return NctId Is Nothing AndAlso Criterion Is Nothing AndAlso Domain Is Nothing _
                   AndAlso Concept Is Nothing AndAlso ConceptCode Is Nothing AndAlso SemanticType Is Nothing
        End Get
    End Property

    Private Shared Function NullIfBlank(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return Nothing
        Return value.Trim()
    End Function

End Class

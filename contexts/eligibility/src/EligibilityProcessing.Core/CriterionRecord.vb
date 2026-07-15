' One extracted eligibility statement before UMLS resolution.
' Field shape per spec section 4.2 (intermediate record, LLM output post-parse).

Public NotInheritable Class CriterionRecord

    Public Sub New(
            nctId As String,
            criterion As String,
            domain As String,
            concept As String,
            qualifier As String,
            timeWindow As String,
            originalText As String)
        Me.NctId = nctId
        Me.Criterion = criterion
        Me.Domain = domain
        Me.Concept = concept
        Me.Qualifier = qualifier
        Me.TimeWindow = timeWindow
        Me.OriginalText = originalText
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criterion As String
    Public ReadOnly Property Domain As String
    Public ReadOnly Property Concept As String
    Public ReadOnly Property Qualifier As String
    Public ReadOnly Property TimeWindow As String
    Public ReadOnly Property OriginalText As String

    ' Spec section 2.5 step 9: emitted when zero records survive a whole batch,
    ' so the per-item downstream topology does not collapse. Persistence skips
    ' it because NctId is empty (section 2.8.2).
    Public Shared ReadOnly Empty As New CriterionRecord(
            nctId:="",
            criterion:="",
            domain:="",
            concept:="",
            qualifier:="",
            timeWindow:="",
            originalText:="")

End Class

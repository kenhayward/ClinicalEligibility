' One row read from the source eligibility table (ctgov.eligibilities).
' Spec section 3.1 / 4.1: the source schema exposes nct_id + criteria; this
' record is the minimal pair the orchestrator hands to the LLM extractor.

Public NotInheritable Class Trial

    Public Sub New(nctId As String, criteria As String)
        Me.NctId = nctId
        Me.Criteria = criteria
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criteria As String

End Class

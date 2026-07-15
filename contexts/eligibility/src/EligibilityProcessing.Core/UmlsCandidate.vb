' One candidate returned from the UMLS /search/current endpoint.
' Fields mirror the response shape per spec section 2.6 / 2.8.1.

Public NotInheritable Class UmlsCandidate

    Public Sub New(ui As String, name As String, rootSource As String)
        Me.Ui = ui
        Me.Name = name
        Me.RootSource = rootSource
    End Sub

    Public ReadOnly Property Ui As String          ' UMLS CUI, e.g. "C0011860"
    Public ReadOnly Property Name As String        ' preferred name of the concept
    Public ReadOnly Property RootSource As String  ' source vocabulary, e.g. "MSH", "SNOMEDCT_US"

End Class

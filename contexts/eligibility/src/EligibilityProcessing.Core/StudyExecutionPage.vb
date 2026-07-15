' One page of <see cref="StudyExecution"/> rows from a Studies-tab search.
' Paired with the unfiltered total count and the page coordinates so the
' dashboard can render a "Page X of Y (Z rows total)" footer.

Public NotInheritable Class StudyExecutionPage

    Public Sub New(
            rows As IReadOnlyList(Of StudyExecution),
            totalRows As Long,
            page As Integer,
            pageSize As Integer)
        Me.Rows = If(rows, CType(Array.Empty(Of StudyExecution)(), IReadOnlyList(Of StudyExecution)))
        Me.TotalRows = Math.Max(totalRows, 0)
        Me.Page = Math.Max(page, 1)
        Me.PageSize = Math.Max(pageSize, 1)
    End Sub

    Public ReadOnly Property Rows As IReadOnlyList(Of StudyExecution)
    Public ReadOnly Property TotalRows As Long
    Public ReadOnly Property Page As Integer
    Public ReadOnly Property PageSize As Integer

    Public ReadOnly Property TotalPages As Integer
        Get
            If TotalRows <= 0 Then Return 0
            Return CInt(Math.Ceiling(CDbl(TotalRows) / CDbl(PageSize)))
        End Get
    End Property

    Public Shared ReadOnly Empty As New StudyExecutionPage(
            Array.Empty(Of StudyExecution)(), 0, 1, 20)

End Class

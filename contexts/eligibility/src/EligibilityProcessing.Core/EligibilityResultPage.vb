' One page of <see cref="EligibilityRow"/> results from a Results-tab search.
' Pairs the row slice with the unfiltered total count and the page coordinates
' so the dashboard can render a "Page X of Y (Z rows total)" footer and decide
' whether to enable Next / Prev buttons.

Public NotInheritable Class EligibilityResultPage

    Public Sub New(
            rows As IReadOnlyList(Of EligibilityRow),
            totalRows As Long,
            page As Integer,
            pageSize As Integer)
        Me.Rows = If(rows, CType(Array.Empty(Of EligibilityRow)(), IReadOnlyList(Of EligibilityRow)))
        Me.TotalRows = Math.Max(totalRows, 0)
        Me.Page = Math.Max(page, 1)
        Me.PageSize = Math.Max(pageSize, 1)
    End Sub

    Public ReadOnly Property Rows As IReadOnlyList(Of EligibilityRow)
    Public ReadOnly Property TotalRows As Long
    Public ReadOnly Property Page As Integer
    Public ReadOnly Property PageSize As Integer

    Public ReadOnly Property TotalPages As Integer
        Get
            If TotalRows <= 0 Then Return 0
            Return CInt(Math.Ceiling(CDbl(TotalRows) / CDbl(PageSize)))
        End Get
    End Property

    Public Shared ReadOnly Empty As New EligibilityResultPage(
            Array.Empty(Of EligibilityRow)(), 0, 1, 20)

End Class

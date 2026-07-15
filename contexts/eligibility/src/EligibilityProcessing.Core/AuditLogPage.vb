' One page of audit_log rows for the Audit Trail view, paired with the
' unfiltered-by-page total so the modal can render a "Page X of Y (N total)"
' footer. Mirrors StudyExecutionPage.

Public NotInheritable Class AuditLogPage

    Public Sub New(
            rows As IReadOnlyList(Of AuditEntry),
            totalRows As Long,
            page As Integer,
            pageSize As Integer)
        Me.Rows = If(rows, CType(Array.Empty(Of AuditEntry)(), IReadOnlyList(Of AuditEntry)))
        Me.TotalRows = Math.Max(totalRows, 0)
        Me.Page = Math.Max(page, 1)
        Me.PageSize = Math.Max(pageSize, 1)
    End Sub

    Public ReadOnly Property Rows As IReadOnlyList(Of AuditEntry)
    Public ReadOnly Property TotalRows As Long
    Public ReadOnly Property Page As Integer
    Public ReadOnly Property PageSize As Integer

    Public ReadOnly Property TotalPages As Integer
        Get
            If TotalRows <= 0 Then Return 0
            Return CInt(Math.Ceiling(CDbl(TotalRows) / CDbl(PageSize)))
        End Get
    End Property

End Class

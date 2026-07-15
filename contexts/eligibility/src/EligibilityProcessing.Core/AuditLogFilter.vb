' Filter for an audit-log query (Manage Accounts → Audit Trail). Every field is
' optional; an empty string / Nothing means "no constraint on this dimension".

Public NotInheritable Class AuditLogFilter

    ''' <summary>Case-insensitive substring match on user_label.</summary>
    Public Property UserSearch As String = ""

    ''' <summary>Exact action match (e.g. "login", "create"); empty = all actions.</summary>
    Public Property Action As String = ""

    ''' <summary>Lower bound on occurred_at (inclusive).</summary>
    Public Property FromUtc As DateTimeOffset?

    ''' <summary>Upper bound on occurred_at (inclusive).</summary>
    Public Property ToUtc As DateTimeOffset?

End Class

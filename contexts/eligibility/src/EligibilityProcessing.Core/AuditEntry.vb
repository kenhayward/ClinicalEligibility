' One row in the append-only audit trail (public.audit_log, migration V12).
' Captures every manual create/update/delete plus every login. UserId is Nothing
' for failed/unknown logins; UserLabel snapshots a human-readable userid/email so
' the row stays legible even after the user is deleted.

Public NotInheritable Class AuditEntry

    ''' <summary>Populated on read (the audit_log identity); 0 when constructing a row to insert.</summary>
    Public Property AuditId As Long
    Public Property OccurredAt As DateTimeOffset
    Public Property UserId As Guid?
    Public Property UserLabel As String = ""
    Public Property Action As String = ""              ' create|update|delete|login|login_denied|bootstrap|role_change
    Public Property EntityType As String = ""          ' authoring_study|authoring_criterion|app_user|session|...
    Public Property EntityId As String = ""            ' affected record id (text)
    Public Property Detail As String = ""

End Class

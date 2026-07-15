' An application user — credentials, role, and audit timestamps. Persisted in
' public.app_user (migration V11).
'
' A user may sign in with a password, a linked Google account, or both
' (linking by email). PasswordHash / GoogleSubject are empty when absent — the
' gateway collapses empty to NULL on write, consistent with the rest of the
' output-DB tables.

Public NotInheritable Class AppUser

    Public Property UserId As Guid
    Public Property UserName As String = ""           ' login "userid"
    Public Property Email As String = ""              ' Google-linking match key
    Public Property DisplayName As String = ""
    Public Property Role As Role = Role.Viewer
    Public Property PasswordHash As String = ""        ' empty = no password (Google-only)
    Public Property GoogleSubject As String = ""       ' empty = not linked to Google
    Public Property PictureUrl As String = ""
    Public Property IsActive As Boolean = True
    Public Property CreatedAt As DateTimeOffset
    Public Property UpdatedAt As DateTimeOffset
    Public Property LastLoginAt As DateTimeOffset?

End Class

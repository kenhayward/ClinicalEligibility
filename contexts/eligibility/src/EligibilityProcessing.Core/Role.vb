' The four application roles (one per user). Owner and Administrator share all
' permissions; the distinction exists only so the app can protect the last Owner
' from deletion/demotion. Stored in app_user.role as the canonical text below and
' carried in the auth cookie as a role claim.

Public Enum Role
    Viewer = 0
    Author = 1
    Administrator = 2
    Owner = 3
End Enum

''' <summary>
''' Text↔enum conversion + permission helpers for <see cref="Role"/>. The string
''' constants are the canonical persisted/claim values.
''' </summary>
Public Module Roles

    Public Const Owner As String = "Owner"
    Public Const Administrator As String = "Administrator"
    Public Const Author As String = "Author"
    Public Const Viewer As String = "Viewer"

    ''' <summary>The canonical text for a role (as stored and put in claims).</summary>
    Public Function ToRoleName(role As Role) As String
        Select Case role
            Case Role.Owner : Return Owner
            Case Role.Administrator : Return Administrator
            Case Role.Author : Return Author
            Case Else : Return Viewer
        End Select
    End Function

    ''' <summary>
    ''' Parses a role name case-insensitively. Returns False for null/unknown
    ''' values (callers decide whether that's an error).
    ''' </summary>
    Public Function TryParseRole(value As String, ByRef role As Role) As Boolean
        role = Role.Viewer
        If String.IsNullOrWhiteSpace(value) Then Return False
        Select Case value.Trim().ToLowerInvariant()
            Case "owner" : role = Role.Owner
            Case "administrator" : role = Role.Administrator
            Case "author" : role = Role.Author
            Case "viewer" : role = Role.Viewer
            Case Else : Return False
        End Select
        Return True
    End Function

    ''' <summary>Parses a role name, throwing on an unknown value.</summary>
    Public Function ParseRole(value As String) As Role
        Dim r As Role
        If Not TryParseRole(value, r) Then
            Throw New ArgumentException($"Unknown role '{value}'.", NameOf(value))
        End If
        Return r
    End Function

    ''' <summary>Owner or Administrator — the "admin level" that may manage users and run the pipeline.</summary>
    Public Function IsAdminLevel(role As Role) As Boolean
        Return role = Role.Owner OrElse role = Role.Administrator
    End Function

    ''' <summary>Owner, Administrator, or Author — may write authored studies/criteria.</summary>
    Public Function CanAuthorWrite(role As Role) As Boolean
        Return IsAdminLevel(role) OrElse role = Role.Author
    End Function

End Module

Imports EligibilityProcessing.Core
Imports Xunit

' Unit tests for the Role enum + Roles helper module: text round-trip and the
' permission helpers that back the authorization policies.

Public Class RoleTests

    <Theory>
    <InlineData(Role.Owner, "Owner")>
    <InlineData(Role.Administrator, "Administrator")>
    <InlineData(Role.Author, "Author")>
    <InlineData(Role.Viewer, "Viewer")>
    Public Sub ToRoleName_returns_canonical_text(role As Role, expected As String)
        Assert.Equal(expected, Roles.ToRoleName(role))
    End Sub

    <Theory>
    <InlineData("Owner", Role.Owner)>
    <InlineData("administrator", Role.Administrator)>
    <InlineData("  Author  ", Role.Author)>
    <InlineData("VIEWER", Role.Viewer)>
    Public Sub TryParseRole_parses_case_and_whitespace_insensitively(input As String, expected As Role)
        Dim role As Role
        Assert.True(Roles.TryParseRole(input, role))
        Assert.Equal(expected, role)
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData(Nothing)>
    <InlineData("superuser")>
    Public Sub TryParseRole_returns_false_for_unknown(input As String)
        Dim role As Role
        Assert.False(Roles.TryParseRole(input, role))
        Assert.Equal(Role.Viewer, role)   ' defaults to least privilege
    End Sub

    <Fact>
    Public Sub ParseRole_throws_on_unknown()
        Assert.Throws(Of ArgumentException)(Function() Roles.ParseRole("nope"))
    End Sub

    <Fact>
    Public Sub RoundTrip_text_to_enum_to_text()
        For Each r In {Role.Owner, Role.Administrator, Role.Author, Role.Viewer}
            Assert.Equal(r, Roles.ParseRole(Roles.ToRoleName(r)))
        Next
    End Sub

    <Theory>
    <InlineData(Role.Owner, True)>
    <InlineData(Role.Administrator, True)>
    <InlineData(Role.Author, False)>
    <InlineData(Role.Viewer, False)>
    Public Sub IsAdminLevel_is_owner_or_administrator(role As Role, expected As Boolean)
        Assert.Equal(expected, Roles.IsAdminLevel(role))
    End Sub

    <Theory>
    <InlineData(Role.Owner, True)>
    <InlineData(Role.Administrator, True)>
    <InlineData(Role.Author, True)>
    <InlineData(Role.Viewer, False)>
    Public Sub CanAuthorWrite_includes_author(role As Role, expected As Boolean)
        Assert.Equal(expected, Roles.CanAuthorWrite(role))
    End Sub

End Class

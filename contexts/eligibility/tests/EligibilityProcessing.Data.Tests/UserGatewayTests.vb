Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports Xunit

' Integration tests for the auth/audit gateway methods (migrations V11/V12):
' app_user CRUD + counts + Google linking, and audit_log inserts. Same
' Testcontainers-backed fixture + Skip-if-no-Docker discipline as the other
' gateway integration tests.

Public Class UserGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    Private Shared Function NewUser(userName As String, role As Role,
                                    Optional email As String = Nothing,
                                    Optional passwordHash As String = "hash",
                                    Optional googleSubject As String = "") As AppUser
        Return New AppUser With {
                .UserId = Guid.NewGuid(),
                .UserName = userName,
                .Email = If(email, userName & "@example.com"),
                .DisplayName = userName,
                .Role = role,
                .PasswordHash = passwordHash,
                .GoogleSubject = googleSubject,
                .IsActive = True}
    End Function

    <SkippableFact>
    Public Async Function CountUsers_is_zero_on_empty_table() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()
        Assert.Equal(0, Await _fixture.Gateway.CountUsersAsync(CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function Create_then_get_round_trips_user() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim user = NewUser("owner", Role.Owner)
        user.PictureUrl = "https://pics/owner.png"
        Await _fixture.Gateway.CreateUserAsync(user, CancellationToken.None)

        Assert.Equal(1, Await _fixture.Gateway.CountUsersAsync(CancellationToken.None))
        Assert.Equal(1, Await _fixture.Gateway.CountOwnersAsync(CancellationToken.None))

        Dim loaded = Await _fixture.Gateway.GetUserAsync(user.UserId, CancellationToken.None)
        Assert.NotNull(loaded)
        Assert.Equal("owner", loaded.UserName)
        Assert.Equal(Role.Owner, loaded.Role)
        Assert.Equal("hash", loaded.PasswordHash)
        Assert.Equal("https://pics/owner.png", loaded.PictureUrl)
        Assert.True(loaded.IsActive)
    End Function

    <SkippableFact>
    Public Async Function GetUserByUserName_and_email_are_case_insensitive() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.CreateUserAsync(NewUser("Alice", Role.Author, email:="Alice@Example.com"), CancellationToken.None)

        Assert.NotNull(Await _fixture.Gateway.GetUserByUserNameAsync("alice", CancellationToken.None))
        Assert.NotNull(Await _fixture.Gateway.GetUserByUserNameAsync("ALICE", CancellationToken.None))
        Assert.NotNull(Await _fixture.Gateway.GetUserByEmailAsync("alice@example.com", CancellationToken.None))
        Assert.Null(Await _fixture.Gateway.GetUserByUserNameAsync("bob", CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function Duplicate_user_name_violates_unique_index() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.CreateUserAsync(NewUser("dupe", Role.Viewer), CancellationToken.None)
        ' Same name (different case) + different email — the lower(user_name)
        ' unique index must reject it.
        Dim clash = NewUser("DUPE", Role.Viewer, email:="other@example.com")
        Await Assert.ThrowsAsync(Of PostgresException)(
            Function() _fixture.Gateway.CreateUserAsync(clash, CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function UpdateRole_and_password_persist() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim user = NewUser("changer", Role.Viewer)
        Await _fixture.Gateway.CreateUserAsync(user, CancellationToken.None)

        Await _fixture.Gateway.UpdateUserRoleAsync(user.UserId, Role.Administrator, CancellationToken.None)
        Await _fixture.Gateway.UpdateUserPasswordHashAsync(user.UserId, "newhash", CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetUserAsync(user.UserId, CancellationToken.None)
        Assert.Equal(Role.Administrator, loaded.Role)
        Assert.Equal("newhash", loaded.PasswordHash)
    End Function

    <SkippableFact>
    Public Async Function LinkGoogleSubject_attaches_and_is_findable() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Password-only account, then link a Google identity by id.
        Dim user = NewUser("googler", Role.Author, googleSubject:="")
        Await _fixture.Gateway.CreateUserAsync(user, CancellationToken.None)
        Assert.Null(Await _fixture.Gateway.GetUserByGoogleSubjectAsync("g-123", CancellationToken.None))

        Await _fixture.Gateway.LinkGoogleSubjectAsync(user.UserId, "g-123", "https://pic", CancellationToken.None)

        Dim byGoogle = Await _fixture.Gateway.GetUserByGoogleSubjectAsync("g-123", CancellationToken.None)
        Assert.NotNull(byGoogle)
        Assert.Equal(user.UserId, byGoogle.UserId)
        Assert.Equal("g-123", byGoogle.GoogleSubject)
        Assert.Equal("https://pic", byGoogle.PictureUrl)
    End Function

    <SkippableFact>
    Public Async Function RecordLogin_sets_last_login_at() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim user = NewUser("logger", Role.Viewer)
        Await _fixture.Gateway.CreateUserAsync(user, CancellationToken.None)
        Assert.Null((Await _fixture.Gateway.GetUserAsync(user.UserId, CancellationToken.None)).LastLoginAt)

        Dim at = DateTimeOffset.UtcNow
        Await _fixture.Gateway.RecordLoginAsync(user.UserId, at, CancellationToken.None)

        Dim loaded = Await _fixture.Gateway.GetUserAsync(user.UserId, CancellationToken.None)
        Assert.True(loaded.LastLoginAt.HasValue)
    End Function

    <SkippableFact>
    Public Async Function ListUsers_orders_by_user_name() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.CreateUserAsync(NewUser("charlie", Role.Viewer), CancellationToken.None)
        Await _fixture.Gateway.CreateUserAsync(NewUser("alice", Role.Owner), CancellationToken.None)
        Await _fixture.Gateway.CreateUserAsync(NewUser("bob", Role.Author), CancellationToken.None)

        Dim list = Await _fixture.Gateway.ListUsersAsync(CancellationToken.None)
        Assert.Equal(3, list.Count)
        Assert.Equal("alice", list(0).UserName)
        Assert.Equal("bob", list(1).UserName)
        Assert.Equal("charlie", list(2).UserName)
    End Function

    <SkippableFact>
    Public Async Function DeleteUser_removes_the_row() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim user = NewUser("temp", Role.Viewer)
        Await _fixture.Gateway.CreateUserAsync(user, CancellationToken.None)
        Assert.Equal(1, Await _fixture.Gateway.DeleteUserAsync(user.UserId, CancellationToken.None))
        Assert.Null(Await _fixture.Gateway.GetUserAsync(user.UserId, CancellationToken.None))
        Assert.Equal(0, Await _fixture.Gateway.DeleteUserAsync(user.UserId, CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function CountOwners_counts_only_active_owners() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.CreateUserAsync(NewUser("o1", Role.Owner), CancellationToken.None)
        Await _fixture.Gateway.CreateUserAsync(NewUser("o2", Role.Owner), CancellationToken.None)
        Await _fixture.Gateway.CreateUserAsync(NewUser("a1", Role.Administrator), CancellationToken.None)

        Assert.Equal(2, Await _fixture.Gateway.CountOwnersAsync(CancellationToken.None))
    End Function

    <SkippableFact>
    Public Async Function InsertAudit_round_trips() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim actor = Guid.NewGuid()
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = DateTimeOffset.UtcNow,
                .UserId = actor,
                .UserLabel = "alice",
                .Action = "create",
                .EntityType = "authoring_study",
                .EntityId = "abc",
                .Detail = "made a study"}, CancellationToken.None)
        ' A denied login carries no user id.
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = DateTimeOffset.UtcNow,
                .UserId = Nothing,
                .UserLabel = "stranger@example.com",
                .Action = "login_denied",
                .EntityType = "session"}, CancellationToken.None)

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM public.audit_log"
                Assert.Equal(2L, Convert.ToInt64(Await cmd.ExecuteScalarAsync()))
            End Using
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM public.audit_log WHERE user_id IS NULL AND action = 'login_denied'"
                Assert.Equal(1L, Convert.ToInt64(Await cmd.ExecuteScalarAsync()))
            End Using
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT user_id FROM public.audit_log WHERE action = 'create'"
                Assert.Equal(actor, CType(Await cmd.ExecuteScalarAsync(), Guid))
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function EnsureSchema_is_idempotent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        ' Re-running every migration (V1..V12) must not throw — guards the
        ' V11/V12 idempotency (CREATE/ALTER ... IF NOT EXISTS).
        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)
        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)
    End Function

    <SkippableFact>
    Public Async Function GetAuditLog_filters_and_paginates() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim t0 = DateTimeOffset.UtcNow
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = t0.AddMinutes(-50), .UserLabel = "alice", .Action = "login", .EntityType = "session"}, CancellationToken.None)
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = t0.AddMinutes(-40), .UserLabel = "bob", .Action = "create", .EntityType = "authoring_study", .EntityId = "s1"}, CancellationToken.None)
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = t0.AddMinutes(-30), .UserLabel = "alice", .Action = "update", .EntityType = "authoring_study", .EntityId = "s1"}, CancellationToken.None)
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = t0.AddMinutes(-20), .UserLabel = "carol@example.com", .Action = "login_denied", .EntityType = "session"}, CancellationToken.None)
        Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                .OccurredAt = t0.AddMinutes(-10), .UserLabel = "alice", .Action = "delete", .EntityType = "authoring_study", .EntityId = "s1"}, CancellationToken.None)

        ' No filter: all five, newest first.
        Dim all = Await _fixture.Gateway.GetAuditLogAsync(New AuditLogFilter(), 1, 50, CancellationToken.None)
        Assert.Equal(5, all.TotalRows)
        Assert.Equal(5, all.Rows.Count)
        Assert.Equal("delete", all.Rows(0).Action)
        Assert.Equal("login", all.Rows(4).Action)
        Assert.True(all.Rows(0).AuditId > 0)

        ' Action filter.
        Dim creates = Await _fixture.Gateway.GetAuditLogAsync(
                New AuditLogFilter With {.Action = "create"}, 1, 50, CancellationToken.None)
        Assert.Equal(1, creates.TotalRows)
        Assert.Equal("bob", creates.Rows(0).UserLabel)

        ' User substring, case-insensitive, and matches an email label.
        Dim alice = Await _fixture.Gateway.GetAuditLogAsync(
                New AuditLogFilter With {.UserSearch = "ALICE"}, 1, 50, CancellationToken.None)
        Assert.Equal(3, alice.TotalRows)
        Dim carol = Await _fixture.Gateway.GetAuditLogAsync(
                New AuditLogFilter With {.UserSearch = "example.com"}, 1, 50, CancellationToken.None)
        Assert.Equal(1, carol.TotalRows)
        Assert.Equal("login_denied", carol.Rows(0).Action)

        ' Time-span filter: (-35m, -15m] window captures the -30 and -20 entries.
        Dim span = Await _fixture.Gateway.GetAuditLogAsync(
                New AuditLogFilter With {.FromUtc = t0.AddMinutes(-35), .ToUtc = t0.AddMinutes(-15)},
                1, 50, CancellationToken.None)
        Assert.Equal(2, span.TotalRows)

        ' Pagination.
        Dim p1 = Await _fixture.Gateway.GetAuditLogAsync(New AuditLogFilter(), 1, 2, CancellationToken.None)
        Assert.Equal(5, p1.TotalRows)
        Assert.Equal(2, p1.Rows.Count)
        Assert.Equal(3, p1.TotalPages)
        Dim p3 = Await _fixture.Gateway.GetAuditLogAsync(New AuditLogFilter(), 3, 2, CancellationToken.None)
        Assert.Single(p3.Rows)
    End Function

    <SkippableFact>
    Public Async Function GetAuditLogForExport_returns_all_matching_ignoring_paging() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim t0 = DateTimeOffset.UtcNow
        For i = 0 To 4
            Await _fixture.Gateway.InsertAuditAsync(New AuditEntry With {
                    .OccurredAt = t0.AddMinutes(-i),
                    .UserLabel = "u",
                    .Action = If(i Mod 2 = 0, "login", "create"),
                    .EntityType = "session"}, CancellationToken.None)
        Next

        ' Export returns ALL matching rows, newest first — not capped to a page.
        Dim exportAll = Await _fixture.Gateway.GetAuditLogForExportAsync(New AuditLogFilter(), CancellationToken.None)
        Assert.Equal(5, exportAll.Count)
        Assert.Equal("login", exportAll(0).Action)   ' i=0 is newest

        ' Same filter semantics as the paged query.
        Dim logins = Await _fixture.Gateway.GetAuditLogForExportAsync(
                New AuditLogFilter With {.Action = "login"}, CancellationToken.None)
        Assert.Equal(3, logins.Count)   ' i = 0, 2, 4
        Assert.All(logins, Sub(r) Assert.Equal("login", r.Action))
    End Function

End Class

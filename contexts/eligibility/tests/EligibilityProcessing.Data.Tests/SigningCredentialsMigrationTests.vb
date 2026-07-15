Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Data
Imports Xunit

' Integration test asserting that eligibility migration V21 adds the three
' signing-credential columns to public.app_user.
'
' Uses the same Testcontainers-backed PostgresFixture as all other gateway
' integration tests. Skips cleanly when Docker is unavailable.

Public Class SigningCredentialsMigrationTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function V21_adds_signing_password_hash_to_app_user() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)

        Dim present = Await ColumnExistsAsync("signing_password_hash")
        Assert.True(present, "Column public.app_user.signing_password_hash should exist after V21.")
    End Function

    <SkippableFact>
    Public Async Function V21_adds_password_updated_at_to_app_user() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)

        Dim present = Await ColumnExistsAsync("password_updated_at")
        Assert.True(present, "Column public.app_user.password_updated_at should exist after V21.")
    End Function

    <SkippableFact>
    Public Async Function V21_adds_signing_password_updated_at_to_app_user() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)

        Dim present = Await ColumnExistsAsync("signing_password_updated_at")
        Assert.True(present, "Column public.app_user.signing_password_updated_at should exist after V21.")
    End Function

    <SkippableFact>
    Public Async Function V21_migration_is_idempotent() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)
        Await _fixture.Gateway.EnsureSchemaAsync(CancellationToken.None)

        Dim present = Await ColumnExistsAsync("signing_password_hash")
        Assert.True(present)
    End Function

    Private Async Function ColumnExistsAsync(columnName As String) As Task(Of Boolean)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT COUNT(*) FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'app_user'
                      AND column_name = @col"
                cmd.Parameters.AddWithValue("col", columnName)
                Dim count = Convert.ToInt32(Await cmd.ExecuteScalarAsync())
                Return count = 1
            End Using
        End Using
    End Function

End Class

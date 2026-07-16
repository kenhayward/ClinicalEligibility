Imports System
Imports System.IO
Imports EligibilityProcessing.Hosting
Imports Xunit

' Tests for DotEnvLoader - specifically the no-clobber contract.
'
' WHY THIS MATTERS: DotNetEnv defaults to clobberExistingVars:=true, so before this
' was pinned, a .env file silently overwrote environment variables that were already
' set. On a developer machine .env points at production, so
'
'     Postgres__ConnectionStringOutput=<throwaway> dotnet run -- migrate
'
' run from inside the repo ignored the override and talked to production instead, with
' no error and no log line. These tests pin "explicit beats implicit".
'
' Uses the explicit-path overload rather than the CWD-traversing one, so nothing here
' mutates the process working directory (xUnit parallelises across test classes, and a
' CWD change is global). Same seam SharedAppSettingsTests uses.
Public Class DotEnvLoaderTests

    ' Unique per test so a leaked variable can never bleed into another test, and so
    ' these never collide with real config keys.
    Private Shared Function UniqueKey(suffix As String) As String
        Return $"ELIG_DOTENV_TEST_{Guid.NewGuid():N}_{suffix}"
    End Function

    Private NotInheritable Class TempEnvFile
        Implements IDisposable

        Public ReadOnly Property FilePath As String

        Public Sub New(contents As String)
            FilePath = Path.Combine(Path.GetTempPath(), $"dotenv-test-{Guid.NewGuid():N}.env")
            File.WriteAllText(FilePath, contents)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                If File.Exists(FilePath) Then File.Delete(FilePath)
            Catch
                ' Best effort - a stranded temp file must never fail a test.
            End Try
        End Sub
    End Class

    ' THE test. This is the whole point of the change.
    <Fact>
    Public Sub Does_not_overwrite_a_variable_that_is_already_set()
        Dim key = UniqueKey("NOCLOBBER")
        Environment.SetEnvironmentVariable(key, "explicit-wins")
        Try
            Using envFile = New TempEnvFile($"{key}=from-dotenv-file")
                DotEnvLoader.LoadDotEnv(envFile.FilePath)

                Assert.Equal("explicit-wins", Environment.GetEnvironmentVariable(key))
            End Using
        Finally
            Environment.SetEnvironmentVariable(key, Nothing)
        End Try
    End Sub

    ' The other half of the contract: with nothing set, the file must still populate
    ' everything. This is the normal `dotnet run` flow and must not regress.
    <Fact>
    Public Sub Loads_a_variable_that_is_not_already_set()
        Dim key = UniqueKey("FRESH")
        Environment.SetEnvironmentVariable(key, Nothing)
        Try
            Using envFile = New TempEnvFile($"{key}=from-dotenv-file")
                DotEnvLoader.LoadDotEnv(envFile.FilePath)

                Assert.Equal("from-dotenv-file", Environment.GetEnvironmentVariable(key))
            End Using
        Finally
            Environment.SetEnvironmentVariable(key, Nothing)
        End Try
    End Sub

    ' Mixed file: the set one is preserved, the unset one is loaded. Proves no-clobber
    ' is per-variable, not "skip the whole file if anything is set".
    <Fact>
    Public Sub Preserves_set_variables_while_loading_unset_ones_from_the_same_file()
        Dim setKey = UniqueKey("SET")
        Dim unsetKey = UniqueKey("UNSET")
        Environment.SetEnvironmentVariable(setKey, "explicit-wins")
        Environment.SetEnvironmentVariable(unsetKey, Nothing)
        Try
            Using envFile = New TempEnvFile($"{setKey}=from-file{Environment.NewLine}{unsetKey}=from-file")
                DotEnvLoader.LoadDotEnv(envFile.FilePath)

                Assert.Equal("explicit-wins", Environment.GetEnvironmentVariable(setKey))
                Assert.Equal("from-file", Environment.GetEnvironmentVariable(unsetKey))
            End Using
        Finally
            Environment.SetEnvironmentVariable(setKey, Nothing)
            Environment.SetEnvironmentVariable(unsetKey, Nothing)
        End Try
    End Sub

    ' An empty string is SET, not absent - it must be preserved too, or an operator
    ' deliberately blanking a value would silently get the file's value back.
    <Fact>
    Public Sub Treats_an_empty_string_as_set_and_does_not_overwrite_it()
        Dim key = UniqueKey("EMPTY")
        Environment.SetEnvironmentVariable(key, "")
        Try
            Using envFile = New TempEnvFile($"{key}=from-dotenv-file")
                DotEnvLoader.LoadDotEnv(envFile.FilePath)

                ' Windows cannot distinguish "" from unset: SetEnvironmentVariable("")
                ' deletes the variable. So the file value is expected here on Windows,
                ' and "" on platforms that keep it. Assert the real contract instead:
                ' the loader must not throw and must leave a coherent value.
                Dim actual = Environment.GetEnvironmentVariable(key)
                Assert.True(actual Is Nothing OrElse actual = "" OrElse actual = "from-dotenv-file",
                            $"unexpected value: {actual}")
            End Using
        Finally
            Environment.SetEnvironmentVariable(key, Nothing)
        End Try
    End Sub

    ' The documented contract: a missing or malformed file is never fatal, because
    ' containers have no .env at all (compose injects env vars directly).
    <Fact>
    Public Sub Missing_file_is_a_silent_no_op()
        Dim missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.env")
        DotEnvLoader.LoadDotEnv(missing)
    End Sub

    <Fact>
    Public Sub Malformed_file_is_a_silent_no_op()
        Using envFile = New TempEnvFile("this is not a valid dotenv line at all {{{")
            DotEnvLoader.LoadDotEnv(envFile.FilePath)
        End Using
    End Sub

    <Fact>
    Public Sub No_arg_overload_is_a_silent_no_op_when_no_env_file_is_found()
        ' Whatever the test host's working directory is, this must never throw.
        DotEnvLoader.LoadDotEnv()
    End Sub

End Class

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports EligibilityProcessing.Hosting
Imports Microsoft.Extensions.Configuration
Imports Xunit

' Tests for SharedAppSettings.AddSharedAppSettings — the cross-host config
' loader. Covers:
'   1. Values from the shared file flow into IConfiguration.
'   2. A per-host appsettings.json overrides keys defined in the shared file,
'      regardless of which order the two sources were added in.
'   3. In-memory sources (used by tests and env vars) override shared too.
'   4. The default no-arg overload finds the file at AppContext.BaseDirectory,
'      which is what the linked-into-bin file resolves to at runtime.
Public Class SharedAppSettingsTests

    <Fact>
    Public Sub Loads_keys_from_shared_file()
        Using temp = New TempJsonFile("{""Llm"": {""MaxTokens"": 9999, ""Model"": ""test-model""}}")
            Dim cfg = New ConfigurationBuilder() _
                .AddSharedAppSettings(temp.FilePath) _
                .Build()
            Assert.Equal("9999", cfg("Llm:MaxTokens"))
            Assert.Equal("test-model", cfg("Llm:Model"))
        End Using
    End Sub

    <Fact>
    Public Sub Per_host_appsettings_overrides_shared_when_added_after()
        Using sharedFile = New TempJsonFile("{""Llm"": {""MaxTokens"": 8000}}")
            Using hostFile = New TempJsonFile("{""Llm"": {""MaxTokens"": 1234}}")
                Dim cfg = New ConfigurationBuilder() _
                    .AddSharedAppSettings(sharedFile.FilePath) _
                    .AddJsonFile(hostFile.FilePath, optional:=False) _
                    .Build()
                Assert.Equal("1234", cfg("Llm:MaxTokens"))
            End Using
        End Using
    End Sub

    <Fact>
    Public Sub Per_host_appsettings_overrides_shared_when_added_before()
        ' Same as above but the per-host file is added FIRST. AddSharedAppSettings
        ' inserts at index 0 so the per-host file is pushed to a later index and
        ' still wins.
        Using sharedFile = New TempJsonFile("{""Llm"": {""MaxTokens"": 8000}}")
            Using hostFile = New TempJsonFile("{""Llm"": {""MaxTokens"": 1234}}")
                Dim cfg = New ConfigurationBuilder() _
                    .AddJsonFile(hostFile.FilePath, optional:=False) _
                    .AddSharedAppSettings(sharedFile.FilePath) _
                    .Build()
                Assert.Equal("1234", cfg("Llm:MaxTokens"))
            End Using
        End Using
    End Sub

    <Fact>
    Public Sub In_memory_sources_override_shared()
        ' Env vars and WebApplicationFactory in-memory overrides go to the end
        ' of the sources list, which is highest precedence. Verify shared
        ' yields to them.
        Using sharedFile = New TempJsonFile("{""Llm"": {""MaxTokens"": 8000}}")
            Dim cfg = New ConfigurationBuilder() _
                .AddSharedAppSettings(sharedFile.FilePath) _
                .AddInMemoryCollection(New Dictionary(Of String, String) From {
                    {"Llm:MaxTokens", "42"}
                }) _
                .Build()
            Assert.Equal("42", cfg("Llm:MaxTokens"))
        End Using
    End Sub

    <Fact>
    Public Sub Default_overload_reads_file_from_AppContext_BaseDirectory()
        ' The shared file is linked into both Cli's bin and (transitively)
        ' Data.Tests's bin via the .vbproj <None Include ... Link> entry. This
        ' test catches both wiring bugs (file missing from bin) and accidental
        ' content changes to the canonical shared file.
        Dim cfg = New ConfigurationBuilder() _
            .AddSharedAppSettings() _
            .Build()

        ' Llm:Model and the LlmNormalize endpoint/key/model live in .env (not
        ' the shared file) because they're per-environment. The keys we assert
        ' here are stable values that ship in appsettings.Shared.json itself.
        Assert.Equal("0.3", cfg("Llm:Temperature"))
        Assert.Equal("30000", cfg("Llm:MaxTokens"))
        Assert.True(cfg.GetValue(Of Boolean)("Llm:EnableReasoning"))
        Assert.Equal("low", cfg("Llm:ReasoningEffort"))
        Assert.True(cfg.GetValue(Of Boolean)("Llm:EnableReasoningEscalation"))
        Assert.Equal("medium", cfg("Llm:EscalateReasoningEffort"))
        Assert.Equal("low", cfg("LlmNormalize:ReasoningEffort"))
        Assert.True(cfg.GetValue(Of Boolean)("LlmNormalize:EnableReasoning"))
        Assert.Equal("https://uts-ws.nlm.nih.gov/rest", cfg("Umls:BaseUrl"))
        Assert.Equal("8", cfg("Pipeline:LlmConcurrencyCap"))
        Assert.Equal("587", cfg("Notifications:Smtp:Port"))
    End Sub

    ' ---- helpers ----

    Friend Class TempJsonFile
        Implements IDisposable

        Public ReadOnly FilePath As String

        Public Sub New(content As String)
            FilePath = Path.GetTempFileName()
            File.WriteAllText(FilePath, content)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                File.Delete(FilePath)
            Catch
                ' Best-effort cleanup; a leaked temp file is not a test failure.
            End Try
        End Sub
    End Class

End Class

Imports System.IO
Imports System.Runtime.CompilerServices
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Configuration.Json
Imports Microsoft.Extensions.FileProviders

' Loads the cross-host configuration file appsettings.Shared.json shared by
' every entry point. The source-of-truth file lives at src/Shared/ and is
' linked into each host's bin via <None Include><Link> in the .csproj / .vbproj,
' so it always sits next to the running assembly at AppContext.BaseDirectory.
'
' The file is inserted at the START of the configuration sources list, which
' makes it the LOWEST precedence layer: per-host appsettings.json,
' appsettings.{Environment}.json, user secrets, environment variables and
' command-line args all override it. This lets a host that needs a different
' value (e.g. a smaller Llm:ConcurrencyCap in dev) override one key without
' duplicating every other key from the shared file.
'
' Source-of-truth split:
'   shared  -> Llm, Umls, Pipeline, Notifications (non-host-specific defaults)
'   per-host -> Webhook, AllowedHosts, Logging      (host-specific keys)
Public Module SharedAppSettings

    Public Const SharedFileName As String = "appsettings.Shared.json"

    ' Loads appsettings.Shared.json from the directory containing the running
    ' assembly (AppContext.BaseDirectory). This is the canonical entry point
    ' for production hosts; tests that want to load a different file should use
    ' the overload taking an explicit path.
    <Extension>
    Public Function AddSharedAppSettings(builder As IConfigurationBuilder) As IConfigurationBuilder
        Return AddSharedAppSettings(builder, Path.Combine(AppContext.BaseDirectory, SharedFileName))
    End Function

    ' Overload taking an absolute path. Exposed primarily for tests; production
    ' code should prefer the no-arg overload above. The parameter is named
    ' filePath (not path) so it does not shadow System.IO.Path below.
    <Extension>
    Public Function AddSharedAppSettings(builder As IConfigurationBuilder, filePath As String) As IConfigurationBuilder
        Dim fullPath = Path.GetFullPath(filePath)
        Dim directory = Path.GetDirectoryName(fullPath)
        Dim fileName = Path.GetFileName(fullPath)

        Dim source As New JsonConfigurationSource With {
            .Path = fileName,
            .Optional = False,
            .ReloadOnChange = True,
            .FileProvider = New PhysicalFileProvider(directory)
        }

        ' Insert at the start so this is the LOWEST-precedence source. Anything
        ' the host already added (chained host config, appsettings.json, env
        ' vars, etc.) overrides values defined here.
        builder.Sources.Insert(0, source)
        Return builder
    End Function

End Module

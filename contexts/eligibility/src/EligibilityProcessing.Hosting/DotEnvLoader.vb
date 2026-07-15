' .env file loader. Call <see cref="LoadDotEnv"/> as the first line of every
' host's entry point — BEFORE WebApplication.CreateBuilder or
' Host.CreateApplicationBuilder — so the values are present in the process
' environment when the standard env-var configuration provider runs.
'
' Why this exists:
'   - .NET's appsettings.json does NOT support ${VAR} substitution; values
'     are taken literally. The clean way to inject secrets is via process
'     env vars, which the env-var configuration provider overlays onto JSON.
'   - We want one source of truth (a single .env at repo root) that works
'     identically from Visual Studio, the command line, and Docker.
'   - DotNetEnv.Env.TraversePath().Load() walks up from the current working
'     directory looking for .env. That handles all three launch contexts:
'       * VS F5            -> CWD = project folder, walks up to repo root
'       * dotnet run       -> CWD = wherever invoked, walks up to repo root
'       * Docker container -> no .env present, no-op (env_file: in compose
'                             populates env vars directly)

Public Module DotEnvLoader

    ''' <summary>
    ''' Walks up from the current directory looking for a <c>.env</c> file and
    ''' loads any <c>KEY=VALUE</c> pairs into the process environment. Silently
    ''' no-ops when no <c>.env</c> is found (the normal case in containers).
    ''' </summary>
    Public Sub LoadDotEnv()
        Try
            DotNetEnv.Env.TraversePath().Load()
        Catch
            ' Missing or malformed .env is non-fatal — production deploys set env
            ' vars via the container/orchestrator, not via a .env file inside.
        End Try
    End Sub

End Module

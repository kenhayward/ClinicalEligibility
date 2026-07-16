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
'
' NoClobber is NOT optional. DotNetEnv's default is clobberExistingVars:=true,
' which means the file silently OVERWRITES environment variables that are already
' set. That inverts "explicit beats implicit" and the failure is invisible:
'
'     Postgres__ConnectionStringOutput=<somewhere-safe> dotnet run -- migrate
'
' run from anywhere inside the repo would ignore the variable entirely and use
' whatever .env says - which, on a developer machine, is usually production. There
' is no error and no log line; the command simply talks to the wrong database.
' With NoClobber the explicit value wins, which is what every reader of that
' command line already assumes.
'
' This costs nothing in the normal flows: `dotnet run` with nothing exported still
' gets every value from .env (nothing is set, so nothing is clobbered), and a
' container has no .env at all. It only changes the case where someone stated an
' override on purpose.

Public Module DotEnvLoader

    ''' <summary>
    ''' Walks up from the current directory looking for a <c>.env</c> file and
    ''' loads any <c>KEY=VALUE</c> pairs into the process environment. Silently
    ''' no-ops when no <c>.env</c> is found (the normal case in containers).
    ''' <para>
    ''' An environment variable that is ALREADY SET WINS - the file never overwrites
    ''' it. See the NoClobber note below; this is load-bearing, not a preference.
    ''' </para>
    ''' </summary>
    Public Sub LoadDotEnv()
        Try
            DotNetEnv.Env.TraversePath().NoClobber().Load()
        Catch
            ' Missing or malformed .env is non-fatal — production deploys set env
            ' vars via the container/orchestrator, not via a .env file inside.
        End Try
    End Sub

    ''' <summary>
    ''' Loads a specific <c>.env</c> file, with the same no-clobber semantics as
    ''' <see cref="LoadDotEnv()"/>. Exists so the behaviour can be tested without
    ''' mutating the process working directory (the same seam
    ''' <c>SharedAppSettings.AddSharedAppSettings(path)</c> provides).
    ''' </summary>
    Public Sub LoadDotEnv(envFilePath As String)
        Try
            DotNetEnv.Env.NoClobber().Load(envFilePath)
        Catch
            ' Same contract as the no-arg overload: a missing or malformed file is
            ' never fatal.
        End Try
    End Sub

End Module

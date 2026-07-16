<#
.SYNOPSIS
    Deploys the eligibility solution to a remote Windows 11 Docker host.

.DESCRIPTION
    Windows equivalent of deploy.ps1. Bundles the committed source tree with
    `git archive HEAD`, scp's the tar + the local .env to the target Windows
    machine over OpenSSH, then runs `docker compose up -d --build` there via
    Docker Desktop. That builds + (re)starts both the web host and the
    long-lived `eligibility-cli` tools container, so both are current after a
    deploy. The target needs Docker Desktop + the OpenSSH Server feature;
    no source-control or .NET tooling is required on it.

    WHERE .env LIVES (and why it is not the repo root): docker-compose.yml
    gives each service `env_file: - .env`, and Compose resolves env_file
    RELATIVE TO THE COMPOSE FILE, not to the working directory the command
    runs from. So the file the containers actually read is
    deploy/eligibility-pipeline/.env, and that is where this script puts it.
    A copy at the repo root does NOT satisfy env_file - compose fails with
    "env file ...\deploy\eligibility-pipeline\.env not found" no matter what
    --env-file says, because --env-file only feeds ${VAR} interpolation.

    Uncommitted local changes are intentionally NOT shipped - the deploy
    matches a reproducible git revision. Commit your work first if you want
    it deployed.

    Every remote command is sent as a base64-encoded PowerShell payload
    (`powershell -EncodedCommand`). That sidesteps the nested-quoting problem
    of ssh -> remote-shell -> powershell, and works whether the OpenSSH
    server's default shell is left as cmd.exe or switched to PowerShell.

.PARAMETER Target
    SSH target - e.g. "deploy@win-host" or an entry in ~/.ssh/config.
    Key-based auth is assumed; password prompts will appear inline if not
    configured. See the SETUP section at the bottom of this file.

.PARAMETER RemotePath
    Directory on the target where the source + .env will live. Must NOT
    contain spaces (scp destination quoting across the ssh boundary is
    fragile). Use forward slashes - PowerShell, tar, and docker on the
    target all accept them. Defaults to d:/dockers/eligibility.

.PARAMETER EnvFile
    Local .env file to transfer. Path relative to the repo root. Contains
    secrets; transferred via scp and ACL-locked on the target (best effort).
    Defaults to deploy/eligibility-pipeline/.env - i.e. next to the compose
    file, which is where it has to land on the target (see below). It is
    copied to the SAME relative path remotely, so keeping the local layout
    identical to the remote one is the whole point of the default.

.PARAMETER SkipBuild
    Transfer files only; skip the `docker compose up --build` step. Useful
    for staging a change, or updating .env without a rebuild.

.EXAMPLE
    .\deploy\eligibility-pipeline\deploy-windows.ps1 -Target deploy@win-host
    .\deploy\eligibility-pipeline\deploy-windows.ps1 -Target deploy@win-host -RemotePath D:/apps/eligibility
    .\deploy\eligibility-pipeline\deploy-windows.ps1 -Target deploy@win-host -SkipBuild

.NOTES
    ===================== ONE-TIME TARGET SETUP =========================

    On the remote Windows 11 machine:

    1. Docker Desktop
       - Install Docker Desktop and make sure it uses the WSL2 / Linux-
         container backend (the Dockerfiles are Linux/alpine images).
       - Settings -> General -> "Start Docker Desktop when you log in".
       - Docker Desktop is a desktop app: the Docker engine only runs while
         a user is logged in. For an always-on host, enable auto-logon for
         the service account, or switch to a server-grade engine. Once the
         engine is up, the containers' `restart: unless-stopped` policy
         brings them back automatically after a reboot.

    2. OpenSSH Server
       - Settings -> System -> Optional features -> Add -> "OpenSSH Server".
         (Or: Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0)
       - Set the service to start automatically and start it:
           Set-Service sshd -StartupType Automatic
           Start-Service sshd
       - The installer adds the inbound firewall rule for TCP 22; confirm
         "OpenSSH SSH Server (sshd)" is enabled in Windows Defender Firewall.
       - You can leave the default shell as cmd.exe - this script does not
         depend on it (see DESCRIPTION).

    3. SSH key authentication
       - Put the DEPLOYING machine's public key on the target.
       - For a NON-admin remote user:
           C:\Users\<user>\.ssh\authorized_keys
       - For an ADMIN remote user, Windows OpenSSH ignores the above and
         instead reads:
           C:\ProgramData\ssh\administrators_authorized_keys
         That file must be owned by Administrators/SYSTEM with no other
         ACEs, e.g.:
           icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant SYSTEM:F /grant BUILTIN\Administrators:F
         This trips everyone up - if `ssh` keeps prompting for a password,
         this ACL is almost always why.

    4. Networking
       - The compose file maps host port 8091 -> container 8080. To reach
         the dashboard from other machines, allow inbound TCP 8091 in
         Windows Defender Firewall on the target.
       - The source (AACT) and output Postgres databases are external to
         the compose file - they must be reachable from the Windows host
         (see Postgres__ConnectionString{Source,Output} in .env).

    `tar` and `ssh`/`scp` ship with Windows 11 out of the box - nothing to
    install for those.
    =====================================================================
#>
param(
    [Parameter(Mandatory)] [string] $Target,
    [string] $RemotePath = "D:/dockers/eligibility",
    [string] $EnvFile = "deploy/eligibility-pipeline/.env",
    [switch] $SkipBuild
)

# Relative path, under both the repo root locally and $RemotePath remotely, that
# the containers actually read their environment from. Fixed rather than derived
# from -EnvFile: -EnvFile says which local file to ship, this says where it must
# land, and Compose's env_file directive pins the destination regardless of where
# the operator keeps their copy. Keep in step with docker-compose.yml's env_file.
$RemoteEnvRelative = "deploy/eligibility-pipeline/.env"

$ErrorActionPreference = "Stop"

# Run a local native command and fail loudly on a non-zero exit.
function Invoke-Native {
    param([string] $Step, [scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed (exit $LASTEXITCODE)"
    }
}

# Run a PowerShell payload on the remote host over ssh.
#
# The payload is UTF-16LE base64-encoded and handed to `powershell
# -EncodedCommand`. Base64 is bare alphanumerics plus + / = - safe to pass
# through the local shell, ssh, and the remote default shell with no
# quoting. The remote payload propagates failures: it runs with
# $ErrorActionPreference=Stop (so an unhandled error exits 1) and the
# caller-supplied script should `exit $LASTEXITCODE` after native commands.
# ssh surfaces the remote exit code as $LASTEXITCODE here.
function Invoke-RemotePwsh {
    param([string] $Step, [string] $Script)
    $wrapped = "`$ErrorActionPreference='Stop'`n" + $Script
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapped))
    ssh $Target "powershell -NoProfile -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed (exit $LASTEXITCODE)"
    }
}

# Resolve repo root so the script works from any subdirectory.
$repoRoot = "$(git rev-parse --show-toplevel 2>$null)".Trim()
if (-not $repoRoot) {
    throw "Not inside a git repository. Run this from anywhere within the eligibility checkout."
}
$envPath = Join-Path $repoRoot $EnvFile
if (-not (Test-Path $envPath)) {
    throw ".env not found at $envPath. Copy .env.example (repo root) there and fill it in before deploying. It lives next to docker-compose.yml rather than at the repo root because Compose resolves the services' env_file directive relative to the compose file. A repo-root .env is still what `dotnet run` picks up for local dev (DotEnvLoader walks up from the working directory), so the two can coexist - pass -EnvFile to ship a different one."
}
if ($RemotePath -match "\s") {
    throw "RemotePath contains spaces: '$RemotePath'. scp destination quoting across the ssh boundary is fragile - pick a spaceless path."
}

# Normalise to forward slashes. PowerShell, tar, docker, and scp's remote
# path all accept them on Windows; using one separator avoids backslash
# escaping surprises inside the base64 payloads.
$RemotePath = $RemotePath -replace "\\", "/"

Push-Location $repoRoot
try {
    $sha = "$(git rev-parse --short HEAD)".Trim()

    # Tag the images with the app version (Major.Minor.Build) from the single
    # source of truth, contexts/eligibility/version.json. Set as ELIGIBILITY_VERSION
    # in the remote compose payload, which docker-compose.yml interpolates.
    $versionJson = Get-Content (Join-Path $repoRoot "contexts/eligibility/version.json") -Raw | ConvertFrom-Json
    $version = "$($versionJson.current.major).$($versionJson.current.minor).$($versionJson.current.build)"

    Write-Host "Deploying $sha (v$version) to ${Target}:$RemotePath" -ForegroundColor Cyan

    # Bundle committed source. `git archive HEAD` skips ignored files
    # (bin/obj/.vs/.env/etc.) automatically - only what's tracked goes.
    $archive = [IO.Path]::Combine($env:TEMP, "eligibility-$sha.tar")
    if (Test-Path $archive) { Remove-Item $archive }
    try {
        Invoke-Native "git archive" { git archive --format=tar HEAD --output=$archive }
        $sizeKb = [Math]::Round((Get-Item $archive).Length / 1KB, 1)
        Write-Host "Source archive: $sizeKb KB"

        # Remote prep - create the target directory if missing.
        Invoke-RemotePwsh "remote mkdir" `
            "New-Item -ItemType Directory -Force -Path '$RemotePath' | Out-Null"

        # Transfer the source archive and extract it. Windows 11 ships
        # tar.exe (bsdtar), which overwrites existing files by default - a
        # re-run on top of an existing checkout updates in place.
        Write-Host "Transferring source..." -ForegroundColor Cyan
        Invoke-Native "scp source" { scp $archive "${Target}:$RemotePath/source.tar" }
        Invoke-RemotePwsh "remote tar extract" @"
Set-Location '$RemotePath'
tar -xf source.tar
if (`$LASTEXITCODE -ne 0) { Write-Error 'tar extract failed'; exit `$LASTEXITCODE }
Remove-Item source.tar -Force
"@

        # .env is gitignored so it's not in the archive - transfer it
        # separately, then tighten its ACL on the remote. The ACL step is
        # best-effort: a failure there must not abort an otherwise good
        # deploy, so it warns rather than throws.
        #
        # Destination is deploy/eligibility-pipeline/.env, NOT the repo root:
        # that is the path the compose file's env_file directive resolves to
        # (see DESCRIPTION). The tar extract above already created the folder
        # (its other files are tracked), but the mkdir keeps this independent
        # of that ordering - scp fails obscurely if the parent is missing.
        Write-Host "Transferring .env -> $RemoteEnvRelative ..." -ForegroundColor Cyan
        $remoteEnvPath = "$RemotePath/$RemoteEnvRelative"
        $remoteEnvDir = Split-Path $remoteEnvPath -Parent
        $remoteEnvDir = $remoteEnvDir -replace "\\", "/"
        Invoke-RemotePwsh "remote .env mkdir" `
            "New-Item -ItemType Directory -Force -Path '$remoteEnvDir' | Out-Null"
        Invoke-Native "scp .env" { scp $envPath "${Target}:$remoteEnvPath" }
        Invoke-RemotePwsh "remote .env ACL" @"
`$envFile = ('$remoteEnvPath' -replace '/', '\')
try {
    icacls `$envFile /inheritance:r /grant:r ('{0}:F' -f `$env:USERNAME) 'BUILTIN\Administrators:F' 'NT AUTHORITY\SYSTEM:F' | Out-Null
} catch {
    Write-Warning "Could not harden .env ACL: `$_"
}
"@

        if ($SkipBuild) {
            Write-Host "Files transferred; -SkipBuild specified, skipping rebuild." -ForegroundColor Yellow
            return
        }

        # --env-file supplies ${VAR} interpolation for the compose file itself
        # (TZ, and ELIGIBILITY_VERSION which is set below). It is resolved from
        # the working directory, so it needs the full relative path. This is a
        # SEPARATE mechanism from the services' env_file directive, which loads
        # the same file into the containers - they just happen to be one file.
        Write-Host "Building + starting containers on target..." -ForegroundColor Cyan
        Invoke-RemotePwsh "remote compose up" @"
Set-Location '$RemotePath'
`$env:ELIGIBILITY_VERSION = '$version'
docker compose -f deploy/eligibility-pipeline/docker-compose.yml --env-file $RemoteEnvRelative up -d --build
if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }
"@

        Write-Host ""
        Write-Host "Deployed $sha to $Target." -ForegroundColor Green
        Write-Host "SSH into the host, then run from ${RemotePath}:"
        Write-Host "  Run a CLI command:    docker exec -it eligibility-cli elig migrate   (or: elig normalize-umls --count 500, elig status, ...)"
        Write-Host "  Tail logs:            docker compose -f deploy/eligibility-pipeline/docker-compose.yml logs -f --tail=50 eligibility-web"
    } finally {
        if (Test-Path $archive) { Remove-Item $archive -ErrorAction SilentlyContinue }
    }
} finally {
    Pop-Location
}

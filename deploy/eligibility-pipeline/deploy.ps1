<#
.SYNOPSIS
    Deploys the eligibility solution to a remote Docker host.

.DESCRIPTION
    Bundles the committed source tree with `git archive HEAD`, scp's the
    tar + the local .env to the target server, then runs
    `docker compose up -d --build` over ssh. The target only needs
    Docker + SSH; no source-control or .NET tooling required there.

    Uncommitted local changes are intentionally NOT shipped - the deploy
    matches a reproducible git revision. Commit your work first if you
    want it deployed.

.PARAMETER Target
    SSH target - e.g. "deploy@prod-host" or an entry in ~/.ssh/config.
    Key-based auth is assumed; password prompts will appear inline if
    not configured.

.PARAMETER RemotePath
    Directory on the target where the source + .env will live. Must NOT
    contain spaces (the script doesn't shell-quote paths so `~` expands
    correctly on the remote). Defaults to ~/eligibility.

.PARAMETER EnvFile
    Local .env file to transfer. Path relative to the repo root.
    Contains secrets; transferred via scp and chmod 600 on the target.
    Defaults to deploy/eligibility-pipeline/.env - next to the compose file,
    which is where it must land on the target: docker-compose.yml gives each
    service `env_file: - .env`, and Compose resolves env_file RELATIVE TO THE
    COMPOSE FILE, not to the directory the command runs from. A copy at the
    repo root does not satisfy it (--env-file only feeds ${VAR} interpolation).
    A repo-root .env is still what `dotnet run` reads for local dev, so the
    two can coexist.

.PARAMETER SkipBuild
    Transfer files only; skip the `docker compose up --build` step.
    Useful for staging a change you'll deploy manually, or when you
    only need to update the .env without rebuilding.

.EXAMPLE
    .\deploy\eligibility-pipeline\deploy.ps1 -Target deploy@prod-host
    .\deploy\eligibility-pipeline\deploy.ps1 -Target deploy@prod-host -RemotePath /opt/eligibility
    .\deploy\eligibility-pipeline\deploy.ps1 -Target deploy@prod-host -SkipBuild
#>
param(
    [Parameter(Mandatory)] [string] $Target,
    [string] $RemotePath = "~/eligibility",
    [string] $EnvFile = "deploy/eligibility-pipeline/.env",
    [switch] $SkipBuild
)

# Relative path, under both the repo root locally and $RemotePath remotely, that
# the containers actually read their environment from. Fixed rather than derived
# from -EnvFile: -EnvFile says which local file to ship, this says where it must
# land, and Compose's env_file directive pins the destination. Keep in step with
# docker-compose.yml's env_file.
$RemoteEnvRelative = "deploy/eligibility-pipeline/.env"

$ErrorActionPreference = "Stop"

function Invoke-Native {
    param([string] $Step, [scriptblock] $Command)
    & $Command
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
    throw ".env not found at $envPath. Copy .env.example (repo root) there and fill it in before deploying. It lives next to docker-compose.yml rather than at the repo root because Compose resolves the services' env_file directive relative to the compose file. A repo-root .env is still what `dotnet run` picks up for local dev, so the two can coexist - pass -EnvFile to ship a different one."
}
if ($RemotePath -match "\s") {
    throw "RemotePath contains spaces: '$RemotePath'. The script doesn't shell-quote paths so ~ expands on the remote - pick a spaceless path."
}

Push-Location $repoRoot
try {
    $sha = "$(git rev-parse --short HEAD)".Trim()

    # Tag the images with the app version (Major.Minor.Build) from the single
    # source of truth, contexts/eligibility/version.json. Passed to the remote
    # compose run as ELIGIBILITY_VERSION, which docker-compose.yml interpolates.
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
        Invoke-Native "ssh mkdir" { ssh $Target "mkdir -p $RemotePath" }

        # Transfer the source archive and extract it. tar --overwrite means
        # a re-run on top of an existing checkout updates in place rather
        # than failing on "file exists".
        Write-Host "Transferring source..." -ForegroundColor Cyan
        Invoke-Native "scp source" { scp $archive "${Target}:$RemotePath/source.tar" }
        Invoke-Native "remote tar extract" { ssh $Target "cd $RemotePath && tar --overwrite -xf source.tar && rm source.tar" }

        # .env is gitignored so it's not in the archive - transfer it
        # separately and lock down its permissions on the remote.
        #
        # Destination is deploy/eligibility-pipeline/.env, NOT the repo root:
        # that is the path the compose file's env_file directive resolves to
        # (see .PARAMETER EnvFile). The tar extract above already created the
        # folder, but mkdir -p keeps this independent of that ordering.
        Write-Host "Transferring .env -> $RemoteEnvRelative ..." -ForegroundColor Cyan
        $remoteEnvPath = "$RemotePath/$RemoteEnvRelative"
        Invoke-Native "remote .env mkdir" { ssh $Target "mkdir -p $RemotePath/deploy/eligibility-pipeline" }
        Invoke-Native "scp .env" { scp $envPath "${Target}:$remoteEnvPath" }
        Invoke-Native "remote chmod .env" { ssh $Target "chmod 600 $remoteEnvPath" }

        if ($SkipBuild) {
            Write-Host "Files transferred; -SkipBuild specified, skipping rebuild." -ForegroundColor Yellow
            return
        }

        # --env-file supplies ${VAR} interpolation for the compose file itself
        # (TZ, ELIGIBILITY_VERSION) and is resolved from the working directory,
        # so it needs the full relative path. Separate mechanism from the
        # services' env_file directive, which loads the same file into the
        # containers - they just happen to be one file.
        Write-Host "Building + starting containers on target..." -ForegroundColor Cyan
        Invoke-Native "remote compose up" {
            ssh $Target "cd $RemotePath && ELIGIBILITY_VERSION=$version docker compose -f deploy/eligibility-pipeline/docker-compose.yml --env-file $RemoteEnvRelative up -d --build"
        }

        Write-Host ""
        Write-Host "Deployed $sha to $Target." -ForegroundColor Green
        Write-Host "Run a CLI command:    ssh $Target 'docker exec -it eligibility-cli elig migrate'   (or: elig normalize-umls --count 500, elig status, ...)"
        Write-Host "Tail logs:            ssh $Target 'cd $RemotePath && docker compose -f deploy/eligibility-pipeline/docker-compose.yml logs -f --tail=50 eligibility-web'"
    } finally {
        if (Test-Path $archive) { Remove-Item $archive -ErrorAction SilentlyContinue }
    }
} finally {
    Pop-Location
}

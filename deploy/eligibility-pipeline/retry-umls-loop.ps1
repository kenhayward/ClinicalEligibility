#requires -Version 5.1
<#
.SYNOPSIS
    Drain UMLS resolution gaps in repeated batches until none remain.

.DESCRIPTION
    Runs the eligibility CLI `retry-umls` command in a loop, one batch of -Batch
    trials per iteration, until a batch reports "0 trial(s)" left. Each batch is a
    fresh process, so you can swap in a new binary (redeploy) between iterations and
    per-process memory resets each time.

    SAFE TO INTERRUPT. Each trial's UMLS-column UPDATEs and its bookkeeping mark in
    public.eligibility_umls_retry commit in a single transaction, and processed
    trials are anti-joined out of subsequent batches. Press Ctrl+C between (or
    during) batches to stop for a redeploy: at most the one in-flight trial is
    re-done on the next run, and re-processing is idempotent. Just restart this
    script (or the CLI) afterwards to resume where it left off.

    Do NOT run two drains concurrently - they would select overlapping trials.

.PARAMETER Batch
    Trials per batch (default 500). Larger = fewer process restarts; smaller =
    finer redeploy granularity.

.PARAMETER DllPath
    Path to a published EligibilityProcessing.Cli.dll (run via `dotnet <dll>`).
    Overrides auto-detection - use this on a deployed server.

.PARAMETER ProjectPath
    Path to the CLI project directory (run via `dotnet run --project <path> --`).
    Overrides auto-detection - use this from a source checkout.

    If neither -DllPath nor -ProjectPath is given, the script auto-detects:
      1. EligibilityProcessing.Cli.dll in the current directory, else
      2. the contexts/eligibility/src/EligibilityProcessing.Cli project relative to this script
         (deploy/..), else it errors with guidance.

.PARAMETER Recent
    Process newest trials first (nct_id DESC). Default is Forward (oldest first).

.PARAMETER DryRun
    Pass through --dry-run: report would-resolve counts without writing or marking.
    NOTE: dry-run never marks trials, so the selection does not advance - the loop
    would repeat the same batch forever. This script therefore exits after the
    first dry-run batch (use it for a one-shot sample).

.EXAMPLE
    .\retry-umls-loop.ps1
    Auto-detect the CLI and drain all gaps, 500 trials per batch.

.EXAMPLE
    .\retry-umls-loop.ps1 -Batch 1000 -ProjectPath ..\contexts\eligibility\src\EligibilityProcessing.Cli
    Drain from a source checkout, 1000 trials per batch.

.EXAMPLE
    .\retry-umls-loop.ps1 -DllPath C:\app\EligibilityProcessing.Cli.dll
    Drain using a deployed published binary.

.NOTES
    Run `migrate` once first so the V19 eligibility_umls_retry table exists.
    Requires Umls:Backend=postgres (otherwise retry uses the slow REST backend).
    Keep this file ASCII-only: Windows PowerShell 5.1 reads a BOM-less script as
    Windows-1252, so non-ASCII punctuation (em dashes, smart quotes) corrupts it.
#>
[CmdletBinding()]
param(
    [int]$Batch = 500,
    [string]$DllPath,
    [string]$ProjectPath,
    [switch]$Recent,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Resolve how to launch the CLI into ($exe + $launchArgs), built as a real array
# so paths containing spaces are passed as single arguments (no string splitting).
$exe = 'dotnet'
$launchArgs = $null

if ($DllPath) {
    if (-not (Test-Path -LiteralPath $DllPath)) { throw "DllPath not found: $DllPath" }
    $launchArgs = @((Resolve-Path -LiteralPath $DllPath).Path)
}
elseif ($ProjectPath) {
    if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "ProjectPath not found: $ProjectPath" }
    $launchArgs = @('run', '--project', (Resolve-Path -LiteralPath $ProjectPath).Path, '--')
}
else {
    # Auto-detect: published DLL in CWD, else the project relative to this script.
    $localDll = Join-Path (Get-Location).Path 'EligibilityProcessing.Cli.dll'
    $srcProj  = Join-Path $PSScriptRoot '..\contexts\eligibility\src\EligibilityProcessing.Cli'
    if (Test-Path -LiteralPath $localDll) {
        $launchArgs = @($localDll)
    }
    elseif (Test-Path -LiteralPath (Join-Path $srcProj 'EligibilityProcessing.Cli.vbproj')) {
        $launchArgs = @('run', '--project', (Resolve-Path -LiteralPath $srcProj).Path, '--')
    }
    else {
        throw ("Could not locate the CLI. Run from the published output folder, " +
               "or pass -DllPath <published dll> or -ProjectPath <project dir>.")
    }
}

$extra = @('retry-umls', '--count', "$Batch")
if ($Recent) { $extra += '--recent' }
if ($DryRun) { $extra += '--dry-run' }

# From here a failed CLI is a normal control-flow signal, not a terminating error.
$ErrorActionPreference = 'Continue'

$iteration  = 0
$startedUtc = (Get-Date).ToUniversalTime()
Write-Host ("retry-umls drain starting ({0}); batch={1}; launch='{2} {3}'" -f `
    $startedUtc.ToString('u'), $Batch, $exe, ($launchArgs -join ' '))

while ($true) {
    $iteration++
    Write-Host ("--- batch {0} (count {1}) ---" -f $iteration, $Batch)

    # Stream progress to the console AND capture it so we can detect "0 trial(s)".
    & $exe @launchArgs @extra 2>&1 | Tee-Object -Variable out
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        Write-Host ("Batch exited with code {0} - stopping (cancelled, redeploy, or error)." -f $code)
        break
    }
    if ($out -match 'retry-umls:\s+0\s+trial') {
        Write-Host ("No gaps left - drain complete after {0} batch(es)." -f $iteration)
        break
    }
    if ($DryRun) {
        Write-Host "Dry-run does not advance the selection - stopping after one batch."
        break
    }
}

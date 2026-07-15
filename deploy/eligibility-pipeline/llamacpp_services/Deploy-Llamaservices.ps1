#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $WinswDir,
    [string[]] $Services = @('llama-main', 'llama-normalize', 'llama-embedding'),
    [timespan] $Timeout  = [timespan]::FromMinutes(2)
)

if (-not $WinswDir) { $WinswDir = $env:LLAMA_WINSW_DIR }
if (-not $WinswDir) { $WinswDir = '.\' }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-WinswExe {
    param([Parameter(Mandatory)] [string] $Service)
    $path = Join-Path $WinswDir "$Service.exe"
    if (-not (Test-Path -LiteralPath $path)) {
        throw "WinSW exe not found: $path"
    }
    return $path
}

function Invoke-Winsw {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [Parameter(Mandatory)] [ValidateSet('install','uninstall','start','stop','restart','status','refresh')]
                               [string] $Command,
        [int[]] $AllowedExitCodes = @(0)
    )
    Write-Host ("  {0} {1}" -f (Split-Path -Leaf $Exe), $Command)
    & $Exe $Command | Out-Host
    $code = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $code) {
        $msg = [System.ComponentModel.Win32Exception]::new($code).Message
        throw "winsw '$Command' for $(Split-Path -Leaf $Exe) failed: exit $code - $msg"
    }
    return $code
}

function Wait-ServiceState {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [ValidateSet('Running','Stopped','Gone')] [string] $State
    )
    $deadline = (Get-Date).Add($Timeout)
    while ((Get-Date) -lt $deadline) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($State -eq 'Gone') {
            if (-not $svc) { return }
        }
        elseif ($svc -and $svc.Status -eq $State) {
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out after $Timeout waiting for $Name to reach state '$State'."
}

# ---- 1. Stop ----------------------------------------------------------------
Write-Host "`n[1/4] Stopping services" -ForegroundColor Cyan
foreach ($svc in $Services) {
    $exe = Get-WinswExe $svc
    if (Get-Service -Name $svc -ErrorAction SilentlyContinue) {
        # 1060 = not installed; 1062 = already stopped - both fine here.
        Invoke-Winsw -Exe $exe -Command 'stop' -AllowedExitCodes 0,1060,1062 | Out-Null
        Wait-ServiceState -Name $svc -State Stopped
    } else {
        Write-Host "  $svc not installed (skip)"
    }
}

# ---- 2. Uninstall -----------------------------------------------------------
Write-Host "`n[2/4] Uninstalling services" -ForegroundColor Cyan
foreach ($svc in $Services) {
    $exe = Get-WinswExe $svc
    Invoke-Winsw -Exe $exe -Command 'uninstall' -AllowedExitCodes 0,1060 | Out-Null
    Wait-ServiceState -Name $svc -State Gone
}

# ---- 3. Install -------------------------------------------------------------
Write-Host "`n[3/4] Installing services" -ForegroundColor Cyan
foreach ($svc in $Services) {
    $exe = Get-WinswExe $svc
    Invoke-Winsw -Exe $exe -Command 'install' | Out-Null
}

# ---- 4. Start ---------------------------------------------------------------
Write-Host "`n[4/4] Starting services" -ForegroundColor Cyan
foreach ($svc in $Services) {
    $exe = Get-WinswExe $svc
    Invoke-Winsw -Exe $exe -Command 'start' | Out-Null
    Wait-ServiceState -Name $svc -State Running
}

Write-Host "`nAll services redeployed successfully." -ForegroundColor Green
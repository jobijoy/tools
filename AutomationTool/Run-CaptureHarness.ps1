<#
.SYNOPSIS
    Builds IdolClick and runs the headless Calculator capture harness.

.DESCRIPTION
    The harness launches Calculator, creates a capture profile automatically,
    records a rolling review buffer, executes deterministic Calculator steps,
    takes orb-equivalent capture snapshots at each checkpoint, and injects
    synthetic sample voice-note WAV files into the capture annotation journal.

.PARAMETER LogPath
    Optional custom log path. Default: generated under bin\<Config>\net8.0-windows\logs.

.PARAMETER NoBuild
    Skip the build step and use the existing binary.

.PARAMETER Config
    Build configuration. Default: Release.
#>

[CmdletBinding()]
param(
    [string]$LogPath,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$projFile = Join-Path $projectRoot "src\IdolClick.App\IdolClick.csproj"
$binDir = Join-Path $projectRoot "src\IdolClick.App\bin\$Config\net8.0-windows"
$exePath = Join-Path $binDir "IdolClick.exe"

if (-not $NoBuild) {
    Write-Host "[build] Building IdolClick ($Config)..." -ForegroundColor Yellow
    & dotnet build $projFile -c $Config --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[build] Build failed" -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path $exePath)) {
    Write-Host "[error] Binary not found: $exePath" -ForegroundColor Red
    exit 1
}

if (-not $LogPath) {
    $logsDir = Join-Path $binDir "logs"
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
    $LogPath = Join-Path $logsDir "capture_harness_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
}

Write-Host "[harness] Running calculator capture harness" -ForegroundColor Cyan
Write-Host "[log] $LogPath" -ForegroundColor DarkGray

& $exePath --capture-harness --log $LogPath
exit $LASTEXITCODE
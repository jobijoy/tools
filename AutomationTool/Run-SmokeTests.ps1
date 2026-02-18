<#
.SYNOPSIS
    Launches IdolClick headless smoke tests and streams the log in real-time.

.DESCRIPTION
    This script:
    1. Builds IdolClick (if needed)
    2. Launches the --smoke CLI runner as a background process
    3. Tails the log file in real-time (Get-Content -Wait)
    4. Waits for test completion and reports pass/fail summary
    5. Exits with the same code as the smoke runner (0 = all pass, 1 = failures)

    Designed for use in CI/CD and by Copilot for autonomous test-fix-retest loops.

.PARAMETER Tests
    Comma-separated test IDs to run (e.g. "ST-01,ST-11"). Default: all tests.

.PARAMETER File
    Path to an external JSON test file (smoke-test.schema.json format).
    Instead of running built-in tests, loads and runs tests from this file.

.PARAMETER LogPath
    Custom log file path. Default: auto-generated in bin/Debug logs folder.

.PARAMETER NoBuild
    Skip the build step (use existing binary).

.PARAMETER Config
    Configuration to build (Debug or Release). Default: Debug.

.EXAMPLE
    .\Run-SmokeTests.ps1
    # Runs all 15 built-in tests with real-time streaming

.EXAMPLE
    .\Run-SmokeTests.ps1 -Tests "ST-11"
    # Runs only ST-11

.EXAMPLE
    .\Run-SmokeTests.ps1 -File "my-tests.json"
    # Runs tests from an external JSON file

.EXAMPLE
    .\Run-SmokeTests.ps1 -File "suite.json" -Tests "DEMO-01,DEMO-02"
    # Runs specific IDs from an external file

.EXAMPLE
    .\Run-SmokeTests.ps1 -Tests "ST-04,ST-07,ST-11" -LogPath "C:\temp\retest.txt"
    # Runs the 3 previously-failing tests with custom log path
#>

[CmdletBinding()]
param(
    [string]$Tests,
    [string]$File,
    [string]$LogPath,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Debug"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$projFile    = Join-Path $projectRoot "src\IdolClick.App\IdolClick.csproj"
$binDir      = Join-Path $projectRoot "src\IdolClick.App\bin\$Config\net8.0-windows"
$exePath     = Join-Path $binDir "IdolClick.exe"

# ── Header ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║           IdolClick Smoke Test Runner                ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── Step 1: Kill any running IdolClick ─────────────────────────────────────────
$existing = Get-Process -Name "IdolClick" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[prep] Stopping existing IdolClick process(es)..." -ForegroundColor Yellow
    $existing | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# ── Step 2: Build ──────────────────────────────────────────────────────────────
if (-not $NoBuild) {
    Write-Host "[build] Building IdolClick ($Config)..." -ForegroundColor Yellow
    $buildOutput = & dotnet build $projFile -c $Config --no-restore 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[build] BUILD FAILED:" -ForegroundColor Red
        $buildOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    $errors   = ($buildOutput | Select-String "Error\(s\)" | ForEach-Object { ($_ -split '\s+')[0] }) -join ""
    $warnings = ($buildOutput | Select-String "Warning\(s\)" | ForEach-Object { ($_ -split '\s+')[0] }) -join ""
    Write-Host "[build] OK — $warnings warning(s), $errors error(s)" -ForegroundColor Green
} else {
    Write-Host "[build] Skipped (NoBuild)" -ForegroundColor DarkGray
}

if (-not (Test-Path $exePath)) {
    Write-Host "[error] Binary not found: $exePath" -ForegroundColor Red
    exit 1
}

# ── Step 3: Prepare log file ──────────────────────────────────────────────────
if (-not $LogPath) {
    $logsDir = Join-Path $binDir "logs"
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
    $LogPath = Join-Path $logsDir "smoke_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
}

# Ensure parent directory exists
$logDir = Split-Path $LogPath -Parent
if ($logDir -and -not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

# Create empty log file so Get-Content -Wait can start immediately
if (-not (Test-Path $LogPath)) {
    New-Item -ItemType File -Path $LogPath -Force | Out-Null
}

Write-Host "[log]   $LogPath" -ForegroundColor DarkGray

# ── Step 4: Build CLI arguments ──────────────────────────────────────────────
$smokeArgs = @("--smoke")
if ($File) {
    $resolvedFile = Resolve-Path $File -ErrorAction Stop
    $smokeArgs += "--file"
    $smokeArgs += $resolvedFile.Path
    Write-Host "[file]  $($resolvedFile.Path)" -ForegroundColor Cyan
}
if ($Tests) {
    $smokeArgs += $Tests
    Write-Host "[tests] Running: $Tests" -ForegroundColor Cyan
} elseif (-not $File) {
    Write-Host "[tests] Running: ALL" -ForegroundColor Cyan
}
$smokeArgs += "--log"
$smokeArgs += $LogPath

Write-Host ""
Write-Host "─── Live Log Stream ────────────────────────────────────" -ForegroundColor DarkGray

# ── Step 5: Launch smoke runner as background process ────────────────────────
$proc = Start-Process -FilePath $exePath -ArgumentList $smokeArgs -PassThru -NoNewWindow -RedirectStandardError (Join-Path $logDir "smoke_stderr.txt")

# ── Step 6: Tail the log file in real-time ───────────────────────────────────
# We use a polling loop instead of Get-Content -Wait to detect process exit
$lastPos = 0
$suiteComplete = $false

while (-not $proc.HasExited -or -not $suiteComplete) {
    if (Test-Path $LogPath) {
        $content = Get-Content $LogPath -Raw -ErrorAction SilentlyContinue
        if ($content -and $content.Length -gt $lastPos) {
            $newText = $content.Substring($lastPos)
            $lastPos = $content.Length

            # Color-code output lines
            foreach ($line in ($newText -split "`n")) {
                $trimmed = $line.TrimEnd()
                if (-not $trimmed) { continue }

                if ($trimmed -match "PASSED") {
                    Write-Host $trimmed -ForegroundColor Green
                }
                elseif ($trimmed -match "FAILED|ERROR") {
                    Write-Host $trimmed -ForegroundColor Red
                }
                elseif ($trimmed -match "Suite Complete") {
                    Write-Host $trimmed -ForegroundColor Cyan
                    $suiteComplete = $true
                }
                elseif ($trimmed -match "^\s*✅") {
                    Write-Host $trimmed -ForegroundColor DarkGreen
                }
                elseif ($trimmed -match "^\s*❌") {
                    Write-Host $trimmed -ForegroundColor Red
                }
                elseif ($trimmed -match "\[tool\]|\[iter\]|\[llm\]|\[flow\]") {
                    Write-Host $trimmed -ForegroundColor DarkGray
                }
                elseif ($trimmed -match "───|═══") {
                    Write-Host $trimmed -ForegroundColor DarkCyan
                }
                else {
                    Write-Host $trimmed
                }
            }
        }
    }

    if ($proc.HasExited -and -not $suiteComplete) {
        # Process exited — read any remaining content
        Start-Sleep -Milliseconds 500
        if (Test-Path $LogPath) {
            $content = Get-Content $LogPath -Raw -ErrorAction SilentlyContinue
            if ($content -and $content.Length -gt $lastPos) {
                $newText = $content.Substring($lastPos)
                foreach ($line in ($newText -split "`n")) {
                    $trimmed = $line.TrimEnd()
                    if ($trimmed) { Write-Host $trimmed }
                }
            }
        }
        break
    }

    Start-Sleep -Milliseconds 250
}

# ── Step 7: Wait for process and report ──────────────────────────────────────
if (-not $proc.HasExited) {
    $proc.WaitForExit()
}

$exitCode = $proc.ExitCode
Write-Host ""
Write-Host "─── Summary ────────────────────────────────────────────" -ForegroundColor DarkGray

# Parse results from log
$logContent = if (Test-Path $LogPath) { Get-Content $LogPath -Raw } else { "" }
$passed  = ([regex]::Matches($logContent, "Result: ✅ PASSED")).Count
$failed  = ([regex]::Matches($logContent, "Result: ❌ FAILED")).Count
$errored = ([regex]::Matches($logContent, "Result: 💥 ERROR")).Count
$total   = $passed + $failed + $errored

if ($exitCode -eq 0) {
    Write-Host "  All $total tests PASSED" -ForegroundColor Green
} else {
    Write-Host "  $passed/$total passed, $failed failed, $errored error(s)" -ForegroundColor Red

    # List failed test names
    $failedTests = [regex]::Matches($logContent, "── \[(ST-\d+)\].*──.*\n.*\n.*\n.*Result: ❌")
    if ($failedTests.Count -eq 0) {
        # Try simpler pattern if multiline doesn't work
        $lines = $logContent -split "`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "Result: ❌ FAILED") {
                # Walk backward to find the test header
                for ($j = $i; $j -ge 0; $j--) {
                    if ($lines[$j] -match "── \[([A-Za-z0-9_-]+)\] (.+?) ──") {
                        Write-Host "    FAIL: $($Matches[1]) — $($Matches[2])" -ForegroundColor Red
                        break
                    }
                }
            }
        }
    }
}

Write-Host "  Log: $LogPath" -ForegroundColor DarkGray
Write-Host "  Exit: $exitCode" -ForegroundColor DarkGray
Write-Host ""

exit $exitCode

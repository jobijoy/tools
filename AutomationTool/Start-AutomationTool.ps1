# Automation Tool - Quick Start
$ErrorActionPreference = "Stop"

Write-Host "Starting Automation Tool..." -ForegroundColor Green

$exePath = Join-Path $PSScriptRoot "src\AutomationTool.App\bin\Release\net8.0-windows\AutomationTool.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Building..." -ForegroundColor Yellow
    dotnet build (Join-Path $PSScriptRoot "AutomationTool.sln") -c Release
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -ForegroundColor Red; exit 1 }
}

Write-Host "Press Ctrl+Alt+T to toggle automation." -ForegroundColor Cyan
Start-Process -FilePath $exePath

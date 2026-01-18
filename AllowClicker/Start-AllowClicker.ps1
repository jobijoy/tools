# UI Automation Tool - Quick Start Script
$ErrorActionPreference = "Stop"

Write-Host "Starting UI Automation Tool..." -ForegroundColor Green

$exePath = Join-Path $PSScriptRoot "src\VsCodeAllowClicker.App\bin\Release\net8.0-windows\UIAutomationTool.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Building..." -ForegroundColor Yellow
    dotnet build (Join-Path $PSScriptRoot "VsCodeAllowClicker.sln") -c Release
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -ForegroundColor Red; exit 1 }
}

Write-Host "Look for the shield icon in your system tray." -ForegroundColor Cyan
Write-Host "Press Ctrl+Alt+A to toggle automation." -ForegroundColor Cyan
Start-Process -FilePath $exePath

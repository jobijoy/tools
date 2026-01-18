# VS Code Allow Clicker - Quick Start Script
# Run this script to launch the application

$ErrorActionPreference = "Stop"

Write-Host "Starting VS Code Allow Clicker..." -ForegroundColor Green

$exePath = Join-Path $PSScriptRoot "src\VsCodeAllowClicker.App\bin\Release\net8.0-windows\VsCodeAllowClicker.App.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Application not built yet. Building now..." -ForegroundColor Yellow
    dotnet build (Join-Path $PSScriptRoot "VsCodeAllowClicker.sln") -c Release
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed! Please check the errors above." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Launching application..." -ForegroundColor Green
Write-Host "Look for the shield icon in your system tray." -ForegroundColor Cyan
Write-Host "Double-click the icon or press Ctrl+Alt+A to toggle on/off." -ForegroundColor Cyan
Write-Host ""

Start-Process -FilePath $exePath

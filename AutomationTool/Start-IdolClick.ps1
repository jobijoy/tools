# IdolClick - Quick Start
$ErrorActionPreference = "Stop"

Write-Host "Starting IdolClick..." -ForegroundColor Green

$exePath = Join-Path $PSScriptRoot "src\IdolClick.App\bin\Release\net8.0-windows\IdolClick.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Building..." -ForegroundColor Yellow
    dotnet build (Join-Path $PSScriptRoot "IdolClick.sln") -c Release
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -ForegroundColor Red; exit 1 }
}

Write-Host "Press Ctrl+Alt+T to toggle automation." -ForegroundColor Cyan
Start-Process -FilePath $exePath

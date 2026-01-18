# Open the application configuration file in the default editor

$configPath = Join-Path $PSScriptRoot "src\VsCodeAllowClicker.App\appsettings.json"

if (Test-Path $configPath) {
    Write-Host "Opening configuration file..." -ForegroundColor Green
    Start-Process -FilePath $configPath
} else {
    Write-Host "Configuration file not found at: $configPath" -ForegroundColor Red
    exit 1
}

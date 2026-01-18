<#
.SYNOPSIS
    Create a portable ZIP distribution of IdolClick

.DESCRIPTION
    Creates a self-contained ZIP file that can be extracted and run anywhere.
    No installation required.
#>

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$srcDir = Join-Path $rootDir "src\IdolClick.App"
$publishDir = Join-Path $rootDir "publish\win-x64"
$outputDir = Join-Path $scriptDir "output"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  IdolClick Portable Build" -ForegroundColor Cyan  
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get version from csproj
$csproj = Join-Path $srcDir "IdolClick.csproj"
[xml]$proj = Get-Content $csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }

Write-Host "[1/3] Building version $version..." -ForegroundColor Yellow

# Publish
Push-Location $srcDir
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir
        
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed"
    }
}
finally {
    Pop-Location
}

Write-Host "[2/3] Creating ZIP archive..." -ForegroundColor Yellow

# Create output directory
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Create ZIP
$zipName = "IdolClick-$version-win-x64-portable.zip"
$zipPath = Join-Path $outputDir $zipName

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

# Create a temp folder with proper structure
$tempDir = Join-Path $env:TEMP "IdolClick-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$tempAppDir = Join-Path $tempDir "IdolClick"
New-Item -ItemType Directory -Path $tempAppDir -Force | Out-Null

# Copy files
Copy-Item (Join-Path $publishDir "IdolClick.exe") $tempAppDir
Copy-Item (Join-Path $publishDir "IdolClick.xml") $tempAppDir -ErrorAction SilentlyContinue

# Copy Plugins folder if exists
$pluginsDir = Join-Path $publishDir "Plugins"
if (Test-Path $pluginsDir) {
    Copy-Item $pluginsDir $tempAppDir -Recurse
}

# Create README for portable
@"
IdolClick $version - Portable Edition
======================================

QUICK START:
1. Run IdolClick.exe
2. The app starts minimized to the system tray
3. Click the tray icon or press Ctrl+Alt+T to open

HOTKEY:
- Ctrl+Alt+T: Toggle the control panel (when app is running)

FILES:
- config.json: Created on first run (stores your rules)
- logs/: Log files directory

For full documentation, visit:
https://github.com/jobijoy/tools

"@ | Set-Content (Join-Path $tempAppDir "README.txt")

# Create ZIP
Compress-Archive -Path $tempAppDir -DestinationPath $zipPath -Force

# Cleanup
Remove-Item $tempDir -Recurse -Force

Write-Host "[3/3] Done!" -ForegroundColor Green
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Portable Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Output: $zipPath" -ForegroundColor Cyan
Write-Host "Size: $([math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB" -ForegroundColor White
Write-Host ""

# Open output folder
explorer.exe $outputDir

<# 
.SYNOPSIS
    Build IdolClick installer using Inno Setup

.DESCRIPTION
    This script builds a release version of IdolClick and creates a Windows installer.
    
.PARAMETER InnoSetupPath
    Path to ISCC.exe (Inno Setup Compiler). If not provided, tries common locations.

.PARAMETER SkipPublish
    Skip dotnet publish step (use existing publish output)

.EXAMPLE
    .\Build-Installer.ps1
    
.EXAMPLE
    .\Build-Installer.ps1 -InnoSetupPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#>

param(
    [string]$InnoSetupPath,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$srcDir = Join-Path $rootDir "src\IdolClick.App"
$publishDir = Join-Path $rootDir "publish\win-x64"
$issFile = Join-Path $scriptDir "IdolClick.iss"
$outputDir = Join-Path $scriptDir "output"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  IdolClick Installer Build Script" -ForegroundColor Cyan  
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Find Inno Setup
if (-not $InnoSetupPath) {
    $commonPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    
    foreach ($path in $commonPaths) {
        if (Test-Path $path) {
            $InnoSetupPath = $path
            break
        }
    }
}

if (-not $InnoSetupPath -or -not (Test-Path $InnoSetupPath)) {
    Write-Host "Inno Setup not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install Inno Setup 6 from:" -ForegroundColor Yellow
    Write-Host "  https://jrsoftware.org/isdl.php" -ForegroundColor White
    Write-Host ""
    Write-Host "Or specify the path manually:" -ForegroundColor Yellow
    Write-Host '  .\Build-Installer.ps1 -InnoSetupPath "C:\Path\To\ISCC.exe"' -ForegroundColor White
    exit 1
}

Write-Host "[1/3] Found Inno Setup: $InnoSetupPath" -ForegroundColor Green

# Step 1: Publish the application
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[2/3] Publishing IdolClick..." -ForegroundColor Yellow
    
    Push-Location $srcDir
    try {
        dotnet publish -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -o $publishDir
            
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed"
        }
        
        Write-Host "  Published to: $publishDir" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host ""
    Write-Host "[2/3] Skipping publish (using existing files)" -ForegroundColor Yellow
}

# Verify publish output
if (-not (Test-Path (Join-Path $publishDir "IdolClick.exe"))) {
    Write-Host "Error: IdolClick.exe not found in publish directory!" -ForegroundColor Red
    exit 1
}

# Step 2: Create output directory
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Step 3: Build installer
Write-Host ""
Write-Host "[3/3] Building installer..." -ForegroundColor Yellow

& $InnoSetupPath $issFile

if ($LASTEXITCODE -ne 0) {
    Write-Host "Installer build failed!" -ForegroundColor Red
    exit 1
}

# Find the output file
$setupFile = Get-ChildItem -Path $outputDir -Filter "IdolClick-*-Setup.exe" | 
    Sort-Object LastWriteTime -Descending | 
    Select-Object -First 1

if ($setupFile) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Build Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Installer: $($setupFile.FullName)" -ForegroundColor Cyan
    Write-Host "Size: $([math]::Round($setupFile.Length / 1MB, 2)) MB" -ForegroundColor White
    Write-Host ""
    
    # Open output folder
    explorer.exe $outputDir
} else {
    Write-Host "Warning: Could not find output installer file" -ForegroundColor Yellow
}

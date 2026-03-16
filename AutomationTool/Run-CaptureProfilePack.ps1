[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,
    [string]$LogPath,
    [string[]]$Set,
    [switch]$Full,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release"
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projFile = Join-Path $projectRoot 'src\IdolClick.App\IdolClick.csproj'
$binDir = Join-Path $projectRoot "src\IdolClick.App\bin\$Config\net8.0-windows"
$exePath = Join-Path $binDir 'IdolClick.exe'

if (-not $NoBuild) {
    & dotnet build $projFile -c $Config --no-restore
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

if (-not (Test-Path $exePath)) {
    throw "Binary not found: $exePath"
}

if (-not $LogPath) {
    $logsDir = Join-Path $binDir 'logs'
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
    $LogPath = Join-Path $logsDir "capture_pack_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
}

$cliArgs = @('--capture-pack', '--file', (Resolve-Path $File).Path, '--log', $LogPath)
if ($Full) { $cliArgs += '--full' }
foreach ($pair in @($Set)) {
    if ($pair) {
        $cliArgs += '--set'
        $cliArgs += $pair
    }
}

$running = Get-Process IdolClick -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

$argumentLine = ($cliArgs | ForEach-Object {
    if ($_ -match '[\s"]') {
        '"' + ($_ -replace '"', '\"') + '"'
    }
    else {
        $_
    }
}) -join ' '

$proc = Start-Process -FilePath $exePath -ArgumentList $argumentLine -PassThru -Wait
exit $proc.ExitCode
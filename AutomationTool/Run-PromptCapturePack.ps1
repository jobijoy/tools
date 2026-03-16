[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PromptText,
    [switch]$Yes,
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

$cliArgs = @('--prompt-pack-run', '--input', $PromptText)
if ($Yes) { $cliArgs += '--yes' }
if ($Full) { $cliArgs += '--full' }

$running = Get-Process IdolClick -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

$proc = Start-Process -FilePath $exePath -ArgumentList $cliArgs -PassThru -Wait
exit $proc.ExitCode

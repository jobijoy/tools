[CmdletBinding()]
param(
    [string]$LogPath,
    [string]$Symbol = 'AAPL',
    [switch]$Full,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release"
)

$symbolValue = $Symbol.Trim().ToUpperInvariant()
if (-not $symbolValue) {
    throw 'Symbol must not be empty.'
}

$runnerArgs = @{
    File = "examples\capture-profiles\finance-yahoo-stock-monitor.capture-profile.json"
    Config = $Config
    Set = @("symbol=$symbolValue")
}
if ($LogPath) { $runnerArgs.LogPath = $LogPath }
if ($Full) { $runnerArgs.Full = $true }
if ($NoBuild) { $runnerArgs.NoBuild = $true }

& "$PSScriptRoot\Run-CaptureProfilePack.ps1" @runnerArgs
exit $LASTEXITCODE
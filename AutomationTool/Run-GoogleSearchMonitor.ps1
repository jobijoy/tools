[CmdletBinding()]
param(
    [string]$LogPath,
    [string]$Query = 'IdolClick automation',
    [switch]$Full,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release"
)

$queryValue = $Query.Trim()
if (-not $queryValue) {
    throw 'Query must not be empty.'
}

$args = @{
    File = "examples\capture-profiles\google-search-monitor.capture-profile.json"
    Config = $Config
    Set = @(
        "query=$queryValue",
        "queryEncoded=$([uri]::EscapeDataString($queryValue))"
    )
}
if ($LogPath) { $args.LogPath = $LogPath }
if ($Full) { $args.Full = $true }
if ($NoBuild) { $args.NoBuild = $true }

& "$PSScriptRoot\Run-CaptureProfilePack.ps1" @args
exit $LASTEXITCODE
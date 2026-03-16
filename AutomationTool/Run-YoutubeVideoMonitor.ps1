[CmdletBinding()]
param(
    [string]$LogPath,
    [switch]$Full,
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release"
)

$args = @{
    File = "examples\capture-profiles\youtube-video-monitor.capture-profile.json"
    Config = $Config
}
if ($LogPath) { $args.LogPath = $LogPath }
if ($Full) { $args.Full = $true }
if ($NoBuild) { $args.NoBuild = $true }

& "$PSScriptRoot\Run-CaptureProfilePack.ps1" @args
exit $LASTEXITCODE
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File,
    [string]$ConfigPath,
    [switch]$SelectProfile
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$resolvedFile = Resolve-Path $File

if (-not $ConfigPath) {
    $releaseConfig = Join-Path $projectRoot 'src\IdolClick.App\bin\Release\net8.0-windows\config.json'
    $debugConfig = Join-Path $projectRoot 'src\IdolClick.App\bin\Debug\net8.0-windows\config.json'
    if (Test-Path $releaseConfig) {
        $ConfigPath = $releaseConfig
    }
    elseif (Test-Path $debugConfig) {
        $ConfigPath = $debugConfig
    }
    else {
        throw 'Could not find a built IdolClick config.json. Pass -ConfigPath explicitly.'
    }
}

if (-not (Test-Path $ConfigPath)) {
    throw "Config file not found: $ConfigPath"
}

$rawPack = Get-Content $resolvedFile.Path -Raw
$pack = $rawPack | ConvertFrom-Json
if ($pack.inputs) {
    foreach ($input in @($pack.inputs)) {
        if ($input.name -and $null -ne $input.defaultValue) {
            $rawPack = $rawPack.Replace("{{$($input.name)}}", [string]$input.defaultValue)
        }
    }
    $pack = $rawPack | ConvertFrom-Json
}
if (-not $pack.captureProfile) {
    throw 'The selected file does not contain captureProfile payload.'
}

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
if (-not $config.capture) {
    $config | Add-Member -MemberType NoteProperty -Name capture -Value ([pscustomobject]@{})
}
if (-not $config.capture.profiles) {
    $config.capture | Add-Member -MemberType NoteProperty -Name profiles -Value @()
}

$profiles = @($config.capture.profiles)
$existingIndex = -1
for ($i = 0; $i -lt $profiles.Count; $i++) {
    if ($profiles[$i].id -eq $pack.captureProfile.id) {
        $existingIndex = $i
        break
    }
}

if ($existingIndex -ge 0) {
    $profiles[$existingIndex] = $pack.captureProfile
    Write-Host "[capture] Updated existing profile: $($pack.captureProfile.name)" -ForegroundColor Yellow
}
else {
    $profiles += $pack.captureProfile
    Write-Host "[capture] Imported new profile: $($pack.captureProfile.name)" -ForegroundColor Green
}

$config.capture.profiles = $profiles
if ($SelectProfile -or -not $config.capture.selectedProfileId) {
    $config.capture.selectedProfileId = $pack.captureProfile.id
    Write-Host "[capture] Selected profile: $($pack.captureProfile.id)" -ForegroundColor Cyan
}

$config | ConvertTo-Json -Depth 40 | Set-Content $ConfigPath -Encoding UTF8
Write-Host "[config] Saved: $ConfigPath" -ForegroundColor DarkGray
if ($pack.bootstrapFlowPath) {
    Write-Host "[flow] Bootstrap flow: $($pack.bootstrapFlowPath)" -ForegroundColor DarkGray
}
if ($pack.queue -and $pack.queue.enabled) {
    Write-Host "[queue] Queue hint: $($pack.queue.queueId)" -ForegroundColor DarkGray
}

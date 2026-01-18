# Analyze automation logs for regression testing and debugging
# Usage: .\Analyze-Logs.ps1 [-Latest] [-ShowClicks] [-ShowErrors] [-ShowStats]

param(
    [switch]$Latest,
    [switch]$ShowClicks,
    [switch]$ShowErrors,
    [switch]$ShowStats,
    [string]$LogFile
)

$logsDir = Join-Path $PSScriptRoot "logs"

if (-not (Test-Path $logsDir)) {
    Write-Host "No logs directory found. Run the application first." -ForegroundColor Yellow
    exit 1
}

# Determine which log file to analyze
if ($LogFile) {
    $targetLog = $LogFile
} elseif ($Latest) {
    $targetLog = Get-ChildItem $logsDir\*.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $targetLog) {
        Write-Host "No log files found." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Analyzing latest log: $(Split-Path $targetLog -Leaf)" -ForegroundColor Cyan
} else {
    # Interactive selection
    $logs = Get-ChildItem $logsDir\*.log | Sort-Object LastWriteTime -Descending
    if ($logs.Count -eq 0) {
        Write-Host "No log files found." -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "`nAvailable log files:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $logs.Count; $i++) {
        $log = $logs[$i]
        Write-Host "  [$i] $($log.Name) - $(Get-Date $log.LastWriteTime -Format 'yyyy-MM-dd HH:mm:ss')"
    }
    
    $selection = Read-Host "`nSelect log file number (or press Enter for latest)"
    if ([string]::IsNullOrWhiteSpace($selection)) {
        $targetLog = $logs[0].FullName
    } else {
        $targetLog = $logs[[int]$selection].FullName
    }
}

Write-Host "`n=== Log Analysis ===" -ForegroundColor Green
Write-Host "File: $targetLog`n"

$content = Get-Content $targetLog

# Show statistics
if ($ShowStats -or (-not $ShowClicks -and -not $ShowErrors)) {
    Write-Host "Statistics:" -ForegroundColor Yellow
    
    $startTime = ($content | Select-Object -First 1) -replace '^\[([^\]]+)\].*', '$1'
    $endTime = ($content | Select-Object -Last 1) -replace '^\[([^\]]+)\].*', '$1'
    
    Write-Host "  Session Start: $startTime"
    Write-Host "  Session End:   $endTime"
    
    $clicks = ($content | Select-String -Pattern "ButtonClick").Count
    $errors = ($content | Select-String -Pattern "ERROR").Count
    $warnings = ($content | Select-String -Pattern "WARN").Count
    $scans = ($content | Select-String -Pattern "ButtonScan").Count
    $windowFound = ($content | Select-String -Pattern "Found target window").Count
    $windowNotFound = ($content | Select-String -Pattern "Target window not found").Count
    
    Write-Host "  Button Clicks:      $clicks" -ForegroundColor $(if ($clicks -gt 0) { "Green" } else { "Gray" })
    Write-Host "  Button Scans:       $scans"
    Write-Host "  Windows Found:      $windowFound"
    Write-Host "  Windows Not Found:  $windowNotFound"
    Write-Host "  Warnings:           $warnings" -ForegroundColor $(if ($warnings -gt 0) { "Yellow" } else { "Gray" })
    Write-Host "  Errors:             $errors" -ForegroundColor $(if ($errors -gt 0) { "Red" } else { "Gray" })
    Write-Host ""
}

# Show button clicks
if ($ShowClicks -or (-not $ShowStats -and -not $ShowErrors)) {
    $clicks = $content | Select-String -Pattern "ButtonClick"
    if ($clicks.Count -gt 0) {
        Write-Host "Button Clicks ($($clicks.Count)):" -ForegroundColor Yellow
        foreach ($click in $clicks) {
            Write-Host "  $click" -ForegroundColor Green
        }
        Write-Host ""
    } else {
        Write-Host "No button clicks recorded.`n" -ForegroundColor Gray
    }
}

# Show errors
if ($ShowErrors -or (-not $ShowStats -and -not $ShowClicks)) {
    $errors = $content | Select-String -Pattern "ERROR"
    if ($errors.Count -gt 0) {
        Write-Host "Errors ($($errors.Count)):" -ForegroundColor Red
        foreach ($error in $errors) {
            Write-Host "  $error" -ForegroundColor Red
        }
        Write-Host ""
    } else {
        Write-Host "No errors recorded.`n" -ForegroundColor Gray
    }
}

# Additional commands hint
Write-Host "Tip: Use these switches for specific analysis:" -ForegroundColor Cyan
Write-Host "  -Latest      : Analyze most recent log"
Write-Host "  -ShowClicks  : Show only button clicks"
Write-Host "  -ShowErrors  : Show only errors"
Write-Host "  -ShowStats   : Show only statistics"
Write-Host ""
Write-Host "Example: .\Analyze-Logs.ps1 -Latest -ShowClicks" -ForegroundColor Cyan

# ID: sample-logger
# Name: Sample Logger Plugin
# Description: Logs rule trigger information to a file
# Version: 1.0.0

# Variables available: $RuleName, $MatchedText, $WindowTitle, $ProcessName, $TriggerTime

$logPath = Join-Path $PSScriptRoot "plugin-log.txt"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$logEntry = "[$timestamp] Rule: $RuleName | Text: $MatchedText | Window: $WindowTitle | Process: $ProcessName"

Add-Content -Path $logPath -Value $logEntry

Write-Output "Logged to: $logPath"

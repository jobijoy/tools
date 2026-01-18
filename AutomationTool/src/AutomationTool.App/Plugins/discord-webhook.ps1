# ID: discord-webhook
# Name: Discord Webhook Plugin
# Description: Sends a notification to a Discord webhook
# Version: 1.0.0

# Configuration: Set your Discord webhook URL here
$webhookUrl = $env:DISCORD_WEBHOOK_URL
if (-not $webhookUrl) {
    Write-Warning "Set DISCORD_WEBHOOK_URL environment variable"
    exit 1
}

$message = @{
    content = "🤖 **AutomationTool Alert**"
    embeds = @(
        @{
            title = "Rule Triggered: $RuleName"
            fields = @(
                @{ name = "Matched Text"; value = $MatchedText; inline = $true }
                @{ name = "Window"; value = $WindowTitle; inline = $true }
                @{ name = "Process"; value = $ProcessName; inline = $true }
            )
            timestamp = (Get-Date).ToString("o")
            color = 3447003
        }
    )
} | ConvertTo-Json -Depth 10

try {
    Invoke-RestMethod -Uri $webhookUrl -Method Post -Body $message -ContentType "application/json"
    Write-Output "Sent to Discord"
} catch {
    Write-Error "Failed to send to Discord: $_"
    exit 1
}

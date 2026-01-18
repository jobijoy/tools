# Automation Tool

A modern, rule-based Windows UI automation platform built with WPF. Define rules to automatically click buttons, send keystrokes, run scripts, or alert you when specific UI elements appear. Extensible with plugins and scripting support.

## Features

- **🎯 Rule-Based Automation** - Create flexible rules with conditions and actions
- **🖱️ Visual Region Selector** - Draw rectangles on screen to target specific areas
- **📜 Scripting Engine** - PowerShell and C# (Roslyn) scripting with context variables
- **🔌 Plugin Architecture** - Extend with custom .NET DLLs or PowerShell scripts
- **📣 Notification Hooks** - Toast, webhook, or script-based notifications
- **📊 Event Timeline** - Live tracking of all automation events
- **🛡️ Safety Controls** - Cooldowns, time windows, dry-run mode, confirmations
- **🌙 Modern Dark UI** - Clean WPF interface with tabbed panels
- **🔔 System Tray** - Runs in background with global hotkey toggle

## Quick Start

```powershell
cd AutomationTool
.\Start-AutomationTool.ps1
```

Or build and run manually:
```powershell
dotnet build AutomationTool.sln -c Release
.\src\AutomationTool.App\bin\Release\net8.0-windows\AutomationTool.exe
```

## Creating Rules

1. Open the control panel
2. Click **+ Add Rule**
3. Configure the rule:

### Target
| Field | Description |
|-------|-------------|
| Target App | Process name(s) to monitor (e.g., `Code, Code - Insiders`) |
| Window Title | Optional: window title must contain this text |
| Element Type | Button, ListItem, Text, Link, or Any |
| Match Text | Comma-separated patterns (e.g., `Allow, Continue, OK`) |
| Use Regex | Treat match text as regular expression |
| Exclude Text | Patterns to skip (e.g., `Continue Chat in`) |

### Action
| Action | Description |
|--------|-------------|
| Click | Click the matched element |
| SendKeys | Send keyboard input (e.g., `Tab, Enter, Ctrl+A`) |
| RunScript | Execute PowerShell or C# script |
| ShowNotification | Display a notification |
| Alert | Log warning and show alert (no action) |
| Plugin | Execute a custom plugin |

### Safety
| Field | Description |
|-------|-------------|
| Cooldown | Seconds between actions for this rule |
| Time Window | Only active during hours (e.g., `09:00-17:00`) |
| Require Focus | Only act when window is focused |
| Dry Run | Log only, don't execute action |
| Confirm Before | Show confirmation dialog |
| Alert If Contains | Alert instead of act if text found nearby |

## Scripting

### PowerShell
```powershell
# Available variables: $RuleName, $MatchedText, $WindowTitle, $ProcessName, $TriggerTime
Write-Output "Rule $RuleName matched: $MatchedText"
```

### C# (Roslyn)
```csharp
// Access context through globals
Log($"Rule {Context.Rule.Name} triggered");
return $"Window: {Context.WindowTitle}";
```

## Plugins

Place plugins in the `Plugins/` folder:

### PowerShell Plugin
```powershell
# ID: my-plugin
# Name: My Plugin
# Description: Does something cool
# Version: 1.0.0

# Your script code here
Write-Output "Plugin executed for rule: $RuleName"
```

### .NET Plugin
Implement `IPluginAction` interface and compile as DLL.

## Notification Hooks

Rules can trigger notifications:
- **Toast** - System tray balloon notification
- **Webhook** - POST JSON to URL with rule context
- **Script** - Run custom notification script

Message templates support placeholders: `{RuleName}`, `{MatchedText}`, `{WindowTitle}`, `{ProcessName}`, `{TriggerTime}`, `{Action}`

## Configuration

Rules are stored in `config.json`:

```json
{
  "settings": {
    "automationEnabled": false,
    "pollingIntervalMs": 3000,
    "toggleHotkey": "Ctrl+Alt+T",
    "showPanelOnStart": true,
    "minimizeToTray": true,
    "logLevel": "Info",
    "scriptingEnabled": true,
    "pluginsEnabled": true,
    "timelineEnabled": true
  },
  "rules": [
    {
      "name": "VS Code Allow",
      "targetApp": "Code",
      "matchText": "Allow, Continue",
      "action": "Click",
      "cooldownSeconds": 2,
      "notification": {
        "type": "toast",
        "message": "Clicked {MatchedText} in {WindowTitle}",
        "onSuccess": true
      }
    }
  ]
}
```

## Hotkeys

| Hotkey | Action |
|--------|--------|
| Ctrl+Alt+T | Toggle automation on/off (configurable) |

## Requirements

- Windows 10/11
- .NET 8.0 Runtime

## Architecture

```
AutomationTool/
├── Models/
│   ├── Rule.cs              # Rule definition with all fields
│   └── AppConfig.cs         # Global settings
├── Services/
│   ├── AutomationEngine.cs     # Core rule evaluation loop
│   ├── ActionExecutor.cs       # Click, SendKeys, RunScript, Plugin
│   ├── ConfigService.cs        # JSON config management
│   ├── LogService.cs           # Logging with file output
│   ├── TrayService.cs          # System tray & hotkeys
│   ├── ScriptExecutionService.cs # PowerShell & C# scripting
│   ├── PluginService.cs        # Plugin discovery & execution
│   ├── NotificationService.cs  # Toast, webhook, script hooks
│   ├── EventTimelineService.cs # Event tracking
│   ├── RegionCaptureService.cs # Visual region selection
│   └── Infrastructure/         # Future extensibility stubs
└── UI/
    ├── MainWindow.xaml         # Main panel with tabs
    ├── RuleEditorWindow.xaml   # Rule editor dialog
    ├── SettingsWindow.xaml     # App settings
    └── RegionSelectorOverlay.xaml # Screen region picker
```

## Sample Rules

### Auto-click VS Code permission dialogs
```json
{
  "name": "VS Code Allow",
  "targetApp": "Code",
  "matchText": "Allow",
  "excludeTexts": ["Continue Chat in"],
  "action": "Click",
  "cooldownSeconds": 2
}
```

### Log Chrome downloads with script
```json
{
  "name": "Chrome Download Logger",
  "targetApp": "chrome",
  "matchText": "Download.*completed",
  "useRegex": true,
  "action": "RunScript",
  "scriptLanguage": "powershell",
  "script": "Add-Content -Path downloads.log -Value \"$TriggerTime: $MatchedText\""
}
```

### Webhook on Slack mention
```json
{
  "name": "Slack Mention Alert",
  "targetApp": "slack",
  "matchText": "@channel|@here|@yourname",
  "useRegex": true,
  "action": "ShowNotification",
  "notification": {
    "type": "webhook",
    "url": "https://your-webhook-url",
    "message": "Slack mention: {MatchedText}",
    "onSuccess": true
  }
}
```

## License

MIT

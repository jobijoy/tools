# Automation Tool

A modern, rule-based Windows UI automation utility built with WPF. Define rules to automatically click buttons, send keystrokes, run scripts, or alert you when specific UI elements appear.

## Features

- **🎯 Rule-Based Automation** - Create flexible rules with conditions and actions
- **🖱️ UI Automation** - Click buttons, interact with list items, detect text
- **⌨️ SendKeys** - Send keyboard shortcuts and key sequences
- **📜 Scripting** - Run PowerShell scripts on rule match
- **🛡️ Safety Controls** - Cooldowns, time windows, confirmation dialogs, alerts
- **🌙 Modern Dark UI** - Clean WPF interface with live logs
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
| RunScript | Execute PowerShell script |
| ShowNotification | Display a notification |
| Alert | Log warning and show alert (no action) |

### Safety
| Field | Description |
|-------|-------------|
| Cooldown | Seconds between actions for this rule |
| Time Window | Only active during hours (e.g., `09:00-17:00`) |
| Require Focus | Only act when window is focused |
| Confirm Before | Show confirmation dialog |
| Alert If Contains | Alert instead of act if text found nearby |

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
    "logLevel": "Info"
  },
  "rules": [
    {
      "name": "VS Code Allow",
      "targetApp": "Code",
      "matchText": "Allow, Continue",
      "action": "Click",
      "cooldownSeconds": 2
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
│   ├── Rule.cs           # Rule definition with all fields
│   └── AppConfig.cs      # Global settings
├── Services/
│   ├── AutomationEngine.cs  # Core rule evaluation loop
│   ├── ActionExecutor.cs    # Click, SendKeys, RunScript
│   ├── ConfigService.cs     # JSON config management
│   ├── LogService.cs        # Logging
│   ├── TrayService.cs       # System tray & hotkeys
│   └── Win32.cs             # Native interop
└── UI/
    ├── MainWindow.xaml      # Main control panel
    ├── RuleEditorWindow.xaml # Rule editor dialog
    └── SettingsWindow.xaml  # App settings
```

## Roadmap

- [ ] Visual region selector (click+drag)
- [ ] C# scripting via Roslyn
- [ ] Action chaining (sequences)
- [ ] Import/export rules
- [ ] Webhook support

## License

MIT

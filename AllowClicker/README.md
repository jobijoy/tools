# UI Automation Tool

A lightweight Windows tray app for automating UI interactions. Create rules to automatically click buttons, send keystrokes, or alert you when specific elements appear.

## Features

- **Rule-based automation** - Define what to find and what to do
- **Visual rule editor** - Easy UI to create and manage rules
- **Safety controls** - Confirmation prompts, alerts, cooldowns
- **System tray** - Runs quietly in background
- **Hotkey toggle** - Ctrl+Alt+A to enable/disable

## Quick Start

```powershell
cd AllowClicker
dotnet run --project src\VsCodeAllowClicker.App
```

Or use the launcher script:
```powershell
.\Start-AllowClicker.ps1
```

## Creating Rules

1. Open the control panel (double-click tray icon)
2. Click **Add Rule**
3. Configure:
   - **Target**: What process/window/element to find
   - **Action**: Click, SendKeys, Alert, or ReadAndAlert
   - **Safety**: Cooldown, confirmation, alert patterns

### Example: Auto-click VS Code "Allow" buttons

```json
{
  "Name": "VS Code Allow",
  "Target": {
    "ProcessNames": ["Code"],
    "ElementType": "Button",
    "TextPatterns": ["Allow", "Continue", "OK"]
  },
  "Action": { "Type": "Click" },
  "Safety": { "CooldownMs": 1500 }
}
```

## Configuration

Rules are stored in `config.json`:

```json
{
  "Settings": {
    "PollingIntervalMs": 3000,
    "AutoStart": false,
    "ShowPanelOnStart": true,
    "ToggleHotkey": "Ctrl+Alt+A",
    "LogLevel": "Info"
  },
  "Rules": [...]
}
```

## Safety Features

- **Cooldown**: Minimum time between actions per rule
- **Confirm before action**: Show dialog before clicking
- **Alert if contains**: Scan nearby text for dangerous patterns
- **ReadAndAlert**: Read content without clicking

## Requirements

- Windows 10/11
- .NET 8.0 Runtime

## License

MIT

# AutomationTool

Rule-based Windows UI automation. Auto-click buttons, send keys, run scripts when specific UI elements appear.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4) ![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4) ![License](https://img.shields.io/badge/License-MIT-green)

## Features

| Feature | Description |
|---------|-------------|
| Rule-Based Automation | Match UI elements by app, text, regex, element type |
| Visual Region Selector | Draw screen regions to target specific areas |
| Scripting | PowerShell & C# (Roslyn) with context variables |
| Plugins | Extend with .NET DLLs or PowerShell scripts |
| Notifications | Toast, webhook, or script hooks |
| Safety | Cooldowns, time windows, dry-run, confirmations |

## Quick Start

```powershell
# Clone and run
git clone https://github.com/jobijoy/tools.git
cd tools/AutomationTool
.\Start-AutomationTool.ps1
```

**Requirements:** Windows 10/11, [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Usage

1. Launch app (runs in system tray)
2. Click tray icon → **Show Panel**
3. Add rules via **+ Add Rule**
4. Toggle automation with **Ctrl+Alt+T** or tray menu

## Config Example

`config.json` in app directory:

```json
{
  "settings": {
    "automationEnabled": false,
    "pollingIntervalMs": 3000,
    "toggleHotkey": "Ctrl+Alt+T"
  },
  "rules": [{
    "name": "VS Code Allow",
    "targetApp": "Code",
    "matchText": "Allow, Continue",
    "excludeTexts": ["Continue Chat in"],
    "action": "Click",
    "cooldownSeconds": 2
  }]
}
```

## Actions

| Action | Description |
|--------|-------------|
| `Click` | Click matched element |
| `SendKeys` | Keyboard input (`Tab`, `Enter`, `Ctrl+A`) |
| `RunScript` | PowerShell or C# script |
| `ShowNotification` | Display notification |
| `Plugin` | Run custom plugin |

## Scripting

**PowerShell** — Variables: `$RuleName`, `$MatchedText`, `$WindowTitle`, `$ProcessName`, `$TriggerTime`

```powershell
Write-Output "Matched: $MatchedText in $WindowTitle"
```

**C# (Roslyn)** — Access via `Context` object

```csharp
Log($"Rule {Context.Rule.Name} triggered");
```

## Plugins

Drop `.ps1` or `.dll` files in `Plugins/` folder. See [Plugins/README.md](src/AutomationTool.App/Plugins/README.md).

## Build

```powershell
dotnet build AutomationTool.sln -c Release
```

Output: `src/AutomationTool.App/bin/Release/net8.0-windows/AutomationTool.exe`

## License

[MIT](LICENSE)

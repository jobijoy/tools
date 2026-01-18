# IdolClick

> **IdolClick** – Smart Windows UI automation from your system tray

Rule-based Windows UI automation. Auto-click buttons, send keys, run scripts when specific UI elements appear.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4) ![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4) ![License](https://img.shields.io/badge/License-MIT-green)

<p align="center">
  <img src="src/IdolClick.App/Assets/idol-click.png" alt="IdolClick" width="150"/>
</p>

## Features

| Feature | Description |
|---------|-------------|
| Rule-Based Automation | Match UI elements by app, text, regex, element type |
| Visual Region Selector | Draw screen regions to target specific areas |
| Scripting | PowerShell & C# (Roslyn) with context variables |
| Plugins | Extend with .NET DLLs or PowerShell scripts |
| Notifications | Toast, webhook, or script hooks |
| Safety | Cooldowns, time windows, dry-run, confirmations |
| Single Instance | Only one instance runs at a time |

## Preview

<!-- Add screenshots here -->
*Coming soon: Main window, tray icon, and rule editor screenshots*

## Quick Start

```powershell
# Clone and run
git clone https://github.com/jobijoy/tools.git
cd tools/AutomationTool
.\Start-IdolClick.ps1
```

**Requirements:** Windows 10/11, [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Usage

1. Launch app (runs in system tray)
2. Click tray icon → **Show Panel**
3. Add rules via **+ Add Rule**
4. Toggle automation with **Ctrl+Alt+T** or tray menu

## 🔥 Hotkey Launching (Ctrl+Alt+T)

You can start or toggle IdolClick using the same hotkey:

### When App is Running
- Press **Ctrl+Alt+T** to show/hide the control panel
- Works globally from any application

### When App is Not Running
Create a Windows shortcut with the hotkey:

1. Right-click `IdolClick.exe` → **Create shortcut**
2. Move shortcut to Desktop or Start Menu folder
3. Right-click shortcut → **Properties**
4. Click in **Shortcut key** field → Press **Ctrl+Alt+T**
5. Click **OK**

Now **Ctrl+Alt+T** launches the app if closed, or toggles the window if running!

### Pro Tip: Auto-Start
Enable **"Start with Windows"** in Settings so IdolClick is always running in tray.

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

Drop `.ps1` or `.dll` files in `Plugins/` folder. See [Plugins/README.md](src/IdolClick.App/Plugins/README.md).

## Build

```powershell
dotnet build IdolClick.sln -c Release
```

Output: `src/IdolClick.App/bin/Release/net8.0-windows/IdolClick.exe`

### Publish (Self-contained)

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

## Security & Privacy

> 🔐 **All UI actions are performed locally.** No data is sent externally. Rules only interact with windows you target. The application requires no network access and stores all configuration locally.

## License

[MIT](LICENSE)

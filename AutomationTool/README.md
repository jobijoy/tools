# IdolClick

> **AI-Compatible Deterministic UI Execution Runtime for Windows**

Desktop UI automation with structured flows, safety guardrails, and AI agent integration. Rule-based or AI-driven — your choice.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4) ![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4) ![License](https://img.shields.io/badge/License-MIT-green) ![Version](https://img.shields.io/badge/v1.0.0-blue)

<p align="center">
  <img src="src/IdolClick.App/Assets/idol-click.png" alt="IdolClick" width="150"/>
</p>

## What It Does

| Mode | Description |
|------|-------------|
| **Classic** | Rule-based polling — auto-click, send keys, run scripts when UI elements match |
| **Agent** | LLM chat with 9 tools — AI authors structured flows, executes them, reads reports, patches + retries |

**For the full product narrative, architecture, and DSL specification, see [PRODUCT.md](PRODUCT.md).**

## Features

| Feature | Description |
|---------|-------------|
| Structured Flows | JSON DSL with 13 actions, typed selectors, post-step assertions |
| AI Agent Integration | Microsoft.Extensions.AI with function-calling (9 tools) |
| Vision Fallback | Screenshot + LLM vision when UIA selectors fail (flagged as Warning) |
| Safety Hardening | Kill switch, target lock, process allowlist, audit log |
| Actionability Checks | Playwright-inspired pre-action validation (visible, enabled, stable) |
| Execution Reports | Machine-readable JSON with timing, element snapshots, backend call log |
| Rule Engine | Match UI elements by app, text, regex, region, element type |
| Scripting | PowerShell & C# (Roslyn) with context variables |
| Plugins | Extend with .NET DLLs or PowerShell scripts |

## Quick Start

```powershell
git clone https://github.com/jobijoy/tools.git
cd tools/AutomationTool
.\Start-IdolClick.ps1
```

**Requirements:** Windows 10/11, [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Usage

1. Launch app (runs in system tray)
2. Click tray icon → **Show Panel**
3. Choose mode: **Classic** (rules) or **Agent** (AI chat)
4. Toggle automation with **Ctrl+Alt+T** or tray menu
5. Emergency stop: **Ctrl+Alt+Escape** (kill switch)

## Safety

| Guardrail | Description |
|-----------|-------------|
| **Kill Switch** | `Ctrl+Alt+Escape` — instantly stops everything, requires manual reset |
| **Target Lock** | Pin to specific window (HWND + PID); fail if focus shifts |
| **Process Allowlist** | Only automate listed processes (wildcards supported) |
| **Vision Warnings** | Vision-resolved steps are flagged, never silently promoted |
| **Audit Log** | All safety events written to `logs/audit_log.txt` |
| **Per-Rule Limits** | Cooldowns, time windows, max executions, dry-run, confirmations |

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

## Security & Safety

> 🔐 **All UI actions are performed locally.** No data is sent externally unless you configure an LLM endpoint for Agent mode. Rules only interact with windows you target. Configuration is stored locally in `config.json`.

> 🛡️ **Safety-first design.** Kill switch, target lock, process allowlist, and audit logging are built-in. See [PRODUCT.md](PRODUCT.md) for the full safety model.

## License

[MIT](LICENSE)

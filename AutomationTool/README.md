# IdolClick

> **Smart Windows desktop automation — rule engine, AI agent, and flow builder in one tool.**

<p align="center">
  <img src="src/IdolClick.App/Assets/idol-click.png" alt="IdolClick" width="120"/>
</p>

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4) ![WPF-UI](https://img.shields.io/badge/WPF--UI-Fluent%20Design-0078D4) ![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4) ![License](https://img.shields.io/badge/License-MIT-green) ![Version](https://img.shields.io/badge/v1.0.0-blue)

---

## What Is IdolClick?

IdolClick automates Windows desktop applications through three modes:

| Mode | Icon | What It Does |
|------|------|--------------|
| **Instinct** | ⚡ | Rule-based engine — auto-click buttons, send keys, run scripts when UI elements match |
| **Reason** | 🧠 | AI agent chat — describe what you want in plain English, the agent plans and executes |
| **Teach** | 🎓 | Smart Sentence Builder — build reusable automation flows step by step |

It works by reading the **Windows UI Automation tree** (the same accessibility layer screen readers use) to find, verify, and interact with controls in any desktop application.

### What IdolClick Is NOT

- **Not a browser automation tool** — use Playwright, Puppeteer, or Selenium for web testing
- **Not a screen-coordinate macro** — actions target semantic UI elements, not pixel positions
- **Not a general-purpose RPA platform** — purpose-built for desktop automation on Windows
- **Not cross-platform** — requires Windows 10/11 and the UIAutomation API

---

## Quick Start

```powershell
git clone https://github.com/jobijoy/IdolClick.git
cd IdolClick
.\Start-IdolClick.ps1
```

**Requirements:** Windows 10/11, [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### First Launch

1. Choose a mode (Instinct, Reason, or Teach) on the home screen
2. **Instinct mode** — add rules that match UI elements and trigger actions
3. **Reason mode** — type a request like *"Click the Allow button in VS Code every 5 seconds"*
4. **Teach mode** — describe a workflow and refine the generated steps
5. Toggle automation with **Ctrl+Alt+T** or the play button
6. Emergency stop: **Ctrl+Alt+Escape** (kill switch)

---

## Architecture

```
┌───────────────────────────────────────────────────────────┐
│                    Brain (LLM via IChatClient)             │
│      Plan → Compile → Execute → Report → Fix → Retry      │
└──────────┬────────────────────────────────┬────────────────┘
           │                                │
     PackOrchestrator                  AgentTools (14 tools)
           │                                │
     ┌─────┴──────┐                   StepExecutor
     │ Plan       │                        │
     │ Compile    │                 IAutomationBackend
     │ Run        │                        │
     │ Report     │                  DesktopBackend
     └────────────┘                  (UIA + Win32)
```

| Layer | Purpose |
|-------|---------|
| **Brain** | LLM (GPT-4o, Claude, or any OpenAI-compatible model) reasons, plans, decides |
| **Eye** | UI Automation tree + optional screenshot capture with LLM vision |
| **Hand** | Deterministic UIA actions — click, type, send keys, assert, wait |

---

## Modules

| Module | Status | Description |
|--------|--------|-------------|
| **Classic** | Stable | Rule-based polling engine — auto-click, send keys, run scripts |
| **Agent** | Stable | LLM chat with 14 tool-calling functions |
| **Pack** | Stable | Orchestrated testing pipeline with confidence scoring |
| **Commands** | Alpha | Natural language → structured flow compiler |
| **API** | Alpha | Embedded Kestrel REST + SignalR for external integration |
| **MCP** | Alpha | Model Context Protocol server for AI agent interop |

## Key Features

| Feature | Description |
|---------|-------------|
| **Structured Flows** | JSON DSL with 13 actions, typed selectors, post-step assertions |
| **AI Agent (14 tools)** | Microsoft.Extensions.AI with function-calling |
| **Pack Pipeline** | Plan → Compile → Validate → Execute → Report |
| **Dual-Perception Eye** | Structural (UIA tree) + Visual (screenshot) |
| **Confidence Scoring** | 0.0–1.0 weighted quality signal after execution |
| **Safety Hardening** | Kill switch, target lock, process allowlist, audit log |
| **Execution Dashboard** | Live step-by-step progress with pass/fail indicators |
| **Execution Reports** | Machine-readable JSON with timing and element snapshots |
| **Scripting** | PowerShell & C# (Roslyn) with context variables |
| **Plugins** | Extend with .NET DLLs or PowerShell scripts |

---

## Safety

| Guardrail | Description |
|-----------|-------------|
| **Kill Switch** | `Ctrl+Alt+Escape` — instantly stops everything |
| **Target Lock** | Pin execution to a specific window (HWND + PID) |
| **Process Allowlist** | Only automate listed processes |
| **Vision Warnings** | Vision-resolved steps are flagged, never silently promoted |
| **Audit Log** | All safety events written to `logs/audit_log.txt` |
| **Per-Rule Limits** | Cooldowns, time windows, max executions, dry-run mode |

---

## Config Example

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

See [`examples/`](examples/) for ready-to-use flow files.

## Actions

| Action | Description |
|--------|-------------|
| `Click` | Click matched element |
| `SendKeys` | Keyboard input (`Tab`, `Enter`, `Ctrl+A`) |
| `RunScript` | PowerShell or C# script |
| `ShowNotification` | Display notification |
| `Plugin` | Run custom plugin |

---

## Build

```powershell
dotnet build IdolClick.sln -c Release
```

Output: `src/IdolClick.App/bin/Release/net8.0-windows/IdolClick.exe`

### Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

---

## Documentation

| Document | Audience | Description |
|----------|----------|-------------|
| [CURRENT-STATE.md](CURRENT-STATE.md) | Everyone | Current validated status, mode maturity, test coverage, and known risks |
| [DOCS-INDEX.md](DOCS-INDEX.md) | Everyone | Documentation map, source-of-truth ownership, and recommended read paths |
| [DESIGN-SYSTEM.md](DESIGN-SYSTEM.md) | UI contributors | Theme/token architecture, styling rules, and future skinning guidance |
| [PRODUCT.md](PRODUCT.md) | Everyone | Full product narrative, DSL spec, safety model |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Architects | Layer architecture, data flow, components |
| [AGENTS-GUIDE.md](AGENTS-GUIDE.md) | Coding agents | Conventions, common tasks, file map |
| [CHANGELOG.md](CHANGELOG.md) | Everyone | Version history and release notes |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contributors | How to contribute, code style, PR process |
| [SECURITY.md](SECURITY.md) | Security researchers | Vulnerability reporting |

## Roadmap

- **v1.0.0** ✅ Classic mode, Agent mode, Pack Pipeline, Desktop UIA backend, safety hardening
- **v1.1** — MCP cross-validation (Figma, backend APIs)
- **v1.2** — Live Execution Dashboard, Confidence Engine v2
- **v1.3** — Advanced UIA selectors, CLI runner, multi-monitor
- **v1.4** — Multi-agent orchestration, distributed execution

## License

[MIT](LICENSE)

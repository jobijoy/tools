# IdolClick — Product Overview

> **AI-Compatible Deterministic UI Execution Runtime for Windows**

---

## What Is IdolClick?

IdolClick is a **spec-driven UI automation runtime** that sits between AI coding agents and Windows desktop applications. It receives structured JSON flow specifications, executes each step deterministically against real UI elements, and returns machine-readable execution reports.

The closed-loop interaction model:

```
Coding Agent → Structured Flow JSON → IdolClick parses → Execution Engine runs deterministic steps → ExecutionReport → AI Consumer → Patch + Retry
```

IdolClick is **not** a chatbot. It is **not** an RPA tool. It is a **strict execution spec + validation layer** for AI agents targeting Windows desktop apps.

---

## Positioning

IdolClick deliberately **does not** compete with:

| Tool | Focus | IdolClick Difference |
|------|-------|---------------------|
| OpenClaw | Vision-first autonomous agents | IdolClick is deterministic-first; vision is a fallback, never primary |
| Power Automate Desktop | Generic enterprise RPA | IdolClick is developer-facing, AI-native, no-code-free |
| Playwright | Browser-first testing | IdolClick targets native Windows UIA; Playwright support is planned for v1.1 |
| AutoHotkey | Scripting + hotkeys | IdolClick is structured/typed; flows are machine-generated, not hand-written |

IdolClick owns one thing:

> **Closed-loop regression testing for Windows desktop applications, orchestrated by AI agents.**

---

## Architecture

### Dual-Mode Operation

| Mode | Description |
|------|-------------|
| **Classic** | Rule-based polling engine — monitors windows and auto-clicks matching UI elements |
| **Agent** | LLM chat with 9 tool-calling functions + structured flow execution |

### Resolution Chain

Element resolution follows a strict hierarchy:

1. **UIA Selector** — Windows UI Automation tree (deterministic, fast, stable)
2. **Vision Fallback** — Screenshot + LLM vision API (non-deterministic, flagged as `Warning`)

Vision is **never primary**. Steps resolved by vision are marked `StepStatus.Warning` with `WarningCode: VisionFallbackUsed` so AI consumers can make informed decisions.

### Execution Pipeline

```
TestFlow JSON
  → FlowValidatorService (schema + semantic checks)
    → StepExecutor (orchestration, ordering, timeout, reporting)
      → IAutomationBackend.ExecuteStepAsync (per-step execution)
        → SelectorParser → Actionability Checks → Action → Assertions
          → StepResult (with timing, element snapshot, backend call log)
    → ExecutionReport (machine-readable, versioned)
```

### Backend Abstraction

```
IAutomationBackend
  ├── DesktopBackend (v1.0 — Windows UIA + Win32)
  └── PlaywrightBackend (v1.1 — Web automation via Chromium/Firefox/WebKit)
```

Each backend owns: selector resolution, actionability checks, action execution, assertion evaluation, and backend call logging.

---

## TestFlow DSL v1

The exchange format between AI agents and IdolClick.

### Example Flow

```json
{
  "schemaVersion": 1,
  "testName": "Verify Notepad Save Dialog",
  "targetApp": "notepad",
  "backend": "desktop",
  "targetLock": true,
  "stopOnFailure": true,
  "timeoutSeconds": 60,
  "steps": [
    {
      "order": 1,
      "action": "click",
      "selector": "Button#Save",
      "description": "Click the Save button",
      "timeoutMs": 5000,
      "delayAfterMs": 500
    },
    {
      "order": 2,
      "action": "assert_exists",
      "selector": "Window#Save As",
      "description": "Verify Save As dialog appeared",
      "timeoutMs": 3000
    },
    {
      "order": 3,
      "action": "type",
      "selector": "Edit#File name:",
      "text": "test-output.txt",
      "description": "Enter filename"
    }
  ]
}
```

### Supported Actions

| Action | Description |
|--------|-------------|
| `click` | Click a UI element (UIA invoke or coordinate-based) |
| `type` | Set text in an input field |
| `send_keys` | Keyboard input (Ctrl+S, Tab, Enter, etc.) |
| `wait` | Wait for a condition or fixed delay |
| `assert_exists` | Verify element is present |
| `assert_not_exists` | Verify element is NOT present |
| `assert_text` | Verify element text content |
| `assert_window` | Verify window title |
| `navigate` | Open URL (web backend) |
| `screenshot` | Capture screen state |
| `scroll` | Scroll in a direction |
| `focus_window` | Bring window to foreground |
| `launch` | Start a process |

### Selector Format (Desktop UIA)

```
ElementType#TextOrAutomationId
```

Examples:
- `Button#Save` — Button with text or AutomationId "Save"
- `Edit#File name:` — Edit box near "File name:" label
- `#myAutomationId` — Any element with AutomationId "myAutomationId"

### Typed Selectors (Multi-Backend)

```json
{
  "typedSelector": {
    "kind": "desktop_uia",
    "value": "Button#Save"
  }
}
```

Supported kinds: `desktop_uia`, `playwright_css`, `playwright_role`, `playwright_text`, `playwright_label`, `playwright_testid`

---

## Execution Report v1

Machine-readable output for AI consumption.

```json
{
  "schemaVersion": 1,
  "testName": "Verify Notepad Save Dialog",
  "result": "passed",
  "totalTimeMs": 3421,
  "startedAt": "2026-02-14T10:30:00Z",
  "finishedAt": "2026-02-14T10:30:03Z",
  "backendUsed": "desktop-uia",
  "backendVersion": "1.0.0",
  "machineName": "DEV-WORKSTATION",
  "osVersion": "Microsoft Windows NT 10.0.22631.0",
  "steps": [
    {
      "step": 1,
      "action": "click",
      "selector": "Button#Save",
      "status": "passed",
      "timeMs": 1200,
      "retryCount": 0,
      "selectorResolvedTo": "Button 'Save' (id=btnSave)",
      "clickPoint": { "x": 450, "y": 320, "width": 1, "height": 1 },
      "backendCallLog": [
        { "timestampMs": 0, "message": "Finding window (app='notepad')", "level": "info" },
        { "timestampMs": 50, "message": "Window found: 'Untitled - Notepad'", "level": "info" },
        { "timestampMs": 100, "message": "Resolving selector: 'Button#Save'", "level": "info" },
        { "timestampMs": 800, "message": "Element resolved after 0 retries", "level": "info" },
        { "timestampMs": 850, "message": "Actionability: visible ✓", "level": "info" },
        { "timestampMs": 900, "message": "Executing action: Click", "level": "info" },
        { "timestampMs": 1100, "message": "Action succeeded", "level": "info" },
        { "timestampMs": 1200, "message": "Step passed", "level": "info" }
      ]
    }
  ],
  "summary": "Flow 'Verify Notepad Save Dialog': PASSED\n  3 passed, 0 failed, 0 warnings, 0 skipped\n  Total time: 3421ms\n  Backend: desktop-uia 1.0.0"
}
```

### Step Statuses

| Status | Meaning |
|--------|---------|
| `passed` | Step completed deterministically |
| `failed` | Assertion mismatch or element not found |
| `skipped` | Skipped due to prior failure or cancellation |
| `error` | Unexpected runtime error |
| `warning` | Passed but via non-deterministic path (vision fallback) — review recommended |

---

## Safety Hardening

IdolClick prioritizes safety as a first-class concern. Automation that can click arbitrary windows requires guardrails.

### Target Lock (HWND + PID Pinning)

When `targetLock: true`, the executor captures the initial window handle, process ID, and title on the first step. Every subsequent step verifies:
- Window handle (HWND) unchanged
- Process ID unchanged
- Window still exists

If any check fails, execution halts with a `TargetLock violation` error. This prevents clicking the wrong window if focus shifts.

### Global Kill Switch

A system-wide hotkey (default: `Ctrl+Alt+Escape`) that instantly:
1. Disables the classic automation engine
2. Cancels any running agent flow
3. Locks the engine in a `DisabledUntilManualReset` state (prevents restart loops)
4. Writes an audit log entry

The engine cannot be re-enabled until the user explicitly resets from the UI.

### Process Allowlist

When configured, automation **only** targets listed processes. Supports wildcards (`MyApp*`). Both the classic engine and flow executor enforce this. Blocked attempts are logged and audited.

### Vision Fallback Transparency

Vision-resolved steps are never silently promoted to "passed". They carry:
- `StepStatus.Warning`
- `WarningCode: VisionFallbackUsed`
- Confidence score in diagnostics

### Per-Rule Guardrails

| Feature | Description |
|---------|-------------|
| `cooldownSeconds` | Minimum time between triggers |
| `timeWindow` | Only active during specific hours (HH:mm-HH:mm) |
| `requireFocus` | Only trigger when window has keyboard focus |
| `confirmBeforeAction` | Prompt user before executing |
| `alertIfContains` | Pause if dangerous text detected near target |
| `dryRun` | Log what would happen without executing |
| `maxExecutionsPerSession` | Cap triggers per app session |

### Audit Log

Safety-critical events are written to a persistent `audit_log.txt`:
- Kill switch activation/reset
- Target lock violations
- Process allowlist blocks
- Vision fallback usage

### Actionability Checks (Playwright-Inspired)

Before every action, the backend verifies:
- **Exists** — element is in the UI tree
- **Visible** — non-zero bounding box
- **Enabled** — not grayed out
- **Stable** — bounds unchanged across two reads (not animating)
- **ReceivesEvents** — not off-screen
- **Editable** — for text input, ValuePattern is writable

---

## Agent Tools

When in Agent mode, the LLM has access to 9 structured tools:

| Tool | Purpose |
|------|---------|
| `ListWindows` | Enumerate visible windows with process info |
| `InspectWindow` | Drill into a window's UI automation tree |
| `ListProcesses` | List running processes |
| `ValidateFlow` | Pre-validate a TestFlow JSON without executing |
| `RunFlow` | Execute a TestFlow and get an ExecutionReport |
| `ListReports` | Browse past execution reports |
| `CaptureScreenshot` | Take a screenshot for visual context |
| `GetCapabilities` | Query what actions/assertions the backend supports |
| `LocateByVision` | Use LLM vision to find an element by description |

The agent interaction is **spec-driven, not chat-driven**. The LLM uses `ValidateFlow` → `RunFlow` → reads report → patches flow → retries. This is the closed-loop regression testing model.

---

## Use Cases

### Primary (v1.0)

1. **Closed-loop Desktop App Testing** — AI agent authors flows, runs them, reads reports, patches and retries
2. **UI Regression Detection** — Run deterministic flows after each build to verify UI stability
3. **Developer Workflow Automation** — Auto-dismiss dialogs, auto-click prompts, auto-fill fields
4. **CI/CD Desktop Test Runner** — CLI execution (v1.1) for pipeline integration
5. **QA Acceleration** — Reduce manual click-through testing for Windows desktop apps

### Secondary

- Accessibility compliance verification (enumerate UIA tree elements)
- Multi-app workflow orchestration (flows can target different processes per step)
- Screen-change monitoring with historical timeline

---

## Roadmap

### v1.0.0 (Current)
- Desktop UIA backend with actionability checks
- Structured TestFlow DSL v1 + FlowValidator
- 9 agent tools with function-calling LLM
- Vision fallback (non-primary)
- Safety hardening (kill switch, target lock, allowlist, audit log)
- Execution reports with full trace metadata
- Plugin system (PowerShell + .NET)

### v1.1.0 (Next)
- Playwright backend (web automation via Chromium/Firefox/WebKit)
- CLI runner (`idolclick run flow.json`)
- `--version`, `--help`, `--validate` arguments
- ServiceHost extraction (no WPF dependencies)

### Future
- DPAPI encryption for API keys
- FlowValidator NuGet package extraction
- Live execution streaming
- SQLite timeline persistence
- Flow recording (watch user actions → generate flow JSON)

---

## Technical Stack

| Component | Technology |
|-----------|------------|
| Framework | .NET 8, WPF (Windows Presentation Foundation) |
| UI Automation | UIAutomationClient, UIAutomationTypes |
| LLM Integration | Microsoft.Extensions.AI + OpenAI-compatible API |
| Tool Calling | FunctionInvokingChatClient (auto-marshaling) |
| Vision | GDI+ screen capture → LLM vision API |
| Scripting | PowerShell, Roslyn (C#) |
| Packaging | Self-contained exe, Inno Setup installer |

---

## Philosophy

1. **Deterministic over autonomous** — Precise selectors, not "click the blue button"
2. **Structured over conversational** — JSON specs, not natural language instructions  
3. **Transparent over magical** — Every action is logged, timed, and traceable
4. **Safe over fast** — Actionability checks, kill switch, target lock before "just click it"
5. **AI-native over AI-bolted** — The execution report schema is designed for machine consumption

---

*IdolClick v1.0.0 — Built for developers who need reliable, repeatable Windows UI automation that AI agents can orchestrate.*

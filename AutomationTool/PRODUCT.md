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
| Playwright | Browser-first testing | IdolClick is desktop-only (UIA); web testing is out of scope |
| AutoHotkey | Scripting + hotkeys | IdolClick is structured/typed; flows are machine-generated, not hand-written |

IdolClick owns one thing:

> **Closed-loop regression testing for Windows desktop applications, orchestrated by AI agents.**

---

## Architecture

### Module Inventory

| Module | Maturity | Description |
|--------|----------|-------------|
| **Classic** | Stable | Rule-based polling engine — monitors windows and auto-clicks matching UI elements |
| **Agent** | Stable | LLM chat with 14 tool-calling functions: 9 classic + 5 Pack orchestration |
| **Pack** | Stable | Orchestrated testing: Plan → Compile → Validate → Execute → Report |
| **Commands** | Alpha | Natural language → structured flow compiler via intent classification + 15 templates |
| **API** | Alpha | Embedded Kestrel REST + SignalR server for external tool integration |
| **MCP** | Alpha | Model Context Protocol server (stdio) for AI agent interop |

### Hand-Eye-Brain (Pack System — v1.1)

The Pack system extends Agent mode with orchestrated multi-journey testing:

| Component | Concept | Implementation |
|-----------|---------|----------------|
| **Brain** | Plans and reasons | LLM via IChatClient — plans journeys (PM role), compiles flows (SDET role) |
| **Eye** | Perceives app state | Dual channel: structural (UIA tree) + visual (screenshot/LLM vision) |
| **Hand** | Executes actions | IAutomationBackend — DesktopBackend (UIA) |

Pipeline: **Plan → Compile → Validate → Execute → Report** (with confidence scoring)

> For the full architecture deep-dive, see [ARCHITECTURE.md](ARCHITECTURE.md).  
> For practical coding guidance, see [AGENTS-GUIDE.md](AGENTS-GUIDE.md).

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
  └── DesktopBackend (v1.0 — Windows UIA + Win32)
```

Each backend owns: selector resolution, actionability checks, action execution, assertion evaluation, and backend call logging.

---

## TestPack DSL v1

The TestPack system extends single-flow execution into orchestrated, multi-journey test campaigns.

### Pack Pipeline

```
TestPack JSON (targets, instructions, guardrails)
  → Phase A: PackPlannerService (LLM as PM/QA)
    → Proposed journeys, coverage map, risks
  → Phase B+C: PackCompilerService (LLM as SDET)
    → Executable TestFlows with selectors + assertions
    → FlowValidatorService (retry loop, max 3 attempts)
  → Phase D: PackRunnerService (Hand)
    → Priority scheduling (p0→p3), backend switching, guardrails
    → IAutomationBackend → StepResults
  → Phase E: PackReportBuilder
    → PackReport with failures, fix queue, coverage map, perception stats
    → Confidence score (0.0–1.0)
```

### Dual-Perception Eye

The Eye gathers evidence about application state through two channels:

| Channel | Mechanism | Best For | Cost |
|---------|-----------|----------|------|
| **Structural** | UIA tree (desktop) | Text, element state, form values | Low — fast, deterministic |

Default mode is `auto` — the engine selects the cheapest sufficient channel per step.

### Confidence Scoring

After execution, the orchestrator produces a confidence score (0.0–1.0):

| Component | Weight | Measures |
|-----------|--------|----------|
| Journey pass rate | 60% | `passed / total` journeys |
| Coverage completion | 20% | `ok / total` coverage areas |
| Perception reliability | 10% | `1 - vision_fallback_rate` |
| Warning impact | 10% | `1 - (warning_rate × 2)` |

Interpretation: 0.9+ = High confidence | 0.7–0.9 = Moderate | 0.5–0.7 = Low | <0.5 = Critical

### Example TestPack

```json
{
  "schemaVersion": "1.0",
  "packName": "Notepad Smoke Test",
  "targets": [{ "targetId": "notepad", "kind": "desktop", "processName": "notepad" }],
  "inputs": {
    "instructions": "Test basic Notepad operations: open, type, save, close",
    "projectContext": {
      "featureList": ["text editing", "file save", "file open"],
      "knownRisks": ["save dialog may prompt for encoding"]
    }
  },
  "guardrails": {
    "safetyMode": "strict",
    "maxJourneys": 5,
    "maxTotalSteps": 50,
    "requireTargetLockForDesktop": true,
    "allowlistedProcesses": ["notepad"]
  }
}
```

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
| `navigate` | Open URL via shell execute (default browser) |
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

Supported kinds: `desktop_uia`

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

### Actionability Checks (Pre-Action Validation)

Before every action, the backend verifies:
- **Exists** — element is in the UI tree
- **Visible** — non-zero bounding box
- **Enabled** — not grayed out
- **Stable** — bounds unchanged across two reads (not animating)
- **ReceivesEvents** — not off-screen
- **Editable** — for text input, ValuePattern is writable

---

## Agent Tools

When in Agent mode, the LLM has access to 14 structured tools:

### Classic Tools (9)

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

### Pack Orchestration Tools (5)

| Tool | Purpose |
|------|---------|
| `RunFullPipeline` | One-shot: Plan → Compile → Execute → Report with confidence score |
| `PlanTestPack` | Generate a test plan (journeys, coverage, risks) without executing |
| `GetFixQueue` | Get ranked fix items from the latest pack execution |
| `GetConfidenceBreakdown` | Get confidence score breakdown (journey rate, coverage, perception, warnings) |
| `AnalyzeReport` | Get comprehensive analysis of latest pack execution |

The agent interaction is **spec-driven, not chat-driven**. The LLM uses `ValidateFlow` → `RunFlow` → reads report → patches flow → retries. For orchestrated campaigns, it uses `RunFullPipeline` → `GetFixQueue` → addresses issues.

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

### v1.0.0 (Shipped)
- Desktop UIA backend with actionability checks
- Structured TestFlow DSL v1 + FlowValidator
- 9 agent tools with function-calling LLM
- Vision fallback (non-primary)
- Safety hardening (kill switch, target lock, allowlist, audit log)
- Execution reports with full trace metadata
- Plugin system (PowerShell + .NET)

### v1.1.0-alpha (Sprint 1 — Complete)
- **TestPack model** — multi-journey test programs with targets, guardrails, data profiles
- **Pack Pipeline** — Plan → Compile → Validate → Execute → Report
- **Dual-perception Eye** — structural (UIA tree) + visual (screenshot) perception
- **Confidence scoring** — 0.0–1.0 weighted score for quality signal
- **5 Pack agent tools** — RunFullPipeline, PlanTestPack, GetFixQueue, GetConfidenceBreakdown, AnalyzeReport
- **Fix queue** — ranked, actionable fix items with FixPackets for coding agents
- **3 new JSON schemas** — test-pack, pack-plan, pack-report (JSON Schema 2020-12)
- **FlowValidator extensions** — backend-specific rules, TypedSelector validation
- **Commands module (alpha)** — NL intent → flow templates, 15 built-in templates, 3-tier escalation
- **API server (alpha)** — Kestrel REST + SignalR for external integration
- **MCP server (alpha)** — Model Context Protocol tool server for AI agent ecosystems

### v1.2.0 (Sprint 2 — Planned)
- MCP client protocol (JSON-RPC 2.0)
- Figma MCP integration for visual regression detection
- Backend API MCP for data consistency validation

### v1.3.0 (Sprint 3 — Planned)
- Live execution dashboard (WPF)
- Confidence Engine v2 with historical trends
- AI-powered suggestion engine

### v1.4.0 (Sprint 4 — Planned)
- Advanced UIA selectors and multi-monitor support
- CLI runner (`idolclick run flow.json`)

### Future
- Multi-agent orchestration
- Accessibility/WCAG compliance testing
- Commands module stabilization and fuzzy intent matching
- Flow recording (watch user → generate flow JSON)

---

## Technical Stack

| Component | Technology |
|-----------|------------|
| Framework | .NET 8, WPF (Windows Presentation Foundation) |
| UI Automation | UIAutomationClient, UIAutomationTypes (GAC) |
| LLM Integration | Microsoft.Extensions.AI 10.3.0 + Azure.AI.OpenAI 2.8.0-beta.1 |
| Tool Calling | Manual tool-calling loop with real-time progress callbacks |
| Vision | GDI+ screen capture → LLM vision API |
| Scripting | PowerShell, Roslyn (C#) |
| Serialization | System.Text.Json (camelCase props, snake_case enums) |
| Schemas | JSON Schema 2020-12 |
| Packaging | Self-contained exe (`PublishSingleFile` + `PublishTrimmed`), Inno Setup installer |

---

## Philosophy

1. **Deterministic over autonomous** — Precise selectors, not "click the blue button"
2. **Structured over conversational** — JSON specs, not natural language instructions  
3. **Transparent over magical** — Every action is logged, timed, and traceable
4. **Safe over fast** — Actionability checks, kill switch, target lock before "just click it"
5. **AI-native over AI-bolted** — The execution report schema is designed for machine consumption

---

*IdolClick v1.0.0 — Built for developers who need reliable, repeatable Windows UI automation that AI agents can orchestrate.*

---

## Related Documentation

| Document | Audience | Content |
|----------|----------|---------|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Agents & architects | Full architecture reference: layers, data flow, component inventory, dependency graph |
| [AGENTS-GUIDE.md](AGENTS-GUIDE.md) | Coding agents | Practical how-to: conventions, common tasks, gotchas, build instructions |
| [CHANGELOG.md](CHANGELOG.md) | Everyone | Version history and release notes |
| [MANUAL-TEST-CASES.md](MANUAL-TEST-CASES.md) | QA | Manual test scenarios for both modes |

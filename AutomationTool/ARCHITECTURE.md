# IdolClick — Architecture Guide

> **The definitive reference for understanding IdolClick's architecture, from philosophy to file-level details.**  
> Written for coding agents, contributors, and future maintainers — pick up this project cold.

---

## Table of Contents

1. [Philosophy & Metaphor](#1-philosophy--metaphor)
2. [System Overview](#2-system-overview)
3. [Layer Architecture](#3-layer-architecture)
4. [Data Flow](#4-data-flow)
5. [Component Inventory](#5-component-inventory)
6. [Pack Pipeline (Hand-Eye-Brain)](#6-pack-pipeline-hand-eye-brain)
7. [Backend Polymorphism](#7-backend-polymorphism)
8. [Agent Tool System](#8-agent-tool-system)
9. [Dual-Perception Eye](#9-dual-perception-eye)
10. [Safety & Guardrails](#10-safety--guardrails)
11. [Serialization & Schemas](#11-serialization--schemas)
12. [Dependency Graph](#12-dependency-graph)
13. [File Index](#13-file-index)

---

## 1. Philosophy & Metaphor

### Core Identity

IdolClick is a **spec-driven UI execution runtime** — it sits between AI coding agents and Windows desktop applications. It receives structured JSON flows, executes them deterministically, and returns machine-readable reports.

### The Hand-Eye-Brain Metaphor

The system is organized around three conceptual components:

| Component | Concept | Implementation |
|-----------|---------|----------------|
| **Brain** | Plans, reasons, decides | LLM via `IChatClient` — PackPlannerService, PackCompilerService, AgentService |
| **Eye** | Perceives application state | Dual-perception: structural (UIA tree) + visual (screenshot/LLM vision) |
| **Hand** | Executes actions | `IAutomationBackend` → DesktopBackend (UIA + Win32) |

The Brain proposes **what** to test. The Eye observes **what is**. The Hand does **what was decided**.

### Design Principles

1. **Deterministic over autonomous** — UIA selectors first; vision is a flagged fallback, never primary
2. **Structured over conversational** — JSON specs, not natural language
3. **Transparent over magical** — every action is logged, timed, traceable
4. **Safe over fast** — actionability checks, kill switch, target lock before execution
5. **Modular over monolithic** — each service is independently testable and replaceable
6. **Stateless pipeline** — no hidden state between Pack phases; all data flows through serializable models
7. **Classic engine untouched** — rule-based automation remains independent of the Pack/Agent systems

---

## 2. System Overview

### Dual-Mode Architecture

IdolClick operates in two independent modes simultaneously:

```
┌───────────────────────────────────────────────────────────────────────┐
│                         IdolClick Runtime                             │
│                                                                       │
│  ┌─────────────────────┐         ┌──────────────────────────────────┐│
│  │   CLASSIC MODE       │         │         AGENT MODE               ││
│  │                      │         │                                  ││
│  │  Rules (config.json) │         │  Brain (LLM via IChatClient)     ││
│  │       ↓              │         │       ↓                          ││
│  │  AutomationEngine    │         │  AgentService (tool-calling)     ││
│  │   (poll + match)     │         │       ↓                          ││
│  │       ↓              │         │  ┌─────────┐  ┌───────────────┐ ││
│  │  ActionExecutor      │         │  │ Classic  │  │ Pack Pipeline │ ││
│  │   (click, keys,      │         │  │ Tools(9) │  │ Tools (5)     │ ││
│  │    scripts, plugins)  │        │  └────┬─────┘  └──────┬────────┘ ││
│  │                      │         │       ↓                ↓         ││
│  │                      │         │  StepExecutor   PackOrchestrator ││
│  │                      │         │       ↓            ↓             ││
│  │                      │         │  IAutomationBackend (shared)     ││
│  └─────────────────────┘         └──────────────────────────────────┘│
│                                                                       │
│  ┌─────────────────────────────────────────────────────────────────┐  │
│  │  SHARED INFRASTRUCTURE                                          │  │
│  │  ConfigService · LogService · HotkeyService · PluginService     │  │
│  │  VisionService · NotificationService · EventTimelineService     │  │
│  └─────────────────────────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────────────┘
```

### Technology Stack

| Concern | Technology |
|---------|-----------|
| **Runtime** | .NET 8 (`net8.0-windows`), WPF, single-project WinExe |
| **UI Automation** | UIAutomationClient + UIAutomationTypes (GAC references) |
| **LLM** | Microsoft.Extensions.AI 10.3.0, Azure.AI.OpenAI 2.8.0-beta.1 |
| **Serialization** | System.Text.Json (camelCase props, snake_case enums) |
| **MVVM** | CommunityToolkit.Mvvm 8.2.2 |
| **Packaging** | Self-contained `PublishSingleFile` + `PublishTrimmed` |
| **Installer** | Inno Setup (via `installer/IdolClick.iss`) |
| **Schemas** | JSON Schema 2020-12 for TestFlow, TestPack, PackPlan, PackReport, ExecutionReport |

---

## 3. Layer Architecture

The codebase has six layers, from lowest to highest abstraction:

```
┌──────────────────────────────────────────────────────┐
│  Layer 6: UI                                         │  WPF windows, tray, overlays
├──────────────────────────────────────────────────────┤
│  Layer 5: Pack Services (Orchestration)              │  PackOrchestrator, Planner, Compiler, Runner
├──────────────────────────────────────────────────────┤
│  Layer 4: Core Services                              │  AgentService, StepExecutor, AutomationEngine
├──────────────────────────────────────────────────────┤
│  Layer 3: Infrastructure Services                    │  IAutomationBackend, DesktopBackend, Vision
├──────────────────────────────────────────────────────┤
│  Layer 2: Models                                     │  TestFlow, TestPack, PackPlan, PackReport, AppConfig
├──────────────────────────────────────────────────────┤
│  Layer 1: Configuration & Startup                    │  App.xaml.cs, ConfigService, GlobalUsings
└──────────────────────────────────────────────────────┘
```

### Layer Rules

- Lower layers **never** reference higher layers
- Services at the same layer may reference each other
- All inter-layer communication uses **models** (Layer 2) as data contracts
- The Pack layer (5) composes Core layer (4) services — it never bypasses them

---

## 4. Data Flow

### Classic Mode

```
config.json → ConfigService → AutomationEngine (polling loop)
  → Finds matching window/element per Rule
    → ActionExecutor → Click / SendKeys / RunScript / Plugin
      → EventTimelineService (audit trail)
```

### Agent Mode — Single Flow

```
User chat message → AgentService
  → IChatClient (LLM)
    → Tool call: InspectWindow → AgentTools → DesktopBackend → UI tree JSON
    → Tool call: RunFlow → AgentTools → StepExecutor
      → FlowValidatorService (pre-flight validation)
        → IAutomationBackend.ExecuteStepAsync (per step)
          → StepResult[]
        → ExecutionReport (JSON)
      → Back to LLM for analysis / retry
  → AgentResponse (text + optional parsed TestFlow)
```

### Agent Mode — Pack Pipeline (Plan → Compile → Execute → Report)

```
TestPack JSON → PackAgentTools.RunFullPipeline
  → PackOrchestrator.RunFullPipelineAsync
    ├── Phase A: PackPlannerService.PlanAsync
    │     → IChatClient (LLM as PM/QA) → PackPlan (journeys, coverage, risks)
    │
    ├── Phase B+C: PackCompilerService.CompileAsync
    │     → IChatClient (LLM as SDET) → TestPack with compiled Flows
    │     → FlowValidatorService (per flow) → retry loop (max 3 attempts)
    │
    ├── Phase D: PackRunnerService.ExecuteAsync
    │     → Sort journeys by priority (p0→p3)
    │     → Per journey: resolve flows → IAutomationBackend → StepResult[]
    │     → Guardrail enforcement (max failures, runtime, forbidden actions)
    │     → PackReport (raw results)
    │
    └── Phase E: PackReportBuilder.BuildFrom
          → Populate failures, warnings, coverage map, perception stats
          → Generate fix queue (ranked, actionable items with FixPackets)
          → Calculate confidence score (0.0–1.0)

  → PackOrchestratorResult (success, confidence, full report)
```

---

## 5. Component Inventory

### Layer 1 — Configuration & Startup

| File | Purpose |
|------|---------|
| `App.xaml.cs` | Application entry point. Creates all services, wires dependencies, manages single-instance mutex, splash screen, kill switch. All services exposed as `static` properties. |
| `GlobalUsings.cs` | Global `using` directives for the project. |

**App.xaml.cs static services** (creation order matters):

```
Config → Log → Profiles → RegionCapture → Notifications → Scripts
→ Plugins → Timeline → Agent → FlowValidator → Vision → DesktopBackend
→ FlowExecutor → Reports → PackRunner → PackOrchestrator → Engine → Hotkey
```

### Layer 2 — Models

| File | Key Types | Purpose |
|------|-----------|---------|
| `Models/TestFlow.cs` | `TestFlow`, `TestStep`, `StepAction` (13 actions), `StepSelector`, `SelectorKind` (6 kinds), `StepAssertion`, `StepResult`, `ExecutionReport`, `FlowJson` | The single-flow DSL — the exchange format between AI agents and IdolClick |
| `Models/TestPack.cs` | `TestPack`, `PackTarget`, `TargetKind`, `Journey`, `FlowRef`, `PackGuardrails`, `PerceptionMode` (5 modes), `PerceptionPolicy`, `DataProfile`, `CoveragePlan`, `PackExecutionConfig`, `ArtifactPolicy`, `CapturePolicy` | Multi-journey test program with dual-perception Eye |
| `Models/PackPlan.cs` | `PackPlan`, `PlannedJourney`, `CoverageMapEntry`, `RiskEntry`, `SuggestedDataProfile`, `PerceptionRecommendation` | Planner output — proposed journeys before compilation |
| `Models/PackReport.cs` | `PackReport`, `PackSummary`, `JourneyResult`, `PackFailure`, `FailureEvidence`, `PackWarning`, `FixQueueItem`, `FixPacket`, `PerceptionStats` | Rich execution report with fix queue and perception analytics |
| `Models/AppConfig.cs` | `AppConfig`, `AppSettings`, `AgentSettings` | Application configuration schema (agent endpoint, model, temperature, etc.) |
| `Models/Rule.cs` | `Rule` | Classic mode rule definition (match patterns, actions, cooldowns, time windows) |

**Critical serialization note**: All models use `FlowJson.Options` — camelCase property names, snake_case enum values. This is the contract with AI agents.

### Layer 3 — Backend

| File | Key Types | Purpose |
|------|-----------|---------|
| `Services/Backend/IAutomationBackend.cs` | `IAutomationBackend`, `BackendCapabilities`, `BackendInitOptions`, `BackendExecutionContext`, `InspectableTarget`, `InspectionResult`, `BackendArtifact` | Polymorphic backend interface — **the central abstraction** |
| `Services/Backend/DesktopBackend.cs` | `DesktopBackend` | Windows UIA + Win32 implementation (fully functional) |

**IAutomationBackend** is the keystone interface. It defines:
- `Name` / `Version` / `Capabilities` — identity and feature declaration
- `InitializeAsync` — backend lifecycle
- `ExecuteStepAsync` — the core step execution method (selector → actionability → action → snapshot)
- `ListTargetsAsync` / `InspectTargetAsync` — discovery for agent tools
- `StartArtifactCaptureAsync` / `StopArtifactCaptureAsync` — tracing/recording

### Layer 4 — Core Services

| File | Constructor Deps | Purpose |
|------|-----------------|---------|
| `Services/ConfigService.cs` | `(string configPath)` | Loads/saves `config.json`, exposes `AppConfig` |
| `Services/LogService.cs` | `()` | File + in-memory logging with level filtering |
| `Services/AutomationEngine.cs` | `(ConfigService, LogService)` | Classic mode: polling loop, rule matching, action dispatch |
| `Services/ActionExecutor.cs` | `(LogService)` | Executes classic-mode actions (click, sendkeys, script, plugin) |
| `Services/AgentService.cs` | `(ConfigService, LogService)` | LLM integration: manages IChatClient, conversation history, manual tool-calling loop (max 15 iterations), test flow detection in responses |
| `Services/AgentTools.cs` | `(ConfigService, LogService, FlowValidatorService, StepExecutor?, ReportService?, VisionService?)` | 9 classic agent tools: ListWindows, InspectWindow, ListProcesses, ValidateFlow, RunFlow, ListReports, CaptureScreenshot, GetCapabilities, LocateByVision |
| `Services/StepExecutor.cs` | `(LogService, FlowValidatorService, IAutomationBackend)` | Orchestrates flow execution: validation gate → step iteration → stop-on-failure → report assembly |
| `Services/FlowActionExecutor.cs` | `(LogService, ActionExecutor)` | Adapts classic ActionExecutor for flow step actions |
| `Services/FlowValidatorService.cs` | `(LogService)` | Pre-flight flow validation: schema checks, selector format, action-field requirements, backend-specific rules, TypedSelector validation |
| `Services/AssertionEvaluator.cs` | `(LogService)` | Post-step assertion evaluation (exists, text_contains, window_title, etc.) |
| `Services/SelectorParser.cs` | `(LogService)` | Parses `ElementType#TextOrAutomationId` format into UIA search criteria |
| `Services/VisionService.cs` | `(ConfigService, LogService)` | Screenshot capture + LLM vision API for fallback element location |
| `Services/HotkeyService.cs` | `()` | Global hotkey registration (Ctrl+Alt+T toggle, Ctrl+Alt+Escape kill switch) |
| `Services/EventTimelineService.cs` | `(LogService)` | Temporal event log for audit and history |
| `Services/NotificationService.cs` | `(LogService, ConfigService)` | System tray notifications |
| `Services/PluginService.cs` | `(LogService)` | Discovers and loads plugins from `Plugins/` folder (.ps1 and .dll) |
| `Services/ProfileService.cs` | `(ConfigService, LogService)` | Named configuration profiles |
| `Services/RegionCaptureService.cs` | `(LogService)` | Screen region selection overlay for visual targeting |
| `Services/ReportService.cs` | `(LogService)` | Execution report persistence (JSON files in `logs/`) |
| `Services/ScriptExecutionService.cs` | `(LogService, ConfigService)` | PowerShell and Roslyn C# script execution |
| `Services/ServiceHost.cs` | (static) | CLI entry points: `--version`, `--help` |

**AgentService internals worth noting**:
- Uses a **manual tool-calling loop** (NOT `FunctionInvokingChatClient`) — this enables real-time progress callbacks to the UI
- Max 15 tool-calling iterations before forcing a final response
- Conversation history is held in `List<ChatMessage>` — cleared via `ClearHistory()`
- Detects structured TestFlow JSON in LLM responses (looks for ` ```json ` blocks with `"steps"`)

### Layer 5 — Pack Services

| File | Constructor Deps | Public API | Purpose |
|------|-----------------|------------|---------|
| `Services/Packs/PackOrchestrator.cs` | `(ConfigService, LogService, FlowValidatorService, PackRunnerService)` | `RunFullPipelineAsync`, `PlanAsync`, `CompileAsync`, `ExecuteAsync`, `BuildReport` | Central hub — coordinates all phases, calculates confidence score |
| `Services/Packs/PackPlannerService.cs` | `(ConfigService, LogService)` | `PlanAsync(TestPack, IChatClient, ct)` → `PackPlanResult` | Phase A — LLM generates journey plan from natural language inputs |
| `Services/Packs/PackCompilerService.cs` | `(ConfigService, LogService, FlowValidatorService)` | `CompileAsync(TestPack, PackPlan, IChatClient, ct)` → `PackCompileResult` | Phase B+C — LLM compiles executable flows + validation retry loop (max 3) |
| `Services/Packs/PackRunnerService.cs` | `(LogService, FlowValidatorService, Func<string, IAutomationBackend?>)` | `ExecuteAsync(TestPack, callback?, ct)` → `PackReport` | Phase D — priority scheduling, backend switching, guardrail enforcement |
| `Services/Packs/PackReportBuilder.cs` | (static) | `BuildFrom(PackReport, TestPack, PackPlan?)` → `PackReport` | Phase E — populates failures, warnings, coverage map, fix queue, perception stats |
| `Services/Packs/PackAgentTools.cs` | `(LogService, PackOrchestrator, Func<IChatClient?>)` | 5 LLM-callable tools | Brain-callable entry points for Pack operations |

**PackOrchestrator's confidence scoring** (0.0–1.0):
- 60% weight: Journey pass rate (`passed / total`)
- 20% weight: Coverage completion (`ok_areas / total_areas`)
- 10% weight: Perception reliability (`1 - fallback_rate`)
- 10% weight: Warning impact (`1 - warning_rate * 2`)

**PackRunnerService's backend factory**: Receives a `Func<string, IAutomationBackend?>` at construction. Maps `"desktop-uia"` → `DesktopBackend`.

### Layer 6 — UI

| File | Purpose |
|------|---------|
| `UI/MainWindow.xaml(.cs)` | Primary control panel: Classic/Agent mode tabs, rule list, chat panel, status bar |
| `UI/SettingsWindow.xaml(.cs)` | Configuration: agent endpoint/model/key, automation settings, hotkey config |
| `UI/RuleEditorWindow.xaml(.cs)` | Visual rule editor for Classic mode |
| `UI/SplashWindow.xaml(.cs)` | Startup splash with progress bar |
| `UI/ClickRadarOverlay.xaml(.cs)` | Visual overlay showing where clicks land (debugging aid) |
| `UI/RegionSelectorOverlay.xaml(.cs)` | Screen region selection for screenshots/region capture |

---

## 6. Pack Pipeline (Hand-Eye-Brain)

The Pack Pipeline is IdolClick's orchestrated testing system. It transforms high-level natural language instructions into deterministic test executions with rich reporting.

### Phase Overview

```
Input: TestPack JSON
  │
  ▼
Phase A: PLAN (Brain = PM/QA role)
  │  PackPlannerService sends pack inputs to LLM
  │  LLM produces: journeys, coverage map, risks, perception recommendations
  │  Output: PackPlan
  │
  ▼
Phase B+C: COMPILE + VALIDATE (Brain = SDET role)
  │  PackCompilerService sends plan + pack to LLM
  │  LLM produces: executable TestFlow objects with steps and selectors
  │  FlowValidatorService validates each flow
  │  If validation fails → send errors back to LLM → retry (max 3 attempts)
  │  Output: TestPack with populated Flows[]
  │
  ▼
Phase D: EXECUTE (Hand)
  │  PackRunnerService sorts journeys by priority (p0 → p3)
  │  Per journey: resolve FlowRefs → execute flows via StepExecutor
  │  Backend: DesktopBackend (UIA + Win32)
  │  Guardrails enforced: max failures, max runtime, forbidden actions
  │  Output: PackReport (raw results)
  │
  ▼
Phase E: REPORT (Analysis)
  │  PackReportBuilder enriches the raw report:
  │  • Failure analysis with evidence
  │  • Warning aggregation
  │  • Coverage map status
  │  • Perception stats (structural vs visual captures)
  │  • Ranked fix queue with FixPackets for coding agents
  │  Output: enriched PackReport + confidence score
```

### Modular Phase Access

The PackOrchestrator supports both **full pipeline** and **individual phase** invocations:

```csharp
// Full pipeline — one-shot
var result = await orchestrator.RunFullPipelineAsync(pack, chatClient, onProgress, ct);

// Individual phases — step by step
var plan = await orchestrator.PlanAsync(pack, chatClient, ct);
var compiled = await orchestrator.CompileAsync(pack, plan.Plan, chatClient, ct);
var report = await orchestrator.ExecuteAsync(compiledPack, ct);
var enriched = orchestrator.BuildReport(report, compiledPack, plan.Plan);
```

---

## 7. Backend Polymorphism

### Interface Contract

`IAutomationBackend` unifies desktop automation behind a single interface:

```
IAutomationBackend
  └── DesktopBackend ("desktop-uia")
        Technology: UIAutomationClient + UIAutomationTypes (GAC)
        Selectors: ElementType#TextOrAutomationId
        Status: FULLY IMPLEMENTED
```

### Selector System

Desktop UIA selectors use the `ElementType#TextOrAutomationId` grammar:

| SelectorKind | Backend | Format Example |
|-------------|---------|---------------|
| `desktop_uia` | Desktop | `Button#Save`, `Edit#File name:`, `#myAutomationId` |

Flows can use either the legacy `selector` string field (backward compat) or the `typedSelector` object:

```json
{
  "selector": "Button#Save",
  "typedSelector": { "kind": "desktop_uia", "value": "Button#Save" }
}
```

### Backend Switching in Pack Runner

The `PackRunnerService` manages backend switching per flow:

```csharp
// Backend factory injected at construction
new PackRunnerService(log, validator, backendName => backendName switch
{
    "desktop-uia" => DesktopBackend,
    _ => null
});
```

---

## 8. Agent Tool System

### Architecture

AgentService uses a **manual tool-calling loop** instead of `FunctionInvokingChatClient`:

```
User message → AgentService → IChatClient.GetResponseAsync
  → Response contains FunctionCallContent?
    YES → Execute tool → Add FunctionResultContent to history → Loop (max 15 iter)
    NO  → Final text response
```

This design enables real-time `OnProgress` events to the UI (which tool is running, intermediate text, iteration count).

### Tool Registry

Tools are registered in `AgentService.CreateToolsAndMap()` via `AIFunctionFactory.Create()`:

| Tool # | Name | Source | Category |
|--------|------|--------|----------|
| 1 | `ListWindows` | AgentTools | Discovery |
| 2 | `InspectWindow` | AgentTools | Discovery |
| 3 | `ListProcesses` | AgentTools | Discovery |
| 4 | `ValidateFlow` | AgentTools | Flow lifecycle |
| 5 | `RunFlow` | AgentTools | Flow lifecycle |
| 6 | `ListReports` | AgentTools | Reporting |
| 7 | `CaptureScreenshot` | AgentTools | Perception |
| 8 | `GetCapabilities` | AgentTools | Metadata |
| 9 | `LocateByVision` | AgentTools | Perception (fallback) |
| 10 | `RunFullPipeline` | PackAgentTools | Pack orchestration |
| 11 | `PlanTestPack` | PackAgentTools | Pack orchestration |
| 12 | `GetFixQueue` | PackAgentTools | Pack reporting |
| 13 | `GetConfidenceBreakdown` | PackAgentTools | Pack reporting |
| 14 | `AnalyzeReport` | PackAgentTools | Pack reporting |

### Adding New Tools

1. Add a public method with `[Description("...")]` attribute to `AgentTools` or `PackAgentTools`
2. Parameters get `[Description("...")]` too — the LLM reads these to know how to call the tool
3. Register in `CreateToolsAndMap()` with `AIFunctionFactory.Create(type.GetMethod(nameof(...))!, instance)`
4. Update system prompt in `AgentService.AddSystemPrompt()` to describe the tool

---

## 9. Dual-Perception Eye

The Eye is the sensory architecture governing **how IdolClick observes application state**.

### Two Perception Channels

| Channel | Mechanism | Best For | Cost |
|---------|-----------|----------|------|
| **Structural** | UIA tree snippet (desktop) | Text content, element state, form values, tree structure | Low — fast, deterministic |
| **Visual** | Screenshot capture (+ optional LLM vision analysis) | Layout, rendering, visual regressions, transient UI | High — image processing, API calls |

### Perception Modes

| Mode | Behavior |
|------|----------|
| `Structural` | Structural only — zero vision cost |
| `Visual` | Visual only — pixel-level fidelity |
| `StructuralFirst` | Try structural, fall back to visual if insufficient |
| `Dual` | Both captured together — maximum data, maximum cost |
| `Auto` | Engine auto-selects per step context (recommended default) |

### Auto Mode Selection Logic

When `PerceptionMode.Auto`, the engine picks the cheapest sufficient channel:

- `assert_text`, `assert_exists`, `wait` → **Structural** (forced via `PerceptionPolicy.ForceStructuralFor`)
- `screenshot` → **Visual** (forced via `PerceptionPolicy.ForceVisualFor`)
- Step failure → **Dual** (configured via `PerceptionPolicy.OnFailureMode`)
- Visual assertions → **Visual** (configured via `PerceptionPolicy.ForVisualAssertions`)

### Configuration

Perception is configured at three levels (most specific wins):

1. **Pack level**: `TestPack.Guardrails.Perception` — default policy
2. **Journey level**: `Journey.PerceptionOverride` — per-journey override
3. **Step level**: Action type auto-selection (via `ForceStructuralFor` / `ForceVisualFor`)

---

## 10. Safety & Guardrails

### System-Level Safety

| Feature | Implementation | Trigger |
|---------|---------------|---------|
| **Kill Switch** | `HotkeyService.OnKillSwitchActivated` → `App.KillSwitchActive = true` | Ctrl+Alt+Escape |
| **Target Lock** | `BackendExecutionContext` captures HWND + PID on first step; validated every step | `targetLock: true` in flow |
| **Process Allowlist** | `ConfigService` allowlist checked before action execution | Config setting |
| **Audit Log** | `LogService.Audit()` writes to `logs/audit_log.txt` | Safety events |

### Pack-Level Guardrails

`PackGuardrails` defines bounds enforced by `PackRunnerService`:

| Guardrail | Default | Enforcement |
|-----------|---------|-------------|
| `MaxRuntimeMinutes` | 45 | Runner checks elapsed time per journey |
| `MaxJourneys` | 20 | Validated before execution starts |
| `MaxTotalSteps` | 800 | Validated before execution starts |
| `MaxStepsPerFlow` | 80 | Validated per flow |
| `MaxFailuresBeforeStop` | 5 | Runner stops after N journey failures |
| `RequireTargetLockForDesktop` | true | Forces `targetLock: true` on desktop flows |
| `ForbiddenActions` | `[]` | Blocks specific action types (e.g., "delete") |
| `VisionFallbackPolicy` | `AllowedButWarning` | Controls if/how vision fallback is used |

### Vision Safety

Vision-resolved steps are **never silently promoted to "passed"**:
- Status: `StepStatus.Warning`
- Warning code: `VisionFallbackUsed`
- Confidence score in diagnostics
- Tracked in `PerceptionStats.StructuralToVisualFallbacks`

---

## 11. Serialization & Schemas

### JSON Conventions

All model serialization uses `FlowJson.Options`:

```csharp
new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
};
```

- Properties: `camelCase` in JSON (`packName`, `schemaVersion`)
- Enums: `snake_case_lower` in JSON (`desktop_uia`, `structural_first`, `allowed_but_warning`)
- Parsing: case-insensitive

### JSON Schema Files

| Schema | Path | Validates |
|--------|------|-----------|
| TestFlow | `schemas/test-flow.schema.json` | Single flow DSL with `backend`, `targetLock`, `typedSelector` |
| Execution Report | `schemas/execution-report.schema.json` | Per-flow execution results with `warning` status |
| TestPack | `schemas/test-pack.schema.json` | Multi-journey test program with targets, guardrails, perception |
| PackPlan | `schemas/pack-plan.schema.json` | Planner output: journeys, coverage, risks |
| PackReport | `schemas/pack-report.schema.json` | Rich execution report with fix queue, coverage map, stats |

---

## 12. Dependency Graph

### Service Dependencies (Construction Time)

```
ConfigService ──────────────────────────────────────────────┐
    │                                                        │
    ├──→ LogService                                          │
    │       │                                                │
    │       ├──→ FlowValidatorService(Log)                   │
    │       │       │                                        │
    │       │       ├──→ SelectorParser(Log)                 │
    │       │       │                                        │
    │       ├──→ ActionExecutor(Log)                         │
    │       │       │                                        │
    │       │       ├──→ FlowActionExecutor(Log, AE)         │
    │       │       │                                        │
    │       ├──→ AssertionEvaluator(Log)                     │
    │       │                                                │
    │       ├──→ VisionService(Config, Log)                  │
    │       │                                                │
    │       ├──→ DesktopBackend(Log, FAE, AE, SP, Vision)    │
    │       │       │                                        │
    │       │       ├──→ StepExecutor(Log, FV, Backend)      │
    │       │       │                                        │
    │       ├──→ ReportService(Log)                          │
    │       │                                                │
    │       ├──→ PackRunnerService(Log, FV, backendFactory)  │
    │       │       │                                        │
    │       │       ├──→ PackOrchestrator(Config, Log, FV, PR)│
    │       │                                                │
    │       ├──→ AgentService(Config, Log)                   │
    │               │                                        │
    │               ├──→ AgentTools(Config, Log, FV, SE, RS, VS)
    │               │                                        │
    │               ├──→ PackAgentTools(Log, PO, clientFactory)
    │                                                        │
    ├──→ AutomationEngine(Config, Log)                       │
    │                                                        │
    └──→ HotkeyService()                                     │
```

### Key Patterns

- **PackOrchestrator creates internal services**: It receives a `PackRunnerService` but creates `PackPlannerService` and `PackCompilerService` internally (both only need `ConfigService` + `LogService` + `FlowValidatorService`)
- **Backend factory pattern**: `PackRunnerService` takes `Func<string, IAutomationBackend?>` — a factory that maps backend names to instances
- **Client factory pattern**: `PackAgentTools` takes `Func<IChatClient?>` — defers client resolution to call time
- **AgentService.SetExecutionServices**: Called after `App.FlowExecutor` is initialized — late binding for circular dependency avoidance

---

## 13. File Index

Complete file listing organized by purpose.

### Project Root

| Path | Purpose |
|------|---------|
| `ARCHITECTURE.md` | This document |
| `PRODUCT.md` | Product specification, DSL reference, safety model |
| `CHANGELOG.md` | Version history and release notes |
| `README.md` | Quick start, features overview, usage |
| `AGENTS-GUIDE.md` | Practical guide for coding agents working on this codebase |
| `MANUAL-TEST-CASES.md` | Manual test case definitions |
| `LICENSE` | MIT license |
| `IdolClick.sln` | Visual Studio solution file |
| `Start-IdolClick.ps1` | Quick start script |

### Source Code (`src/IdolClick.App/`)

| Path | Layer | Purpose |
|------|-------|---------|
| `IdolClick.csproj` | Build | Project config: `net8.0-windows`, NuGet refs, GAC refs |
| `App.xaml` / `App.xaml.cs` | Startup | Entry point, DI wiring, static service properties |
| `GlobalUsings.cs` | Startup | Global using directives |

#### Models

| Path | Key Types |
|------|-----------|
| `Models/TestFlow.cs` | `TestFlow`, `TestStep`, `StepAction`, `StepSelector`, `SelectorKind`, `StepResult`, `ExecutionReport`, `FlowJson` |
| `Models/TestPack.cs` | `TestPack`, `PackTarget`, `Journey`, `FlowRef`, `PackGuardrails`, `PerceptionMode`, `PerceptionPolicy`, `DataProfile`, `CoveragePlan` |
| `Models/PackPlan.cs` | `PackPlan`, `PlannedJourney`, `CoverageMapEntry`, `RiskEntry`, `PerceptionRecommendation` |
| `Models/PackReport.cs` | `PackReport`, `PackSummary`, `JourneyResult`, `PackFailure`, `FailureEvidence`, `FixQueueItem`, `FixPacket`, `PerceptionStats` |
| `Models/AppConfig.cs` | `AppConfig`, `AppSettings`, `AgentSettings` |
| `Models/Rule.cs` | `Rule` |

#### Backend

| Path | Key Types |
|------|-----------|
| `Services/Backend/IAutomationBackend.cs` | `IAutomationBackend`, `BackendCapabilities`, `BackendInitOptions`, `BackendExecutionContext` |
| `Services/Backend/DesktopBackend.cs` | `DesktopBackend : IAutomationBackend` |

#### Core Services

| Path | Key Types |
|------|-----------|
| `Services/ConfigService.cs` | `ConfigService` |
| `Services/LogService.cs` | `LogService` |
| `Services/AutomationEngine.cs` | `AutomationEngine` |
| `Services/ActionExecutor.cs` | `ActionExecutor` |
| `Services/AgentService.cs` | `AgentService`, `IAgentService`, `AgentResponse`, `AgentProgress` |
| `Services/AgentTools.cs` | `AgentTools` (9 classic tools) |
| `Services/StepExecutor.cs` | `StepExecutor`, `StepProgressCallback` |
| `Services/FlowActionExecutor.cs` | `FlowActionExecutor` |
| `Services/FlowValidatorService.cs` | `FlowValidatorService` |
| `Services/AssertionEvaluator.cs` | `AssertionEvaluator` |
| `Services/SelectorParser.cs` | `SelectorParser` |
| `Services/VisionService.cs` | `VisionService` |
| `Services/HotkeyService.cs` | `HotkeyService` |
| `Services/EventTimelineService.cs` | `EventTimelineService` |
| `Services/NotificationService.cs` | `NotificationService` |
| `Services/PluginService.cs` | `PluginService` |
| `Services/ProfileService.cs` | `ProfileService` |
| `Services/RegionCaptureService.cs` | `RegionCaptureService` |
| `Services/ReportService.cs` | `ReportService` |
| `Services/ScriptExecutionService.cs` | `ScriptExecutionService` |
| `Services/ServiceHost.cs` | `ServiceHost` (static) |

#### Pack Services

| Path | Key Types |
|------|-----------|
| `Services/Packs/PackOrchestrator.cs` | `PackOrchestrator`, `PackOrchestratorResult`, `PackPipelinePhase`, `PackPipelineProgress` |
| `Services/Packs/PackPlannerService.cs` | `PackPlannerService`, `PackPlanResult` |
| `Services/Packs/PackCompilerService.cs` | `PackCompilerService`, `PackCompileResult` |
| `Services/Packs/PackRunnerService.cs` | `PackRunnerService`, `JourneyProgressCallback` |
| `Services/Packs/PackReportBuilder.cs` | `PackReportBuilder` (static) |
| `Services/Packs/PackAgentTools.cs` | `PackAgentTools` (5 tools + 2 Sprint 2+ stubs) |

#### Commands (Alpha — Intent-to-Flow Compiler)

| Path | Key Types |
|------|-----------|
| `Services/IntentSplitterService.cs` | `IntentSplitterService`, `IntentKind`, `ClassifiedIntent`, `SplitResult` |
| `Services/Templates/IFlowTemplate.cs` | `IFlowTemplate` interface |
| `Services/Templates/TemplateRegistry.cs` | `TemplateRegistry` — discovers and scores templates |
| `Services/Templates/CoreTemplates.cs` | 8 core templates (click, type, launch, close, etc.) |
| `Services/Templates/ExperimentalTemplates.cs` | 7 experimental templates (scroll, drag, multi-step, etc.) |

> **Status**: Alpha. Keyword-based classifier with no fuzzy matching. Zero test coverage. See the Roadmap section in [PRODUCT.md](PRODUCT.md) for stabilization plans.

#### API Server (Alpha)

| Path | Key Types |
|------|-----------|
| `Services/Api/ApiHostService.cs` | Embedded Kestrel host — REST + SignalR |
| `Services/Api/AgentEndpoints.cs` | Agent chat endpoints |
| `Services/Api/FlowEndpoints.cs` | Flow CRUD + execution endpoints |
| `Services/Api/TemplateEndpoints.cs` | Template listing + NL-to-flow endpoints |
| `Services/Api/ToolEndpoints.cs` | Tool invocation endpoints |
| `Services/Api/ExecutionHub.cs` | SignalR hub for real-time execution progress |

#### MCP Server (Alpha)

| Path | Key Types |
|------|-----------|
| `Services/Mcp/McpServerService.cs` | MCP stdio server host |
| `Services/Mcp/McpToolDefinitions.cs` | Tool schema definitions for MCP protocol |
| `Services/Mcp/TestSpecRunner.cs` | Test spec execution via MCP |
| `Services/Mcp/ToolCatalog.cs` | MCP tool catalog and discovery |

#### UI

| Path | Purpose |
|------|---------|
| `UI/MainWindow.xaml(.cs)` | Primary control panel |
| `UI/SettingsWindow.xaml(.cs)` | Configuration UI |
| `UI/RuleEditorWindow.xaml(.cs)` | Classic rule editor |
| `UI/SplashWindow.xaml(.cs)` | Startup splash |
| `UI/ClickRadarOverlay.xaml(.cs)` | Click visualization overlay |
| `UI/RegionSelectorOverlay.xaml(.cs)` | Screen region selection |

### Schemas

| Path | Standard | Purpose |
|------|----------|---------|
| `schemas/test-flow.schema.json` | JSON Schema 2020-12 | TestFlow validation |
| `schemas/execution-report.schema.json` | JSON Schema 2020-12 | ExecutionReport validation |
| `schemas/test-pack.schema.json` | JSON Schema 2020-12 | TestPack validation |
| `schemas/pack-plan.schema.json` | JSON Schema 2020-12 | PackPlan validation |
| `schemas/pack-report.schema.json` | JSON Schema 2020-12 | PackReport validation |

### Plugins

| Path | Purpose |
|------|---------|
| `Plugins/README.md` | Plugin development guide |
| `Plugins/discord-webhook.ps1` | Sample: send results to Discord |
| `Plugins/sample-logger.ps1` | Sample: event logging plugin |

### Installer

| Path | Purpose |
|------|---------|
| `installer/IdolClick.iss` | Inno Setup script |
| `installer/Build-Installer.ps1` | Build installer |
| `installer/Build-Portable.ps1` | Build portable zip |
| `installer/build-installer.bat` | Batch wrapper |
| `installer/assets/sample-config.json` | Default config for installer |

---

## Version History

| Version | Sprint | Changes |
|---------|--------|---------|
| v1.0.0 | — | Classic engine, Agent mode (9 tools), DesktopBackend, FlowValidator, ExecutionReport |
| v1.1.0-alpha | Sprint 1 | TestPack model, PackPlan model, PackReport model, Pack Pipeline (5 services), PackOrchestrator, PackAgentTools (5 tools), dual-perception Eye, confidence scoring, 3 new schemas, Commands module (alpha), API server (alpha), MCP server (alpha) |

---

*This document is the source of truth for IdolClick's architecture. Keep it updated as the system evolves.*

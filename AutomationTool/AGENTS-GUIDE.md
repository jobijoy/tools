# IdolClick — Coding Agent Guide

> **Practical reference for AI coding agents and developers working on this codebase.**  
> Read ARCHITECTURE.md first for the "what" — this document covers the "how."

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [Project Conventions](#2-project-conventions)
3. [Build & Run](#3-build--run)
4. [Common Tasks (How-To)](#4-common-tasks-how-to)
5. [Model & Type Reference](#5-model--type-reference)
6. [Gotchas & Pitfalls](#6-gotchas--pitfalls)
7. [Testing & Validation](#7-testing--validation)
8. [Sprint Status & Roadmap](#8-sprint-status--roadmap)

---

## 1. Quick Start

### Orientation

```
AutomationTool/
  ├── src/IdolClick.App/          ← ALL source code (single-project)
  │     ├── Models/               ← Data contracts (TestFlow, TestPack, PackPlan, PackReport)
  │     ├── Services/             ← Core services
  │     │     ├── Backend/         ← IAutomationBackend, DesktopBackend
  │     │     └── Packs/          ← Pack pipeline services (Orchestrator, Planner, Compiler, Runner)
  │     ├── UI/                   ← WPF windows
  │     └── Assets/               ← Icons, images
  ├── schemas/                    ← JSON Schema 2020-12 files
  ├── installer/                  ← Inno Setup + build scripts
  ├── ARCHITECTURE.md             ← Full architecture reference
  ├── PRODUCT.md                  ← Product spec, DSL reference
  ├── CHANGELOG.md                ← Version history and release notes
  └── AGENTS-GUIDE.md             ← This file
```

### Key Principle

IdolClick is a **single .NET 8 WPF project** — no class libraries, no microservices. Everything lives under `src/IdolClick.App/`. The project file is `IdolClick.csproj`.

### IdolClick Is Always Running

The application is typically running while you develop. This means:
- **Build will show MSB3027** ("Could not copy IdolClick.exe because it's in use") — this is expected
- The build succeeds (0 CS errors) even with the file lock warning
- To verify compilation: look for `0 Error(s)` in build output, ignore the MSB3027 warning
- If you need a clean build, close IdolClick first

---

## 2. Project Conventions

### Naming

| Item | Convention | Example |
|------|-----------|---------|
| Classes | PascalCase | `PackOrchestrator`, `StepExecutor` |
| Methods | PascalCase | `RunFullPipelineAsync`, `ValidateFlow` |
| Properties | PascalCase | `SchemaVersion`, `PackName` |
| Private fields | `_camelCase` | `_log`, `_validator`, `_lastPipelineResult` |
| JSON properties | camelCase | `packName`, `schemaVersion` |
| JSON enums | snake_case_lower | `desktop_uia`, `structural_first` |
| Files | PascalCase | `PackOrchestrator.cs`, `TestPack.cs` |
| Folders | PascalCase | `Services/Packs/`, `Services/Backend/` |

### Serialization

**ALL** model serialization MUST use `FlowJson.Options`:

```csharp
JsonSerializer.Serialize(obj, FlowJson.Options);
JsonSerializer.Deserialize<T>(json, FlowJson.Options);
```

`FlowJson.Options` is defined in `Models/TestFlow.cs`:
- `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)`
- `PropertyNameCaseInsensitive = true`
- `WriteIndented = true`

**Never** create custom `JsonSerializerOptions` — always use `FlowJson.Options`.

### File Documentation

Every source file should have a documentation header block using box-drawing characters:

```csharp
// ═══════════════════════════════════════════════════════════════════════════════════
// COMPONENT NAME — One-line purpose.
//
// Detailed description:
//   • Key responsibility 1
//   • Key responsibility 2
//   • How it fits in the architecture
//
// Sprint roadmap:
//   Sprint N: What was added
//   Sprint N+1: What's planned
// ═══════════════════════════════════════════════════════════════════════════════════
```

### Async Patterns

- All I/O and LLM operations are async (`Task<T>`)
- Accept `CancellationToken ct = default` on all async public methods
- Use `ct.ThrowIfCancellationRequested()` in loops
- Name async methods with `Async` suffix: `PlanAsync`, `CompileAsync`, `ExecuteAsync`

### Service Registration

Services are **NOT** registered via DI container. They are created manually in `App.xaml.cs` and exposed as static properties:

```csharp
public static ConfigService Config { get; private set; } = null!;
public static LogService Log { get; private set; } = null!;
// ... etc
```

Access from anywhere: `App.Config`, `App.Log`, `App.PackOrchestrator`

---

## 3. Build & Run

### Prerequisites

- Windows 10/11
- .NET 8.0 SDK
- Visual Studio 2022 or VS Code with C# Dev Kit

### Build

```powershell
cd AutomationTool
dotnet build IdolClick.sln -c Release
```

Output: `src/IdolClick.App/bin/Release/net8.0-windows/IdolClick.exe`

### Publish

```powershell
# Self-contained (full runtime bundled)
dotnet publish src/IdolClick.App -c Release -r win-x64 --self-contained true

# Framework-dependent (requires .NET 8 runtime on target)
dotnet publish src/IdolClick.App -c Release -r win-x64 --self-contained false
```

### GAC References

The project references UIAutomationClient and UIAutomationTypes from the GAC (Global Assembly Cache). These are Windows system assemblies — no NuGet package needed:

```xml
<Reference Include="UIAutomationClient" />
<Reference Include="UIAutomationTypes" />
```

### launch.ps1

```powershell
.\Start-IdolClick.ps1
```

---

## 4. Common Tasks (How-To)

### Add a New Agent Tool

1. **Choose the tool class**:
   - General desktop/flow tool → `Services/AgentTools.cs`
   - Pack pipeline tool → `Services/Packs/PackAgentTools.cs`

2. **Add the method** with `[Description]` attribute:

```csharp
[Description("What this tool does — the LLM reads this to decide when to call it.")]
public async Task<string> MyNewTool(
    [Description("What this parameter is for")] string param1)
{
    // Implementation — return JSON string via FlowJson.Options
    return JsonSerializer.Serialize(new { result = "..." }, FlowJson.Options);
}
```

3. **Register in AgentService.CreateToolsAndMap()**:

```csharp
// In CreateToolsAndMap method:
functions.Add(AIFunctionFactory.Create(
    packType.GetMethod(nameof(PackAgentTools.MyNewTool))!, packTools));
```

4. **Update system prompt** in `AgentService.AddSystemPrompt()` to describe the new tool

5. **Update `MapToolDisplayName()`** in AgentService for UI display name

### Add a New Automation Backend

1. **Create the backend class** in `Services/Backend/`:

```csharp
public class MyBackend : IAutomationBackend
{
    public string Name => "my-backend";
    public string Version => "1.0.0";
    // Implement all interface members...
}
```

2. **Register in App.xaml.cs** backend factory:

```csharp
var packRunner = new PackRunnerService(Log, flowValidator, backendName =>
    backendName switch
    {
        "desktop-uia" => DesktopBackend,
        "my-backend" => myBackendInstance,
        _ => null
    });
```

3. **Add SelectorKind values** in `Models/TestFlow.cs` if the backend has new selector types

4. **Add validation rules** in `FlowValidatorService.ValidateBackendRules()` for the new backend

5. **Update schemas** in `schemas/test-flow.schema.json` to include new backend and selector kinds

### Add a New Pack Pipeline Phase

1. **Create the service** in `Services/Packs/`:

```csharp
public class PackMyPhaseService
{
    private readonly LogService _log;
    
    public PackMyPhaseService(LogService log)
    {
        _log = log;
    }
    
    public async Task<MyPhaseResult> RunAsync(/* inputs */, CancellationToken ct = default)
    {
        // Implementation
    }
}
```

2. **Wire into PackOrchestrator** — add a field, create in constructor, call in `RunFullPipelineAsync`

3. **Add result to PackOrchestratorResult** if the result should be accessible

4. **Expose as agent tool** in PackAgentTools if the Brain should be able to call it

### Add a New Model Type

1. **Create/extend in the appropriate model file**:
   - Flow-level types → `Models/TestFlow.cs`
   - Pack-level types → `Models/TestPack.cs`
   - Plan types → `Models/PackPlan.cs`
   - Report types → `Models/PackReport.cs`

2. **Use correct JSON attributes**:
   - `[JsonPropertyName("snake_case")]` only if the property name differs from camelCase convention
   - Enums: add to existing `[JsonStringEnumConverter]` — they auto-convert to snake_case

3. **Update JSON Schema** in `schemas/` directory

### Add a New UI Window

1. **Create XAML + code-behind** in `UI/` folder
2. **Access services** via static `App.` properties (e.g., `App.Config`, `App.Log`)
3. **Show from MainWindow** or other appropriate trigger

### Extend FlowValidator

1. **Add validation logic** in `Services/FlowValidatorService.cs`
2. Add method call in the main `Validate()` method
3. **Error format**: `result.Errors.Add("step[N]: error description")`
4. **Warning format**: `result.Warnings.Add("step[N]: warning description")`

---

## 5. Model & Type Reference

### StepResult (in TestFlow.cs) — Common Confusion Points

```csharp
public class StepResult
{
    public string? Error { get; set; }       // ← NOT "ErrorMessage"
    public string? Selector { get; set; }    // ← string, NOT StepSelector object
    // No "Confidence" property exists
}
```

### PlannedJourney (in PackPlan.cs) — Available Properties

```csharp
public class PlannedJourney
{
    public string JourneyId { get; set; }
    public string Title { get; set; }
    public string Priority { get; set; }           // "p0", "p1", "p2", "p3"
    public List<string> Tags { get; set; }
    public List<string> CoverageAreas { get; set; }
    public List<string> RequiredBackends { get; set; }
    public PerceptionRecommendation? RecommendedPerception { get; set; }
    // NO: Description, SuggestedFlowCount, Category — these do NOT exist
}
```

### StepSelector vs Selector

- `StepSelector` (class in TestFlow.cs): Typed selector with `Kind` + `Value` + `Extra`
- `step.Selector` (property on TestStep): `string?` — the raw selector string
- `step.TypedSelector` (property on TestStep): `StepSelector?` — the typed version
- In FlowValidator, the parameter type for typed selector validation is `StepSelector`

### Constructor Signatures (Easy to Get Wrong)

```csharp
// These ALL take ConfigService as first parameter:
new PackPlannerService(ConfigService, LogService)
new PackCompilerService(ConfigService, LogService, FlowValidatorService)
new PackOrchestrator(ConfigService, LogService, FlowValidatorService, PackRunnerService)

// These do NOT take ConfigService:
new PackRunnerService(LogService, FlowValidatorService, Func<string, IAutomationBackend?>)
new PackReportBuilder  // Static class, no constructor
new PackAgentTools(LogService, PackOrchestrator, Func<IChatClient?>)
```

---

## 6. Gotchas & Pitfalls

### Build Warnings

| Warning | Meaning | Action |
|---------|---------|--------|
| MSB3027 | IdolClick.exe is locked (running) | Expected — 0 CS errors means success |
| CS0108 | `JourneySuccessCriterion.Equals` hides `Object.Equals` | Fixed with `new` keyword — do not change |
| Nullable warnings | String ranges on nullable | Use ternary: `s?.Length > N ? s[..N] : s` |

### Common Coding Errors

1. **Using `ErrorMessage` instead of `Error`** on `StepResult` — the property is `Error`
2. **Using `Selector.Value`** — `StepResult.Selector` is `string?`, not `StepSelector`
3. **Adding `Confidence`** to `StepResult` — this property doesn't exist
4. **Wrong constructor params** — PackPlannerService and PackCompilerService need `ConfigService` first
5. **Custom JSON options** — always use `FlowJson.Options`, never create new options
6. **Forgetting `new` keyword** on `JourneySuccessCriterion.Equals` — suppresses CS0108

### AgentService.CreateToolsAndMap

The `functions` variable is a `List<AIFunction>`, not an array. Pack tools are added via `.Add()` after the initial classic tools list.

### Pack Pipeline State

- `PackOrchestrator` is **stateless** — it does not cache results between invocations
- `PackAgentTools` caches `_lastPipelineResult`, `_lastCompiledPack`, `_lastPlan` for follow-up tool calls (e.g., `GetFixQueue` after `RunFullPipeline`)
- These caches are **instance-scoped** — they reset when AgentService reconfigures

### UI Thread

- `AutomationEngine` and `HotkeyService` must be created on the UI thread (see `Dispatcher.Invoke` in `App.xaml.cs`)
- All other services can be created on background threads

---

## 7. Testing & Validation

### Build Verification

```powershell
dotnet build IdolClick.sln -c Release 2>&1 | Select-String "Error\(s\)|Warning\(s\)|error CS"
```

Expected output (with IdolClick running):
```
    0 Error(s)
```

The MSB3027 warning about file copy is normal and does not indicate a problem.

### Flow Validation

Use `FlowValidatorService` to validate any TestFlow:

```csharp
var validator = new FlowValidatorService(App.Log);
var result = validator.Validate(flow);
// result.IsValid, result.Errors, result.Warnings
```

### Schema Validation

JSON schemas in `schemas/` can be used with any JSON Schema 2020-12 validator to verify:
- TestFlow JSON before feeding to RunFlow
- TestPack JSON before feeding to PackOrchestrator
- ExecutionReport/PackReport JSON for consumers

### Manual Testing

See `MANUAL-TEST-CASES.md` for manual test scenarios covering both Classic and Agent modes.

---

## 8. Sprint Status & Roadmap

### Current State

| Sprint | Status | Key Deliverables |
|--------|--------|-----------------|
| 1 — Pack Pipeline | **COMPLETE** | TestPack/PackPlan/PackReport models, 3 schemas, PackOrchestrator + 5 services, 5 agent tools, confidence scoring |
| 2 — MCP Cross-Validation | Planned | MCP client, Figma MCP, Backend API MCP, Insight Generator |
| 3 — RPC IDE Dashboard | Planned | Live dashboard, Confidence Engine v2, Suggestion Engine |
| 4 — Desktop Hardening | Planned | Advanced UIA selectors, multi-monitor, per-app profiles, stability |
| 5 — Multi-Agent | Planned | Agent coordination, parallel journeys, remote execution |
| 6 — Accessibility | Planned | WCAG validator, screen reader simulation, compliance reports |

### Stubs for Future Sprints

Known stubs in the codebase (search for "Sprint 2", "Sprint 3", etc.):

| Location | Stub | Purpose |
|----------|------|---------|
| `PackAgentTools.CrossValidateWithMCP()` | Sprint 2 | MCP cross-validation integration |
| `PackAgentTools.SuggestSystemImprovements()` | Sprint 3 | AI-powered improvement suggestions |

### Architecture Decision Log

| Decision | Rationale |
|----------|-----------|
| Manual tool-calling loop (not FunctionInvokingChatClient) | Enables real-time progress callbacks to UI |
| Static service properties (not DI container) | Simpler for single-project WPF app; all services have clear lifecycle |
| Backend factory function (not DI) | Deferred backend creation; easy to add new backends without changing DI config |
| PackOrchestrator creates Planner/Compiler internally | These are thin wrappers around LLM calls; don't need external injection |
| Typed selectors per backend (not universal grammar) | Each backend has fundamentally different selector semantics |
| FlowJson.Options as single serialization config | Prevents subtle serialization bugs from mismatched options |

---

*This guide is the practical companion to ARCHITECTURE.md. Keep both updated together.*

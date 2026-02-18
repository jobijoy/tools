# Contributing to IdolClick

Thank you for your interest in contributing to IdolClick! This document provides guidelines and information for contributors.

## Getting Started

1. **Fork** the repository
2. **Clone** your fork locally
3. **Build** with `dotnet build` from `src/IdolClick.App/`
4. **Run** with `dotnet run` or launch the built executable

### Prerequisites

- .NET 8 SDK (Windows)
- Windows 10/11 (required for UIAutomation APIs)
- Visual Studio 2022 or VS Code with C# Dev Kit

## How to Contribute

### Reporting Issues

- Use GitHub Issues to report bugs or request features
- Include OS version, .NET version, and steps to reproduce
- Attach relevant log files from `logs/` (redact any API keys)

### Pull Requests

1. Create a feature branch from `main`
2. Make focused, atomic commits
3. Ensure `dotnet build` succeeds with zero errors and zero warnings
4. Update relevant documentation (ARCHITECTURE.md, PRODUCT.md) if you change behavior
5. Add or update JSON schemas in `schemas/` if you modify model contracts
6. Open a PR with a clear description of what and why

### Code Style

- Follow existing patterns in the codebase
- Use `PascalCase` for public members, `_camelCase` for private fields
- Keep services stateless where possible
- Document public APIs with XML doc comments
- No `TODO` or `HACK` comments in committed code

### Architecture Principles

IdolClick follows these core principles — PRs that violate them will be asked to revise:

1. **Deterministic over autonomous** — UIA selectors first; vision is a flagged fallback
2. **Structured over conversational** — JSON specs, not natural language
3. **Transparent over magical** — every action is logged, timed, traceable
4. **Safe over fast** — actionability checks, kill switch, target lock
5. **Desktop-only** — no browser automation; web testing belongs to Chrome DevTools MCP

### Architecture Boundaries

These boundaries define what IdolClick is NOT. PRs that cross them won't be merged:

| Boundary | What to do instead |
|----------|--------------------|
| **No browser automation** | Use Playwright, Puppeteer, or Selenium. IdolClick targets the Windows UIA layer. |
| **No cross-platform** | IdolClick depends on Windows UIAutomation and Win32 APIs. macOS/Linux is out of scope. |
| **No pixel-based matching** | All actions resolve through the UIA tree. Screenshot/vision is a fallback, always flagged. |
| **No embedded credentials** | API keys belong in `config.json` or the encrypted `.kv/` store, never hardcoded. |
| **No external network calls** | Only the configured LLM endpoint is contacted. No telemetry, no phone-home. |

**Where each concern lives:**

| Concern | Owner Service | File |
|---------|---------------|------|
| UI element resolution | `DesktopBackend` | `Services/Backend/DesktopBackend.cs` |
| Flow execution | `FlowActionExecutor` | `Services/FlowActionExecutor.cs` |
| LLM tool calling | `AgentTools` | `Services/AgentTools.cs` |
| Pack orchestration | `PackOrchestrator` | `Services/Packs/PackOrchestrator.cs` |
| Safety & kill switch | `HotkeyService` | `Services/HotkeyService.cs` |
| Config & persistence | `ConfigService` | `Services/ConfigService.cs` |
| REST API | `ApiHostService` | `Services/Api/ApiHostService.cs` |
| MCP server | `McpServerService` | `Services/Mcp/McpServerService.cs` |

See [PRODUCT.md](PRODUCT.md) for the full product vision and philosophy.

## Development Workflow

```
src/IdolClick.App/
├── Models/          ← Data contracts (modify schemas too)
├── Services/        ← Core logic (one service per concern)
│   ├── Backend/     ← IAutomationBackend + DesktopBackend (UIA + Win32)
│   ├── Api/         ← Kestrel REST + SignalR (alpha)
│   ├── Mcp/         ← Model Context Protocol server (alpha)
│   ├── Packs/       ← Plan → Compile → Execute → Report pipeline
│   └── Templates/   ← NL → Flow templates (alpha)
├── UI/              ← WPF windows (XAML + code-behind)
└── Assets/          ← Icons, images
```

### Running Tests

```powershell
# Built-in smoke tests (15 tests)
IdolClick.exe --smoke

# External test suites
.\Run-SmokeTests.ps1 -File "tests\level-1-desktop-basic.json"
```

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).

# IdolClick — Current State

Last verified: 2026-03-13

This file is the fastest source of truth for the repo's current operating status.
Use it before reading roadmap-heavy documents.

## Canonical Terms

- **Instinct** = the Classic rule-engine mode
- **Reason** = the Agent chat mode
- **Teach** = guided flow authoring mode
- **Pack** = orchestrated multi-journey testing pipeline on top of the flow runtime
- **Commands / API / MCP** = extension surfaces around the core desktop runtime

## Product Shape

IdolClick has **three user-facing modes** and **two execution families**.

- **Execution family 1:** Instinct / Classic rule engine
- **Execution family 2:** Flow runtime used by Reason and Teach

Teach is a distinct UX mode, but not a distinct execution engine.

## Validated Status

### Verified in this repo on 2026-03-13

- `dotnet build IdolClick.sln -c Release` succeeds
- `dotnet test IdolClick.sln -c Release` succeeds (`8/8` tests passed)
- Built-in Classic integration tests passed: `14/14`
- Editor diagnostics reported no active source errors in `src/IdolClick.App`

### Evidence

- Build command: `dotnet build IdolClick.sln -c Release`
- Test command: `dotnet test IdolClick.sln -c Release`
- Classic integration command: `src/IdolClick.App/bin/Release/net8.0-windows/IdolClick.exe --test-classic`
- Classic result log: `src/IdolClick.App/bin/Release/net8.0-windows/logs/log_20260313_003223.txt`

## Maturity by Surface

| Surface | Current Read | Basis |
|--------|--------------|-------|
| Instinct / Classic | Stable | Deterministic engine, lazy startup isolation, 14/14 built-in integration tests passed |
| Reason / Agent | Beta | Implemented and buildable, with new core API contract coverage but broader runtime validation still maturing |
| Teach | Beta | Real UI and flow compilation path, now backed by deterministic intent-splitting tests |
| Pack | Beta / Alpha boundary | Implemented pipeline with models and services, but lower verification confidence than Classic |
| Commands | Alpha | Intent splitter is still keyword-based, but core split/compound-context behavior is now covered by automated tests |
| API | Alpha | Implemented embedded Kestrel surface with automated coverage for intent-resolve and flow validate/run contracts |
| MCP | Alpha | Implemented stdio server surface; still ecosystem-facing rather than hardened product core |

## Known Risks

- Documentation drift exists across README, PRODUCT, ARCHITECTURE, and AGENTS-GUIDE.
- Agent, Teach, Pack, API, and MCP do not yet have the same automated confidence level as Classic.
- Some external dependencies are still preview/beta quality.

## Sprint 1 Focus

- Remove known build/analyzer warnings with runtime implications
- Align architecture wording around three UX modes vs. two execution families
- Add a compact source-of-truth status document for humans and LLMs
- Raise trust in non-Classic surfaces through targeted validation work

## Read Order

1. `CURRENT-STATE.md`
2. `README.md`
3. `ARCHITECTURE.md`
4. `AGENTS-GUIDE.md`
5. `PRODUCT.md`

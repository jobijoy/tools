# IdolClick — Documentation Index

Last updated: 2026-03-13

This file defines what each document owns, how to read the repo quickly, and how to avoid contradictory edits.

## Recommended Read Paths

### Fast human orientation

1. `CURRENT-STATE.md`
2. `README.md`
3. `DOCS-INDEX.md`

### Architect / maintainer

1. `CURRENT-STATE.md`
2. `ARCHITECTURE.md`
3. `PRODUCT.md`
4. `AGENTS-GUIDE.md`

### Coding agent / LLM

1. `CURRENT-STATE.md`
2. `DOCS-INDEX.md`
3. `ARCHITECTURE.md`
4. `AGENTS-GUIDE.md`
5. `PRODUCT.md`

## Source-of-Truth Ownership

| Document | Owns | Does not own |
|----------|------|--------------|
| `CURRENT-STATE.md` | Current validated status, maturity language, verification commands, known risks | Future roadmap detail, deep architecture narrative |
| `README.md` | Quick orientation, install/run basics, document discovery | Detailed architecture or duplicated status matrices |
| `ARCHITECTURE.md` | Runtime shape, layers, components, dependency boundaries | Product marketing, current validation status |
| `PRODUCT.md` | Product positioning, external behavior, roadmap framing | Low-level code ownership or current build/test truth |
| `DESIGN-SYSTEM.md` | UI token architecture, theming rules, maintainability guidance | Product roadmap or runtime ownership |
| `AGENTS-GUIDE.md` | Coding workflows, conventions, file map, maintenance tasks | Product positioning or status authority |
| `CONTRIBUTING.md` | Contribution workflow and doc hygiene rules | Architecture explanations or roadmap duplication |

## Canonical Terms

- **Instinct / Classic**: rule-engine execution family
- **Reason / Agent**: natural-language flow execution surface
- **Teach**: guided flow-authoring surface over the flow runtime
- **Pack**: orchestrated multi-journey testing pipeline
- **Commands**: deterministic natural-language to flow compiler/template layer
- **API / MCP**: external control surfaces around the runtime

## Update Rules

When behavior changes:

1. Update `CURRENT-STATE.md` if the validated truth changed.
2. Update `ARCHITECTURE.md` if runtime boundaries or component relationships changed.
3. Update `PRODUCT.md` if user-facing positioning or maturity messaging changed.
4. Update `AGENTS-GUIDE.md` if coding workflow or maintenance guidance changed.
5. Update `README.md` only if onboarding or discovery changed.

## LLM Optimization Rules

- Prefer one short fact in one authoritative file over repeating the same fact in several files.
- Put validated present-tense facts in `CURRENT-STATE.md`.
- Put durable conceptual explanations in `ARCHITECTURE.md` or `PRODUCT.md`, not both.
- Use canonical mode names consistently, with aliases only where needed for mapping.
- Keep roadmap items out of current-state summaries.
- When adding a new major doc, add it here with ownership and read-path guidance.

## Validation Commands

- Build: `dotnet build IdolClick.sln -c Release`
- Tests: `dotnet test IdolClick.sln -c Release`
- Classic integration: `src/IdolClick.App/bin/Release/net8.0-windows/IdolClick.exe --test-classic`

# Changelog

All notable changes to IdolClick will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-02-17

### Added

**Classic Mode**
- Rule-based polling engine — monitors windows and auto-clicks matching UI elements
- Per-rule configuration: target app, match text, regex, exclude texts, cooldown, dry run
- Time windows, focus requirements, and execution caps per rule
- Import/Export rules as JSON

**Agent Mode**
- LLM chat interface with 14 tool-calling functions (9 classic + 5 Pack orchestration)
- Real-time streaming responses with tool progress callbacks
- Configurable LLM endpoint (Azure OpenAI, OpenAI, local models via Microsoft.Extensions.AI)

**Pack Pipeline**
- Orchestrated multi-journey testing: Plan → Compile → Validate → Execute → Report
- Dual-perception Eye: structural (UIA tree) + visual (screenshot/LLM vision)
- Confidence scoring (0.0–1.0) with weighted breakdown
- Fix queue with ranked, actionable items

**Desktop UIA Backend**
- Windows UI Automation tree traversal and element resolution
- Selector format: `ElementType#TextOrAutomationId`
- Full actionability checks: exists, visible, enabled, stable, editable
- 13 supported actions: click, type, send_keys, wait, assert_exists, assert_not_exists, assert_text, assert_window, navigate, screenshot, scroll, focus_window, launch

**Safety**
- Global kill switch (Ctrl+Alt+Escape) with manual reset requirement
- Target lock (HWND + PID pinning) prevents clicking wrong windows
- Process allowlist restricts which apps can be automated
- Vision fallback transparency — non-deterministic steps flagged as warnings
- Persistent audit log for safety-critical events

**Structured DSL**
- TestFlow JSON v1 — exchange format between AI agents and IdolClick
- TestPack JSON v1 — multi-journey test campaigns with guardrails
- 7 JSON schemas (JSON Schema 2020-12)
- Machine-readable ExecutionReport with full step traces

**Extensibility**
- Plugin system: PowerShell scripts and .NET assemblies
- Event hooks: OnRuleTriggered, OnFlowCompleted, OnError
- Sample plugins: Discord webhook notifications, file logger

**Infrastructure**
- Commands module (alpha) — natural language → structured flow compiler
- API server (alpha) — Kestrel REST + SignalR for external integration
- MCP server (alpha) — Model Context Protocol for AI agent interop
- WPF-UI 4.2.0 Fluent Design dark theme
- Self-contained single-file exe packaging
- Inno Setup installer + portable ZIP distribution

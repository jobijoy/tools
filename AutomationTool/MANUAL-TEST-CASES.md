# IdolClick — Manual Test Cases

> **Purpose**: Systematically verify all features across Sprints 1–7.  
> **Prerequisites**: A build from `dotnet build`, a running instance of IdolClick, Notepad (or any simple app) for automation targets, and an LLM API key (GitHub Models, Azure OpenAI, or local Ollama).

---

## TC-01: App Startup & Single Instance

| # | Step | Expected |
|---|------|----------|
| 1 | Launch `IdolClick.exe` | Splash screen shows, progress bar fills, main window opens |
| 2 | Launch a second `IdolClick.exe` | Second instance exits immediately, first instance comes to foreground |
| 3 | Close and relaunch | Config is preserved from previous run |

---

## TC-02: Classic Mode — Rule Engine

| # | Step | Expected |
|---|------|----------|
| 1 | Verify mode toggle pill is visible in the toolbar | Shows "Classic" / "Agent" pill |
| 2 | Select "Classic" mode | Rules panel is visible, chat panel is hidden |
| 3 | Create a new rule with a target window + selector | Rule appears in the list |
| 4 | Toggle automation ON (hotkey or button) | Engine starts polling |
| 5 | Open the target app and trigger the rule condition | Rule executes (click, type, etc.) |
| 6 | Check the 🔁 execution count column (if enabled in settings) | Counter increments |

---

## TC-03: Agent Mode — Chat + LLM

| # | Step | Expected |
|---|------|----------|
| 1 | Switch to "Agent" mode via the pill toggle | Chat panel appears, rules panel hides |
| 2 | With no API key configured, type a message | Error: "Agent is not configured. Go to Settings → Agent…" |
| 3 | Open Settings → Agent, enter a valid Endpoint, Model ID, and API Key | Fields accept input |
| 4 | Save settings, return to chat | Status bar shows "Connected · {model}" |
| 5 | Type "Hello" and press Enter | LLM response appears in chat |
| 6 | Click 🗑 Clear button | Chat history clears, system prompt re-added internally |

---

## TC-04: Agent Tools — Window Discovery

| # | Step | Expected |
|---|------|----------|
| 1 | Open Notepad (or any app) beforehand | — |
| 2 | In agent chat, ask: "List all open windows" | Agent calls `ListWindows`, response shows process names + titles including Notepad |
| 3 | Ask: "Inspect the Notepad window" | Agent calls `InspectWindow`, response shows UI tree with element types, names, AutomationIds, selectors |
| 4 | Ask: "List running processes" | Agent calls `ListProcesses`, response shows PIDs + window titles |
| 5 | Ask: "What are your capabilities?" | Agent calls `GetCapabilities`, response includes actions, assertions, selector format, backend info, **vision fallback status** |

---

## TC-05: Flow Generation + Validation

| # | Step | Expected |
|---|------|----------|
| 1 | Ask the agent: "Create a test flow that types 'Hello World' into Notepad and presses Enter" | Agent inspects Notepad first, then generates a `TestFlow` JSON |
| 2 | Observe the chat response | JSON code block appears with `schemaVersion`, `testName`, `steps` |
| 3 | A "flow detected" action bar appears below the response | ▶ Run and other flow action buttons visible |
| 4 | Agent should have called `ValidateFlow` internally | Validation passes |

---

## TC-06: Flow Execution — Run Button

| # | Step | Expected |
|---|------|----------|
| 1 | After TC-05, click the ▶ Run button on the flow action bar | Execution starts, live progress updates per step |
| 2 | Watch Notepad | "Hello World" is typed, Enter is pressed |
| 3 | Execution completes | Report shows passed/failed per step, total timing |
| 4 | Check the `reports/` directory | A new report folder with `report.json` + optional screenshots |

---

## TC-07: Flow Import — File & Clipboard

| # | Step | Expected |
|---|------|----------|
| 1 | Create a `.json` file with a valid TestFlow on disk | — |
| 2 | Click 📂 "Load Flow" button in the chat header | File picker opens |
| 3 | Select the flow JSON file | Flow is validated and displayed in chat |
| 4 | Copy a valid flow JSON to clipboard | — |
| 5 | Click 📋 "Paste Flow" button | Flow is imported from clipboard, validated, displayed |
| 6 | Try importing invalid JSON | Error message appears (validation failure) |

---

## TC-08: Closed-Loop Agent Execution

| # | Step | Expected |
|---|------|----------|
| 1 | Ask the agent: "Open Notepad, type 'Test 123', save the file as 'test.txt'" | Agent generates a multi-step flow |
| 2 | Agent calls `RunFlow` automatically (or you click Run) | Flow executes step by step |
| 3 | If a step fails (e.g., wrong selector), agent should analyze the report | Agent reads the error, fixes the selector |
| 4 | Agent re-runs the corrected flow | All steps pass on retry |

---

## TC-09: Report Service

| # | Step | Expected |
|---|------|----------|
| 1 | After running a flow, ask the agent: "List recent reports" | Agent calls `ListReports`, shows folder names + results |
| 2 | Ask: "Take a screenshot" | Agent calls `CaptureScreenshot`, reports the file path |
| 3 | Check the file on disk | PNG screenshot of the full desktop exists |

---

## TC-10: Click Radar Overlay

| # | Step | Expected |
|---|------|----------|
| 1 | Enable "Click Radar" in Settings → Display | Checkbox is checked |
| 2 | Run a flow with click actions | Expanding concentric circles animate at each click point |
| 3 | Verify the overlay is click-through | Clicks pass through the radar animation, no focus stealing |
| 4 | Disable "Click Radar" in settings | No more pulse animations |

---

## TC-11: Settings Persistence

| # | Step | Expected |
|---|------|----------|
| 1 | Open Settings, change several values (hotkey, log level, agent endpoint) | — |
| 2 | Click Save | Settings saved, dialog closes |
| 3 | Close and relaunch IdolClick | All changed settings are preserved |
| 4 | Open `config.json` in a text editor | Values match what was set in the UI |

---

## TC-12: Profiles

| # | Step | Expected |
|---|------|----------|
| 1 | Look for profile management in the UI | Profile selector/buttons visible |
| 2 | Create a new profile with different rules | Profile saved |
| 3 | Switch between profiles | Rules update to match the selected profile |
| 4 | Delete a profile | Profile removed, reverts to default |

---

## TC-13: Plugin System

| # | Step | Expected |
|---|------|----------|
| 1 | Check that `Plugins/` folder exists next to the exe | Contains `discord-webhook.ps1`, `sample-logger.ps1`, `README.md` |
| 2 | Verify plugins are loaded at startup | Log shows "Discovered X plugins" |
| 3 | Trigger a rule that fires a plugin hook | Plugin script executes (check log output) |

---

## TC-14: IAutomationBackend — Desktop Backend

| # | Step | Expected |
|---|------|----------|
| 1 | Run a flow against Notepad | Steps execute via DesktopBackend |
| 2 | Check the step result's `backendName` field | Shows `"desktop-uia"` |
| 3 | Check `backendCallLog` in the report | Contains entries like "Finding window…", "Resolving selector…", "Actionability: visible ✓" |
| 4 | Run a flow with a Click action on a disabled button | Actionability check fails: "Element is disabled" |
| 5 | Run a flow against a non-existent window | Error: "Target window not found" |

---

## TC-15: Actionability Checks

| # | Step | Expected |
|---|------|----------|
| 1 | Create a flow that clicks a visible, enabled button | All actionability checks pass (visible ✓, enabled ✓, stable ✓, receives_events ✓) |
| 2 | Create a flow that clicks a hidden/collapsed element | Fails: "Element is not visible (bounding box is empty)" |
| 3 | Create a flow that types into a read-only field | Fails: "Element is read-only" |
| 4 | Check the backend call log for actionability entries | Each check logged with ✓ or failure reason |

---

## TC-16: Vision Fallback — Settings

| # | Step | Expected |
|---|------|----------|
| 1 | Open Settings → Agent section | "Vision Fallback" sub-section visible with checkbox, model field, confidence threshold |
| 2 | Check "Enable vision fallback" | Checkbox activates |
| 3 | Set confidence threshold to 0.8 | Value accepted |
| 4 | Optionally set a separate Vision Model ID | Field accepts input |
| 5 | Save settings | Config saved, VisionService reconfigured |
| 6 | Verify `config.json` contains `visionFallbackEnabled`, `visionConfidenceThreshold`, `visionModelId` | Fields present with correct values |

---

## TC-17: Vision Fallback — Agent Tool

| # | Step | Expected |
|---|------|----------|
| 1 | Enable vision fallback in settings (TC-16) | — |
| 2 | Ask the agent: "Use vision to find the File menu in Notepad" | Agent calls `LocateByVision` tool |
| 3 | Response includes coordinates, confidence, and description | `found: true`, `centerX`/`centerY`, `confidence >= threshold` |
| 4 | Response includes `screenshotPath` | Screenshot file exists on disk in `reports/_vision/` |
| 5 | Disable vision fallback, ask again | Error: "Vision fallback is not enabled" |

---

## TC-18: Vision Fallback — In Execution Pipeline

| # | Step | Expected |
|---|------|----------|
| 1 | Enable vision fallback in settings | — |
| 2 | Create a flow with a deliberately wrong/nonexistent UIA selector but a good `description` field for a click action | e.g., `"selector": "Button#NonExistent", "description": "the File menu"` |
| 3 | Run the flow | UIA resolution fails → vision fallback triggers |
| 4 | Check the step result | `selectorResolvedTo` starts with `[Vision]`, `diagnostics` says "Resolved by vision fallback" |
| 5 | If vision confidence is below threshold | Step fails (vision not confident enough) |
| 6 | With vision disabled, same flow | Step fails normally (element not found, no vision attempt) |

---

## TC-19: Vision Fallback — GetCapabilities

| # | Step | Expected |
|---|------|----------|
| 1 | Ask the agent: "What are your capabilities?" | Response includes `visionFallback` section |
| 2 | Vision enabled | Shows `enabled: true`, `confidenceThreshold: 0.7` (or configured value) |
| 3 | Vision disabled | `visionFallback` shows `enabled: false` |

---

## TC-20: Flow DSL — All Actions

Test each of the 13 step actions individually:

| # | Action | Test Flow Step | Expected |
|---|--------|---------------|----------|
| 1 | `launch` | `{ "action": "launch", "processPath": "notepad.exe" }` | Notepad opens |
| 2 | `focus_window` | `{ "action": "focus_window", "app": "notepad" }` | Notepad comes to foreground |
| 3 | `click` | `{ "action": "click", "selector": "Button#MenuItemFile" }` | File menu opens (or appropriate button) |
| 4 | `type` | `{ "action": "type", "text": "Hello World" }` | Text appears in focused element |
| 5 | `send_keys` | `{ "action": "send_keys", "keys": "Ctrl+A" }` | All text selected |
| 6 | `wait` | `{ "action": "wait", "timeoutMs": 1000 }` | Pauses for 1 second |
| 7 | `assert_exists` | `{ "action": "assert_exists", "selector": "Edit#Editor" }` | Passes if element exists |
| 8 | `assert_not_exists` | `{ "action": "assert_not_exists", "selector": "Button#Nonexistent" }` | Passes if element missing |
| 9 | `assert_text` | `{ "action": "assert_text", "selector": "Edit#Editor", "contains": "Hello" }` | Passes if text contains "Hello" |
| 10 | `assert_window` | `{ "action": "assert_window", "windowTitle": "Notepad" }` | Passes if window title matches |
| 11 | `screenshot` | `{ "action": "screenshot" }` | Screenshot saved to report |
| 12 | `scroll` | `{ "action": "scroll", "direction": "down", "scrollAmount": 3 }` | Scrolls down |
| 13 | `navigate` | `{ "action": "navigate", "url": "https://example.com" }` | (Playwright only — skip for desktop) |

---

## TC-21: Post-Step Assertions

| # | Step | Expected |
|---|------|----------|
| 1 | Create a flow step with inline assertions: `"assertions": [{ "type": "exists", "selector": "Edit#Editor" }]` | Post-step assertion evaluates after action |
| 2 | Assertion passes | Step passes, assertion result logged |
| 3 | Create a step with a failing assertion | Step fails, error: "Post-step assertion(s) failed" |
| 4 | Test all 6 assertion types: `exists`, `not_exists`, `text_contains`, `text_equals`, `window_title`, `process_running` | Each type evaluates correctly |

---

## TC-22: Flow Validation

| # | Step | Expected |
|---|------|----------|
| 1 | Submit a flow with no steps | Validation error: "must have at least one step" |
| 2 | Submit a flow with duplicate step orders | Warning or error |
| 3 | Submit a flow with an invalid action name | Validation error |
| 4 | Submit a flow with a click action but no selector | Validation error: selector required |
| 5 | Submit a valid flow | Validation passes, `valid: true` |

---

## TC-23: Error Handling & Edge Cases

| # | Step | Expected |
|---|------|----------|
| 1 | Run a flow while the target app is closed | Error: "Target window not found" |
| 2 | Run a flow and close the target app mid-execution | Step fails gracefully with error, remaining steps skipped if `stopOnFailure` |
| 3 | Start execution and cancel mid-flow | Steps cancelled, report shows partial results |
| 4 | Enter an invalid LLM endpoint in settings | Agent shows connection error on next message |
| 5 | Submit malformed JSON as a flow | Parse error, not a crash |

---

## TC-24: Logs & Observability

| # | Step | Expected |
|---|------|----------|
| 1 | Set log level to "Debug" in settings | — |
| 2 | Perform various operations (run flow, chat, etc.) | Log file in `logs/` directory has detailed entries |
| 3 | Check log entries for correlation IDs | Operations are traceable |
| 4 | Open logs folder via Settings → 📋 Logs Folder | Explorer opens the logs directory |

---

## TC-25: End-to-End: Full Agent Workflow

This is the comprehensive happy-path test combining everything:

| # | Step | Expected |
|---|------|----------|
| 1 | Launch IdolClick, switch to Agent mode | Chat panel visible |
| 2 | Ask: "List open windows" | Agent discovers windows |
| 3 | Open Notepad if not already open | — |
| 4 | Ask: "Inspect Notepad" | Agent lists UI elements with selectors |
| 5 | Ask: "Create a flow to type 'Sprint 6 Complete' into Notepad" | Agent generates flow JSON |
| 6 | Click ▶ Run | Flow executes, text appears in Notepad |
| 7 | Report shows all steps passed | — |
| 8 | Ask: "List recent reports" | Latest report appears |
| 9 | Ask: "Take a screenshot" | Screenshot saved |
| 10 | Close Notepad without saving | — |

---

## Summary

| Category | Test Cases | Sprints Covered |
|----------|-----------|----------------|
| Foundation & UI | TC-01 to TC-03, TC-11, TC-12 | Sprint 1 |
| LLM Integration | TC-03 to TC-05 | Sprint 2 |
| Tool Calling | TC-04, TC-05, TC-22 | Sprint 3 |
| Execution Engine | TC-06, TC-14, TC-15, TC-20, TC-21 | Sprint 4, Sprint 7 |
| Pipeline I/O & Closed Loop | TC-07 to TC-09 | Sprint 5 |
| Vision Fallback | TC-16 to TC-19 | Sprint 6 |
| Backend Polymorphism | TC-14, TC-15, TC-19 | Sprint 7 |
| Cross-cutting | TC-10, TC-13, TC-23, TC-24, TC-25 | All |

# Example Flows

Ready-to-run IdolClick automation flows demonstrating core actions and patterns.

## Flows

| File | Actions Used | Description |
|------|-------------|-------------|
| [hello-world-click.json](hello-world-click.json) | `launch`, `click`, `assert_exists`, `send_keys` | Minimal example — open Notepad, click File menu, verify it opened |
| [notepad-type-and-verify.json](notepad-type-and-verify.json) | `launch`, `click`, `type`, `assert_text`, `send_keys` | Type text, verify content, clean up |
| [calculator-smoke-test.json](calculator-smoke-test.json) | `launch`, `click`, `assert_text` | Compute 2+3 in Calculator and verify the result |

## How to Use

### In Reason Mode (AI Agent)

Ask the agent to run a flow:

> "Run the calculator smoke test from examples"

### In Teach Mode

1. Open a flow file in the flow editor
2. Review steps and selectors
3. Click **Run** to execute

### Programmatically

Load the JSON and pass it to `FlowActionExecutor`:

```csharp
var json = File.ReadAllText("examples/hello-world-click.json");
var flow = JsonSerializer.Deserialize<TestFlow>(json, FlowJson.Options);
```

## Selector Format

Selectors use the format `ElementType#TextOrAutomationId`:

- `Button#Save` — a button with text or AutomationId "Save"
- `MenuItem#File` — a menu item labeled "File"
- `Document#Text Editor` — a document control with name "Text Editor"
- `#AutomationId` — match by AutomationId only (any element type)

## Writing Your Own

Start from any example and modify. Key rules:

1. Set `schemaVersion` to `1`
2. Every flow needs `testName` and at least one step
3. Steps execute in `order` sequence
4. Use `targetApp` to scope to a specific process
5. Add `assertions` to steps for post-action verification
6. Set `targetLock: true` to prevent clicking the wrong window

Full schema: [`schemas/test-flow.schema.json`](../schemas/test-flow.schema.json)

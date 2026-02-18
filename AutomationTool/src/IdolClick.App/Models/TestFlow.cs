using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdolClick.Models;

// ═══════════════════════════════════════════════════════════════════════════════════
// STRUCTURED TEST FLOW v1 — Deterministic DSL for AI-authored UI automation.
//
// Design principles:
//   • Rigid schema — strongly typed actions, assertions, and selectors
//   • Deterministic — no vague instructions, precise selectors
//   • Machine-writable — coding agents emit this JSON directly
//   • Machine-readable — execution reports feed back to the coding agent
//   • Validated — FlowValidatorService validates before execution
//   • Versioned — SchemaVersion tracks format evolution
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared JSON serialization options for all flow-related objects.
/// Uses snake_case for enum values to match the DSL format that AI agents generate.
/// </summary>
internal static class FlowJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}

// ═══════════════════════════════════════════════════════════════════════════════════
// SELECTOR MODEL — Backend-typed selector union.
//
// Design: Do NOT force all backends into one selector grammar.
//   • Desktop UIA uses "ElementType#TextOrAutomationId"
//   • SelectorKind + Value = typed discriminated union
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Discriminated union tag for selector types. Each backend supports a subset.
/// Serialized as snake_case (e.g., "desktop_uia").
/// </summary>
public enum SelectorKind
{
    /// <summary>Desktop UIA selector: "ElementType#TextOrAutomationId".</summary>
    DesktopUia
}

/// <summary>
/// A typed selector with backend-specific kind and value.
/// Used alongside the raw Selector string for backward compatibility.
/// </summary>
public class StepSelector
{
    /// <summary>Which selector engine to use.</summary>
    public SelectorKind Kind { get; set; } = SelectorKind.DesktopUia;

    /// <summary>The selector value (format depends on Kind).</summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Optional extra qualifier for advanced selector matching.
    /// </summary>
    public string? Extra { get; set; }

    /// <summary>
    /// When true, the selector identifier must match exactly (string.Equals)
    /// instead of allowing prefix/StartsWith matching. Reduces false-positive
    /// element resolution when multiple elements share similar names.
    /// </summary>
    public bool ExactMatch { get; set; }
}

/// <summary>
/// Supported step actions. Strongly typed to prevent free-form string chaos.
/// Serialized as snake_case via FlowJson.Options (e.g., "click", "send_keys", "assert_exists").
/// </summary>
public enum StepAction
{
    Click,
    Type,
    SendKeys,
    Wait,
    AssertExists,
    AssertNotExists,
    AssertText,
    AssertWindow,
    Navigate,
    Screenshot,
    Scroll,
    FocusWindow,
    Launch,
    Hover
}

/// <summary>
/// Assertion types for post-step verification.
/// </summary>
public enum AssertionType
{
    Exists,
    NotExists,
    TextContains,
    TextEquals,
    WindowTitle,
    ProcessRunning
}

/// <summary>
/// TestFlow v1 schema — the exchange format between coding agents and IdolClick.
/// </summary>
public class TestFlow
{
    /// <summary>Schema version for forward compatibility. Currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Human-readable name for this test flow.
    /// </summary>
    public string TestName { get; set; } = "Untitled Flow";

    /// <summary>
    /// Optional description of what this flow verifies.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Target application process name(s), comma-separated. Empty = any app.
    /// </summary>
    public string? TargetApp { get; set; }

    /// <summary>
    /// Ordered list of steps to execute.
    /// </summary>
    public List<TestStep> Steps { get; set; } = [];

    /// <summary>
    /// Maximum total execution time in seconds. 0 = no limit.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Stop executing on first failure, or continue all steps.
    /// </summary>
    public bool StopOnFailure { get; set; } = true;

    /// <summary>
    /// When this flow was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Preferred automation backend: "desktop" (default).
    /// Individual steps can override via TypedSelector.Kind.
    /// </summary>
    public string Backend { get; set; } = "desktop";

    /// <summary>
    /// When true, the executor pins to the initial target window handle.
    /// If focus shifts away from the locked window during execution,
    /// the step fails instead of clicking the wrong target.
    /// </summary>
    public bool TargetLock { get; set; } = false;
}

/// <summary>
/// A single step in a test flow — strongly typed action with optional post-step assertions.
/// </summary>
public class TestStep
{
    /// <summary>Explicit execution order (1-based). Steps execute in Order sequence.</summary>
    public int Order { get; set; }

    /// <summary>The action to perform. Strongly typed enum — no free-form strings.</summary>
    public StepAction Action { get; set; }

    /// <summary>
    /// UI element selector. Format: "ElementType#TextOrAutomationId"
    /// Examples: "Button#Save", "Toggle#Notifications", "#AutomationId", "TextBlock#Welcome"
    /// For window-level actions, use "Window#Title".
    /// </summary>
    public string? Selector { get; set; }

    /// <summary>
    /// Text to type (for "type" action).
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Keys to send (for "sendkeys" action). Comma-separated: "Ctrl+S", "Tab, Enter".
    /// </summary>
    public string? Keys { get; set; }

    /// <summary>
    /// URL to navigate to (for "navigate" action).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Expected text content (for "assert_text"). Substring match unless Exact is true.
    /// </summary>
    public string? Contains { get; set; }

    /// <summary>
    /// Require exact text match instead of substring. Default: false.
    /// </summary>
    public bool Exact { get; set; }

    /// <summary>
    /// Target application process name override for this step.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Window title filter for this step.
    /// </summary>
    public string? WindowTitle { get; set; }

    /// <summary>
    /// Wait/timeout in milliseconds for this step. Default: 5000ms.
    /// For "wait" action: how long to wait for the condition.
    /// For other actions: max time to find the element before failing.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Fixed delay in milliseconds AFTER this step completes. Default: 200ms.
    /// </summary>
    public int DelayAfterMs { get; set; } = 200;

    /// <summary>
    /// Optional human-readable description of this step.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Process path and arguments (for "launch" action).
    /// </summary>
    public string? ProcessPath { get; set; }

    /// <summary>
    /// Scroll direction: "up", "down", "left", "right" (for "scroll" action).
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Scroll amount in clicks (for Scroll action). Default: 3.
    /// </summary>
    public int ScrollAmount { get; set; } = 3;

    /// <summary>
    /// Post-step assertions. Evaluated after the action completes successfully.
    /// Allows verifying state changes as part of the same logical step.
    /// </summary>
    public List<Assertion> Assertions { get; set; } = [];

    /// <summary>
    /// Typed selector (optional). When set, takes precedence over the raw Selector string.
    /// Enables backend-specific selector strategies.
    /// If null, the Selector string is parsed as DesktopUia format for backward compatibility.
    /// </summary>
    public StepSelector? TypedSelector { get; set; }
}

/// <summary>
/// A single assertion to verify after a step executes.
/// </summary>
public class Assertion
{
    /// <summary>The type of assertion to evaluate.</summary>
    public AssertionType Type { get; set; }

    /// <summary>UI element selector. Format: "ElementType#TextOrAutomationId".</summary>
    public string? Selector { get; set; }

    /// <summary>Expected value for the assertion.</summary>
    public string? Expected { get; set; }

    /// <summary>Require exact match instead of contains. Default: false.</summary>
    public bool Exact { get; set; }

    /// <summary>Timeout in milliseconds. Default: 5000ms.</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>Human-readable description of what this assertion verifies.</summary>
    public string? Description { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// EXECUTION REPORT v1 — What IdolClick produces after running a test flow.
// Versioned schema for forward compatibility with AI consumption.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Machine-readable execution report produced after running a <see cref="TestFlow"/>.
/// Designed for AI consumption: the coding agent reads this to determine what to fix.
/// </summary>
public class ExecutionReport
{
    /// <summary>Report schema version. Currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Name of the test flow that was executed.
    /// </summary>
    public string TestName { get; set; } = "";

    /// <summary>
    /// Overall result: "passed", "failed", "error", "aborted".
    /// </summary>
    public string Result { get; set; } = "error";

    /// <summary>
    /// 1-based index of the first failed step. Null if all passed.
    /// </summary>
    public int? FailedStep { get; set; }

    /// <summary>
    /// Total execution time in milliseconds.
    /// </summary>
    public long TotalTimeMs { get; set; }

    /// <summary>
    /// When execution started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When execution finished.
    /// </summary>
    public DateTime FinishedAt { get; set; }

    /// <summary>
    /// Per-step execution logs.
    /// </summary>
    public List<StepResult> Steps { get; set; } = [];

    /// <summary>
    /// Paths to screenshots captured during execution.
    /// </summary>
    public List<string> Screenshots { get; set; } = [];

    /// <summary>
    /// Summary message suitable for display in chat.
    /// </summary>
    public string Summary { get; set; } = "";

    // ── Report Integrity Metadata ─────────────────────────────────────────

    /// <summary>Which automation backend executed this flow.</summary>
    public string? BackendUsed { get; set; }

    /// <summary>Backend version string.</summary>
    public string? BackendVersion { get; set; }

    /// <summary>Machine name where the flow was executed.</summary>
    public string? MachineName { get; set; }

    /// <summary>OS version string (e.g., "Microsoft Windows NT 10.0.22631.0").</summary>
    public string? OsVersion { get; set; }

    /// <summary>
    /// Total steps that passed.
    /// </summary>
    public int PassedCount => Steps.Count(s => s.Status == StepStatus.Passed);

    /// <summary>
    /// Total steps that failed.
    /// </summary>
    public int FailedCount => Steps.Count(s => s.Status == StepStatus.Failed);

    /// <summary>
    /// Total steps that were skipped (due to StopOnFailure).
    /// </summary>
    public int SkippedCount => Steps.Count(s => s.Status == StepStatus.Skipped);

    /// <summary>
    /// Steps that passed but with warnings (e.g., resolved via vision fallback).
    /// These are non-deterministic and should be reviewed.
    /// </summary>
    public int WarningCount => Steps.Count(s => s.Status == StepStatus.Warning);
}

/// <summary>
/// Result of executing a single test step.
/// Enhanced with timing, retry, and element snapshot metadata.
/// </summary>
public class StepResult
{
    /// <summary>
    /// 1-based step number.
    /// </summary>
    public int Step { get; set; }

    /// <summary>
    /// The action that was executed.
    /// </summary>
    public StepAction Action { get; set; }

    /// <summary>
    /// The selector used, if any.
    /// </summary>
    public string? Selector { get; set; }

    /// <summary>
    /// Step description from the flow, or auto-generated.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Execution status.
    /// </summary>
    public StepStatus Status { get; set; } = StepStatus.Skipped;

    /// <summary>
    /// When this step started executing (UTC).
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// When this step finished executing (UTC).
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Execution duration in milliseconds (EndTime - StartTime).
    /// </summary>
    public long TimeMs { get; set; }

    /// <summary>
    /// Number of retry attempts before final result. 0 = first attempt succeeded/failed.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Expected value (for assertions).
    /// </summary>
    public string? Expected { get; set; }

    /// <summary>
    /// Actual value found (for assertions).
    /// </summary>
    public string? Found { get; set; }

    /// <summary>
    /// Error message if the step failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Path to screenshot taken at this step, if any.
    /// </summary>
    public string? Screenshot { get; set; }

    /// <summary>
    /// Additional diagnostic details for AI consumption.
    /// </summary>
    public string? Diagnostics { get; set; }

    /// <summary>
    /// Snapshot of the matched UI element at the time of execution.
    /// Null if the step doesn't target an element or element was not found.
    /// </summary>
    public ElementSnapshot? Element { get; set; }

    /// <summary>
    /// Results of post-step assertion evaluations.
    /// </summary>
    public List<AssertionResult> AssertionResults { get; set; } = [];

    // ── Sprint 7: Backend trace fields ───────────────────────────────────────

    /// <summary>
    /// Backend-internal call log entries showing auto-wait reasons, retry events,
    /// scroll-into-view attempts, and other behind-the-scenes activity.
    /// Internal backend call log entries.
    /// </summary>
    public List<BackendCallLogEntry> BackendCallLog { get; set; } = [];

    /// <summary>
    /// Metadata about how the selector was resolved (match count, final target info).
    /// Helps diagnosing strict-mode or ambiguous-match issues.
    /// </summary>
    public string? SelectorResolvedTo { get; set; }

    /// <summary>
    /// Screen coordinates where a click/interaction was performed.
    /// Null for non-positional actions.
    /// </summary>
    public ElementBounds? ClickPoint { get; set; }

    /// <summary>
    /// Which automation backend executed this step.
    /// </summary>
    public string? BackendName { get; set; }

    /// <summary>
    /// Structured warning code when Status is Warning (e.g., "VisionFallbackUsed").
    /// Gives AI consumers machine-readable interpretation power.
    /// </summary>
    public string? WarningCode { get; set; }
}

/// <summary>
/// Result of evaluating a single post-step assertion.
/// </summary>
public class AssertionResult
{
    /// <summary>The assertion type that was evaluated.</summary>
    public AssertionType Type { get; set; }

    /// <summary>Whether the assertion passed.</summary>
    public bool Passed { get; set; }

    /// <summary>Expected value.</summary>
    public string? Expected { get; set; }

    /// <summary>Actual value found.</summary>
    public string? Actual { get; set; }

    /// <summary>Assertion description.</summary>
    public string? Description { get; set; }

    /// <summary>Error message if assertion failed.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Snapshot of a UI element at the time of interaction.
/// Stored in StepResult for post-mortem analysis and debugging.
/// </summary>
public class ElementSnapshot
{
    /// <summary>UI Automation control type (e.g., "Button", "TextBox").</summary>
    public string ControlType { get; set; } = "";

    /// <summary>Element name/text.</summary>
    public string? Name { get; set; }

    /// <summary>Automation ID (stable identifier).</summary>
    public string? AutomationId { get; set; }

    /// <summary>Whether the element was enabled at interaction time.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Bounding rectangle on screen (x, y, width, height).</summary>
    public ElementBounds? Bounds { get; set; }
}

/// <summary>
/// Screen-space bounding rectangle for a UI element.
/// </summary>
public class ElementBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// A single log entry from the backend's internal execution (auto-wait, scroll, retry).
/// A single log entry from the backend's internal execution trace.
/// </summary>
public class BackendCallLogEntry
{
    /// <summary>Timestamp relative to step start (ms).</summary>
    public long TimestampMs { get; set; }

    /// <summary>Log message (e.g., "waiting for element to be visible", "scrolling into view").</summary>
    public string Message { get; set; } = "";

    /// <summary>Severity: "info", "warn", "error", "debug".</summary>
    public string Level { get; set; } = "info";
}

/// <summary>
/// Status of a single test step execution.
/// Serialized as snake_case via FlowJson.Options.
/// </summary>
public enum StepStatus
{
    /// <summary>Step completed successfully.</summary>
    Passed,
    /// <summary>Step failed (assertion mismatch, element not found, etc.).</summary>
    Failed,
    /// <summary>Step was skipped (due to StopOnFailure after a prior failure).</summary>
    Skipped,
    /// <summary>Step encountered an unexpected error.</summary>
    Error,
    /// <summary>
    /// Step succeeded but with a non-deterministic resolution path (e.g., vision fallback).
    /// Treated as passed for flow result, but flagged for human review.
    /// </summary>
    Warning
}

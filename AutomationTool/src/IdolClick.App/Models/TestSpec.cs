using System.Text.Json.Serialization;

namespace IdolClick.Models;

// ═══════════════════════════════════════════════════════════════════════════════════
// TEST SPEC — High-level test specification for coding agent integration.
//
// TestSpec is the "human-intent" bridging format between external coding agents
// and IdolClick's execution engine. Coding agents generate TestSpecs in natural
// language (no UIA selectors needed); IdolClick's LLM compiles them into
// executable TestFlows internally.
//
// Flow:
//   Coding Agent  →  TestSpec JSON  →  IdolClick MCP  →  compile to TestFlow
//                                                     →  execute against real UI
//                                                     →  return TestSpecReport
//
// Key design decisions:
//   • Steps use natural language descriptions, NOT selector paths.
//   • Each step has an "expected" field — the acceptance criterion.
//   • Tags enable filtering/grouping by feature area.
//   • The format is intentionally simple so any LLM can generate it.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A high-level test specification that coding agents generate.
/// Describes what to test in natural language — IdolClick compiles it to TestFlow.
/// </summary>
public sealed class TestSpec
{
    /// <summary>Human-readable name for this test specification.</summary>
    [JsonPropertyName("specName")]
    public string SpecName { get; set; } = "";

    /// <summary>
    /// Target application to test.
    /// Process name or window title (e.g., "notepad", "Calculator").
    /// </summary>
    [JsonPropertyName("targetApp")]
    public string TargetApp { get; set; } = "";

    /// <summary>
    /// Optional description providing context for the LLM compiler.
    /// Helps the LLM understand the overall goal when compiling to TestFlow.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Ordered list of test steps to execute.
    /// </summary>
    [JsonPropertyName("steps")]
    public List<TestSpecStep> Steps { get; set; } = [];

    /// <summary>
    /// Optional tags for categorization (e.g., "checkout", "regression", "smoke").
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Optional timeout in milliseconds for the entire spec execution.
    /// 0 = use default (60000ms / 1 minute).
    /// </summary>
    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 60000;
}

/// <summary>
/// A single step within a TestSpec. Uses natural language, not selectors.
/// </summary>
public sealed class TestSpecStep
{
    /// <summary>
    /// Action type: "click", "type", "verify", "wait", "scroll", "hover", "screenshot".
    /// These are human-intent actions — the compiler maps them to concrete TestFlow steps.
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>
    /// Natural language description of what to do.
    /// Examples: "Click the 'Place Order' button", "Type 'john@example.com' in the email field".
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Expected outcome — the acceptance criterion for this step.
    /// Examples: "Order confirmation page appears", "Email field shows the typed text".
    /// </summary>
    [JsonPropertyName("expected")]
    public string Expected { get; set; } = "";
}

/// <summary>
/// Result report from executing a TestSpec.
/// Machine-readable so coding agents can act on failures.
/// </summary>
public sealed class TestSpecReport
{
    /// <summary>Name of the executed spec.</summary>
    [JsonPropertyName("specName")]
    public string SpecName { get; set; } = "";

    /// <summary>Overall result: "passed", "failed", "error".</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "error";

    /// <summary>Overall confidence score 0.0–1.0.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Total execution time in milliseconds.</summary>
    [JsonPropertyName("totalTimeMs")]
    public long TotalTimeMs { get; set; }

    /// <summary>Per-step results.</summary>
    [JsonPropertyName("steps")]
    public List<TestSpecStepResult> Steps { get; set; } = [];

    /// <summary>
    /// Machine-readable fix suggestions for the coding agent.
    /// Each entry describes what likely went wrong and how to fix it.
    /// </summary>
    [JsonPropertyName("fixSuggestions")]
    public List<string> FixSuggestions { get; set; } = [];

    /// <summary>Timestamp when execution started (ISO 8601).</summary>
    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    /// <summary>
    /// Scoring breakdown by dimension.
    /// </summary>
    [JsonPropertyName("scoring")]
    public TestSpecScoring Scoring { get; set; } = new();
}

/// <summary>
/// Result of a single TestSpec step execution.
/// </summary>
public sealed class TestSpecStepResult
{
    /// <summary>Step index (0-based).</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Original step description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Step result: "passed", "failed", "skipped", "error".</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "error";

    /// <summary>Execution time for this step in milliseconds.</summary>
    [JsonPropertyName("timeMs")]
    public long TimeMs { get; set; }

    /// <summary>Error message if the step failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Screenshot path if captured during this step.</summary>
    [JsonPropertyName("screenshot")]
    public string? Screenshot { get; set; }
}

/// <summary>
/// Multi-dimensional scoring breakdown.
/// </summary>
public sealed class TestSpecScoring
{
    /// <summary>Functional score: steps passed / total steps (0.0–1.0).</summary>
    [JsonPropertyName("functional")]
    public double Functional { get; set; }

    /// <summary>Timing score: steps within acceptable thresholds (0.0–1.0).</summary>
    [JsonPropertyName("timing")]
    public double Timing { get; set; }

    /// <summary>Overall weighted score (0.0–1.0).</summary>
    [JsonPropertyName("overall")]
    public double Overall { get; set; }
}

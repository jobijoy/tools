using IdolClick.Models;

namespace IdolClick.Services.Backend;

// ═══════════════════════════════════════════════════════════════════════════════════
// AUTOMATION BACKEND — Polymorphic interface for desktop automation.
//
// Design principles:
//   • Unify the pipeline (TestFlow → StepResult → ExecutionReport) across backends
//   • Keep selectors typed per backend — do NOT cram different grammars together
//   • Each backend owns its actionability contract (what "ready to click" means)
//   • Trace artifacts are first-class (UIA screenshots, tree snapshots)
//   • Optional inspection hooks for agent tool-calling
//
// Current backends:
//   • DesktopBackend (desktop-uia) — Windows UI Automation + Win32
//
// Package strategy (future):
//   • IdolClick.Core — TestFlow, validation, runner pipeline, this interface
//   • IdolClick.DesktopBackend — UIA implementation
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Polymorphic automation backend. Each backend implements step execution,
/// target inspection, and optional artifact capture (tracing/screenshots).
/// </summary>
public interface IAutomationBackend : IAsyncDisposable
{
    /// <summary>
    /// Machine-readable backend identifier: "desktop-uia".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Backend version string for diagnostics.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Declares what this backend can do (actions, assertions, selector kinds, tracing).
    /// </summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// Initialize the backend (e.g., launch browser, connect to UI Automation).
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task InitializeAsync(BackendInitOptions options, CancellationToken ct = default);

    /// <summary>
    /// Executes a single validated step and returns a populated StepResult.
    /// The backend owns the full lifecycle: resolve selector → actionability wait → execute → snapshot.
    /// </summary>
    Task<StepResult> ExecuteStepAsync(TestStep step, BackendExecutionContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Lists inspectable targets (windows for desktop, pages/frames for browser).
    /// Used by agent tools for discovery.
    /// </summary>
    Task<IReadOnlyList<InspectableTarget>> ListTargetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Inspects a specific target and returns its element/node tree.
    /// Used by agent tools for element discovery.
    /// </summary>
    Task<InspectionResult> InspectTargetAsync(InspectTargetRequest request, CancellationToken ct = default);

    /// <summary>
    /// Start artifact capture (tracing, screen recording) for a session.
    /// Returns null if the backend does not support artifacts.
    /// </summary>
    Task<BackendArtifact?> StartArtifactCaptureAsync(ArtifactOptions options, CancellationToken ct = default);

    /// <summary>
    /// Stop artifact capture and return the artifact path.
    /// Returns null if capture was not started.
    /// </summary>
    Task<BackendArtifact?> StopArtifactCaptureAsync(CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════════════════════════
// SUPPORTING TYPES
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Declares what a backend can do. Used by the executor to validate flows
/// against backend capabilities before execution.
/// </summary>
public sealed class BackendCapabilities
{
    /// <summary>Actions this backend can execute.</summary>
    public IReadOnlySet<StepAction> SupportedActions { get; init; } = new HashSet<StepAction>();

    /// <summary>Assertion types this backend can evaluate.</summary>
    public IReadOnlySet<AssertionType> SupportedAssertions { get; init; } = new HashSet<AssertionType>();

    /// <summary>Selector kinds this backend understands.</summary>
    public IReadOnlySet<SelectorKind> SupportedSelectorKinds { get; init; } = new HashSet<SelectorKind>();

    /// <summary>Whether this backend can produce trace artifacts.</summary>
    public bool SupportsTracing { get; init; }

    /// <summary>Whether this backend can take screenshots.</summary>
    public bool SupportsScreenshots { get; init; }

    /// <summary>Whether this backend supports per-action actionability checks.</summary>
    public bool SupportsActionabilityChecks { get; init; }
}

/// <summary>
/// Options for backend initialization.
/// </summary>
public class BackendInitOptions
{
    /// <summary>Base directory for storing artifacts (screenshots, traces).</summary>
    public string? ArtifactDirectory { get; set; }

    /// <summary>Default timeout for element resolution in milliseconds.</summary>
    public int DefaultTimeoutMs { get; set; } = 5000;

    /// <summary>Whether to enable verbose logging from the backend.</summary>
    public bool Verbose { get; set; }

}

/// <summary>
/// Execution context passed to each step. Carries flow-level state.
/// </summary>
public class BackendExecutionContext
{
    /// <summary>The flow being executed (for access to TargetApp, etc.).</summary>
    public required TestFlow Flow { get; init; }

    /// <summary>Current step index (0-based).</summary>
    public int StepIndex { get; set; }

    /// <summary>Total steps in the flow.</summary>
    public int TotalSteps { get; set; }

    /// <summary>Directory to save step screenshots to.</summary>
    public string? ScreenshotDirectory { get; set; }

    /// <summary>Shared state bag for cross-step data passing.</summary>
    public Dictionary<string, object> State { get; init; } = [];
}

/// <summary>
/// A discoverable target (window, browser tab, frame) that can be inspected.
/// </summary>
public class InspectableTarget
{
    /// <summary>Unique identifier for the target.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Target type: "window", "page", "frame".</summary>
    public string TargetType { get; set; } = "window";

    /// <summary>Process name (desktop) or URL (browser).</summary>
    public string? Source { get; set; }

    /// <summary>Bounding rectangle on screen (desktop only).</summary>
    public ElementBounds? Bounds { get; set; }

    /// <summary>Additional metadata key-value pairs.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// Request to inspect a specific target.
/// </summary>
public class InspectTargetRequest
{
    /// <summary>Target ID to inspect (from ListTargets).</summary>
    public string TargetId { get; set; } = "";

    /// <summary>Maximum depth to traverse in the element tree.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Optional selector to scope inspection to a subtree.</summary>
    public string? ScopeSelector { get; set; }
}

/// <summary>
/// Result of inspecting a target — a tree of elements/nodes.
/// </summary>
public class InspectionResult
{
    /// <summary>The target that was inspected.</summary>
    public string TargetId { get; set; } = "";

    /// <summary>Root elements of the inspection tree.</summary>
    public List<InspectionNode> Nodes { get; set; } = [];

    /// <summary>Total elements found.</summary>
    public int TotalCount { get; set; }

    /// <summary>Whether the tree was truncated due to depth limit.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// A single node in the inspection tree.
/// </summary>
public class InspectionNode
{
    /// <summary>Element type or tag name.</summary>
    public string Type { get; set; } = "";

    /// <summary>Element name/text.</summary>
    public string? Name { get; set; }

    /// <summary>Automation ID (desktop) or id attribute (web).</summary>
    public string? Id { get; set; }

    /// <summary>Suggested selector to target this element.</summary>
    public string? SuggestedSelector { get; set; }

    /// <summary>Whether this element is interactive.</summary>
    public bool IsInteractive { get; set; }

    /// <summary>Bounding rectangle.</summary>
    public ElementBounds? Bounds { get; set; }

    /// <summary>Child nodes.</summary>
    public List<InspectionNode> Children { get; set; } = [];
}

/// <summary>
/// Options for starting artifact capture (tracing/recording).
/// </summary>
public class ArtifactOptions
{
    /// <summary>Capture screenshots at each action.</summary>
    public bool Screenshots { get; set; } = true;

    /// <summary>Capture DOM/UIA snapshots at each action.</summary>
    public bool Snapshots { get; set; } = true;

    /// <summary>Include source code in traces.</summary>
    public bool Sources { get; set; }

    /// <summary>Trace title for identification.</summary>
    public string? Title { get; set; }

    /// <summary>Output path for the trace artifact.</summary>
    public string? OutputPath { get; set; }
}

/// <summary>
/// A captured artifact (trace file, recording).
/// </summary>
public class BackendArtifact
{
    /// <summary>Artifact type: "trace", "recording", "har".</summary>
    public string Type { get; set; } = "trace";

    /// <summary>Absolute file path to the artifact.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>When capture started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When capture stopped.</summary>
    public DateTime StoppedAt { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// ACTIONABILITY CONTRACT — Per-action pre-conditions that must be met.
//
// Auto-wait pattern:
//   • Click: exists + visible + stable + enabled + receives-events
//   • Fill/Type: exists + visible + enabled + editable
//   • Assert: exists (with retry)
//
// Each backend implements these checks in its own way.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Actionability check types. A backend evaluates a set of these before performing an action.
/// </summary>
public enum ActionabilityCheck
{
    /// <summary>Element exists in the tree.</summary>
    Exists,
    /// <summary>Element is visible (non-zero bounding box).</summary>
    Visible,
    /// <summary>Element is enabled (not disabled/grayed).</summary>
    Enabled,
    /// <summary>Element bounds are stable (not animating). Requires two consecutive reads.</summary>
    Stable,
    /// <summary>Element can receive input events (not obscured by modal overlay).</summary>
    ReceivesEvents,
    /// <summary>Element is editable (for text input actions).</summary>
    Editable
}

/// <summary>
/// Defines the actionability contract for a specific action type.
/// Maps which checks must pass before an action can execute.
/// </summary>
public static class ActionabilityContracts
{
    /// <summary>
    /// Returns the set of actionability checks required for a given action.
    /// </summary>
    public static IReadOnlySet<ActionabilityCheck> GetRequiredChecks(StepAction action)
    {
        return action switch
        {
            // Click-like: full actionability contract  
            StepAction.Click => new HashSet<ActionabilityCheck>
            {
                ActionabilityCheck.Exists,
                ActionabilityCheck.Visible,
                ActionabilityCheck.Stable,
                ActionabilityCheck.Enabled,
                ActionabilityCheck.ReceivesEvents
            },

            // Type/Fill: element must be editable
            StepAction.Type => new HashSet<ActionabilityCheck>
            {
                ActionabilityCheck.Exists,
                ActionabilityCheck.Visible,
                ActionabilityCheck.Enabled,
                ActionabilityCheck.Editable
            },

            // Scroll: needs to exist and be visible
            StepAction.Scroll => new HashSet<ActionabilityCheck>
            {
                ActionabilityCheck.Exists,
                ActionabilityCheck.Visible
            },

            // Assert-like: just needs to exist
            StepAction.AssertExists or StepAction.AssertText or StepAction.AssertWindow => new HashSet<ActionabilityCheck>
            {
                ActionabilityCheck.Exists
            },

            // AssertNotExists: no preconditions (element should NOT exist)
            StepAction.AssertNotExists => new HashSet<ActionabilityCheck>(),

            // FocusWindow: needs the window element
            StepAction.FocusWindow => new HashSet<ActionabilityCheck>
            {
                ActionabilityCheck.Exists
            },

            // No-element actions: no checks
            StepAction.SendKeys or StepAction.Wait or StepAction.Navigate or
            StepAction.Screenshot or StepAction.Launch => new HashSet<ActionabilityCheck>(),

            // Hover: element must exist, be visible and stable (like click minus enabled/receives-events)
            StepAction.Hover => new HashSet<ActionabilityCheck>
            {
                ActionabilityCheck.Exists,
                ActionabilityCheck.Visible,
                ActionabilityCheck.Stable
            },

            _ => new HashSet<ActionabilityCheck> { ActionabilityCheck.Exists }
        };
    }
}

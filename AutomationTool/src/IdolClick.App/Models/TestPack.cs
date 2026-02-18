using System.Text.Json.Serialization;

namespace IdolClick.Models;

// ═══════════════════════════════════════════════════════════════════════════════════
// TEST PACK v1 — Bounded, repeatable, deterministic test program.
//
// A TestPack is the unit of work for the Hand-Eye-Brain architecture:
//   • Brain  — PackPlannerService (PM perspective) + PackCompilerService (SDET perspective)
//   • Hand   — PackRunnerService orchestrates flows via IAutomationBackend
//   • Eye    — Dual-perception: structural (UIA tree) + visual (screenshot/vision)
//
// Key design:
//   • A Pack contains Journeys (PM-meaningful scenarios)
//   • A Journey contains ordered FlowRefs (each flow runs on one backend)
//   • Desktop journeys execute flows via UIA backend
//   • Guardrails enforce safety bounds before and during execution
//   • Perception policy controls how the Eye gathers evidence (UIA tree vs. screenshot)
//
// The Eye's dual-perception model:
//   Structural capture (UIA tree for desktop) is fast, deterministic,
//   and sufficient for most assertions. Visual capture (screenshot → LLM vision) is
//   reserved for layout verification, rendering bugs, and complex visual state.
//   The engine auto-selects the cheapest sufficient mode per step context.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// TestPack v1 — the top-level container for a bounded, repeatable test program.
/// Contains scope, goals, constraints, journeys, and execution policies.
/// </summary>
public class TestPack
{
    /// <summary>Schema version for forward compatibility.</summary>
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>Unique pack identifier.</summary>
    public string PackId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable pack name.</summary>
    public string PackName { get; set; } = "Untitled Pack";

    /// <summary>When this pack was created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Automation targets (desktop apps) this pack tests against.</summary>
    public List<PackTarget> Targets { get; set; } = [];

    /// <summary>Instructions, project context, and documentation sources.</summary>
    public PackInput Inputs { get; set; } = new();

    /// <summary>Safety guardrails enforced before and during execution.</summary>
    public PackGuardrails Guardrails { get; set; } = new();

    /// <summary>Parameterized data profiles for test input variation.</summary>
    public List<DataProfile> DataProfiles { get; set; } = [];

    /// <summary>Coverage strategy: breadth vs. depth, required categories.</summary>
    public CoveragePlan CoveragePlan { get; set; } = new();

    /// <summary>User-meaningful test scenarios (PM perspective).</summary>
    public List<Journey> Journeys { get; set; } = [];

    /// <summary>Deterministic step sequences (SDET perspective). Referenced by journeys.</summary>
    public List<TestFlow> Flows { get; set; } = [];

    /// <summary>Execution mode, artifact capture, and reporting configuration.</summary>
    public PackExecutionConfig Execution { get; set; } = new();
}

// ═══════════════════════════════════════════════════════════════════════════════════
// TARGETS — What the pack tests against.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The kind of automation target.
/// </summary>
public enum TargetKind
{
    /// <summary>Windows desktop application (Desktop UIA backend).</summary>
    Desktop
}

/// <summary>
/// An automation target — a desktop app the pack interacts with.
/// </summary>
public class PackTarget
{
    /// <summary>Unique identifier referenced by flows.</summary>
    public string TargetId { get; set; } = "";

    /// <summary>Target kind.</summary>
    public TargetKind Kind { get; set; }

    // ── Desktop-specific ─────────────────────────────────────────────────
    /// <summary>Process name for desktop targets.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Target lock configuration for desktop safety.</summary>
    public TargetLockConfig? TargetLock { get; set; }
}

/// <summary>
/// Target lock configuration — pins execution to a specific window/process.
/// </summary>
public class TargetLockConfig
{
    /// <summary>Whether target lock is enforced.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Window title substring to match for lock.</summary>
    public string? WindowTitleContains { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// INPUTS — What the pack needs to know.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pack inputs: instructions, project context, and documentation sources.
/// Fed to the Planner prompt for journey generation.
/// </summary>
public class PackInput
{
    /// <summary>High-level test instructions (natural language).</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Structured project context for the Planner.</summary>
    public ProjectContext ProjectContext { get; set; } = new();

    /// <summary>Documentation sources (acceptance criteria, specs, etc.).</summary>
    public List<DocumentationSource> DocumentationSources { get; set; } = [];
}

/// <summary>
/// Structured context about the project being tested.
/// Helps the Planner generate relevant journeys.
/// </summary>
public class ProjectContext
{
    /// <summary>Feature list for coverage planning.</summary>
    public List<string> FeatureList { get; set; } = [];

    /// <summary>Known screens/views for the target application.</summary>
    public List<string> Routes { get; set; } = [];

    /// <summary>Known risk areas (e.g., "Deletion", "Payment").</summary>
    public List<string> KnownRisks { get; set; } = [];

    /// <summary>UI notes for the compiler (e.g., "Submit disabled until all fields valid").</summary>
    public List<string> UiNotes { get; set; } = [];
}

/// <summary>
/// A source document provided as context for test generation.
/// </summary>
public class DocumentationSource
{
    /// <summary>Source type: "text", "markdown", "url".</summary>
    public string Type { get; set; } = "text";

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Content (inline text or URL).</summary>
    public string Content { get; set; } = "";
}

// ═══════════════════════════════════════════════════════════════════════════════════
// GUARDRAILS — Safety bounds enforced before and during execution.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Safety mode level.
/// </summary>
public enum SafetyMode
{
    /// <summary>All guardrails enforced. No exceptions.</summary>
    Strict,
    /// <summary>Guardrails enforced with configurable overrides.</summary>
    Standard,
    /// <summary>Minimal guardrails. Developer mode only.</summary>
    Permissive
}

/// <summary>
/// Vision/perception fallback policy.
/// </summary>
public enum VisionFallbackPolicy
{
    /// <summary>Vision fallback is completely disabled.</summary>
    Disabled,
    /// <summary>Vision fallback is allowed but flagged as a warning.</summary>
    AllowedButWarning,
    /// <summary>Vision fallback is used freely (not recommended).</summary>
    AllowedSilent
}

/// <summary>
/// Pack-level safety guardrails. Validated before any execution begins.
/// Maps to existing safety infrastructure: kill switch, target lock, process allowlist.
/// </summary>
public class PackGuardrails
{
    /// <summary>Safety enforcement level.</summary>
    public SafetyMode SafetyMode { get; set; } = SafetyMode.Strict;

    /// <summary>Process names allowed for desktop automation.</summary>
    public List<string> AllowlistedProcesses { get; set; } = [];

    /// <summary>Actions that are absolutely forbidden (e.g., "delete", "purchase").</summary>
    public List<string> ForbiddenActions { get; set; } = [];

    /// <summary>Maximum total runtime in minutes. 0 = no limit.</summary>
    public int MaxRuntimeMinutes { get; set; } = 45;

    /// <summary>Maximum number of journeys in this pack.</summary>
    public int MaxJourneys { get; set; } = 20;

    /// <summary>Maximum total steps across all flows.</summary>
    public int MaxTotalSteps { get; set; } = 800;

    /// <summary>Maximum steps in a single flow.</summary>
    public int MaxStepsPerFlow { get; set; } = 80;

    /// <summary>Stop execution after this many journey failures. 0 = no limit.</summary>
    public int MaxFailuresBeforeStop { get; set; } = 5;

    /// <summary>Require target lock for desktop flows.</summary>
    public bool RequireTargetLockForDesktop { get; set; } = true;

    /// <summary>Actions requiring explicit user confirmation before execution.</summary>
    public List<string> RequireUserConfirmationFor { get; set; } = [];

    /// <summary>Policy for vision/screenshot fallback when structural perception fails.</summary>
    public VisionFallbackPolicy VisionFallbackPolicy { get; set; } = VisionFallbackPolicy.AllowedButWarning;

    /// <summary>Retry policy for transient failures.</summary>
    public PackRetryPolicy RetryPolicy { get; set; } = new();

    /// <summary>Perception policy — controls how the Eye gathers information.</summary>
    public PerceptionPolicy Perception { get; set; } = new();
}

/// <summary>
/// Retry policy for transient step failures.
/// </summary>
public class PackRetryPolicy
{
    /// <summary>Maximum retries per step before marking as failed.</summary>
    public int MaxRetriesPerStep { get; set; } = 1;

    /// <summary>Failure types eligible for retry.</summary>
    public List<string> RetryOn { get; set; } = ["timeout", "transient_overlay"];
}

// ═══════════════════════════════════════════════════════════════════════════════════
// EYE — Dual-perception model (structural + visual).
//
// The Eye is the sensory component of the Hand-Eye-Brain architecture.
// It gathers evidence about the current state of the application under test.
//
// Two perception channels:
//   Structural — UIA tree snippet (desktop)
//     • Fast, deterministic, machine-readable
//     • Perfect for text content, element state, form values, tree structure
//     • Low cognitive load for the Brain (LLM or assertion engine)
//     • No ambiguity — you see exactly what the runtime sees
//
//   Visual — Screenshot capture (pixel-level)
//     • Layout, rendering, visual regressions, canvas/video content
//     • Required when structural data doesn't convey the full picture
//     • Higher cost (image processing, LLM vision API if needed)
//     • Can reveal issues invisible to structural inspection (z-index, opacity, overflow)
//
// The engine auto-selects the cheapest sufficient channel:
//   • assert_text on a desktop element → structural (UIA text content)
//   • assert_exists on a desktop button → structural (UIA tree lookup)
//   • verify layout hasn't shifted → visual (screenshot comparison)
//   • verify toast notification appearance → visual (transient, may not be in UIA tree)
//   • complex form state validation → structural (read all field values at once)
//   • visual regression check → visual (pixel diff)
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// How the Eye captures information about the application state.
/// The engine selects the cheapest sufficient mode per step.
/// </summary>
public enum PerceptionMode
{
    /// <summary>
    /// Structural inspection only: UIA tree for desktop.
    /// Fast, deterministic, zero vision cost. Sufficient for most assertions.
    /// </summary>
    Structural,

    /// <summary>
    /// Visual capture only: screenshot (+ optional LLM vision analysis).
    /// Pixel-level fidelity. Required for layout/rendering verification.
    /// </summary>
    Visual,

    /// <summary>
    /// Structural first, fall back to visual if structural is insufficient.
    /// Best default — gets deterministic data when possible, escalates when needed.
    /// </summary>
    StructuralFirst,

    /// <summary>
    /// Both structural and visual captured together. Merges insights.
    /// Most thorough but most expensive. Use for deep investigation or triage.
    /// </summary>
    Dual,

    /// <summary>
    /// Engine auto-selects based on action type, assertion type, and backend.
    /// Recommended default — minimizes cost while maintaining coverage.
    /// </summary>
    Auto
}

/// <summary>
/// Policy governing how the Eye perceives application state.
/// Controls the trade-off between cost/speed and perception depth.
/// </summary>
public class PerceptionPolicy
{
    /// <summary>
    /// Default perception mode for evidence gathering.
    /// Auto is recommended — the engine picks structural vs. visual per step context.
    /// </summary>
    public PerceptionMode DefaultMode { get; set; } = PerceptionMode.Auto;

    /// <summary>
    /// Perception mode used when a step fails (typically more thorough).
    /// Dual mode captures both structural and visual for maximum triage data.
    /// </summary>
    public PerceptionMode OnFailureMode { get; set; } = PerceptionMode.Dual;

    /// <summary>
    /// Perception mode used when specifically verifying visual/layout correctness.
    /// </summary>
    public PerceptionMode ForVisualAssertions { get; set; } = PerceptionMode.Visual;

    /// <summary>
    /// For desktop targets: maximum depth of UIA subtree to capture.
    /// </summary>
    public int UiaTreeDepth { get; set; } = 3;

    /// <summary>
    /// When Auto mode is active, these action types always use structural perception.
    /// Default: assertion actions (cheap, deterministic check is sufficient).
    /// </summary>
    public List<StepAction> ForceStructuralFor { get; set; } =
    [
        StepAction.AssertExists,
        StepAction.AssertNotExists,
        StepAction.AssertText,
        StepAction.AssertWindow,
        StepAction.Wait
    ];

    /// <summary>
    /// When Auto mode is active, these action types always capture visual evidence.
    /// Default: screenshot action (obviously), and navigation (to capture page render).
    /// </summary>
    public List<StepAction> ForceVisualFor { get; set; } =
    [
        StepAction.Screenshot
    ];
}

// ═══════════════════════════════════════════════════════════════════════════════════
// DATA PROFILES — Parameterized test inputs.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A named set of input values for test parameterization.
/// Flows reference profiles by ID to inject different data per scenario.
/// </summary>
public class DataProfile
{
    /// <summary>Unique profile identifier.</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>Human-readable description of this data set.</summary>
    public string Description { get; set; } = "";

    /// <summary>Key-value pairs of test data.</summary>
    public Dictionary<string, string> Values { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════════════
// COVERAGE PLAN — What the pack aims to cover.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Coverage strategy: how much breadth vs. depth, and required test categories.
/// </summary>
public class CoveragePlan
{
    /// <summary>Weight given to breadth (covering more features). 0.0–1.0.</summary>
    public double BreadthWeight { get; set; } = 0.6;

    /// <summary>Weight given to depth (thorough testing per feature). 0.0–1.0.</summary>
    public double DepthWeight { get; set; } = 0.4;

    /// <summary>Categories that must be covered (e.g., "happy_path", "validation").</summary>
    public List<string> RequiredCategories { get; set; } = ["happy_path", "validation", "navigation"];

    /// <summary>Budget for exploratory testing beyond required coverage.</summary>
    public ExplorationBudget ExplorationBudget { get; set; } = new();
}

/// <summary>
/// Budget for exploratory/extra journeys beyond required coverage.
/// </summary>
public class ExplorationBudget
{
    /// <summary>Number of additional exploratory journeys allowed.</summary>
    public int ExtraJourneys { get; set; } = 5;

    /// <summary>Focus areas for exploration (e.g., "recent_changes", "high_risk").</summary>
    public List<string> Focus { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════════════
// JOURNEYS — User-meaningful test scenarios (PM perspective).
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A user-meaningful test scenario. Contains one or more flows.
/// Desktop journeys execute flows via UIA backend.
/// </summary>
public class Journey
{
    /// <summary>Unique journey identifier (e.g., "J01").</summary>
    public string JourneyId { get; set; } = "";

    /// <summary>Human-readable title describing the user scenario.</summary>
    public string Title { get; set; } = "";

    /// <summary>Priority: "p0" (critical), "p1" (high), "p2" (medium), "p3" (low).</summary>
    public string Priority { get; set; } = "p1";

    /// <summary>Tags for categorization and filtering.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Conditions that define journey success.</summary>
    public List<JourneySuccessCriterion> SuccessCriteria { get; set; } = [];

    /// <summary>Ordered flow references. Executed sequentially — hybrid = interleaved backends.</summary>
    public List<FlowRef> Flows { get; set; } = [];

    /// <summary>Data profile IDs to use for this journey.</summary>
    public List<string> DataProfileIds { get; set; } = [];

    /// <summary>
    /// Perception mode override for this journey. Null = use pack-level default.
    /// Useful for setting visual mode on journeys that test layout/rendering.
    /// </summary>
    public PerceptionMode? PerceptionOverride { get; set; }
}

/// <summary>
/// A reference to a flow within a journey's execution order.
/// </summary>
public class FlowRef
{
    /// <summary>Flow ID to execute (must match a flow in the pack's Flows list).</summary>
    public string FlowRefId { get; set; } = "";
}

/// <summary>
/// The type of success criterion — which perception channel evaluates it.
/// </summary>
public enum SuccessCriterionType
{
    /// <summary>Check text content via structural perception (UIA).</summary>
    StructuralText,
    /// <summary>Check element existence via structural perception (UIA).</summary>
    StructuralElement,
    /// <summary>Check text via UIA tree.</summary>
    DesktopText,
    /// <summary>Check element existence via UIA tree.</summary>
    DesktopElement,
    /// <summary>Visual verification — requires screenshot capture.</summary>
    VisualMatch,
    /// <summary>Custom assertion expression.</summary>
    Custom
}

/// <summary>
/// A measurable condition that defines journey success.
/// The criterion type determines which perception channel is used for evaluation.
/// </summary>
public class JourneySuccessCriterion
{
    /// <summary>What kind of check this is (structural text, visual, etc.).</summary>
    public SuccessCriterionType Type { get; set; }

    /// <summary>Selector kind for the target element.</summary>
    public string? SelectorKind { get; set; }

    /// <summary>Selector value.</summary>
    public string? SelectorValue { get; set; }

    /// <summary>Expected text content (substring match).</summary>
    public string? Contains { get; set; }

    /// <summary>Expected exact text.</summary>
    public new string? Equals { get; set; }

    /// <summary>Description of what this criterion verifies.</summary>
    public string? Description { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// EXECUTION CONFIG — How the pack runs.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Execution mode for the pack.
/// </summary>
public enum PackExecutionMode
{
    /// <summary>Run all journeys in order.</summary>
    Batch,
    /// <summary>Run one journey at a time, wait for user confirmation between.</summary>
    Interactive,
    /// <summary>Dry run — validate everything but don't execute actions.</summary>
    DryRun
}

/// <summary>
/// Execution configuration: mode, artifact policies, reporting.
/// </summary>
public class PackExecutionConfig
{
    /// <summary>Execution mode.</summary>
    public PackExecutionMode Mode { get; set; } = PackExecutionMode.Batch;

    /// <summary>When to capture artifacts (screenshots, traces, snapshots).</summary>
    public ArtifactPolicy ArtifactPolicy { get; set; } = new();

    /// <summary>Reporting configuration.</summary>
    public ReportingConfig Reporting { get; set; } = new();
}

/// <summary>
/// When to capture each type of evidence artifact.
/// </summary>
public enum CapturePolicy
{
    /// <summary>Never capture.</summary>
    Never,
    /// <summary>Capture only on failure.</summary>
    OnFail,
    /// <summary>Capture on every step.</summary>
    Always
}

/// <summary>
/// Policy governing what evidence artifacts are captured and when.
/// Integrates with the Eye's perception model — structural captures are cheap,
/// visual captures are reserved per policy.
/// </summary>
public class ArtifactPolicy
{
    /// <summary>When to capture screenshots (visual perception).</summary>
    public CapturePolicy Screenshots { get; set; } = CapturePolicy.OnFail;

    /// <summary>
    /// When to capture UIA tree snippets (structural perception — desktop).
    /// Cheap and fast — recommended on_fail at minimum.
    /// </summary>
    public CapturePolicy UiaTreeSnapshot { get; set; } = CapturePolicy.OnFail;
}

/// <summary>
/// Reporting configuration for pack execution.
/// </summary>
public class ReportingConfig
{
    /// <summary>Report format version.</summary>
    public string Format { get; set; } = "pack_report_v1";

    /// <summary>Include ranked fix queue in the report.</summary>
    public bool IncludeFixQueue { get; set; } = true;

    /// <summary>Include coverage map in the report.</summary>
    public bool IncludeCoverageMap { get; set; } = true;

    /// <summary>Include perception channel metadata per step (which Eye mode was used).</summary>
    public bool IncludePerceptionMetadata { get; set; } = true;
}

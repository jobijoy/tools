namespace IdolClick.Models;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK REPORT v1 — Structured execution report for AI consumer + human triage.
//
// Produced by PackReportBuilder after PackRunnerService completes execution.
// Designed for dual consumption:
//   • AI coding agents — read fix_queue, evidence paths, repro steps → generate patches
//   • Human testers — read failure cards, screenshots, coverage gaps → file bugs
//
// Evidence always links to artifacts, never embedded blobs.
// Each failure and warning records which perception channel (Eye mode) was used,
// so consumers know whether they're looking at structural or visual evidence.
//
// The fix_queue is the actionable output — ranked failures with suspected causes
// and recommended next investigation steps.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PackReport v1 — the complete execution report for a TestPack run.
/// Contains summary statistics, coverage map, failures, warnings, and ranked fix queue.
/// </summary>
public class PackReport
{
    /// <summary>Report schema version.</summary>
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>Pack ID that was executed.</summary>
    public string PackId { get; set; } = "";

    /// <summary>Pack name.</summary>
    public string PackName { get; set; } = "";

    /// <summary>Unique run identifier.</summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>When execution started (UTC).</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When execution ended (UTC).</summary>
    public DateTime EndedAtUtc { get; set; }

    /// <summary>Total execution time in milliseconds.</summary>
    public long TotalTimeMs => (long)(EndedAtUtc - StartedAtUtc).TotalMilliseconds;

    /// <summary>Execution environment metadata.</summary>
    public PackEnvironment Environment { get; set; } = new();

    /// <summary>High-level execution statistics.</summary>
    public PackSummary Summary { get; set; } = new();

    /// <summary>Coverage map: which areas were tested and their status.</summary>
    public List<CoverageMapEntry> CoverageMap { get; set; } = [];

    /// <summary>Detailed failure records with evidence.</summary>
    public List<PackFailure> Failures { get; set; } = [];

    /// <summary>Warnings (e.g., vision fallback, perception downgrade).</summary>
    public List<PackWarning> Warnings { get; set; } = [];

    /// <summary>
    /// Ranked fix queue — the actionable output for coding agents and developers.
    /// Ordered by severity, frequency, and evidence completeness.
    /// </summary>
    public List<FixQueueItem> FixQueue { get; set; } = [];

    /// <summary>Per-flow execution reports (the raw data underlying this summary).</summary>
    public List<ExecutionReport> FlowReports { get; set; } = [];

    /// <summary>Per-journey execution results.</summary>
    public List<JourneyResult> JourneyResults { get; set; } = [];

    /// <summary>
    /// Perception usage statistics — how often each Eye mode was used.
    /// Helps assess whether the perception policy is well-tuned.
    /// </summary>
    public PerceptionStats PerceptionStats { get; set; } = new();
}

// ═══════════════════════════════════════════════════════════════════════════════════
// ENVIRONMENT
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Execution environment metadata for reproducibility.
/// </summary>
public class PackEnvironment
{
    /// <summary>OS version string.</summary>
    public string OsVersion { get; set; } = "";

    /// <summary>Machine name.</summary>
    public string Machine { get; set; } = "";

    /// <summary>Backend versions used during execution.</summary>
    public Dictionary<string, string> BackendVersions { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════════════
// SUMMARY
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// High-level execution statistics.
/// </summary>
public class PackSummary
{
    /// <summary>Total journeys in the pack.</summary>
    public int JourneysTotal { get; set; }

    /// <summary>Journeys that passed all success criteria.</summary>
    public int JourneysPassed { get; set; }

    /// <summary>Journeys with at least one failure.</summary>
    public int JourneysFailed { get; set; }

    /// <summary>Journeys that were skipped (due to stop rules or dependencies).</summary>
    public int JourneysSkipped { get; set; }

    /// <summary>Total flows executed.</summary>
    public int FlowsTotal { get; set; }

    /// <summary>Total steps executed.</summary>
    public int StepsTotal { get; set; }

    /// <summary>Total warnings across all flows.</summary>
    public int WarningsTotal { get; set; }

    /// <summary>Overall result: "passed", "failed", "partial", "aborted".</summary>
    public string OverallResult { get; set; } = "pending";
}

// ═══════════════════════════════════════════════════════════════════════════════════
// JOURNEY RESULTS
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Execution result for a single journey.
/// </summary>
public class JourneyResult
{
    /// <summary>Journey identifier.</summary>
    public string JourneyId { get; set; } = "";

    /// <summary>Journey title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Result: "passed", "failed", "skipped", "aborted".</summary>
    public string Result { get; set; } = "pending";

    /// <summary>Flow IDs executed in this journey.</summary>
    public List<string> FlowIds { get; set; } = [];

    /// <summary>Total execution time for this journey in milliseconds.</summary>
    public long TotalTimeMs { get; set; }

    /// <summary>Success criteria evaluation results.</summary>
    public List<CriterionResult> CriteriaResults { get; set; } = [];
}

/// <summary>
/// Result of evaluating a single success criterion.
/// </summary>
public class CriterionResult
{
    /// <summary>Whether the criterion was met.</summary>
    public bool Passed { get; set; }

    /// <summary>Description of the criterion.</summary>
    public string? Description { get; set; }

    /// <summary>Expected value.</summary>
    public string? Expected { get; set; }

    /// <summary>Observed value.</summary>
    public string? Observed { get; set; }

    /// <summary>Which perception channel was used to evaluate this criterion.</summary>
    public PerceptionMode PerceptionUsed { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// FAILURES — Detailed failure records with evidence.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A detailed failure record with evidence links.
/// </summary>
public class PackFailure
{
    /// <summary>Journey where the failure occurred.</summary>
    public string JourneyId { get; set; } = "";

    /// <summary>Flow where the failure occurred.</summary>
    public string FlowId { get; set; } = "";

    /// <summary>Step order (1-based) where the failure occurred.</summary>
    public int StepOrder { get; set; }

    /// <summary>Type of failure: "assertion_failed", "element_not_found", "timeout", "error".</summary>
    public string FailureType { get; set; } = "";

    /// <summary>What was expected.</summary>
    public string Expected { get; set; } = "";

    /// <summary>What was observed.</summary>
    public string Observed { get; set; } = "";

    /// <summary>Evidence artifacts linked to this failure.</summary>
    public FailureEvidence Evidence { get; set; } = new();

    /// <summary>Reproduction information.</summary>
    public ReproInfo Repro { get; set; } = new();

    /// <summary>Which perception channel captured the evidence.</summary>
    public PerceptionMode PerceptionUsed { get; set; }
}

/// <summary>
/// Evidence artifacts for a failure — always file paths, never embedded blobs.
/// Includes both structural and visual evidence when the Eye captured both.
/// </summary>
public class FailureEvidence
{
    /// <summary>Screenshot file path (visual perception).</summary>
    public string? Screenshot { get; set; }

    /// <summary>
    /// UIA tree snippet file path (structural perception — desktop).
    /// Contains the UIA subtree around the failed element.
    /// </summary>
    public string? UiaTreeSnapshot { get; set; }

    /// <summary>
    /// Accessibility tree snapshot file path.
    /// Lighter than full tree dump, includes element roles/names/states.
    /// </summary>
    public string? AccessibilityTree { get; set; }

    /// <summary>Network log (HAR) file path.</summary>
    public string? NetworkLog { get; set; }
}

/// <summary>
/// Reproduction information for a failure.
/// </summary>
public class ReproInfo
{
    /// <summary>Number of execution attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Number of attempts that failed (consistency check).</summary>
    public int FailedAttempts { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// WARNINGS
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A non-blocking warning from execution.
/// </summary>
public class PackWarning
{
    /// <summary>Flow where the warning occurred.</summary>
    public string FlowId { get; set; } = "";

    /// <summary>Step order (1-based).</summary>
    public int StepOrder { get; set; }

    /// <summary>Warning code: "vision_fallback_used", "perception_downgrade", "slow_step".</summary>
    public string Code { get; set; } = "";

    /// <summary>Human-readable details.</summary>
    public string Details { get; set; } = "";

    /// <summary>Confidence score (for vision fallback warnings).</summary>
    public double? Confidence { get; set; }

    /// <summary>Which perception mode was active when the warning occurred.</summary>
    public PerceptionMode? PerceptionMode { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// FIX QUEUE — Ranked actionable items for coding agents.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A ranked fix queue item — the primary actionable output for coding agents.
/// Contains failure summary, evidence refs, suspected causes, and next steps.
/// </summary>
public class FixQueueItem
{
    /// <summary>Priority rank (1 = highest priority).</summary>
    public int Rank { get; set; }

    /// <summary>Category: "ui_regression", "logic_error", "flaky_selector", "timing_issue".</summary>
    public string Category { get; set; } = "";

    /// <summary>Concise title describing the issue.</summary>
    public string Title { get; set; } = "";

    /// <summary>References to failure entries (by index in the Failures array).</summary>
    public List<string> EvidenceRefs { get; set; } = [];

    /// <summary>Suspected root causes (tags for agent investigation).</summary>
    public List<string> LikelyCauses { get; set; } = [];

    /// <summary>Recommended next investigation steps.</summary>
    public List<string> RecommendedNextChecks { get; set; } = [];

    /// <summary>
    /// Fix packet content — a structured summary that coding agents can consume
    /// to understand and fix the issue without reading the full report.
    /// </summary>
    public FixPacket? FixPacket { get; set; }
}

/// <summary>
/// A self-contained fix packet for a coding agent to consume.
/// Contains everything needed to understand and fix one issue.
/// </summary>
public class FixPacket
{
    /// <summary>One-line failure summary.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Evidence file paths.</summary>
    public List<string> EvidenceFilePaths { get; set; } = [];

    /// <summary>Suspected cause tags.</summary>
    public List<string> SuspectedCauses { get; set; } = [];

    /// <summary>Minimal reproduction steps (extracted from the flow).</summary>
    public List<string> ReproSteps { get; set; } = [];

    /// <summary>Suggested source files to inspect (if repo metadata is available).</summary>
    public List<string> SuggestedFilesToInspect { get; set; } = [];

    /// <summary>
    /// Which perception channels provided evidence for this fix.
    /// Helps the agent know whether to look at UIA tree or screenshots.
    /// </summary>
    public List<string> EvidenceChannels { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════════════
// PERCEPTION STATS — How the Eye was used during execution.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Statistics about perception channel usage during pack execution.
/// Helps assess whether the perception policy is well-tuned.
/// </summary>
public class PerceptionStats
{
    /// <summary>Steps where structural perception was used (UIA tree).</summary>
    public int StructuralCaptures { get; set; }

    /// <summary>Steps where visual perception was used (screenshot).</summary>
    public int VisualCaptures { get; set; }

    /// <summary>Steps where both channels were used (dual mode).</summary>
    public int DualCaptures { get; set; }

    /// <summary>Steps where structural was attempted but fell back to visual.</summary>
    public int StructuralToVisualFallbacks { get; set; }

    /// <summary>Total perception cost estimate (structural = 1, visual = 10, dual = 11).</summary>
    public int EstimatedCostUnits { get; set; }
}

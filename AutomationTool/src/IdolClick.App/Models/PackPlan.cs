namespace IdolClick.Models;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK PLAN v1 — Output of the Planner phase (Brain: PM + Tester perspective).
//
// The PackPlan is the intermediate artifact between natural language instructions
// and a fully compiled TestPack. It captures:
//   • What to test (journeys with titles, priorities, success criteria)
//   • How thoroughly (coverage map, risk analysis)
//   • What data is needed (required data profiles)
//
// The Planner generates this from:
//   • Pack instructions (natural language)
//   • Project context (features, routes, risks, UI notes)
//   • Guardrails (what's forbidden, what's bounded)
//   • Capabilities (what actions/assertions the engine supports)
//
// The Compiler then takes this plan and produces deterministic TestFlow steps.
//
// No low-level steps here — this is the PM/tester brain, not the SDET brain.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PackPlan v1 — the Planner's output. Proposed journeys, coverage map, and risk analysis.
/// Input to the Compiler phase which produces deterministic flows.
/// </summary>
public class PackPlan
{
    /// <summary>Schema version for forward compatibility.</summary>
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>Pack name (carried through to the final TestPack).</summary>
    public string PackName { get; set; } = "";

    /// <summary>Proposed journeys with priorities, tags, and success criteria.</summary>
    public List<PlannedJourney> Journeys { get; set; } = [];

    /// <summary>Coverage map: which areas are covered by which journeys.</summary>
    public List<CoverageMapEntry> CoverageMap { get; set; } = [];

    /// <summary>Identified risks and their mitigations.</summary>
    public List<RiskEntry> Risks { get; set; } = [];

    /// <summary>Suggested data profiles needed for the proposed journeys.</summary>
    public List<SuggestedDataProfile> SuggestedDataProfiles { get; set; } = [];

    /// <summary>
    /// Perception recommendations from the Planner — which journeys need visual
    /// perception vs. structural-only. Helps the Compiler set per-journey overrides.
    /// </summary>
    public List<PerceptionRecommendation> PerceptionRecommendations { get; set; } = [];
}

/// <summary>
/// A journey proposed by the Planner. Contains intent and success criteria
/// but no low-level steps (those come from the Compiler).
/// </summary>
public class PlannedJourney
{
    /// <summary>Unique journey identifier (e.g., "J01").</summary>
    public string JourneyId { get; set; } = "";

    /// <summary>Human-readable title describing the user scenario.</summary>
    public string Title { get; set; } = "";

    /// <summary>Priority: "p0" (critical), "p1" (high), "p2" (medium), "p3" (low).</summary>
    public string Priority { get; set; } = "p1";

    /// <summary>Tags for categorization (e.g., "happy_path", "validation", "hybrid").</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Coverage areas this journey addresses.</summary>
    public List<string> CoverageAreas { get; set; } = [];

    /// <summary>Measurable success criteria.</summary>
    public List<JourneySuccessCriterion> SuccessCriteria { get; set; } = [];

    /// <summary>Data profile IDs required for this journey.</summary>
    public List<string> RequiredDataProfiles { get; set; } = [];

    /// <summary>Which backends this journey needs (informs hybrid flow generation).</summary>
    public List<string> RequiredBackends { get; set; } = [];

    /// <summary>
    /// Planner's recommended perception mode for this journey.
    /// Null = let the Compiler/engine decide.
    /// </summary>
    public PerceptionMode? RecommendedPerception { get; set; }
}

/// <summary>
/// Maps a coverage area to the journeys that test it.
/// </summary>
public class CoverageMapEntry
{
    /// <summary>The functional area being covered.</summary>
    public string Area { get; set; } = "";

    /// <summary>Journey IDs that cover this area.</summary>
    public List<string> Journeys { get; set; } = [];

    /// <summary>Coverage status: "ok", "gap", "partial".</summary>
    public string Status { get; set; } = "ok";
}

/// <summary>
/// A risk identified by the Planner with its mitigation strategy.
/// </summary>
public class RiskEntry
{
    /// <summary>Risk tag (e.g., "destructive", "data_loss", "external_dependency").</summary>
    public string Tag { get; set; } = "";

    /// <summary>Description of the risk.</summary>
    public string Description { get; set; } = "";

    /// <summary>How the guardrails or test design mitigates this risk.</summary>
    public string Mitigation { get; set; } = "";
}

/// <summary>
/// A data profile suggested by the Planner for test coverage.
/// </summary>
public class SuggestedDataProfile
{
    /// <summary>Profile identifier.</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>What this data set represents.</summary>
    public string Description { get; set; } = "";

    /// <summary>Suggested key-value data.</summary>
    public Dictionary<string, string> SuggestedValues { get; set; } = [];
}

/// <summary>
/// Planner's recommendation for which perception mode to use for a journey.
/// Helps the Compiler decide when structural (UIA) is sufficient
/// vs. when visual (screenshot) evidence is needed.
/// </summary>
public class PerceptionRecommendation
{
    /// <summary>Journey ID this recommendation applies to.</summary>
    public string JourneyId { get; set; } = "";

    /// <summary>Recommended perception mode.</summary>
    public PerceptionMode RecommendedMode { get; set; } = PerceptionMode.Auto;

    /// <summary>
    /// Rationale for the recommendation (e.g., "Layout verification needed",
    /// "Text-only assertions — structural sufficient").
    /// </summary>
    public string Rationale { get; set; } = "";
}

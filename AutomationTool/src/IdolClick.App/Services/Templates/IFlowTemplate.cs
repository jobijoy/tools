using IdolClick.Models;

namespace IdolClick.Services.Templates;

// ═══════════════════════════════════════════════════════════════════════════════════
// FLOW TEMPLATE INTERFACE — Contract for deterministic flow generators.
//
// Templates are the structured automation compiler's core unit.
// Each template:
//   • Declares what intent it handles (IntentKind)
//   • Declares required + optional slots
//   • Declares RiskLevel and Maturity
//   • Can check if it can handle a parsed intent
//   • Builds a complete TestFlow from filled slots
//
// Design constraints (RA-validated):
//   • Templates depend ONLY on Models (TestFlow, TestStep, StepAction, etc.)
//   • Templates do NOT reference AgentEndpoints, FlowEndpoints, or any API layer
//   • Templates do NOT call InspectWindow or any runtime service
//   • Templates emit TestFlow only — no backend mutations
//   • Templates are deterministic — same input → same output
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A flow template that can build a complete TestFlow from a parsed intent.
/// Implementations must be stateless and deterministic.
/// </summary>
public interface IFlowTemplate
{
    /// <summary>Unique template identifier (e.g., "browser-search", "launch-app").</summary>
    string TemplateId { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Short description of what this template automates.</summary>
    string Description { get; }

    /// <summary>The primary intent kind this template handles.</summary>
    IntentKind IntentKind { get; }

    /// <summary>Named slots that MUST be filled for this template to work.</summary>
    IReadOnlyList<string> RequiredSlots { get; }

    /// <summary>Named slots that improve the flow but aren't mandatory.</summary>
    IReadOnlyList<string> OptionalSlots { get; }

    /// <summary>
    /// Risk level. High-risk templates require confirmation even at high confidence.
    /// </summary>
    RiskLevel RiskLevel { get; }

    /// <summary>
    /// Maturity classification. Experimental templates default to confirmation mode.
    /// </summary>
    TemplateMaturity Maturity { get; }

    /// <summary>
    /// Quick check: can this template handle the given intent?
    /// Checked before scoring. Return false to skip scoring.
    /// </summary>
    bool CanHandle(IntentParse intent);

    /// <summary>
    /// Build a complete, validated TestFlow from the parsed intent's slots.
    /// Caller guarantees all required slots are present.
    /// </summary>
    TestFlow BuildFlow(IntentParse intent);
}

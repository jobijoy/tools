using System.ComponentModel;
using System.Text.Json;
using IdolClick.Models;
using Microsoft.Extensions.AI;

namespace IdolClick.Services.Packs;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK AGENT TOOLS — Brain-callable functions for LLM-driven TestPack operations.
//
// These tools extend the Brain's capabilities from single-flow execution
// (AgentTools) to full orchestrated test campaigns (TestPacks).
//
// The Brain can:
//   1. PlanTestPack    — Generate a test plan from natural language instructions
//   2. CompileTestPack — Compile the plan into executable flows
//   3. RunTestPack     — Execute a compiled pack and get results
//   4. RunFullPipeline — One-shot: plan → compile → execute → report
//   5. GetFixQueue     — Get ranked fix items for coding agents
//   6. GetConfidence   — Get the confidence score breakdown
//   7. AnalyzeReport   — Get a human-readable analysis of the latest report
//
// SPRINT 2+ STUBS (not yet implemented):
//   • CrossValidateWithMCP — Connect to external MCPs for cross-validation
//   • CompareWithDesign    — Compare live UI against Figma/design specs
//   • SuggestImprovements  — AI-powered system improvement suggestions
//
// All tools are stateless — they operate on serializable inputs/outputs.
// State is held in the Orchestrator result, not in the tool layer.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// LLM-callable tools for the TestPack system.
/// Registered as function-calling tools alongside AgentTools.
/// </summary>
public class PackAgentTools
{
    private readonly LogService _log;
    private readonly PackOrchestrator _orchestrator;
    private readonly Func<IChatClient?> _chatClientFactory;

    // Holds the latest pipeline result for follow-up queries
    private PackOrchestratorResult? _lastPipelineResult;
    private TestPack? _lastCompiledPack;
    private PackPlan? _lastPlan;

    public PackAgentTools(
        LogService log,
        PackOrchestrator orchestrator,
        Func<IChatClient?> chatClientFactory)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TOOL: RUN FULL PIPELINE
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Runs the complete TestPack pipeline: Plan → Compile → Execute → Report. " +
        "Accepts a TestPack JSON with instructions, targets, and guardrails. " +
        "Returns a complete report with confidence score, failures, fix queue, and coverage map. " +
        "This is the primary tool for orchestrated testing — use it when the user wants a thorough test campaign.")]
    public async Task<string> RunFullPipeline(
        [Description("Complete TestPack JSON string with packName, targets, inputs (instructions, features), guardrails, and optionally journeys/flows")] string packJson)
    {
        var client = _chatClientFactory();
        if (client == null)
            return Error("No AI client available. Configure AI settings first.");

        try
        {
            var pack = JsonSerializer.Deserialize<TestPack>(packJson, FlowJson.Options);
            if (pack == null)
                return Error("Failed to parse TestPack JSON.");

            _log.Info("PackTools", $"RunFullPipeline: '{pack.PackName}' — targets: {pack.Targets.Count}");

            var result = await _orchestrator.RunFullPipelineAsync(pack, client);

            _lastPipelineResult = result;
            _lastCompiledPack = result.CompileResult?.Pack;
            _lastPlan = result.PlanResult?.Plan;

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                summary = result.GetSummary(),
                confidenceScore = result.ConfidenceScore,
                confidenceLabel = GetConfidenceLabel(result.ConfidenceScore),
                failedAtPhase = result.FailedAtPhase?.ToString(),
                errorMessage = result.ErrorMessage,
                totalDurationMs = result.TotalDurationMs,
                phases = new
                {
                    planning = result.PlanResult != null ? new
                    {
                        success = result.PlanResult.Success,
                        journeysPlanned = result.PlanResult.Plan?.Journeys.Count ?? 0,
                        coverageAreas = result.PlanResult.Plan?.CoverageMap.Count ?? 0,
                        durationMs = result.PlanResult.DurationMs
                    } : null,
                    compiling = result.CompileResult != null ? new
                    {
                        success = result.CompileResult.Success,
                        flowsCompiled = result.CompileResult.Pack?.Flows.Count ?? 0,
                        attempts = result.CompileResult.CompileAttempts,
                        validationErrors = result.CompileResult.ValidationErrors.Count,
                        durationMs = result.CompileResult.DurationMs
                    } : null,
                    execution = result.Report != null ? new
                    {
                        overallResult = result.Report.Summary.OverallResult,
                        journeysPassed = result.Report.Summary.JourneysPassed,
                        journeysFailed = result.Report.Summary.JourneysFailed,
                        journeysSkipped = result.Report.Summary.JourneysSkipped,
                        failureCount = result.Report.Failures.Count,
                        warningCount = result.Report.Warnings.Count,
                        fixQueueCount = result.Report.FixQueue.Count,
                        durationMs = result.Report.TotalTimeMs
                    } : null
                }
            }, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("PackTools", $"RunFullPipeline error: {ex.Message}");
            return Error(ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TOOL: PLAN ONLY
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Plans test journeys and coverage from a TestPack input without executing. " +
        "Returns proposed journeys, coverage map, risks, and perception recommendations. " +
        "Use this to preview what the Brain would test before committing to execution.")]
    public async Task<string> PlanTestPack(
        [Description("TestPack JSON with inputs (instructions, features, routes, risks) and targets")] string packJson)
    {
        var client = _chatClientFactory();
        if (client == null)
            return Error("No AI client available.");

        try
        {
            var pack = JsonSerializer.Deserialize<TestPack>(packJson, FlowJson.Options);
            if (pack == null)
                return Error("Failed to parse TestPack JSON.");

            var result = await _orchestrator.PlanAsync(pack, client);
            _lastPlan = result.Plan;

            if (!result.Success || result.Plan == null)
                return Error($"Planning failed. Raw response: {(result.RawResponse?.Length > 500 ? result.RawResponse[..500] : result.RawResponse)}");

            return JsonSerializer.Serialize(new
            {
                success = true,
                journeys = result.Plan.Journeys.Select(j => new
                {
                    j.JourneyId,
                    j.Title,
                    j.Priority,
                    tags = j.Tags,
                    coverageAreas = j.CoverageAreas,
                    requiredBackends = j.RequiredBackends
                }),
                coverageMap = result.Plan.CoverageMap,
                risks = result.Plan.Risks,
                perceptionRecommendations = result.Plan.PerceptionRecommendations,
                durationMs = result.DurationMs
            }, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("PackTools", $"PlanTestPack error: {ex.Message}");
            return Error(ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TOOL: GET FIX QUEUE
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Returns the ranked fix queue from the latest TestPack execution. " +
        "Each item has a rank, category, title, likely causes, and a FixPacket " +
        "that coding agents can directly consume to understand and fix the issue. " +
        "Use this after RunFullPipeline to get actionable fixes.")]
    public string GetFixQueue()
    {
        if (_lastPipelineResult?.Report == null)
            return Error("No execution results available. Run a TestPack first.");

        var report = _lastPipelineResult.Report;

        if (report.FixQueue.Count == 0)
            return JsonSerializer.Serialize(new
            {
                message = "No fixes needed — all tests passed!",
                confidenceScore = _lastPipelineResult.ConfidenceScore,
                confidenceLabel = GetConfidenceLabel(_lastPipelineResult.ConfidenceScore)
            }, FlowJson.Options);

        return JsonSerializer.Serialize(new
        {
            fixItemCount = report.FixQueue.Count,
            fixes = report.FixQueue.Select(f => new
            {
                f.Rank,
                f.Category,
                f.Title,
                f.LikelyCauses,
                f.RecommendedNextChecks,
                fixPacket = f.FixPacket != null ? new
                {
                    f.FixPacket.Summary,
                    f.FixPacket.SuspectedCauses,
                    f.FixPacket.ReproSteps,
                    f.FixPacket.EvidenceChannels,
                    evidenceFileCount = f.FixPacket.EvidenceFilePaths.Count
                } : null
            })
        }, FlowJson.Options);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TOOL: GET CONFIDENCE BREAKDOWN
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Returns the confidence score breakdown from the latest TestPack execution. " +
        "Shows journey pass rate, coverage completion, perception reliability, and warning impact. " +
        "Useful for giving the test assignee a clear quality signal.")]
    public string GetConfidenceBreakdown()
    {
        if (_lastPipelineResult?.Report == null)
            return Error("No execution results available. Run a TestPack first.");

        var report = _lastPipelineResult.Report;
        var score = _lastPipelineResult.ConfidenceScore;

        // Recalculate component scores for breakdown
        double journeyPassRate = report.Summary.JourneysTotal > 0
            ? (double)report.Summary.JourneysPassed / report.Summary.JourneysTotal : 0;

        double coverageScore = 1.0;
        if (report.CoverageMap.Count > 0)
        {
            var okCount = report.CoverageMap.Count(c => c.Status == "ok");
            coverageScore = (double)okCount / report.CoverageMap.Count;
        }

        double perceptionScore = 1.0;
        var totalPerception = report.PerceptionStats.StructuralCaptures +
                              report.PerceptionStats.VisualCaptures +
                              report.PerceptionStats.DualCaptures;
        if (totalPerception > 0)
        {
            var fallbackRate = (double)report.PerceptionStats.StructuralToVisualFallbacks / totalPerception;
            perceptionScore = 1.0 - fallbackRate;
        }

        return JsonSerializer.Serialize(new
        {
            overallScore = score,
            label = GetConfidenceLabel(score),
            components = new
            {
                journeyPassRate = new { score = journeyPassRate, weight = "60%", detail = $"{report.Summary.JourneysPassed}/{report.Summary.JourneysTotal} journeys passed" },
                coverageCompletion = new { score = coverageScore, weight = "20%", detail = $"{report.CoverageMap.Count(c => c.Status == "ok")}/{report.CoverageMap.Count} areas fully covered" },
                perceptionReliability = new { score = perceptionScore, weight = "10%", detail = $"{report.PerceptionStats.StructuralToVisualFallbacks} vision fallbacks out of {totalPerception} total" },
                warningImpact = new { weight = "10%", detail = $"{report.Summary.WarningsTotal} warnings across {report.Summary.StepsTotal} steps" }
            },
            recommendation = score switch
            {
                >= 0.9 => "HIGH confidence — system behaves as expected. Safe to proceed.",
                >= 0.7 => "MODERATE confidence — some issues detected. Review fix queue before release.",
                >= 0.5 => "LOW confidence — significant failures. Address fix queue items before release.",
                _ => "CRITICAL — serious issues detected. Do NOT release without resolving failures."
            }
        }, FlowJson.Options);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TOOL: ANALYZE REPORT
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Returns a comprehensive human-readable analysis of the latest TestPack execution. " +
        "Includes coverage map, failure summary, warning patterns, perception stats, " +
        "and improvement suggestions. Use this to give the test assignee a clear picture.")]
    public string AnalyzeReport()
    {
        if (_lastPipelineResult?.Report == null)
            return Error("No execution results available. Run a TestPack first.");

        var report = _lastPipelineResult.Report;

        return JsonSerializer.Serialize(new
        {
            summary = _lastPipelineResult.GetSummary(),

            coverageMap = report.CoverageMap.Select(c => new
            {
                c.Area,
                c.Status,
                journeyCount = c.Journeys.Count
            }),

            failureSummary = report.Failures.GroupBy(f => f.FailureType).Select(g => new
            {
                type = g.Key,
                count = g.Count(),
                affectedFlows = g.Select(f => f.FlowId).Distinct().ToList()
            }),

            warningPatterns = report.Warnings.GroupBy(w => w.Code).Select(g => new
            {
                code = g.Key,
                count = g.Count(),
                affectedFlows = g.Select(w => w.FlowId).Distinct().ToList()
            }),

            perceptionStats = new
            {
                structural = report.PerceptionStats.StructuralCaptures,
                visual = report.PerceptionStats.VisualCaptures,
                dual = report.PerceptionStats.DualCaptures,
                fallbacks = report.PerceptionStats.StructuralToVisualFallbacks,
                estimatedCost = report.PerceptionStats.EstimatedCostUnits
            },

            improvementSuggestions = GenerateImprovementSuggestions(report)
        }, FlowJson.Options);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SPRINT 2+ STUBS — Future MCP cross-validation tools
    // ═══════════════════════════════════════════════════════════════════════════════

    // These are intentionally NOT [Description]-tagged — they won't appear as tools yet.
    // They serve as architectural placeholders for the next sprint plan.

    /// <summary>
    /// [Sprint 2] Cross-validate test results with an external MCP.
    /// E.g., compare runtime UI state against a Figma design MCP,
    /// or validate data consistency against a backend API MCP.
    /// </summary>
    internal Task<string> CrossValidateWithMCP(string mcpEndpoint, string validationType)
    {
        // Stub: Sprint 2 will implement MCP client protocol + routing
        return Task.FromResult(Error("MCP cross-validation is planned for Sprint 2."));
    }

    /// <summary>
    /// [Sprint 3] Generate system improvement suggestions based on test patterns.
    /// Analyzes failure patterns, coverage gaps, and perception fallbacks
    /// to suggest architectural improvements, better selectors, etc.
    /// </summary>
    internal Task<string> SuggestSystemImprovements(string reportJson)
    {
        // Stub: Sprint 3 will connect to Brain for deep analysis
        return Task.FromResult(Error("System improvement suggestions are planned for Sprint 3."));
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    private static string GetConfidenceLabel(double score) => score switch
    {
        >= 0.9 => "HIGH",
        >= 0.7 => "MODERATE",
        >= 0.5 => "LOW",
        _ => "CRITICAL"
    };

    /// <summary>
    /// Generate improvement suggestions from report patterns.
    /// </summary>
    private static List<string> GenerateImprovementSuggestions(PackReport report)
    {
        var suggestions = new List<string>();

        // Selector stability
        var selectorFailures = report.Failures.Count(f => f.FailureType == "element_not_found");
        if (selectorFailures > 0)
            suggestions.Add($"{selectorFailures} element-not-found failures. Consider adding AutomationId/data-testid attributes to frequently-accessed elements.");

        // Timing issues
        var timeoutFailures = report.Failures.Count(f => f.FailureType == "timeout");
        if (timeoutFailures > 0)
            suggestions.Add($"{timeoutFailures} timeout failures. Review application response times or increase step timeouts.");

        // Vision fallback overuse
        if (report.PerceptionStats.StructuralToVisualFallbacks > 3)
            suggestions.Add($"{report.PerceptionStats.StructuralToVisualFallbacks} vision fallbacks detected. Improve structural selectors to reduce cost and increase reliability.");

        // Coverage gaps
        var gaps = report.CoverageMap.Count(c => c.Status is "gap" or "not_executed");
        if (gaps > 0)
            suggestions.Add($"{gaps} coverage areas have gaps. Add journeys to cover these functional areas.");

        // High warning rate
        if (report.Summary.WarningsTotal > report.Summary.StepsTotal * 0.2)
            suggestions.Add("High warning rate (>20%). Review warning patterns to identify systematic issues.");

        // No failures — suggest expansion
        if (report.Failures.Count == 0 && report.Summary.JourneysTotal > 0)
            suggestions.Add("All tests passed! Consider expanding coverage: edge cases, error scenarios, accessibility testing.");

        return suggestions;
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, FlowJson.Options);
}

using IdolClick.Models;

namespace IdolClick.Services.Packs;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK REPORT BUILDER — Aggregates per-flow execution data into a cohesive PackReport.
//
// Responsibilities:
//   1. Build CoverageMap from journey results and planned coverage
//   2. Extract Failures from StepResult records with evidence linking
//   3. Extract Warnings from StepStatus.Warning records
//   4. Generate ranked FixQueue with FixPackets for coding agents
//   5. Calculate PerceptionStats
//   6. Generate human-readable and machine-readable report summaries
//
// This builder is used in two contexts:
//   a) By PackRunnerService at the end of execution (real-time data)
//   b) By re-analysis tools that re-process existing FlowReports
//
// The FixQueue is the primary actionable output — each item contains enough
// context for a coding agent to understand and begin fixing the issue.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Builds a complete PackReport from raw execution data.
/// </summary>
public class PackReportBuilder
{
    private readonly PackReport _report;

    public PackReportBuilder(PackReport report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>
    /// Static factory: build a fully populated PackReport from execution data.
    /// </summary>
    public static PackReport BuildFrom(
        PackReport baseReport,
        TestPack pack,
        PackPlan? plan = null)
    {
        var builder = new PackReportBuilder(baseReport);
        builder.PopulateFailures(pack);
        builder.PopulateWarnings();
        builder.PopulateCoverageMap(pack, plan);
        builder.PopulatePerceptionStats();
        builder.PopulateFixQueue(pack);
        return baseReport;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FAILURES — Extract from FlowReports
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts failures from all FlowReports and populates the Failures array.
    /// Each failed step becomes a PackFailure with evidence linking.
    /// </summary>
    public void PopulateFailures(TestPack pack)
    {
        _report.Failures.Clear();

        // Map flow name → journey for attribution
        var flowToJourney = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var journey in pack.Journeys)
        {
            foreach (var flowRef in journey.Flows)
            {
                flowToJourney.TryAdd(flowRef.FlowRefId, journey.JourneyId);
            }
        }

        foreach (var flowReport in _report.FlowReports)
        {
            foreach (var step in flowReport.Steps)
            {
                if (step.Status != StepStatus.Failed && step.Status != StepStatus.Error)
                    continue;

                var failure = new PackFailure
                {
                    JourneyId = flowToJourney.GetValueOrDefault(flowReport.TestName, "unknown"),
                    FlowId = flowReport.TestName,
                    StepOrder = step.Step,
                    FailureType = ClassifyFailureType(step),
                    Expected = step.Description ?? "",
                    Observed = step.Error ?? ""
                };

                // Link screenshots from the flow report
                if (flowReport.Screenshots.Count > 0)
                {
                    failure.Evidence.Screenshot = flowReport.Screenshots
                        .LastOrDefault(); // Most recent screenshot is typically the failure
                }

                _report.Failures.Add(failure);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WARNINGS — Extract from FlowReports
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts warnings from all FlowReports (steps with StepStatus.Warning).
    /// </summary>
    public void PopulateWarnings()
    {
        _report.Warnings.Clear();
        int totalWarnings = 0;

        foreach (var flowReport in _report.FlowReports)
        {
            foreach (var step in flowReport.Steps)
            {
                if (step.Status != StepStatus.Warning)
                    continue;

                totalWarnings++;

                _report.Warnings.Add(new PackWarning
                {
                    FlowId = flowReport.TestName,
                    StepOrder = step.Step,
                    Code = step.WarningCode ?? "unknown",
                    Details = step.Description ?? ""
                });
            }
        }

        _report.Summary.WarningsTotal = totalWarnings;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // COVERAGE MAP — Synthesize from journeys + plan coverage
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a coverage map from journey results and the optional PackPlan coverage entries.
    /// </summary>
    public void PopulateCoverageMap(TestPack pack, PackPlan? plan = null)
    {
        _report.CoverageMap.Clear();

        // Build execution status per journey
        var journeyStatus = _report.JourneyResults.ToDictionary(
            j => j.JourneyId,
            j => j.Result,
            StringComparer.OrdinalIgnoreCase);

        if (plan?.CoverageMap != null && plan.CoverageMap.Count > 0)
        {
            // Overlay execution results onto planned coverage
            foreach (var planned in plan.CoverageMap)
            {
                string status = "gap"; // default: gap if no journey ran

                if (planned.Journeys.Count > 0)
                {
                    var results = planned.Journeys
                        .Select(jId => journeyStatus.GetValueOrDefault(jId, "not_executed"))
                        .ToList();

                    if (results.All(r => r == "passed"))
                        status = "ok";
                    else if (results.Any(r => r == "passed"))
                        status = "partial";
                    else if (results.Any(r => r == "failed"))
                        status = "failed";
                    else
                        status = "not_executed";
                }

                _report.CoverageMap.Add(new CoverageMapEntry
                {
                    Area = planned.Area,
                    Journeys = planned.Journeys,
                    Status = status
                });
            }
        }
        else
        {
            // No plan — synthesize from journeys
            foreach (var journey in pack.Journeys)
            {
                var result = journeyStatus.GetValueOrDefault(journey.JourneyId, "not_executed");
                _report.CoverageMap.Add(new CoverageMapEntry
                {
                    Area = journey.Title,
                    Journeys = [journey.JourneyId],
                    Status = result switch
                    {
                        "passed" => "ok",
                        "failed" => "failed",
                        "skipped" => "gap",
                        _ => "partial"
                    }
                });
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PERCEPTION STATS — Aggregated from FlowReport step data
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Recalculates perception stats from all FlowReports.
    /// </summary>
    public void PopulatePerceptionStats()
    {
        var stats = new PerceptionStats();

        foreach (var flowReport in _report.FlowReports)
        {
            foreach (var step in flowReport.Steps)
            {
                if (step.WarningCode == "VisionFallbackUsed")
                {
                    stats.StructuralToVisualFallbacks++;
                    stats.VisualCaptures++;
                }
                else
                {
                    // Default: structural. Future: tag steps with perception mode.
                    stats.StructuralCaptures++;
                }
            }
        }

        stats.EstimatedCostUnits =
            stats.StructuralCaptures * 1 +
            stats.VisualCaptures * 10 +
            stats.DualCaptures * 11;

        _report.PerceptionStats = stats;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FIX QUEUE — Ranked list of actionable fixes for coding agents
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a ranked fix queue from failures.
    /// Priority: unique flow failures → frequency → evidence completeness.
    /// </summary>
    public void PopulateFixQueue(TestPack pack)
    {
        _report.FixQueue.Clear();

        if (_report.Failures.Count == 0)
            return;

        // Group failures by flow + failure type for deduplication
        var grouped = _report.Failures
            .GroupBy(f => new { f.FlowId, f.FailureType })
            .OrderByDescending(g => g.Count()) // Most frequent first
            .ThenBy(g => g.First().StepOrder);  // Earlier steps first

        int rank = 1;
        foreach (var group in grouped)
        {
            var representative = group.First();
            var evidenceRefs = group.Select((f, i) =>
                $"failures[{_report.Failures.IndexOf(f)}]").ToList();

            var fixItem = new FixQueueItem
            {
                Rank = rank++,
                Category = CategorizeFailure(representative),
                Title = BuildFixTitle(representative),
                EvidenceRefs = evidenceRefs,
                LikelyCauses = InferCauses(representative),
                RecommendedNextChecks = SuggestNextChecks(representative),
                FixPacket = BuildFixPacket(representative, group.ToList(), pack)
            };

            _report.FixQueue.Add(fixItem);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Classify the failure type from a step result.
    /// </summary>
    private static string ClassifyFailureType(StepResult step)
    {
        var msg = step.Error?.ToLowerInvariant() ?? "";

        if (msg.Contains("assert"))
            return "assertion_failed";
        if (msg.Contains("not found") || msg.Contains("element"))
            return "element_not_found";
        if (msg.Contains("timeout") || msg.Contains("timed out"))
            return "timeout";
        if (step.Status == StepStatus.Error)
            return "error";

        return "unknown";
    }

    /// <summary>
    /// Categorize a failure for fix queue triage.
    /// </summary>
    private static string CategorizeFailure(PackFailure failure)
    {
        return failure.FailureType switch
        {
            "assertion_failed" => "logic_error",
            "element_not_found" => "flaky_selector",
            "timeout" => "timing_issue",
            _ => "ui_regression"
        };
    }

    /// <summary>
    /// Build a human-readable fix title.
    /// </summary>
    private static string BuildFixTitle(PackFailure failure)
    {
        return failure.FailureType switch
        {
            "assertion_failed" =>
                $"Assertion failed in '{failure.FlowId}' at step {failure.StepOrder}",
            "element_not_found" =>
                $"Element not found in '{failure.FlowId}' at step {failure.StepOrder}",
            "timeout" =>
                $"Timeout in '{failure.FlowId}' at step {failure.StepOrder}",
            _ =>
                $"Error in '{failure.FlowId}' at step {failure.StepOrder}: {failure.Observed}"
        };
    }

    /// <summary>
    /// Infer likely causes from a failure.
    /// </summary>
    private static List<string> InferCauses(PackFailure failure)
    {
        var causes = new List<string>();

        switch (failure.FailureType)
        {
            case "element_not_found":
                causes.Add("Selector may have changed (UI update)");
                causes.Add("Element may not be visible or loaded yet");
                causes.Add("Wrong window or frame targeted");
                break;

            case "assertion_failed":
                causes.Add("Expected value may have changed (data or logic update)");
                causes.Add("Localization or formatting change");
                break;

            case "timeout":
                causes.Add("Application response time degradation");
                causes.Add("Network latency or dependency failure");
                causes.Add("Element never appeared (conditional UI)");
                break;

            default:
                causes.Add("Unexpected runtime error");
                break;
        }

        return causes;
    }

    /// <summary>
    /// Suggest next investigation steps for a failure.
    /// </summary>
    private static List<string> SuggestNextChecks(PackFailure failure)
    {
        var checks = new List<string>();

        switch (failure.FailureType)
        {
            case "element_not_found":
                checks.Add("Inspect the screenshot for visual changes");
                checks.Add("Check if the element's AutomationId changed");
                checks.Add("Run Accessibility Insights to verify UIA tree");
                break;

            case "assertion_failed":
                checks.Add("Compare expected vs. observed values in the failure evidence");
                checks.Add("Check recent commits that modified the relevant feature");
                break;

            case "timeout":
                checks.Add("Check application logs for errors during the timeout window");
                checks.Add("Increase timeout or add explicit wait");
                break;

            default:
                checks.Add("Check the full stack trace in the flow report");
                break;
        }

        return checks;
    }

    /// <summary>
    /// Build a self-contained FixPacket for a coding agent.
    /// </summary>
    private static FixPacket BuildFixPacket(
        PackFailure representative,
        List<PackFailure> allInGroup,
        TestPack pack)
    {
        var evidencePaths = new List<string>();
        var evidenceChannels = new List<string>();

        foreach (var f in allInGroup)
        {
            if (f.Evidence.Screenshot != null)
            {
                evidencePaths.Add(f.Evidence.Screenshot);
                if (!evidenceChannels.Contains("visual"))
                    evidenceChannels.Add("visual");
            }
            if (f.Evidence.UiaTreeSnapshot != null)
            {
                evidencePaths.Add(f.Evidence.UiaTreeSnapshot);
                if (!evidenceChannels.Contains("structural-uia"))
                    evidenceChannels.Add("structural-uia");
            }
            if (f.Evidence.AccessibilityTree != null)
            {
                evidencePaths.Add(f.Evidence.AccessibilityTree);
                if (!evidenceChannels.Contains("accessibility"))
                    evidenceChannels.Add("accessibility");
            }
        }

        // Extract repro steps from the flow
        var reproSteps = new List<string>();
        var flow = pack.Flows.FirstOrDefault(f =>
            f.TestName.Equals(representative.FlowId, StringComparison.OrdinalIgnoreCase));
        if (flow != null)
        {
            for (int i = 0; i < Math.Min(representative.StepOrder, flow.Steps.Count); i++)
            {
                var s = flow.Steps[i];
                reproSteps.Add($"Step {i + 1}: {s.Action} on '{s.Selector ?? "N/A"}'");
            }
        }

        return new FixPacket
        {
            Summary = $"{representative.FailureType} in '{representative.FlowId}' step {representative.StepOrder}: " +
                      $"expected [{representative.Expected}], observed [{representative.Observed}]",
            EvidenceFilePaths = evidencePaths,
            SuspectedCauses = InferCauses(representative),
            ReproSteps = reproSteps,
            EvidenceChannels = evidenceChannels
        };
    }
}

using System.Diagnostics;
using IdolClick.Models;
using IdolClick.Services;
using IdolClick.Services.Backend;

namespace IdolClick.Services.Packs;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK RUNNER SERVICE — The Hand: Orchestrates TestPack execution.
//
// Responsibilities:
//   1. Validate pack-level guardrails before any execution
//   2. Validate each flow with FlowValidator
//   3. Schedule journeys by priority (p0 → p1 → p2 → p3)
//   4. For each journey, execute flows in order using the correct backend
//   5. Apply stop rules (max failures, max runtime)
//   6. Select perception mode per step (Eye: structural vs. visual)
//   7. Capture artifacts per policy
//   8. Produce PackReport via PackReportBuilder
//
// The Hand delegates per-flow execution to StepExecutor, which delegates
// per-step execution to IAutomationBackend. The Hand owns orchestration:
// ordering, backend switching, guardrail enforcement, and perception selection.
//
// Classic rule engine is NOT touched — this is a parallel execution pathway.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Callback for live journey progress during pack execution.
/// </summary>
public delegate void JourneyProgressCallback(int journeyNumber, int totalJourneys, JourneyResult result);

/// <summary>
/// The Hand: orchestrates TestPack execution across backends with perception-aware evidence capture.
/// </summary>
public class PackRunnerService
{
    private readonly LogService _log;
    private readonly FlowValidatorService _validator;
    private readonly Func<string, IAutomationBackend?> _backendFactory;

    /// <summary>
    /// Fired during execution for UI progress updates.
    /// </summary>
    public event Action<PackRunnerProgress>? OnProgress;

    /// <summary>
    /// Creates a PackRunnerService.
    /// </summary>
    /// <param name="log">Logging service.</param>
    /// <param name="validator">Flow validator for pre-execution validation.</param>
    /// <param name="backendFactory">
    /// Factory that returns an IAutomationBackend for a backend name ("desktop-uia").
    /// Returns null if the backend is not available.
    /// </param>
    public PackRunnerService(
        LogService log,
        FlowValidatorService validator,
        Func<string, IAutomationBackend?> backendFactory)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
    }

    /// <summary>
    /// Execute a validated TestPack and produce a PackReport.
    /// </summary>
    public async Task<PackReport> ExecuteAsync(
        TestPack pack,
        JourneyProgressCallback? onJourneyComplete = null,
        CancellationToken ct = default)
    {
        var report = new PackReport
        {
            PackId = pack.PackId,
            PackName = pack.PackName,
            StartedAtUtc = DateTime.UtcNow,
            Environment = CaptureEnvironment()
        };

        var overallSw = Stopwatch.StartNew();
        var perceptionStats = new PerceptionStats();

        try
        {
            // ── Phase 1: Pre-execution guardrail validation ──────────────────
            EmitProgress("Validating pack guardrails...", PackRunnerPhase.Validating);

            var guardrailErrors = ValidateGuardrails(pack);
            if (guardrailErrors.Count > 0)
            {
                report.Summary.OverallResult = "aborted";
                report.EndedAtUtc = DateTime.UtcNow;
                foreach (var err in guardrailErrors)
                    _log.Error("PackRunner", $"Guardrail violation: {err}");
                return report;
            }

            // ── Phase 2: Validate all flows ──────────────────────────────────
            EmitProgress("Validating flows...", PackRunnerPhase.Validating);

            foreach (var flow in pack.Flows)
            {
                var validation = _validator.Validate(flow);
                if (!validation.IsValid)
                {
                    report.Summary.OverallResult = "aborted";
                    report.EndedAtUtc = DateTime.UtcNow;
                    _log.Error("PackRunner", $"Flow '{flow.TestName}' failed validation: {string.Join("; ", validation.Errors)}");
                    return report;
                }
            }

            // ── Phase 3: Schedule journeys by priority ───────────────────────
            var orderedJourneys = pack.Journeys
                .OrderBy(j => j.Priority switch
                {
                    "p0" => 0,
                    "p1" => 1,
                    "p2" => 2,
                    "p3" => 3,
                    _ => 4
                })
                .ThenBy(j => j.JourneyId)
                .ToList();

            report.Summary.JourneysTotal = orderedJourneys.Count;

            // Build flow lookup
            var flowLookup = pack.Flows.ToDictionary(
                f => f.TestName,
                f => f,
                StringComparer.OrdinalIgnoreCase);

            int failureCount = 0;

            // ── Phase 4: Execute journeys ────────────────────────────────────
            for (int ji = 0; ji < orderedJourneys.Count; ji++)
            {
                ct.ThrowIfCancellationRequested();

                // Check runtime limit
                if (pack.Guardrails.MaxRuntimeMinutes > 0 &&
                    overallSw.Elapsed.TotalMinutes > pack.Guardrails.MaxRuntimeMinutes)
                {
                    _log.Warn("PackRunner", $"Max runtime ({pack.Guardrails.MaxRuntimeMinutes}min) exceeded. Stopping.");
                    // Mark remaining journeys as skipped
                    for (int rj = ji; rj < orderedJourneys.Count; rj++)
                    {
                        report.JourneyResults.Add(new JourneyResult
                        {
                            JourneyId = orderedJourneys[rj].JourneyId,
                            Title = orderedJourneys[rj].Title,
                            Result = "skipped"
                        });
                        report.Summary.JourneysSkipped++;
                    }
                    break;
                }

                // Check failure limit
                if (pack.Guardrails.MaxFailuresBeforeStop > 0 &&
                    failureCount >= pack.Guardrails.MaxFailuresBeforeStop)
                {
                    _log.Warn("PackRunner", $"Max failures ({pack.Guardrails.MaxFailuresBeforeStop}) reached. Stopping.");
                    for (int rj = ji; rj < orderedJourneys.Count; rj++)
                    {
                        report.JourneyResults.Add(new JourneyResult
                        {
                            JourneyId = orderedJourneys[rj].JourneyId,
                            Title = orderedJourneys[rj].Title,
                            Result = "skipped"
                        });
                        report.Summary.JourneysSkipped++;
                    }
                    break;
                }

                var journey = orderedJourneys[ji];
                EmitProgress($"Journey {ji + 1}/{orderedJourneys.Count}: {journey.Title}",
                    PackRunnerPhase.Executing, journey.JourneyId);

                var journeyResult = await ExecuteJourneyAsync(
                    journey, pack, flowLookup, perceptionStats, ct).ConfigureAwait(false);

                report.JourneyResults.Add(journeyResult);

                if (journeyResult.Result == "passed")
                    report.Summary.JourneysPassed++;
                else if (journeyResult.Result == "failed")
                {
                    report.Summary.JourneysFailed++;
                    failureCount++;
                }

                report.Summary.FlowsTotal += journeyResult.FlowIds.Count;
                report.Summary.StepsTotal += journeyResult.CriteriaResults.Count; // approximation

                onJourneyComplete?.Invoke(ji + 1, orderedJourneys.Count, journeyResult);
            }

            // ── Finalize ─────────────────────────────────────────────────────
            report.Summary.OverallResult = report.Summary.JourneysFailed == 0
                ? (report.Summary.WarningsTotal > 0 ? "partial" : "passed")
                : (report.Summary.JourneysPassed > 0 ? "partial" : "failed");
        }
        catch (OperationCanceledException)
        {
            report.Summary.OverallResult = "aborted";
            _log.Warn("PackRunner", "Pack execution cancelled.");
        }
        catch (Exception ex)
        {
            report.Summary.OverallResult = "aborted";
            _log.Error("PackRunner", $"Pack execution failed: {ex}");
        }

        overallSw.Stop();
        report.EndedAtUtc = DateTime.UtcNow;
        report.PerceptionStats = perceptionStats;

        EmitProgress("Pack execution complete.", PackRunnerPhase.Complete);

        _log.Info("PackRunner",
            $"Pack '{pack.PackName}' {report.Summary.OverallResult}: " +
            $"{report.Summary.JourneysPassed}/{report.Summary.JourneysTotal} passed, " +
            $"{report.Summary.JourneysFailed} failed, {report.Summary.JourneysSkipped} skipped " +
            $"({report.TotalTimeMs}ms)");

        return report;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // JOURNEY EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute a single journey: run its flows in order, switching backends as needed.
    /// </summary>
    private async Task<JourneyResult> ExecuteJourneyAsync(
        Journey journey,
        TestPack pack,
        Dictionary<string, TestFlow> flowLookup,
        PerceptionStats perceptionStats,
        CancellationToken ct)
    {
        var journeyResult = new JourneyResult
        {
            JourneyId = journey.JourneyId,
            Title = journey.Title
        };

        var journeySw = Stopwatch.StartNew();

        try
        {
            foreach (var flowRef in journey.Flows)
            {
                ct.ThrowIfCancellationRequested();

                if (!flowLookup.TryGetValue(flowRef.FlowRefId, out var flow))
                {
                    _log.Error("PackRunner", $"Journey '{journey.JourneyId}': flow '{flowRef.FlowRefId}' not found.");
                    journeyResult.Result = "failed";
                    return journeyResult;
                }

                journeyResult.FlowIds.Add(flow.TestName);

                // ── Resolve backend for this flow ────────────────────────────
                var backendName = ResolveBackendName(flow);
                var backend = _backendFactory(backendName);

                if (backend == null)
                {
                    _log.Error("PackRunner",
                        $"Backend '{backendName}' not available for flow '{flow.TestName}'. " +
                        "Ensure the required backend is installed.");
                    journeyResult.Result = "failed";
                    return journeyResult;
                }

                // ── Execute flow via StepExecutor ────────────────────────────
                EmitProgress($"  Flow: {flow.TestName} (backend: {backendName})",
                    PackRunnerPhase.Executing, journey.JourneyId);

                var executor = new StepExecutor(_log, _validator, backend);
                var flowReport = await executor.ExecuteFlowAsync(flow, null, ct).ConfigureAwait(false);

                // Track perception stats
                UpdatePerceptionStats(flowReport, perceptionStats);

                // Check flow result
                if (flowReport.Result is "failed" or "error")
                {
                    journeyResult.Result = "failed";
                    _log.Warn("PackRunner",
                        $"Journey '{journey.JourneyId}' failed at flow '{flow.TestName}': {flowReport.Result}");
                    return journeyResult;
                }
            }

            journeyResult.Result = "passed";
        }
        catch (OperationCanceledException)
        {
            journeyResult.Result = "aborted";
        }
        catch (Exception ex)
        {
            journeyResult.Result = "failed";
            _log.Error("PackRunner", $"Journey '{journey.JourneyId}' error: {ex.Message}");
        }

        journeySw.Stop();
        journeyResult.TotalTimeMs = journeySw.ElapsedMilliseconds;
        return journeyResult;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the backend name for a flow based on its Backend field.
    /// </summary>
    private static string ResolveBackendName(TestFlow flow)
    {
        return flow.Backend?.ToLowerInvariant() switch
        {
            "desktop" or "desktop-uia" or "" or null => "desktop-uia",
            _ => flow.Backend ?? "desktop-uia"
        };
    }

    /// <summary>
    /// Validates pack-level guardrails before execution.
    /// </summary>
    private List<string> ValidateGuardrails(TestPack pack)
    {
        var errors = new List<string>();

        if (pack.Journeys.Count == 0)
            errors.Add("Pack has no journeys.");

        if (pack.Flows.Count == 0)
            errors.Add("Pack has no flows.");

        if (pack.Journeys.Count > pack.Guardrails.MaxJourneys)
            errors.Add($"Pack has {pack.Journeys.Count} journeys, max {pack.Guardrails.MaxJourneys}.");

        var totalSteps = pack.Flows.Sum(f => f.Steps.Count);
        if (totalSteps > pack.Guardrails.MaxTotalSteps)
            errors.Add($"Pack has {totalSteps} total steps, max {pack.Guardrails.MaxTotalSteps}.");

        foreach (var flow in pack.Flows)
        {
            if (flow.Steps.Count > pack.Guardrails.MaxStepsPerFlow)
                errors.Add($"Flow '{flow.TestName}' has {flow.Steps.Count} steps, max {pack.Guardrails.MaxStepsPerFlow}.");
        }

        // Validate targets exist
        if (pack.Targets.Count == 0)
            errors.Add("Pack has no targets defined.");

        return errors;
    }

    /// <summary>
    /// Update perception statistics from a flow execution report.
    /// </summary>
    private static void UpdatePerceptionStats(ExecutionReport report, PerceptionStats stats)
    {
        foreach (var step in report.Steps)
        {
            // Count vision fallbacks as visual captures
            if (step.WarningCode == "VisionFallbackUsed")
            {
                stats.StructuralToVisualFallbacks++;
                stats.VisualCaptures++;
            }
            else
            {
                stats.StructuralCaptures++;
            }
        }

        stats.EstimatedCostUnits =
            stats.StructuralCaptures * 1 +
            stats.VisualCaptures * 10 +
            stats.DualCaptures * 11;
    }

    /// <summary>
    /// Captures current execution environment metadata.
    /// </summary>
    private static PackEnvironment CaptureEnvironment()
    {
        return new PackEnvironment
        {
            OsVersion = Environment.OSVersion.ToString(),
            Machine = Environment.MachineName,
            BackendVersions = new Dictionary<string, string>
            {
                ["desktop-uia"] = "1.0.0"
            }
        };
    }

    private void EmitProgress(string message, PackRunnerPhase phase, string? journeyId = null)
    {
        OnProgress?.Invoke(new PackRunnerProgress
        {
            Message = message,
            Phase = phase,
            JourneyId = journeyId
        });
        _log.Debug("PackRunner", message);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// SUPPORTING TYPES
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Execution phase of the PackRunner.
/// </summary>
public enum PackRunnerPhase
{
    Validating,
    Executing,
    Reporting,
    Complete
}

/// <summary>
/// Progress update from the PackRunner for UI consumption.
/// </summary>
public class PackRunnerProgress
{
    /// <summary>Human-readable progress message.</summary>
    public string Message { get; set; } = "";

    /// <summary>Current execution phase.</summary>
    public PackRunnerPhase Phase { get; set; }

    /// <summary>Currently executing journey ID (if applicable).</summary>
    public string? JourneyId { get; set; }
}

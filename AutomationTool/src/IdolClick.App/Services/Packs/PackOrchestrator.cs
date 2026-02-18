using System.Diagnostics;
using System.Text.Json;
using IdolClick.Models;
using Microsoft.Extensions.AI;

namespace IdolClick.Services.Packs;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK ORCHESTRATOR — The Brain's entry point for the entire Pack lifecycle.
//
// This is the modular hub that coordinates:
//   Phase A: Plan   → PackPlannerService   → PackPlan (proposed journeys + coverage)
//   Phase B: Compile → PackCompilerService  → TestPack (executable flows)
//   Phase C: Validate (built into Compiler  → retry loop up to 3x)
//   Phase D: Execute → PackRunnerService    → raw results
//   Phase E: Report  → PackReportBuilder    → PackReport (fix queue, coverage, stats)
//
// Designed as a MODULAR entry point:
//   • Each phase can run independently (compile without plan, execute from saved pack)
//   • The orchestrator does NOT hold state between calls (stateless)
//   • Future sprints add MCP cross-validation hooks between phases
//   • All phase results are serializable for audit / persistence
//
// Sprint roadmap:
//   Sprint 1 (current): Core pipeline — Plan → Compile → Execute → Report
//   Sprint 2: Insight engine — connect to external MCPs for cross-validation
//   Sprint 3: RPC IDE mode — live dashboard, confidence scoring, suggestion engine
//   Sprint 4: Figma/design MCP — compare live UI against design specs
//   Sprint 5: Multi-agent orchestration — parallel journeys, distributed execution
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The Brain's orchestrator for TestPack lifecycle: Plan → Compile → Execute → Report.
/// Stateless and modular — each phase can be invoked independently.
/// </summary>
public class PackOrchestrator
{
    private readonly LogService _log;
    private readonly PackPlannerService _planner;
    private readonly PackCompilerService _compiler;
    private readonly PackRunnerService _runner;
    private readonly FlowValidatorService _validator;

    public PackOrchestrator(
        ConfigService config,
        LogService log,
        FlowValidatorService validator,
        PackRunnerService runner)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _planner = new PackPlannerService(config, log);
        _compiler = new PackCompilerService(config, log, validator);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FULL PIPELINE — Plan → Compile → Execute → Report
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run the full pipeline from natural language input to PackReport.
    /// This is the "one-click" path for the Brain.
    /// </summary>
    public async Task<PackOrchestratorResult> RunFullPipelineAsync(
        TestPack packInput,
        IChatClient chatClient,
        Action<PackPipelineProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var result = new PackOrchestratorResult();
        var pipelineSw = Stopwatch.StartNew();

        try
        {
            // ── Phase A: Plan ────────────────────────────────────────────────
            onProgress?.Invoke(new PackPipelineProgress(PackPipelinePhase.Planning, "Brain is planning journeys and coverage..."));
            _log.Info("Orchestrator", "Phase A: Planning...");

            var planResult = await _planner.PlanAsync(packInput, chatClient, ct);
            result.PlanResult = planResult;

            if (!planResult.Success || planResult.Plan == null)
            {
                result.Success = false;
                result.FailedAtPhase = PackPipelinePhase.Planning;
                result.ErrorMessage = "Planning failed — Brain could not produce a valid PackPlan.";
                _log.Error("Orchestrator", $"Phase A failed: {(planResult.RawResponse?.Length > 200 ? planResult.RawResponse[..200] : planResult.RawResponse)}");
                return result;
            }

            _log.Info("Orchestrator",
                $"Phase A complete: {planResult.Plan.Journeys.Count} journeys, " +
                $"{planResult.Plan.CoverageMap.Count} coverage areas ({planResult.DurationMs}ms)");

            // ── Phase B+C: Compile + Validate ────────────────────────────────
            onProgress?.Invoke(new PackPipelineProgress(PackPipelinePhase.Compiling, "Brain is compiling executable flows..."));
            _log.Info("Orchestrator", "Phase B+C: Compiling...");

            var compileResult = await _compiler.CompileAsync(packInput, planResult.Plan, chatClient, ct);
            result.CompileResult = compileResult;

            if (!compileResult.Success || compileResult.Pack == null)
            {
                result.Success = false;
                result.FailedAtPhase = PackPipelinePhase.Compiling;
                result.ErrorMessage = $"Compilation failed after {compileResult.CompileAttempts} attempts. " +
                                      $"Errors: {string.Join("; ", compileResult.ValidationErrors)}";
                _log.Error("Orchestrator", $"Phase B+C failed: {compileResult.ValidationErrors.Count} errors");
                return result;
            }

            var compiledPack = compileResult.Pack;

            _log.Info("Orchestrator",
                $"Phase B+C complete: {compiledPack.Flows.Count} flows, " +
                $"{compiledPack.Flows.Sum(f => f.Steps.Count)} total steps, " +
                $"{compileResult.CompileAttempts} attempt(s) ({compileResult.DurationMs}ms)");

            // ── Phase D: Execute ─────────────────────────────────────────────
            onProgress?.Invoke(new PackPipelineProgress(PackPipelinePhase.Executing,
                $"Executing {compiledPack.Journeys.Count} journeys across {compiledPack.Flows.Count} flows..."));
            _log.Info("Orchestrator", "Phase D: Executing...");

            var packReport = await _runner.ExecuteAsync(compiledPack, null, ct);
            result.Report = packReport;

            _log.Info("Orchestrator",
                $"Phase D complete: {packReport.Summary.OverallResult} — " +
                $"{packReport.Summary.JourneysPassed}/{packReport.Summary.JourneysTotal} passed ({packReport.TotalTimeMs}ms)");

            // ── Phase E: Build Report ────────────────────────────────────────
            onProgress?.Invoke(new PackPipelineProgress(PackPipelinePhase.Reporting, "Building report with fix queue..."));
            _log.Info("Orchestrator", "Phase E: Building report...");

            PackReportBuilder.BuildFrom(packReport, compiledPack, planResult.Plan);

            _log.Info("Orchestrator",
                $"Phase E complete: {packReport.Failures.Count} failures, " +
                $"{packReport.Warnings.Count} warnings, {packReport.FixQueue.Count} fix items");

            result.Success = true;

            // ── Confidence Score ─────────────────────────────────────────────
            result.ConfidenceScore = CalculateConfidenceScore(packReport, planResult.Plan);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.FailedAtPhase = PackPipelinePhase.Cancelled;
            result.ErrorMessage = "Pipeline cancelled by user.";
            _log.Warn("Orchestrator", "Pipeline cancelled.");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Pipeline error: {ex.Message}";
            _log.Error("Orchestrator", $"Pipeline error: {ex}");
        }

        pipelineSw.Stop();
        result.TotalDurationMs = pipelineSw.ElapsedMilliseconds;

        onProgress?.Invoke(new PackPipelineProgress(
            result.Success ? PackPipelinePhase.Complete : PackPipelinePhase.Failed,
            result.Success
                ? $"Pipeline complete — confidence: {result.ConfidenceScore:P0}"
                : $"Pipeline failed at {result.FailedAtPhase}: {result.ErrorMessage}"));

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // INDIVIDUAL PHASE ACCESS — For modular / step-by-step usage
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Phase A only: Plan from a TestPack input.
    /// </summary>
    public Task<PackPlanResult> PlanAsync(TestPack input, IChatClient client, CancellationToken ct = default)
        => _planner.PlanAsync(input, client, ct);

    /// <summary>
    /// Phase B+C only: Compile a plan into executable flows.
    /// </summary>
    public Task<PackCompileResult> CompileAsync(TestPack input, PackPlan plan, IChatClient client, CancellationToken ct = default)
        => _compiler.CompileAsync(input, plan, client, ct);

    /// <summary>
    /// Phase D only: Execute a pre-compiled TestPack.
    /// </summary>
    public Task<PackReport> ExecuteAsync(TestPack pack, CancellationToken ct = default)
        => _runner.ExecuteAsync(pack, null, ct);

    /// <summary>
    /// Phase E only: Build report from existing execution data.
    /// </summary>
    public PackReport BuildReport(PackReport rawReport, TestPack pack, PackPlan? plan = null)
        => PackReportBuilder.BuildFrom(rawReport, pack, plan);

    // ═══════════════════════════════════════════════════════════════════════════════
    // CONFIDENCE SCORING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calculate a confidence score (0.0–1.0) for the test execution.
    /// This gives the person who assigned the testing a clear signal:
    ///   0.9+ = High confidence — system behaves as expected
    ///   0.7–0.9 = Moderate — some issues but core flows pass
    ///   0.5–0.7 = Low — significant failures found
    ///   &lt;0.5 = Critical — system has serious issues
    /// </summary>
    private static double CalculateConfidenceScore(PackReport report, PackPlan plan)
    {
        if (report.Summary.JourneysTotal == 0)
            return 0.0;

        // Base: journey pass rate (60% weight)
        double journeyPassRate = (double)report.Summary.JourneysPassed / report.Summary.JourneysTotal;

        // Coverage completion (20% weight)
        double coverageScore = 1.0;
        if (report.CoverageMap.Count > 0)
        {
            var okCount = report.CoverageMap.Count(c => c.Status == "ok");
            coverageScore = (double)okCount / report.CoverageMap.Count;
        }

        // Perception reliability — fewer fallbacks = higher confidence (10% weight)
        double perceptionScore = 1.0;
        var totalPerception = report.PerceptionStats.StructuralCaptures +
                              report.PerceptionStats.VisualCaptures +
                              report.PerceptionStats.DualCaptures;
        if (totalPerception > 0)
        {
            var fallbackRate = (double)report.PerceptionStats.StructuralToVisualFallbacks / totalPerception;
            perceptionScore = 1.0 - fallbackRate;
        }

        // Warning penalty (10% weight) — many warnings reduce confidence
        double warningScore = 1.0;
        if (report.Summary.StepsTotal > 0)
        {
            var warningRate = (double)report.Summary.WarningsTotal / report.Summary.StepsTotal;
            warningScore = Math.Max(0, 1.0 - warningRate * 2); // Penalize: 50% warnings = 0 score
        }

        return Math.Clamp(
            journeyPassRate * 0.60 +
            coverageScore * 0.20 +
            perceptionScore * 0.10 +
            warningScore * 0.10,
            0.0, 1.0);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// PIPELINE RESULT & PROGRESS TYPES
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Complete result of a full Pack pipeline invocation.
/// Contains results from every phase for audit and inspection.
/// </summary>
public class PackOrchestratorResult
{
    /// <summary>Whether the full pipeline completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Which phase failed (if not successful).</summary>
    public PackPipelinePhase? FailedAtPhase { get; set; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Total pipeline execution time in milliseconds.</summary>
    public long TotalDurationMs { get; set; }

    /// <summary>
    /// Confidence score (0.0–1.0) measuring how reliably the system passed.
    /// Used by the assigning person to gauge quality quickly.
    /// </summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Phase A result — the plan.</summary>
    public PackPlanResult? PlanResult { get; set; }

    /// <summary>Phase B+C result — the compiled pack.</summary>
    public PackCompileResult? CompileResult { get; set; }

    /// <summary>Phase D+E result — the execution report.</summary>
    public PackReport? Report { get; set; }

    /// <summary>
    /// Human-readable summary suitable for dashboard display.
    /// </summary>
    public string GetSummary()
    {
        if (!Success)
            return $"FAILED at {FailedAtPhase}: {ErrorMessage}";

        var r = Report?.Summary;
        if (r == null)
            return "No report available.";

        return $"Confidence: {ConfidenceScore:P0} | " +
               $"Journeys: {r.JourneysPassed}/{r.JourneysTotal} passed | " +
               $"Failures: {Report?.Failures.Count ?? 0} | " +
               $"Fix items: {Report?.FixQueue.Count ?? 0} | " +
               $"Time: {TotalDurationMs / 1000.0:F1}s";
    }
}

/// <summary>
/// Pipeline execution phases.
/// </summary>
public enum PackPipelinePhase
{
    Planning,
    Compiling,
    Executing,
    Reporting,
    Complete,
    Failed,
    Cancelled
}

/// <summary>
/// Progress update from the pipeline for UI / logging.
/// </summary>
public record PackPipelineProgress(PackPipelinePhase Phase, string Message);

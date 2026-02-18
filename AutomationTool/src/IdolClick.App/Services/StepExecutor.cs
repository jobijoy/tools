using System.Diagnostics;
using IdolClick.Models;
using IdolClick.Services.Backend;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// STEP EXECUTOR — Orchestrates flow execution through automation backends.
//
// Sprint 7 refactor: Pipeline now delegates to IAutomationBackend instead of
// directly calling FlowActionExecutor/AssertionEvaluator/SelectorParser.
//
// Pipeline: FlowValidator → StepExecutor → IAutomationBackend.ExecuteStepAsync
//
// Each backend (DesktopBackend) owns:
//   • Selector resolution
//   • Actionability checks (pre-action contract)
//   • Action execution
//   • Post-step assertion evaluation
//   • Backend call logging
//
// StepExecutor still owns:
//   • Flow-level validation gate
//   • Step ordering and iteration
//   • StopOnFailure / cancellation / overall timeout
//   • Report generation
//   • Live progress callbacks
//
// Backward compatibility: The legacy 5-parameter constructor was removed in
// Entropy R5 — all callers now use the 3-parameter IAutomationBackend constructor.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Callback for live step-by-step progress during execution.
/// </summary>
public delegate void StepProgressCallback(int stepNumber, int totalSteps, StepResult result);

/// <summary>
/// Orchestrates the execution of a validated TestFlow, producing an ExecutionReport.
/// Delegates per-step execution to the active <see cref="IAutomationBackend"/>.
/// </summary>
public class StepExecutor
{
    private readonly LogService _log;
    private readonly FlowValidatorService _validator;
    private readonly IAutomationBackend _backend;

    /// <summary>
    /// Creates a StepExecutor backed by the given automation backend.
    /// </summary>
    public StepExecutor(
        LogService log,
        FlowValidatorService validator,
        IAutomationBackend backend)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>
    /// The active automation backend.
    /// </summary>
    public IAutomationBackend Backend => _backend;

    /// <summary>
    /// Executes a test flow and produces a structured execution report.
    /// </summary>
    /// <param name="flow">The test flow to execute (must be pre-validated).</param>
    /// <param name="onStepComplete">Optional callback for live step progress.</param>
    /// <param name="cancellationToken">Cancellation token for aborting execution.</param>
    /// <returns>Structured execution report for AI consumption.</returns>
    public async Task<ExecutionReport> ExecuteFlowAsync(
        TestFlow flow,
        StepProgressCallback? onStepComplete = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ExecutionReport
        {
            SchemaVersion = 1,
            TestName = flow.TestName,
            StartedAt = DateTime.UtcNow
        };

        var overallSw = Stopwatch.StartNew();

        // ── Gate: Validate before execution ──────────────────────────────────
        var validation = _validator.Validate(flow);
        if (!validation.IsValid)
        {
            report.Result = "error";
            report.FinishedAt = DateTime.UtcNow;
            report.TotalTimeMs = overallSw.ElapsedMilliseconds;
            report.Summary = $"Flow validation failed: {string.Join("; ", validation.Errors)}";
            _log.Error("Executor", $"Flow '{flow.TestName}' failed validation: {validation.Errors.Count} error(s)");
            return report;
        }

        _log.Info("Executor", $"Executing flow '{flow.TestName}' — {flow.Steps.Count} steps, timeout={flow.TimeoutSeconds}s");

        // ── Safety: Process allowlist check ──────────────────────────────────
        var allowedProcesses = App.Config?.GetConfig().Settings.AllowedProcesses ?? [];
        if (allowedProcesses.Count > 0 && !string.IsNullOrEmpty(flow.TargetApp))
        {
            var flowProcesses = flow.TargetApp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool anyAllowed = flowProcesses.Any(p =>
                allowedProcesses.Any(a =>
                    a.EndsWith('*')
                        ? p.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)
                        : p.Equals(a, StringComparison.OrdinalIgnoreCase)));

            if (!anyAllowed)
            {
                report.Result = "error";
                report.FinishedAt = DateTime.UtcNow;
                report.TotalTimeMs = overallSw.ElapsedMilliseconds;
                report.Summary = $"Blocked by process allowlist: '{flow.TargetApp}' not in [{string.Join(", ", allowedProcesses)}]";
                _log.Audit("Safety", $"Flow '{flow.TestName}' blocked — process '{flow.TargetApp}' not in allowlist");
                return report;
            }
        }

        // ── Report integrity metadata ────────────────────────────────────────
        report.BackendUsed = _backend.Name;
        report.BackendVersion = _backend.Version;
        report.MachineName = Environment.MachineName;
        report.OsVersion = Environment.OSVersion.ToString();

        // ── Sort steps by Order ──────────────────────────────────────────────
        var orderedSteps = flow.Steps.OrderBy(s => s.Order).ToList();
        bool hasFailed = false;

        // ── Execute each step ────────────────────────────────────────────────
        for (int i = 0; i < orderedSteps.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Mark remaining steps as skipped
                for (int j = i; j < orderedSteps.Count; j++)
                {
                    report.Steps.Add(new StepResult
                    {
                        Step = orderedSteps[j].Order,
                        Action = orderedSteps[j].Action,
                        Selector = orderedSteps[j].Selector,
                        Description = orderedSteps[j].Description,
                        Status = StepStatus.Skipped,
                        Diagnostics = "Execution cancelled"
                    });
                }
                report.Result = "aborted";
                break;
            }

            // Check overall timeout
            if (flow.TimeoutSeconds > 0 && overallSw.Elapsed.TotalSeconds > flow.TimeoutSeconds)
            {
                for (int j = i; j < orderedSteps.Count; j++)
                {
                    report.Steps.Add(new StepResult
                    {
                        Step = orderedSteps[j].Order,
                        Action = orderedSteps[j].Action,
                        Selector = orderedSteps[j].Selector,
                        Description = orderedSteps[j].Description,
                        Status = StepStatus.Skipped,
                        Diagnostics = $"Overall timeout ({flow.TimeoutSeconds}s) exceeded"
                    });
                }
                report.Result = "error";
                break;
            }

            var step = orderedSteps[i];

            // Skip remaining steps if StopOnFailure and we already failed
            if (hasFailed && flow.StopOnFailure)
            {
                var skipResult = new StepResult
                {
                    Step = step.Order,
                    Action = step.Action,
                    Selector = step.Selector,
                    Description = step.Description,
                    Status = StepStatus.Skipped,
                    Diagnostics = "Skipped due to prior failure (stopOnFailure=true)"
                };
                report.Steps.Add(skipResult);
                onStepComplete?.Invoke(i + 1, orderedSteps.Count, skipResult);
                continue;
            }

            // Execute the step via backend
            var ctx = new BackendExecutionContext
            {
                Flow = flow,
                StepIndex = i,
                TotalSteps = orderedSteps.Count
            };
            var stepResult = await _backend.ExecuteStepAsync(step, ctx, cancellationToken).ConfigureAwait(false);
            report.Steps.Add(stepResult);

            // Track first failure
            if (stepResult.Status == StepStatus.Failed || stepResult.Status == StepStatus.Error)
            {
                hasFailed = true;
                report.FailedStep ??= step.Order;
            }

            // Live progress callback
            onStepComplete?.Invoke(i + 1, orderedSteps.Count, stepResult);

            // Smart inter-step delay: use explicit delayAfterMs if provided,
            // otherwise a minimal 100ms pause (UI needs at least one frame to settle).
            // The real "readiness" logic lives in launch (WaitForInputIdle + UIA poll)
            // and in DesktopBackend's actionability checks (visible, stable, enabled).
            if (!cancellationToken.IsCancellationRequested)
            {
                var delayMs = step.DelayAfterMs > 0 ? step.DelayAfterMs : 100;
                await Task.Delay(delayMs, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // ── Finalize report ──────────────────────────────────────────────────
        overallSw.Stop();
        report.FinishedAt = DateTime.UtcNow;
        report.TotalTimeMs = overallSw.ElapsedMilliseconds;

        if (string.IsNullOrEmpty(report.Result) || report.Result == "error" && !hasFailed)
            report.Result = hasFailed ? "failed" : (report.WarningCount > 0 ? "passed_with_warnings" : "passed");

        report.Summary = BuildSummary(report);

        _log.Info("Executor",
            $"Flow '{flow.TestName}' {report.Result}: {report.PassedCount}/{report.Steps.Count} passed, " +
            $"{report.FailedCount} failed, {report.WarningCount} warnings, {report.SkippedCount} skipped ({report.TotalTimeMs}ms)");

        return report;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a human-readable summary for the execution report.
    /// </summary>
    private static string BuildSummary(ExecutionReport report)
    {
        var parts = new List<string>
        {
            $"Flow '{report.TestName}': {report.Result.ToUpperInvariant()}",
            $"  {report.PassedCount} passed, {report.FailedCount} failed, {report.WarningCount} warnings, {report.SkippedCount} skipped",
            $"  Total time: {report.TotalTimeMs}ms",
            $"  Backend: {report.BackendUsed ?? "unknown"} {report.BackendVersion ?? ""}"
        };

        if (report.FailedStep.HasValue)
        {
            var failedStepResult = report.Steps.FirstOrDefault(s => s.Step == report.FailedStep.Value);
            if (failedStepResult != null)
            {
                parts.Add($"  First failure at step {failedStepResult.Step}: {failedStepResult.Action} '{failedStepResult.Selector}'");
                parts.Add($"    Error: {failedStepResult.Error}");
            }
        }

        return string.Join("\n", parts);
    }
}

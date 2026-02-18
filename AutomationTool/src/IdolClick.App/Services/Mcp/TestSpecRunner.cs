using System.Diagnostics;
using System.Text.Json;
using IdolClick.Models;

namespace IdolClick.Services.Mcp;

// ═══════════════════════════════════════════════════════════════════════════════════
// TEST SPEC RUNNER — Compiles and executes TestSpec via LLM + execution engine.
//
// Flow:
//   1. Receive TestSpec (NL steps from coding agent)
//   2. Use AgentService LLM to compile NL steps → TestFlow JSON (with selectors)
//   3. Validate compiled TestFlow via FlowValidatorService
//   4. Execute TestFlow via StepExecutor
//   5. Score results → produce TestSpecReport with fix suggestions
//
// The "compilation" step is the key innovation: coding agents describe WHAT to test
// in natural language, and IdolClick's LLM figures out HOW (selectors, actions, waits).
// This means coding agents never need to understand UIA trees or DOM selectors.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Compiles TestSpec (natural language) to TestFlow (executable) and executes it.
/// Holds the last report for follow-up queries.
/// </summary>
public class TestSpecRunner
{
    private readonly ServiceHost _host;
    private TestSpecReport? _lastReport;

    /// <summary>Gets the last execution report (for idolclick_get_last_spec_report).</summary>
    public TestSpecReport? LastReport => _lastReport;

    public TestSpecRunner(ServiceHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Compiles a TestSpec to a TestFlow using the LLM, validates, executes, and scores.
    /// </summary>
    public async Task<TestSpecReport> RunAsync(TestSpec spec, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = new TestSpecReport
        {
            SpecName = spec.SpecName,
            StartedAt = DateTime.UtcNow.ToString("o")
        };

        try
        {
            _host.Log.Info("TestSpecRunner", $"Running spec: '{spec.SpecName}' ({spec.Steps.Count} steps) target: {spec.TargetApp}");

            // Step 1: Compile TestSpec → TestFlow JSON via LLM
            var flowJson = await CompileToFlowAsync(spec, ct);

            // Diagnostic: dump compiled flow to file for debugging
            try
            {
                var diagDir = Path.Combine(AppContext.BaseDirectory, "_spec_diag");
                Directory.CreateDirectory(diagDir);
                var safeName = string.Join("_", spec.SpecName.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllText(Path.Combine(diagDir, $"{safeName}_compiled.json"), flowJson ?? "(null)");
                // Also dump vision service status
                var visionStatus = $"Vision enabled: {_host.Vision.IsEnabled}, Threshold: {_host.Vision.ConfidenceThreshold}";
                File.WriteAllText(Path.Combine(diagDir, $"{safeName}_vision_status.txt"), visionStatus);
            }
            catch { /* diagnostic only — non-fatal */ }

            if (flowJson == null)
            {
                report.Result = "error";
                report.FixSuggestions.Add("LLM compilation failed — check that the AI agent is configured and the target application is running.");
                FinalizeReport(report, sw, spec);
                return report;
            }

            // Step 2: Validate the compiled flow — parse first then validate
            var flow = JsonSerializer.Deserialize<TestFlow>(flowJson, FlowJson.Options);
            if (flow == null)
            {
                report.Result = "error";
                report.FixSuggestions.Add("Failed to deserialize the compiled TestFlow.");
                FinalizeReport(report, sw, spec);
                return report;
            }

            var validationResult = _host.FlowValidator.Validate(flow);
            if (!validationResult.IsValid)
            {
                _host.Log.Warn("TestSpecRunner", $"Compiled flow has validation errors: {string.Join("; ", validationResult.Errors)}");
                report.Result = "error";
                report.FixSuggestions.Add($"LLM-compiled flow failed validation: {string.Join("; ", validationResult.Errors)}");
                FinalizeReport(report, sw, spec);
                return report;
            }

            // Auto-fill descriptions on compiled steps. The vision fallback requires
            // step.Description to be set. Map from original spec step descriptions.
            for (int i = 0; i < flow.Steps.Count && i < spec.Steps.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(flow.Steps[i].Description))
                {
                    flow.Steps[i].Description = spec.Steps[i].Description;
                    _host.Log.Debug("TestSpecRunner", $"Auto-filled description for step {i}: '{spec.Steps[i].Description}'");
                }
            }

            // Diagnostic: dump flow with descriptions post-fill
            try
            {
                var diagDir = Path.Combine(AppContext.BaseDirectory, "_spec_diag");
                Directory.CreateDirectory(diagDir);
                var safeName = string.Join("_", spec.SpecName.Split(Path.GetInvalidFileNameChars()));
                var stepDiag = string.Join("\n", flow.Steps.Select((s, i) =>
                    $"Step {i}: action={s.Action} selector='{s.Selector}' desc='{s.Description}' timeout={s.TimeoutMs}"));
                File.WriteAllText(Path.Combine(diagDir, $"{safeName}_steps.txt"), stepDiag);
            }
            catch { /* diagnostic only */ }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = spec.TimeoutMs > 0 ? spec.TimeoutMs : 60000;
            timeoutCts.CancelAfter(timeout);

            var execReport = await _host.FlowExecutor.ExecuteFlowAsync(flow, cancellationToken: timeoutCts.Token);

            // Step 4: Map execution results to TestSpecReport
            MapExecutionResults(report, execReport, spec);
        }
        catch (OperationCanceledException)
        {
            report.Result = "error";
            report.FixSuggestions.Add("Execution timed out. Increase timeoutMs or simplify the spec.");
        }
        catch (Exception ex)
        {
            _host.Log.Error("TestSpecRunner", $"Spec execution failed: {ex.Message}");
            report.Result = "error";
            report.FixSuggestions.Add($"Unexpected error: {ex.Message}");
        }

        FinalizeReport(report, sw, spec);
        return report;
    }

    /// <summary>
    /// Generates a TestSpec from a target app and natural language description.
    /// </summary>
    public async Task<TestSpec> GenerateSpecAsync(string targetApp, string featureDescription, CancellationToken ct = default)
    {
        if (!_host.Agent.IsConfigured)
            throw new InvalidOperationException("AI agent is not configured. Set LLM endpoint/key in config.json.");

        var prompt = $$"""
            Generate a TestSpec JSON for testing the following feature.
            
            Target: {{targetApp}}
            Feature: {{featureDescription}}
            
            Return ONLY valid JSON matching this schema:
            {
              "specName": "descriptive name",
              "targetApp": "{{targetApp}}",
              "description": "brief context",
              "steps": [
                { "action": "click|type|verify|wait|scroll|hover|screenshot", "description": "what to do", "expected": "what should happen" }
              ],
              "tags": ["relevant", "tags"],
              "timeoutMs": 60000
            }
            
            Rules:
            - Use natural language descriptions, NOT CSS selectors or UIA paths
            - Each step must have an "expected" outcome
            - Include "verify" steps to check important state
            - Keep steps atomic — one action per step
            - 5-15 steps is typical for a feature test
            """;

        var response = await _host.Agent.CompletionAsync(prompt, ct);

        // Extract JSON from LLM response (may be wrapped in markdown code block)
        var json = ExtractJson(response);
        var spec = JsonSerializer.Deserialize<TestSpec>(json, FlowJson.Options);
        
        return spec ?? new TestSpec
        {
            SpecName = "Generated spec",
            TargetApp = targetApp,
            Description = featureDescription,
            Steps = [new TestSpecStep { Action = "click", Description = $"Interact with {targetApp}", Expected = "Action completes successfully" }]
        };
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task<string?> CompileToFlowAsync(TestSpec spec, CancellationToken ct)
    {
        if (!_host.Agent.IsConfigured)
        {
            _host.Log.Error("TestSpecRunner", "Agent not configured — cannot compile TestSpec.");
            return null;
        }

        var stepsDescription = string.Join("\n", spec.Steps.Select((s, i) =>
            $"  {i + 1}. [{s.Action}] {s.Description} → Expected: {s.Expected}"));

        // Always use desktop compilation prompt
        var basePrompt = BuildDesktopCompilationPrompt(spec, stepsDescription);

        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var prompt = attempt == 1 ? basePrompt : basePrompt;
                var rawText = await _host.Agent.CompletionAsync(prompt, ct);
                var json = ExtractJson(rawText);

                // Quick validation — must be parseable JSON with steps array
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("steps", out _))
                {
                    _host.Log.Warn("TestSpecRunner", $"Attempt {attempt}/{maxAttempts}: LLM response was valid JSON but missing 'steps' array.");
                    if (attempt < maxAttempts) continue;
                    return null;
                }

                // Deep validation — parse into TestFlow and validate with FlowValidatorService
                var candidateFlow = JsonSerializer.Deserialize<TestFlow>(json, FlowJson.Options);
                if (candidateFlow == null)
                {
                    _host.Log.Warn("TestSpecRunner", $"Attempt {attempt}/{maxAttempts}: Failed to deserialize compiled TestFlow.");
                    if (attempt < maxAttempts) continue;
                    return null;
                }

                var validation = _host.FlowValidator.Validate(candidateFlow);
                if (validation.IsValid)
                {
                    if (attempt > 1)
                        _host.Log.Info("TestSpecRunner", $"Compilation succeeded on attempt {attempt}.");
                    return json;
                }

                // Validation failed — build a retry prompt with the error feedback
                var errors = string.Join("\n", validation.Errors.Select(e => $"  - {e}"));
                _host.Log.Warn("TestSpecRunner", $"Attempt {attempt}/{maxAttempts}: Validation errors:\n{errors}");

                if (attempt < maxAttempts)
                {
                    basePrompt = $$"""
                        Your previous compilation had validation errors. Fix them and return corrected TestFlow JSON.

                        Validation errors:
                        {{errors}}
                        
                        Original spec:
                          Name: {{spec.SpecName}}
                          Target: {{spec.TargetApp}}
                          Steps:
                        {{stepsDescription}}
                        
                        Return ONLY valid TestFlow JSON. Fix ALL validation errors listed above.
                        Use snake_case action names: click, type, send_keys, wait, assert_exists, assert_not_exists, 
                        assert_text, assert_window, navigate, screenshot, scroll, focus_window, launch, hover
                        Every step MUST include a "description" field.
                        Use UIA selector format: "ElementType#TextOrAutomationId" (e.g., "Button#Save", "Edit#SearchBox")
                        """;
                }
                else
                {
                    // Final attempt failed validation — return the JSON anyway (RunAsync will handle validation errors)
                    _host.Log.Warn("TestSpecRunner", $"All {maxAttempts} compilation attempts had validation errors. Returning last result.");
                    return json;
                }
            }
            catch (JsonException jex)
            {
                _host.Log.Warn("TestSpecRunner", $"Attempt {attempt}/{maxAttempts}: LLM returned invalid JSON: {jex.Message}");
                if (attempt < maxAttempts)
                {
                    basePrompt = $$"""
                        Your previous response was not valid JSON. Return ONLY a valid JSON object with no markdown or explanation.
                        
                        Spec: {{spec.SpecName}}
                        Target: {{spec.TargetApp}}
                        Steps:
                        {{stepsDescription}}
                        
                        Return ONLY valid TestFlow JSON. No markdown fences, no text before or after the JSON.
                        """;
                    continue;
                }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _host.Log.Error("TestSpecRunner", $"Attempt {attempt}/{maxAttempts}: LLM compilation failed: {ex.Message}");
                if (attempt < maxAttempts) continue;
                return null;
            }
        }

        return null;
    }

    // ─── Compilation prompt builders ─────────────────────────────────────────

    /// <summary>
    /// Builds a compilation prompt for desktop application targets (UIA-driven).
    /// </summary>
    private static string BuildDesktopCompilationPrompt(TestSpec spec, string stepsDescription)
    {
        return $$"""
            Compile the following test specification into an executable IdolClick TestFlow JSON.
            
            Spec: {{spec.SpecName}}
            Target: {{spec.TargetApp}}
            Description: {{spec.Description}}
            
            Steps:
            {{stepsDescription}}
            
            You MUST return ONLY valid TestFlow JSON (no markdown, no explanation).
            Selectors use the UIA format: "ElementType#TextOrAutomationId" (e.g., "Button#Save", "Edit#SearchBox").
            Use the element's accessible name from the application's UIA tree. If you cannot determine
            the exact selector, use the most likely element type and accessible name from the step description.
            Every step MUST include a "description" field with a human-readable description of the action
            (this enables vision fallback if UIA resolution fails).
            
            Map each spec step to one or more TestFlow steps with proper selectors.
            Include appropriate waits and delays for application loading.
            Set "targetApp" to the process name of the target application.
            
            Available actions (use snake_case in JSON):
              click, type, send_keys, wait, assert_exists, assert_not_exists, assert_text,
              assert_window, navigate, screenshot, scroll, focus_window, launch, hover
            
            TestFlow JSON format:
            {
              "testName": "{{spec.SpecName}}",
              "targetApp": "processName",
              "steps": [
                { "order": 1, "action": "launch", "processPath": "app.exe", "delayAfterMs": 3000, "description": "Launch the application" },
                { "order": 2, "action": "wait", "selector": "Button#Save", "timeoutMs": 10000, "description": "Wait for Save button to appear" },
                { "order": 3, "action": "click", "selector": "Button#Save", "delayAfterMs": 500, "description": "Click the Save button" },
                { "order": 4, "action": "type", "selector": "Edit#SearchBox", "text": "search term", "description": "Type into search box" },
                { "order": 5, "action": "assert_text", "selector": "Text#StatusBar", "contains": "Saved", "description": "Verify status shows Saved" },
                { "order": 6, "action": "screenshot", "delayAfterMs": 500, "description": "Capture final state" }
              ]
            }
            """;
    }

    private void MapExecutionResults(TestSpecReport report, ExecutionReport execReport, TestSpec spec)
    {
        int passed = 0;
        int total = spec.Steps.Count;
        long totalStepTime = 0;
        const long acceptableStepTimeMs = 10000; // 10s per step threshold for timing score

        // Map each spec step to the corresponding execution step(s)
        for (int i = 0; i < spec.Steps.Count; i++)
        {
            var specStep = spec.Steps[i];
            var stepResult = new TestSpecStepResult
            {
                Index = i,
                Description = specStep.Description
            };

            // Find matching execution step (by index — compilation is 1:1 or 1:many)
            if (i < execReport.Steps.Count)
            {
                var execStep = execReport.Steps[i];
                stepResult.Result = execStep.Status.ToString().ToLowerInvariant();
                stepResult.TimeMs = execStep.TimeMs;
                stepResult.Error = execStep.Error;
                stepResult.Screenshot = execStep.Screenshot;
                totalStepTime += execStep.TimeMs;

                if (stepResult.Result == "passed") passed++;
            }
            else
            {
                // More spec steps than execution steps — remaining are skipped
                stepResult.Result = "skipped";
                stepResult.Error = "Step was not compiled or execution stopped before reaching this step.";
            }

            report.Steps.Add(stepResult);
        }

        // Calculate scores
        double functionalScore = total > 0 ? (double)passed / total : 0;
        double timingScore = total > 0
            ? report.Steps.Count(s => s.TimeMs <= acceptableStepTimeMs) / (double)total
            : 0;
        double overallScore = (functionalScore * 0.7) + (timingScore * 0.3);

        report.Result = passed == total ? "passed" : "failed";
        report.Score = Math.Round(overallScore, 3);
        report.Scoring = new TestSpecScoring
        {
            Functional = Math.Round(functionalScore, 3),
            Timing = Math.Round(timingScore, 3),
            Overall = Math.Round(overallScore, 3)
        };

        // Generate fix suggestions for failed steps
        foreach (var step in report.Steps.Where(s => s.Result == "failed" || s.Result == "error"))
        {
            var suggestion = step.Error != null
                ? $"Step {step.Index + 1} ({step.Description}): {step.Error}"
                : $"Step {step.Index + 1} ({step.Description}): Failed without error message — check if the expected UI state was present.";
            report.FixSuggestions.Add(suggestion);
        }

        if (report.Result == "failed" && report.FixSuggestions.Count == 0)
        {
            report.FixSuggestions.Add("Some steps failed but no specific errors were captured. Try running with screenshots enabled to diagnose visually.");
        }
    }

    private void FinalizeReport(TestSpecReport report, Stopwatch sw, TestSpec spec)
    {
        sw.Stop();
        report.TotalTimeMs = sw.ElapsedMilliseconds;

        // Ensure scores are set even for error cases
        if (report.Scoring.Overall == 0 && report.Steps.Count == 0)
        {
            report.Scoring = new TestSpecScoring { Functional = 0, Timing = 0, Overall = 0 };
            // Create placeholder step results for error cases
            for (int i = 0; i < spec.Steps.Count; i++)
            {
                report.Steps.Add(new TestSpecStepResult
                {
                    Index = i,
                    Description = spec.Steps[i].Description,
                    Result = "skipped",
                    Error = report.FixSuggestions.FirstOrDefault() ?? "Execution did not reach this step."
                });
            }
        }

        _lastReport = report;
        _host.Log.Info("TestSpecRunner",
            $"Spec '{report.SpecName}' completed: {report.Result} " +
            $"(score: {report.Score:F2}, {report.TotalTimeMs}ms, " +
            $"{report.Steps.Count(s => s.Result == "passed")}/{report.Steps.Count} passed)");
    }

    private static string ExtractJson(string text)
    {
        // Strip markdown code fences if present
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        // Find the first { and last } for JSON extraction
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return trimmed;
    }
}

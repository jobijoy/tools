using System.Text.Json;
using IdolClick.Models;
using Microsoft.Extensions.AI;

namespace IdolClick.Services.Packs;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK COMPILER SERVICE — Phase B of the Two-Phase Prompt System.
//
// The Brain's SDET perspective:
//   • Takes a PackPlan + capabilities + selector guidance + targets + data profiles
//   • Produces a full TestPack with deterministic TestFlow steps
//   • Every step must be one of the StepAction enum values
//   • Every selector must specify a valid SelectorKind
//   • Must pass FlowValidator — if not, retry with structured error feedback (Phase C)
//
// The Compiler thinks like a senior SDET: precise selectors, bounded flow sizes,
// explicit waits, deterministic assertions, and proper perception mode annotations.
//
// Phase C (Validation Feedback Loop) is integrated:
//   1. Compile → validate each flow
//   2. If validation errors → send structured error list back to LLM
//   3. LLM outputs corrected flows
//   4. Cap retries (default 3)
//   5. If still invalid → return PackPlan + errors (no execution)
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Phase B+C: Compiles a PackPlan into a validated TestPack with deterministic flows.
/// Includes validation feedback loop for self-correction.
/// </summary>
public class PackCompilerService
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly FlowValidatorService _validator;

    /// <summary>Maximum validation-retry cycles before giving up.</summary>
    private const int MaxCompileRetries = 3;

    /// <summary>
    /// Fired during compilation for progress visibility.
    /// </summary>
    public event Action<string>? OnProgress;

    public PackCompilerService(ConfigService config, LogService log, FlowValidatorService validator)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// Compile a PackPlan into a fully validated TestPack.
    /// Runs validation feedback loop (Phase C) if flows have errors.
    /// </summary>
    public async Task<PackCompileResult> CompileAsync(
        TestPack packTemplate,
        PackPlan plan,
        IChatClient client,
        CancellationToken ct = default)
    {
        var result = new PackCompileResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _log.Info("PackCompiler", $"Compiling pack '{plan.PackName}' — {plan.Journeys.Count} journeys");

            TestPack? compiledPack = null;
            List<string> lastErrors = [];
            int attempt = 0;

            while (attempt <= MaxCompileRetries)
            {
                attempt++;
                ct.ThrowIfCancellationRequested();

                OnProgress?.Invoke(attempt == 1
                    ? "Compiling deterministic flows..."
                    : $"Retry {attempt - 1}/{MaxCompileRetries} — fixing validation errors...");

                // ── Build compiler prompt ────────────────────────────────────
                var systemPrompt = BuildCompilerSystemPrompt(packTemplate);
                var userPrompt = attempt == 1
                    ? BuildCompilerUserPrompt(plan, packTemplate)
                    : BuildCorrectionPrompt(plan, lastErrors);

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, userPrompt)
                };

                var settings = _config.GetConfig().AgentSettings;
                var options = new ChatOptions
                {
                    MaxOutputTokens = Math.Min(settings.MaxTokens * 2, 16384), // Compiler needs more tokens
                    // Some models (gpt-5.2, o-series) only accept the default temperature.
                    Temperature = settings.Temperature is > 0 and < 1.0 ? (float)settings.Temperature : null,
                    ResponseFormat = ChatResponseFormat.Json
                };

                _log.Debug("PackCompiler", $"Attempt {attempt}: sending compiler prompt ({systemPrompt.Length + userPrompt.Length} chars)");

                var response = await client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
                var responseText = response.Text ?? "";

                // ── Parse TestPack ───────────────────────────────────────────
                compiledPack = TryParseTestPack(responseText, packTemplate);
                if (compiledPack == null)
                {
                    lastErrors = ["Failed to parse TestPack JSON from compiler response."];
                    _log.Warn("PackCompiler", $"Attempt {attempt}: parse failed. Preview: {responseText[..Math.Min(200, responseText.Length)]}");
                    if (attempt > MaxCompileRetries)
                    {
                        result.RawResponse = responseText;
                        break;
                    }
                    continue;
                }

                // ── Validate all flows (Phase C gate) ────────────────────────
                OnProgress?.Invoke("Validating compiled flows...");
                lastErrors = ValidateAllFlows(compiledPack);

                if (lastErrors.Count == 0)
                {
                    // All flows valid — success
                    result.Pack = compiledPack;
                    result.Success = true;
                    result.CompileAttempts = attempt;
                    result.Message = $"Pack compiled: {compiledPack.Journeys.Count} journeys, {compiledPack.Flows.Count} flows, validated in {attempt} attempt(s)";
                    _log.Info("PackCompiler", result.Message);
                    break;
                }

                _log.Warn("PackCompiler", $"Attempt {attempt}: {lastErrors.Count} validation error(s) — retrying");
            }

            // If exhausted retries
            if (!result.Success)
            {
                result.Success = false;
                result.CompileAttempts = attempt;
                result.ValidationErrors = lastErrors;
                result.Message = $"Compilation failed after {attempt} attempt(s). {lastErrors.Count} validation error(s) remain.";
                _log.Error("PackCompiler", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Message = "Compilation cancelled.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Compilation error: {ex.Message}";
            _log.Error("PackCompiler", $"Compilation failed: {ex}");
        }

        result.DurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // VALIDATION (Phase C — Deterministic Gate)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates all flows in a TestPack and returns aggregated errors.
    /// </summary>
    private List<string> ValidateAllFlows(TestPack pack)
    {
        var allErrors = new List<string>();

        // Pack-level guardrail checks
        if (pack.Journeys.Count > pack.Guardrails.MaxJourneys)
            allErrors.Add($"Pack has {pack.Journeys.Count} journeys, max allowed is {pack.Guardrails.MaxJourneys}.");

        var totalSteps = pack.Flows.Sum(f => f.Steps.Count);
        if (totalSteps > pack.Guardrails.MaxTotalSteps)
            allErrors.Add($"Pack has {totalSteps} total steps, max allowed is {pack.Guardrails.MaxTotalSteps}.");

        // Validate each flow
        foreach (var flow in pack.Flows)
        {
            if (flow.Steps.Count > pack.Guardrails.MaxStepsPerFlow)
                allErrors.Add($"Flow '{flow.TestName}' has {flow.Steps.Count} steps, max allowed is {pack.Guardrails.MaxStepsPerFlow}.");

            var validation = _validator.Validate(flow);
            foreach (var err in validation.Errors)
                allErrors.Add($"Flow '{flow.TestName}': {err}");
        }

        // Validate journey flow refs
        var flowIds = pack.Flows.Select(f => f.TestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var journey in pack.Journeys)
        {
            foreach (var flowRef in journey.Flows)
            {
                if (!flowIds.Contains(flowRef.FlowRefId) &&
                    !pack.Flows.Any(f => f.TestName.Equals(flowRef.FlowRefId, StringComparison.OrdinalIgnoreCase)))
                {
                    allErrors.Add($"Journey '{journey.JourneyId}' references flow '{flowRef.FlowRefId}' which does not exist in the pack.");
                }
            }

            if (journey.SuccessCriteria.Count == 0)
                allErrors.Add($"Journey '{journey.JourneyId}' has no success criteria.");
        }

        return allErrors;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PROMPT CONSTRUCTION
    // ═══════════════════════════════════════════════════════════════════════════════

    private static string BuildCompilerSystemPrompt(TestPack pack)
    {
        var sb = new System.Text.StringBuilder(4096);

        sb.AppendLine("You are an SDET test compiler for IdolClick, a deterministic UI automation runtime.");
        sb.AppendLine("Your job: take a PackPlan and produce a full TestPack JSON with deterministic flows.");
        sb.AppendLine();
        sb.AppendLine("=== RULES ===");
        sb.AppendLine("1. Output ONLY valid JSON matching the TestPack schema. No prose.");
        sb.AppendLine("2. Every step action must be one of: click, type, send_keys, wait, assert_exists, assert_not_exists, assert_text, assert_window, navigate, screenshot, scroll, focus_window, launch");
        sb.AppendLine("3. Every selector must specify a selectorKind. For desktop: desktop_uia (format: ElementType#TextOrId).");
        sb.AppendLine("4. Prefer stable selectors: automationId/name for desktop UIA.");
        sb.AppendLine("5. Keep each flow under the max steps per flow guardrail.");
        sb.AppendLine("6. Include at least 1 assertion per journey end-state.");
        sb.AppendLine("7. Each flow runs on exactly one backend. Hybrid = multiple flows per journey.");
        sb.AppendLine("8. Set timeoutMs on steps that may need waiting (form submissions, page loads).");
        sb.AppendLine("9. Include explicit waits where needed, but prefer backend actionability (auto-wait).");
        sb.AppendLine("10. For perception: structural assertions (text, exists) are cheapest. Use visual only when needed.");
        sb.AppendLine();

        // ── Inject guardrails ────────────────────────────────────────────────
        sb.AppendLine("=== GUARDRAILS ===");
        sb.AppendLine($"Max steps per flow: {pack.Guardrails.MaxStepsPerFlow}");
        sb.AppendLine($"Max total steps: {pack.Guardrails.MaxTotalSteps}");
        if (pack.Guardrails.ForbiddenActions.Count > 0)
            sb.AppendLine($"Forbidden: {string.Join(", ", pack.Guardrails.ForbiddenActions)}");
        sb.AppendLine($"Require target lock for desktop: {pack.Guardrails.RequireTargetLockForDesktop}");
        sb.AppendLine();

        // ── Inject targets ───────────────────────────────────────────────────
        sb.AppendLine("=== TARGETS ===");
        foreach (var t in pack.Targets)
        {
            var backend = "desktop-uia";
            sb.AppendLine($"- targetId: {t.TargetId}, backend: {backend}, " +
                          $"processName: {t.ProcessName}");
        }
        sb.AppendLine();

        // ── Data profiles ────────────────────────────────────────────────────
        if (pack.DataProfiles.Count > 0)
        {
            sb.AppendLine("=== DATA PROFILES ===");
            foreach (var dp in pack.DataProfiles)
            {
                sb.AppendLine($"- {dp.ProfileId}: {dp.Description}");
                foreach (var kv in dp.Values)
                    sb.AppendLine($"    {kv.Key}: \"{kv.Value}\"");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildCompilerUserPrompt(PackPlan plan, TestPack pack)
    {
        var planJson = JsonSerializer.Serialize(plan, FlowJson.Options);

        var sb = new System.Text.StringBuilder(planJson.Length + 512);
        sb.AppendLine("=== PACK PLAN ===");
        sb.AppendLine(planJson);
        sb.AppendLine();
        sb.AppendLine("Compile this plan into a complete TestPack JSON with deterministic flows.");
        sb.AppendLine("Each flow must have a unique testName, ordered steps, and proper selectors.");
        sb.AppendLine("Journey flowRefId values must match flow testName values.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a correction prompt with the original plan context AND structured validation errors.
    /// Preserving the plan context prevents the LLM from drifting on retries.
    /// </summary>
    private static string BuildCorrectionPrompt(PackPlan plan, List<string> errors)
    {
        var sb = new System.Text.StringBuilder(4096);

        sb.AppendLine("The previously compiled TestPack has validation errors. Fix them and output the corrected TestPack JSON.");
        sb.AppendLine();

        // Re-include plan context so the LLM doesn't lose sight of the original intent
        sb.AppendLine("=== ORIGINAL PACK PLAN (for context) ===");
        sb.AppendLine($"Pack: {plan.PackName}");
        sb.AppendLine($"Journeys: {plan.Journeys.Count}");
        foreach (var j in plan.Journeys)
        {
            sb.AppendLine($"  - {j.JourneyId}: {j.Title} (priority={j.Priority}, backends={string.Join(",", j.RequiredBackends)})");
        }
        sb.AppendLine();

        sb.AppendLine("=== VALIDATION ERRORS ===");
        for (int i = 0; i < errors.Count; i++)
            sb.AppendLine($"{i + 1}. {errors[i]}");
        sb.AppendLine();
        sb.AppendLine("Fix ALL errors and output the complete corrected TestPack JSON. No prose.");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PARSING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to parse a TestPack from compiler output.
    /// Merges structural data from the template (targets, guardrails, etc.).
    /// </summary>
    private TestPack? TryParseTestPack(string text, TestPack template)
    {
        TestPack? parsed = null;

        // Try direct parse
        try
        {
            parsed = JsonSerializer.Deserialize<TestPack>(text, FlowJson.Options);
        }
        catch { /* try code block extraction */ }

        // Try markdown code block
        if (parsed == null)
        {
            var jsonBlock = ExtractJsonBlock(text);
            if (jsonBlock != null)
            {
                try { parsed = JsonSerializer.Deserialize<TestPack>(jsonBlock, FlowJson.Options); }
                catch { return null; }
            }
        }

        if (parsed == null || parsed.Flows.Count == 0) return null;

        // Merge template data that the LLM shouldn't control
        parsed.PackId = template.PackId;
        parsed.Targets = template.Targets;
        parsed.Guardrails = template.Guardrails;
        parsed.Execution = template.Execution;
        parsed.DataProfiles = template.DataProfiles.Count > 0
            ? template.DataProfiles
            : parsed.DataProfiles;

        return parsed;
    }

    private static string? ExtractJsonBlock(string text)
    {
        var start = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = text.IndexOf("```", StringComparison.Ordinal);
        if (start < 0) return null;

        start = text.IndexOf('\n', start);
        if (start < 0) return null;
        start++;

        var end = text.IndexOf("```", start, StringComparison.Ordinal);
        if (end < 0) return null;

        return text[start..end].Trim();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// RESULT TYPES
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Result of the compilation phase.
/// </summary>
public class PackCompileResult
{
    /// <summary>Whether compilation produced a valid TestPack.</summary>
    public bool Success { get; set; }

    /// <summary>Status message.</summary>
    public string Message { get; set; } = "";

    /// <summary>The compiled and validated TestPack (null if failed).</summary>
    public TestPack? Pack { get; set; }

    /// <summary>Number of compile attempts (including retries).</summary>
    public int CompileAttempts { get; set; }

    /// <summary>Remaining validation errors (if compilation failed).</summary>
    public List<string> ValidationErrors { get; set; } = [];

    /// <summary>Raw LLM response (for debugging).</summary>
    public string? RawResponse { get; set; }

    /// <summary>Duration in milliseconds.</summary>
    public long DurationMs { get; set; }
}

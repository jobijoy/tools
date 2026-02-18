using System.Text.Json;
using IdolClick.Models;
using Microsoft.Extensions.AI;

namespace IdolClick.Services.Packs;

// ═══════════════════════════════════════════════════════════════════════════════════
// PACK PLANNER SERVICE — Phase A of the Two-Phase Prompt System.
//
// The Brain's PM+Tester perspective:
//   • Takes natural language instructions + project context + guardrails
//   • Produces a PackPlan: proposed journeys, coverage map, risk analysis
//   • Includes perception recommendations (when to use structural vs. visual Eye)
//   • No low-level steps — that's the Compiler's job
//
// The Planner is the "what to test" brain. It thinks like a PM who understands
// risk, coverage, and user journeys, combined with a senior QA who knows
// what categories of tests matter (happy path, validation, edge cases, etc.).
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Phase A: Plans test journeys from natural language instructions and project context.
/// Produces a <see cref="PackPlan"/> that the Compiler transforms into deterministic flows.
/// </summary>
public class PackPlannerService
{
    private readonly LogService _log;
    private readonly ConfigService _config;

    /// <summary>
    /// Fired during planning for progress visibility.
    /// </summary>
    public event Action<string>? OnProgress;

    public PackPlannerService(ConfigService config, LogService log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Generate a PackPlan from a TestPack's inputs and guardrails.
    /// Uses the LLM as a PM+QA planner — structured output only, no prose.
    /// </summary>
    public async Task<PackPlanResult> PlanAsync(
        TestPack pack,
        IChatClient client,
        CancellationToken ct = default)
    {
        var result = new PackPlanResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _log.Info("PackPlanner", $"Planning pack '{pack.PackName}' — {pack.Targets.Count} targets");
            OnProgress?.Invoke("Building planner prompt...");

            // ── Build the planner system prompt ──────────────────────────────
            var systemPrompt = BuildPlannerSystemPrompt(pack);
            var userPrompt = BuildPlannerUserPrompt(pack);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var settings = _config.GetConfig().AgentSettings;
            var options = new ChatOptions
            {
                MaxOutputTokens = settings.MaxTokens,
                // Some models (gpt-5.2, o-series) only accept the default temperature.
                Temperature = settings.Temperature is > 0 and < 1.0 ? (float)settings.Temperature : null,
                ResponseFormat = ChatResponseFormat.Json
            };

            OnProgress?.Invoke("Generating test plan...");
            _log.Debug("PackPlanner", $"Sending planner prompt ({systemPrompt.Length + userPrompt.Length} chars)");

            var response = await client.GetResponseAsync(messages, options, ct);
            var responseText = response.Text ?? "";

            _log.Debug("PackPlanner", $"Planner response: {responseText.Length} chars");

            // ── Parse PackPlan from response ─────────────────────────────────
            var plan = TryParsePackPlan(responseText);
            if (plan != null)
            {
                result.Plan = plan;
                result.Success = true;
                result.Message = $"Plan generated: {plan.Journeys.Count} journeys, {plan.CoverageMap.Count} coverage areas";
                _log.Info("PackPlanner", result.Message);
            }
            else
            {
                result.Success = false;
                result.Message = "Failed to parse PackPlan JSON from planner response.";
                result.RawResponse = responseText;
                _log.Warn("PackPlanner", $"Parse failed. Response preview: {responseText[..Math.Min(200, responseText.Length)]}");
            }
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Message = "Planning cancelled.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Planning error: {ex.Message}";
            _log.Error("PackPlanner", $"Planning failed: {ex}");
        }

        result.DurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PROMPT CONSTRUCTION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the system prompt that turns the LLM into a PM+QA planner.
    /// </summary>
    private static string BuildPlannerSystemPrompt(TestPack pack)
    {
        var sb = new System.Text.StringBuilder(4096);

        sb.AppendLine("You are a PM+QA test planner for IdolClick, a deterministic UI automation runtime.");
        sb.AppendLine("Your job: analyze the instructions and project context, then output a PackPlan JSON.");
        sb.AppendLine();
        sb.AppendLine("=== RULES ===");
        sb.AppendLine("1. Output ONLY valid JSON matching the PackPlan schema. No prose, no markdown, no explanation.");
        sb.AppendLine("2. Respect all guardrails — never exceed max journeys, max steps, or max runtime.");
        sb.AppendLine("3. Every journey must have measurable success criteria that are assertable by the engine.");
        sb.AppendLine("4. Coverage map must map every required category to at least one journey.");
        sb.AppendLine("5. Identify risks and show how guardrails mitigate them.");
        sb.AppendLine("6. For each journey, recommend a perception mode:");
        sb.AppendLine("   - 'structural' for text/state assertions (UIA tree for desktop)");
        sb.AppendLine("   - 'visual' for layout/rendering/visual regression checks");
        sb.AppendLine("   - 'auto' when the engine should decide per step (recommended default)");
        sb.AppendLine("   - 'dual' for deep investigation journeys needing both channels");
        sb.AppendLine("7. All journeys target desktop (UIA) backends.");
        sb.AppendLine("8. Priority: p0=critical, p1=high, p2=medium, p3=low. At least one p0 journey.");
        sb.AppendLine();

        // ── Inject guardrails ────────────────────────────────────────────────
        sb.AppendLine("=== GUARDRAILS ===");
        sb.AppendLine($"Safety mode: {pack.Guardrails.SafetyMode}");
        sb.AppendLine($"Max journeys: {pack.Guardrails.MaxJourneys}");
        sb.AppendLine($"Max total steps: {pack.Guardrails.MaxTotalSteps}");
        sb.AppendLine($"Max steps per flow: {pack.Guardrails.MaxStepsPerFlow}");
        sb.AppendLine($"Max runtime minutes: {pack.Guardrails.MaxRuntimeMinutes}");
        if (pack.Guardrails.ForbiddenActions.Count > 0)
            sb.AppendLine($"Forbidden actions: {string.Join(", ", pack.Guardrails.ForbiddenActions)}");
        sb.AppendLine($"Vision fallback policy: {pack.Guardrails.VisionFallbackPolicy}");
        sb.AppendLine($"Default perception mode: {pack.Guardrails.Perception.DefaultMode}");
        sb.AppendLine();

        // ── Inject capabilities ──────────────────────────────────────────────
        sb.AppendLine("=== CAPABILITIES ===");
        sb.AppendLine("Actions: click, type, send_keys, wait, assert_exists, assert_not_exists, assert_text, assert_window, navigate, screenshot, scroll, focus_window, launch");
        sb.AppendLine("Assertion types: exists, not_exists, text_contains, text_equals, window_title, process_running");
        sb.AppendLine("Success criterion types: structural_text, structural_element, desktop_text, desktop_element, visual_match, custom");
        sb.AppendLine("Selector kinds (desktop): desktop_uia (format: ElementType#TextOrAutomationId)");
        sb.AppendLine("Perception channels: structural (UIA — fast, deterministic), visual (screenshot — layout/rendering)");
        sb.AppendLine();

        // ── Inject targets ───────────────────────────────────────────────────
        sb.AppendLine("=== TARGETS ===");
        foreach (var t in pack.Targets)
        {
            sb.AppendLine($"- {t.TargetId}: kind={t.Kind}, " +
                          $"process={t.ProcessName}");
        }
        sb.AppendLine();

        // ── Required coverage categories ─────────────────────────────────────
        if (pack.CoveragePlan.RequiredCategories.Count > 0)
        {
            sb.AppendLine("=== REQUIRED COVERAGE CATEGORIES ===");
            foreach (var cat in pack.CoveragePlan.RequiredCategories)
                sb.AppendLine($"- {cat}");
            sb.AppendLine();
        }

        // ── Output schema ────────────────────────────────────────────────────
        sb.AppendLine("=== OUTPUT SCHEMA ===");
        sb.AppendLine(PackPlanJsonSchema);

        return sb.ToString();
    }

    /// <summary>
    /// Builds the user prompt from pack inputs.
    /// </summary>
    private static string BuildPlannerUserPrompt(TestPack pack)
    {
        var sb = new System.Text.StringBuilder(2048);

        sb.AppendLine("=== INSTRUCTIONS ===");
        sb.AppendLine(pack.Inputs.Instructions);
        sb.AppendLine();

        if (pack.Inputs.ProjectContext.FeatureList.Count > 0)
        {
            sb.AppendLine("=== FEATURES ===");
            foreach (var f in pack.Inputs.ProjectContext.FeatureList)
                sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        if (pack.Inputs.ProjectContext.Routes.Count > 0)
        {
            sb.AppendLine("=== ROUTES ===");
            foreach (var r in pack.Inputs.ProjectContext.Routes)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (pack.Inputs.ProjectContext.KnownRisks.Count > 0)
        {
            sb.AppendLine("=== KNOWN RISKS ===");
            foreach (var r in pack.Inputs.ProjectContext.KnownRisks)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (pack.Inputs.ProjectContext.UiNotes.Count > 0)
        {
            sb.AppendLine("=== UI NOTES ===");
            foreach (var n in pack.Inputs.ProjectContext.UiNotes)
                sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        if (pack.Inputs.DocumentationSources.Count > 0)
        {
            sb.AppendLine("=== DOCUMENTATION ===");
            foreach (var d in pack.Inputs.DocumentationSources)
            {
                sb.AppendLine($"--- {d.Name} ({d.Type}) ---");
                sb.AppendLine(d.Content);
                sb.AppendLine();
            }
        }

        sb.AppendLine("Generate the PackPlan JSON now.");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PARSING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to parse a PackPlan from the LLM response text.
    /// Handles both raw JSON and JSON wrapped in markdown code blocks.
    /// </summary>
    private PackPlan? TryParsePackPlan(string text)
    {
        try
        {
            // Try direct parse first
            var plan = JsonSerializer.Deserialize<PackPlan>(text, FlowJson.Options);
            if (plan?.Journeys.Count > 0) return plan;
        }
        catch { /* not raw JSON, try extracting */ }

        // Try extracting from markdown code block
        var jsonBlock = ExtractJsonBlock(text);
        if (jsonBlock != null)
        {
            try
            {
                var plan = JsonSerializer.Deserialize<PackPlan>(jsonBlock, FlowJson.Options);
                if (plan?.Journeys.Count > 0) return plan;
            }
            catch (Exception ex)
            {
                _log.Debug("PackPlanner", $"JSON extraction parse failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the first JSON block from markdown-formatted text.
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════════════
    // SCHEMA (embedded for prompt injection)
    // ═══════════════════════════════════════════════════════════════════════════════

    private const string PackPlanJsonSchema = """
        {
          "schemaVersion": "1.0",
          "packName": "string",
          "journeys": [
            {
              "journeyId": "J01",
              "title": "string",
              "priority": "p0|p1|p2|p3",
              "tags": ["string"],
              "coverageAreas": ["string"],
              "successCriteria": [
                {
                  "type": "structural_text|structural_element|desktop_text|desktop_element|visual_match|custom",
                  "selectorKind": "desktop_uia",
                  "selectorValue": "string",
                  "contains": "string (optional)",
                  "equals": "string (optional)",
                  "description": "string"
                }
              ],
              "requiredDataProfiles": ["string"],
              "requiredBackends": ["desktop-uia"],
              "recommendedPerception": "structural|visual|auto|dual"
            }
          ],
          "coverageMap": [
            { "area": "string", "journeys": ["J01"], "status": "ok|gap|partial" }
          ],
          "risks": [
            { "tag": "string", "description": "string", "mitigation": "string" }
          ],
          "suggestedDataProfiles": [
            { "profileId": "string", "description": "string", "suggestedValues": { "key": "value" } }
          ],
          "perceptionRecommendations": [
            { "journeyId": "J01", "recommendedMode": "structural|visual|auto|dual", "rationale": "string" }
          ]
        }
        """;
}

// ═══════════════════════════════════════════════════════════════════════════════════
// RESULT TYPES
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Result of the planning phase.
/// </summary>
public class PackPlanResult
{
    /// <summary>Whether planning succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Status message.</summary>
    public string Message { get; set; } = "";

    /// <summary>The generated plan (null if failed).</summary>
    public PackPlan? Plan { get; set; }

    /// <summary>Raw LLM response (for debugging parse failures).</summary>
    public string? RawResponse { get; set; }

    /// <summary>Planning duration in milliseconds.</summary>
    public long DurationMs { get; set; }
}

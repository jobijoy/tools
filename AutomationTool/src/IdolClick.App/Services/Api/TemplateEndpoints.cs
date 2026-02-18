using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using IdolClick.Models;
using IdolClick.Services.Templates;
using System.Text.Json;

namespace IdolClick.Services.Api;

// ═══════════════════════════════════════════════════════════════════════════════════
// TEMPLATE ENDPOINTS — REST surface for intent splitting and template registry.
//
// RA-validated constraint: These endpoints ONLY:
//   • List templates
//   • Parse intent
//   • Resolve candidate template
//   • Return draft TestFlow
//   • Return escalation decision
//
// They NEVER execute flows. Execution stays under FlowEndpoints.
// This keeps the execution surface centralized.
//
// Routes:
//   GET  /api/templates            — List all registered templates
//   GET  /api/templates/{id}       — Get template details by ID
//   POST /api/intent/parse         — Classify NL input into intent
//   POST /api/intent/resolve       — Full split pipeline: classify → score → escalate
// ═══════════════════════════════════════════════════════════════════════════════════

internal static class TemplateEndpoints
{
    public static void Map(WebApplication app)
    {
        // ── List all templates ──────────────────────────────────────────────
        app.MapGet("/api/templates", () =>
        {
            var registry = App.Templates;
            if (registry == null)
                return Results.Ok(Array.Empty<object>());

            var templates = registry.All.Select(t => new
            {
                templateId = t.TemplateId,
                displayName = t.DisplayName,
                description = t.Description,
                intentKind = t.IntentKind.ToString(),
                requiredSlots = t.RequiredSlots,
                optionalSlots = t.OptionalSlots,
                riskLevel = t.RiskLevel.ToString(),
                maturity = t.Maturity.ToString()
            });

            return Results.Ok(templates);
        });

        // ── Get template by ID ──────────────────────────────────────────────
        app.MapGet("/api/templates/{id}", (string id) =>
        {
            var registry = App.Templates;
            var template = registry?.GetById(id);

            if (template == null)
                return Results.NotFound(new { error = $"Template '{id}' not found" });

            return Results.Ok(new
            {
                templateId = template.TemplateId,
                displayName = template.DisplayName,
                description = template.Description,
                intentKind = template.IntentKind.ToString(),
                requiredSlots = template.RequiredSlots,
                optionalSlots = template.OptionalSlots,
                riskLevel = template.RiskLevel.ToString(),
                maturity = template.Maturity.ToString()
            });
        });

        // ── Parse intent (classify only, no template scoring) ───────────────
        app.MapPost("/api/intent/parse", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadBodyAsync(ctx);
                var request = JsonSerializer.Deserialize<IntentRequest>(body, FlowJson.Options);
                if (request == null || string.IsNullOrWhiteSpace(request.Input))
                    return Results.BadRequest(new { error = "Input text is required" });

                var classifier = new KeywordIntentClassifier();
                var parse = classifier.Classify(request.Input);

                return Results.Ok(new
                {
                    kind = parse.Kind.ToString(),
                    confidence = parse.Confidence,
                    slots = parse.Slots,
                    matchedKeywords = parse.MatchedKeywords,
                    rawInput = parse.RawInput
                });
            }
            catch (Exception ex)
            {
                App.Log?.Error("TemplateEndpoints", $"Parse error: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // ── Full resolve: classify → score → escalate → draft flow ──────────
        app.MapPost("/api/intent/resolve", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadBodyAsync(ctx);
                var request = JsonSerializer.Deserialize<IntentRequest>(body, FlowJson.Options);
                if (request == null || string.IsNullOrWhiteSpace(request.Input))
                    return Results.BadRequest(new { error = "Input text is required" });

                if (App.IntentSplitter == null)
                    return Results.Problem("Intent splitter not initialized", statusCode: 503);

                var executionContext = new ExecutionContext
                {
                    IsInteractive = request.Interactive ?? true,
                    IsPackPipeline = request.PackPipeline ?? false,
                    TrustMode = request.TrustMode ?? false
                };

                var result = App.IntentSplitter.Split(request.Input, executionContext);

                return Results.Ok(new
                {
                    intent = new
                    {
                        kind = result.Intent.Kind.ToString(),
                        confidence = result.Intent.Confidence,
                        slots = result.Intent.Slots,
                        matchedKeywords = result.Intent.MatchedKeywords
                    },
                    decision = new
                    {
                        tier = result.Decision.Tier.ToString(),
                        reason = result.Decision.Reason,
                        templateId = result.Decision.Template?.TemplateId,
                        templateName = result.Decision.Template?.DisplayName,
                        riskLevel = result.Decision.Template?.RiskLevel.ToString(),
                        maturity = result.Decision.Template?.Maturity.ToString(),
                        draftFlow = result.Decision.DraftFlow != null
                            ? JsonSerializer.SerializeToElement(result.Decision.DraftFlow, FlowJson.Options)
                            : (JsonElement?)null,
                    },
                    candidates = result.Decision.AllCandidates.Select(c => new
                    {
                        templateId = c.Template.TemplateId,
                        score = c.Score,
                        filledRequired = c.FilledRequiredSlots,
                        missingRequired = c.MissingRequiredSlots,
                        filledOptional = c.FilledOptionalSlots,
                        missingOptional = c.MissingOptionalSlots
                    }),
                    processingMs = result.ProcessingMs
                });
            }
            catch (Exception ex)
            {
                App.Log?.Error("TemplateEndpoints", $"Resolve error: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // ── Build flow from specific template (manual override) ─────────────
        app.MapPost("/api/templates/{id}/build", async (HttpContext ctx, string id) =>
        {
            try
            {
                var registry = App.Templates;
                var template = registry?.GetById(id);
                if (template == null)
                    return Results.NotFound(new { error = $"Template '{id}' not found" });

                var body = await ReadBodyAsync(ctx);
                var request = JsonSerializer.Deserialize<BuildRequest>(body, FlowJson.Options);
                if (request == null)
                    return Results.BadRequest(new { error = "Invalid build request" });

                // Build IntentParse from provided slots
                var intent = new IntentParse
                {
                    Kind = template.IntentKind,
                    Confidence = 1.0, // Manual override = full confidence
                    Slots = request.Slots ?? [],
                    RawInput = request.Input ?? ""
                };

                // Validate required slots
                var missing = template.RequiredSlots
                    .Where(s => !intent.Slots.ContainsKey(s))
                    .ToList();

                if (missing.Count > 0)
                    return Results.BadRequest(new
                    {
                        error = "Missing required slots",
                        missingSlots = missing,
                        requiredSlots = template.RequiredSlots
                    });

                var flow = template.BuildFlow(intent);

                return Results.Ok(new
                {
                    templateId = template.TemplateId,
                    flow = JsonSerializer.SerializeToElement(flow, FlowJson.Options)
                });
            }
            catch (Exception ex)
            {
                App.Log?.Error("TemplateEndpoints", $"Build error: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync();
    }

    // ── Request DTOs ────────────────────────────────────────────────────────

    private sealed class IntentRequest
    {
        public string Input { get; set; } = "";
        public bool? Interactive { get; set; }
        public bool? PackPipeline { get; set; }
        public bool? TrustMode { get; set; }
    }

    private sealed class BuildRequest
    {
        public string? Input { get; set; }
        public Dictionary<string, string>? Slots { get; set; }
    }
}

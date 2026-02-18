using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using IdolClick.Models;
using System.Text.Json;

namespace IdolClick.Services.Api;

// ═══════════════════════════════════════════════════════════════════════════════════
// AGENT ENDPOINTS — REST surface for the AI agent chat and Pack pipeline.
//
// Thin translation layer — delegates to AgentService and PackOrchestrator via App.*.
//
// Routes:
//   POST /api/agent/chat        — Send a message to the agent
//   GET  /api/agent/status      — Get agent configuration status
//   POST /api/agent/clear       — Clear conversation history
//   POST /api/packs/plan        — Run Pack planner (Phase A only)
//   POST /api/packs/compile     — Compile a plan into flows (Phase B+C)
//   POST /api/packs/run-pipeline — Run the full Pack pipeline (A→E)
//   GET  /api/packs/fix-queue   — Get the fix queue from last run
//   GET  /api/packs/confidence  — Get confidence score from last run
// ═══════════════════════════════════════════════════════════════════════════════════

internal static class AgentEndpoints
{
    public static void Map(WebApplication app)
    {
        // ── Agent chat ──────────────────────────────────────────────────────
        app.MapPost("/api/agent/chat", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadBodyAsync(ctx);
                var request = JsonSerializer.Deserialize<ChatRequest>(body, FlowJson.Options);
                if (request == null || string.IsNullOrWhiteSpace(request.Message))
                    return Results.BadRequest(new { error = "Message is required" });

                // Wire SignalR progress — broadcast agent tool activity
                var hubContext = GetHubContext(ctx);
                Action<AgentProgress>? progressHandler = null;
                if (hubContext != null)
                {
                    progressHandler = progress =>
                    {
                        _ = HubBroadcaster.AgentProgress(hubContext, new AgentProgressEvent
                        {
                            Kind = progress.Kind.ToString().ToLowerInvariant(),
                            ToolName = progress.ToolName,
                            Message = progress.Message,
                            PartialText = progress.IntermediateText
                        });
                    };
                    App.Agent.OnProgress += progressHandler;
                }

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    if (request.TimeoutSeconds > 0)
                        cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

                    var response = await App.Agent.SendMessageAsync(request.Message, cts.Token);

                    return Results.Ok(new
                    {
                        text = response.Text,
                        hasFlow = response.HasFlow,
                        flow = response.Flow != null
                            ? JsonSerializer.SerializeToElement(response.Flow, FlowJson.Options)
                            : (JsonElement?)null,
                        isError = response.IsError
                    });
                }
                finally
                {
                    if (progressHandler != null)
                        App.Agent.OnProgress -= progressHandler;
                }
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499); // Client closed / timeout
            }
            catch (Exception ex)
            {
                App.Log?.Error("AgentEndpoints", $"Chat error: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // ── Agent status ────────────────────────────────────────────────────
        app.MapGet("/api/agent/status", () =>
        {
            return Results.Ok(new
            {
                configured = App.Agent.IsConfigured,
                statusText = App.Agent.StatusText
            });
        });

        // ── Clear history ───────────────────────────────────────────────────
        app.MapPost("/api/agent/clear", () =>
        {
            App.Agent.ClearHistory();
            return Results.Ok(new { cleared = true });
        });

        // ── Pack: Plan only ─────────────────────────────────────────────────
        app.MapPost("/api/packs/plan", async (HttpContext ctx) =>
        {
            return await HandlePackAsync(ctx, async (pack, client) =>
            {
                var result = await App.PackOrchestrator.PlanAsync(pack, client, ctx.RequestAborted);
                return Results.Ok(JsonSerializer.SerializeToElement(result, FlowJson.Options));
            });
        });

        // ── Pack: Compile only ──────────────────────────────────────────────
        app.MapPost("/api/packs/compile", async (HttpContext ctx) =>
        {
            return await HandlePackAsync(ctx, async (pack, client) =>
            {
                var body = await ReadBodyAsync(ctx);
                var request = JsonSerializer.Deserialize<CompileRequest>(body, FlowJson.Options);
                if (request?.Plan == null)
                    return Results.BadRequest(new { error = "Plan is required for compilation" });

                var result = await App.PackOrchestrator.CompileAsync(
                    pack, request.Plan, client, ctx.RequestAborted);
                return Results.Ok(JsonSerializer.SerializeToElement(result, FlowJson.Options));
            });
        });

        // ── Pack: Full pipeline ─────────────────────────────────────────────
        app.MapPost("/api/packs/run-pipeline", async (HttpContext ctx) =>
        {
            return await HandlePackAsync(ctx, async (pack, client) =>
            {
                var hubContext = GetHubContext(ctx);
                Action<Packs.PackPipelineProgress>? progressHandler = null;
                if (hubContext != null)
                {
                    progressHandler = p =>
                    {
                        _ = HubBroadcaster.PipelineProgress(hubContext, new PipelineProgressEvent
                        {
                            Phase = p.Phase.ToString().ToLowerInvariant(),
                            Message = p.Message
                        });
                    };
                }

                var result = await App.PackOrchestrator.RunFullPipelineAsync(
                    pack, client, progressHandler, ctx.RequestAborted);

                return Results.Ok(new
                {
                    success = result.Success,
                    failedAtPhase = result.FailedAtPhase?.ToString(),
                    errorMessage = result.ErrorMessage,
                    totalDurationMs = result.TotalDurationMs,
                    confidenceScore = result.ConfidenceScore,
                    summary = result.GetSummary(),
                    report = result.Report != null
                        ? JsonSerializer.SerializeToElement(result.Report, FlowJson.Options)
                        : (JsonElement?)null
                });
            });
        });

        // ── Pack: Fix queue ─────────────────────────────────────────────────
        app.MapGet("/api/packs/fix-queue", () =>
        {
            // No stateful access yet — this will be wired when PackAgentTools
            // exposes its cached state. For now, return empty.
            return Results.Ok(new { fixQueue = Array.Empty<object>() });
        });

        // ── Pack: Confidence ────────────────────────────────────────────────
        app.MapGet("/api/packs/confidence", () =>
        {
            return Results.Ok(new { confidenceScore = (double?)null, message = "Run a pack pipeline first" });
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<IResult> HandlePackAsync(
        HttpContext ctx,
        Func<TestPack, Microsoft.Extensions.AI.IChatClient, Task<IResult>> handler)
    {
        try
        {
            var body = await ReadBodyAsync(ctx);
            var pack = JsonSerializer.Deserialize<TestPack>(body, FlowJson.Options);
            if (pack == null)
                return Results.BadRequest(new { error = "Invalid TestPack JSON" });

            // Get the AI client from agent service
            var agentSvc = App.Agent as AgentService;
            var client = agentSvc?.GetChatClient();
            if (client == null)
                return Results.BadRequest(new { error = "AI client not configured. Set up AI settings first." });

            return await handler(pack, client);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = $"JSON parse error: {ex.Message}" });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            App.Log?.Error("AgentEndpoints", $"Pack error: {ex.Message}");
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync();
    }

    private static Microsoft.AspNetCore.SignalR.IHubContext<ExecutionHub>? GetHubContext(HttpContext ctx)
    {
        return ctx.RequestServices
            .GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<ExecutionHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<ExecutionHub>;
    }

    // ── Request DTOs ────────────────────────────────────────────────────────

    private sealed class ChatRequest
    {
        public string Message { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 120;
    }

    private sealed class CompileRequest
    {
        public PackPlan? Plan { get; set; }
    }
}

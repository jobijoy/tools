using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using IdolClick.Models;
using System.Text.Json;

namespace IdolClick.Services.Api;

// ═══════════════════════════════════════════════════════════════════════════════════
// FLOW ENDPOINTS — REST surface for flow validation, execution, and reports.
//
// Thin translation layer — delegates everything to existing services via App.*.
// All responses use FlowJson.Options (camelCase props, snake_case enums).
//
// Routes:
//   POST /api/flows/validate     — Validate a TestFlow JSON
//   POST /api/flows/run          — Execute a validated TestFlow
//   GET  /api/reports             — List saved execution reports
//   GET  /api/reports/{folder}    — Load a specific report
//   GET  /api/windows             — List visible desktop windows
//   GET  /api/inspect/{process}   — Inspect a window's UIA tree
//   GET  /api/capabilities        — Get available automation capabilities
// ═══════════════════════════════════════════════════════════════════════════════════

internal static class FlowEndpoints
{
    public static void Map(WebApplication app)
    {
        // ── Flow validation ─────────────────────────────────────────────────
        app.MapPost("/api/flows/validate", (HttpContext ctx) =>
        {
            return HandleAsync(ctx, async () =>
            {
                var body = await ReadBodyAsync(ctx);
                var flow = JsonSerializer.Deserialize<TestFlow>(body, FlowJson.Options);
                if (flow == null)
                    return Results.BadRequest(new { error = "Invalid TestFlow JSON" });

                var validator = new FlowValidatorService(App.Log);
                var result = validator.Validate(flow);

                return Results.Ok(new
                {
                    valid = result.IsValid,
                    errors = result.Errors,
                    warnings = result.Warnings,
                    stepCount = flow.Steps.Count,
                    testName = flow.TestName
                });
            });
        });

        // ── Flow execution ──────────────────────────────────────────────────
        app.MapPost("/api/flows/run", (HttpContext ctx) =>
        {
            return HandleAsync(ctx, async () =>
            {
                var body = await ReadBodyAsync(ctx);
                var flow = JsonSerializer.Deserialize<TestFlow>(body, FlowJson.Options);
                if (flow == null)
                    return Results.BadRequest(new { error = "Invalid TestFlow JSON" });

                // Validate first
                var validator = new FlowValidatorService(App.Log);
                var validation = validator.Validate(flow);
                if (!validation.IsValid)
                    return Results.BadRequest(new { error = "Flow validation failed", errors = validation.Errors });

                // Execute with SignalR progress broadcasting
                var hubContext = GetHubContext(ctx);
                var report = await App.FlowExecutor.ExecuteFlowAsync(flow, (stepNum, total, result) =>
                {
                    if (hubContext != null)
                    {
                        _ = HubBroadcaster.StepProgress(hubContext, new StepProgressEvent
                        {
                            StepIndex = stepNum,
                            TotalSteps = total,
                            Description = result.Description ?? $"Step {stepNum}",
                            Status = result.Status.ToString().ToLowerInvariant(),
                            Error = result.Error,
                            ElapsedMs = result.TimeMs
                        });
                    }
                });

                // Save report
                App.Reports.SaveReport(report);

                return Results.Ok(JsonSerializer.SerializeToElement(report, FlowJson.Options));
            });
        });

        // ── List reports ────────────────────────────────────────────────────
        app.MapGet("/api/reports", (int? max) =>
        {
            var reports = App.Reports.ListReports(max ?? 20);
            var result = reports.Select(r => new
            {
                folder = r.Folder,
                testName = r.TestName,
                result = r.Result,
                path = r.Path
            });
            return Results.Ok(result);
        });

        // ── Load specific report ────────────────────────────────────────────
        app.MapGet("/api/reports/{folder}", (string folder) =>
        {
            // Sanitize: prevent path traversal
            if (folder.Contains("..") || folder.Contains('/') || folder.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid folder name" });

            var reportPath = Path.Combine(App.Reports.ReportsDirectory, folder, "report.json");
            if (!File.Exists(reportPath))
                return Results.NotFound(new { error = $"Report not found: {folder}" });

            var report = App.Reports.LoadReport(reportPath);
            return report != null
                ? Results.Ok(JsonSerializer.SerializeToElement(report, FlowJson.Options))
                : Results.NotFound(new { error = "Failed to load report" });
        });

        // ── Window discovery ────────────────────────────────────────────────
        app.MapGet("/api/windows", () =>
        {
            var tools = CreateAgentTools();
            var json = tools.ListWindows();
            return Results.Content(json, "application/json");
        });

        // ── Window inspection ───────────────────────────────────────────────
        app.MapGet("/api/inspect/{process}", (string process, int? depth, int? maxElements) =>
        {
            var tools = CreateAgentTools();
            var json = tools.InspectWindow(process, depth ?? 3, maxElements ?? 50);
            return Results.Content(json, "application/json");
        });

        // ── Capabilities ───────────────────────────────────────────────────
        app.MapGet("/api/capabilities", () =>
        {
            var tools = CreateAgentTools();
            var json = tools.GetCapabilities();
            return Results.Content(json, "application/json");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AgentTools CreateAgentTools()
        => new(App.Config, App.Log,
               new FlowValidatorService(App.Log),
               App.FlowExecutor,
               App.Reports,
               App.Vision);

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

    private static async Task<IResult> HandleAsync(HttpContext ctx, Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = $"JSON parse error: {ex.Message}" });
        }
        catch (Exception ex)
        {
            App.Log?.Error("FlowEndpoints", $"Unhandled error: {ex.Message}");
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }
}

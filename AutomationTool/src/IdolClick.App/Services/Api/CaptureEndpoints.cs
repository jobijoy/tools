using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using IdolClick.Models;

namespace IdolClick.Services.Api;

internal static class CaptureEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/captures/recent", (int? max) =>
        {
            var events = App.SnapCapture.ListRecentCaptureEvents(max ?? 20)
                .Select(capture => new
                {
                    eventId = capture.EventId,
                    capturedAt = capture.CapturedAt,
                    profileId = capture.ProfileId,
                    profileName = capture.ProfileName,
                    note = capture.Note,
                    artifactCount = capture.Artifacts.Count,
                    failureCount = capture.Failures.Count,
                    metadataPath = capture.MetadataPath,
                    analysisPath = capture.AnalysisPath,
                    previewPath = capture.PreviewPath
                });
            return Results.Ok(events);
        });

        app.MapGet("/api/captures/{eventId}", (string eventId) =>
        {
            var capture = App.SnapCapture.GetCaptureEvent(eventId);
            return capture != null
                ? Results.Ok(capture)
                : Results.NotFound(new { error = $"Capture event not found: {eventId}" });
        });

        app.MapGet("/api/captures/{eventId}/analysis", (string eventId) =>
        {
            var analysis = App.SnapCapture.GetCaptureAnalysis(eventId);
            return analysis != null
                ? Results.Ok(analysis.Value)
                : Results.NotFound(new { error = $"Capture analysis not found: {eventId}" });
        });

        app.MapGet("/api/captures/perf", (int? sampleSize) =>
        {
            var snapshot = App.SnapCapture.GetPerformanceSnapshot(sampleSize ?? 50);
            return Results.Ok(snapshot);
        });

        app.MapGet("/api/captures/annotations/recent", (int? max) =>
        {
            var annotations = App.CaptureAnnotations.ListRecentAnnotations(max ?? 20);
            return Results.Ok(annotations);
        });

        app.MapGet("/api/captures/timeline", (int? max) =>
        {
            var timeline = App.CaptureAnnotations.ListMergedTimeline(max ?? 30);
            return Results.Ok(timeline);
        });

        app.MapGet("/api/captures/cleanup/profile/{profileId}/preview", (string profileId) =>
        {
            var preview = App.SnapCapture.PreviewDeleteProfileAndCaptures(profileId);
            return Results.Ok(preview);
        });

        app.MapPost("/api/captures/cleanup/profile/{profileId}", (string profileId) =>
        {
            var cleanup = App.SnapCapture.DeleteProfileAndCaptures(profileId);
            return Results.Ok(cleanup);
        });

        app.MapGet("/api/captures/cleanup/orphaned/preview", () =>
        {
            var preview = App.SnapCapture.PreviewOrphanedCapturesCleanup();
            return Results.Ok(preview);
        });

        app.MapPost("/api/captures/cleanup/orphaned", () =>
        {
            var cleanup = App.SnapCapture.CleanupOrphanedCaptures();
            return Results.Ok(cleanup);
        });

        app.MapGet("/api/review-buffers/recent", (int? max) =>
        {
            var bundles = App.ReviewBuffer.ListSavedBundles(max ?? 20);
            return Results.Ok(bundles);
        });

        app.MapGet("/api/review-buffers/{bundleId}", (string bundleId) =>
        {
            var bundle = App.ReviewBuffer.GetSavedBundle(bundleId);
            return bundle != null
                ? Results.Ok(bundle)
                : Results.NotFound(new { error = $"Review buffer not found: {bundleId}" });
        });

        app.MapPost("/api/capture-packs/run-from-prompt", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<CapturePackPromptRunRequest>(body, FlowJson.Options);
            if (request == null || string.IsNullOrWhiteSpace(request.Input))
                return Results.BadRequest(new { error = "Input is required" });

            var orchestrator = new PromptCapturePackOrchestratorService(
                App.Config,
                App.Log,
                App.Reports,
                App.SnapCapture,
                App.CaptureAnnotations,
                App.FlowExecutor);

            var result = await orchestrator.RunAsync(
                request.Input,
                request.ConfirmExecution,
                request.SmokeMode,
                ctx.RequestAborted);

            return Results.Ok(new
            {
                input = result.Input,
                packId = result.PackId,
                packName = result.PackName,
                packPath = result.PackPath,
                reason = result.Reason,
                inputs = result.ResolvedInputs,
                requiresConfirmation = result.RequiresConfirmation,
                executed = result.Executed,
                succeeded = result.Succeeded,
                error = result.Error,
                reportPath = result.ReportPath,
                report = result.Report != null
                    ? JsonSerializer.SerializeToElement(result.Report, CaptureProfilePackJson.Options)
                    : (JsonElement?)null
            });
        });
    }

    private sealed class CapturePackPromptRunRequest
    {
        public string Input { get; set; } = string.Empty;
        public bool ConfirmExecution { get; set; }
        public bool SmokeMode { get; set; } = true;
    }
}
using Microsoft.AspNetCore.SignalR;

namespace IdolClick.Services.Api;

// ═══════════════════════════════════════════════════════════════════════════════════
// EXECUTION HUB — SignalR hub for real-time execution events.
//
// Channels (server → client):
//   • StepProgress  — per-step status during flow execution
//   • AgentProgress — tool-call and thinking status during agent chat
//   • PipelineProgress — Pack pipeline phase transitions
//
// Architecture:
//   Hub is stateless — it's a broadcast surface. Backend services push events
//   by calling ExecutionHub.BroadcastXxx() static helpers, which resolve the
//   IHubContext from the running WebApplication's service provider.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// SignalR hub for real-time execution progress events.
/// Connected clients (React UI via WebView2) receive push updates.
/// </summary>
public sealed class ExecutionHub : Hub
{
    /// <summary>
    /// Client can call this to verify connectivity.
    /// </summary>
    public Task Ping() => Clients.Caller.SendAsync("Pong", DateTimeOffset.UtcNow);

    public override Task OnConnectedAsync()
    {
        App.Log?.Debug("ExecutionHub", $"Client connected: {Context.ConnectionId}");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        App.Log?.Debug("ExecutionHub", $"Client disconnected: {Context.ConnectionId}");
        return base.OnDisconnectedAsync(exception);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// HUB BROADCASTER — Static helper to push events from backend services.
//
// Usage from any service:
//   await HubBroadcaster.StepProgress(hubContext, stepData);
//
// The IHubContext<ExecutionHub> is resolved once at startup and stored in App.*.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Static helper to broadcast events to all connected SignalR clients.
/// </summary>
public static class HubBroadcaster
{
    /// <summary>
    /// Broadcast a step execution progress event.
    /// </summary>
    public static Task StepProgress(IHubContext<ExecutionHub> hub, StepProgressEvent evt)
        => hub.Clients.All.SendAsync("StepProgress", evt);

    /// <summary>
    /// Broadcast an agent progress event (tool calls, thinking).
    /// </summary>
    public static Task AgentProgress(IHubContext<ExecutionHub> hub, AgentProgressEvent evt)
        => hub.Clients.All.SendAsync("AgentProgress", evt);

    /// <summary>
    /// Broadcast a Pack pipeline progress event.
    /// </summary>
    public static Task PipelineProgress(IHubContext<ExecutionHub> hub, PipelineProgressEvent evt)
        => hub.Clients.All.SendAsync("PipelineProgress", evt);
}

// ── Event DTOs ──────────────────────────────────────────────────────────────

/// <summary>
/// Per-step execution progress pushed to the React UI.
/// </summary>
public sealed class StepProgressEvent
{
    /// <summary>1-based step index.</summary>
    public int StepIndex { get; init; }
    /// <summary>Total steps in the flow.</summary>
    public int TotalSteps { get; init; }
    /// <summary>Step description for display.</summary>
    public string Description { get; init; } = "";
    /// <summary>Current status: running, passed, failed, skipped.</summary>
    public string Status { get; init; } = "running";
    /// <summary>Optional error message if failed.</summary>
    public string? Error { get; init; }
    /// <summary>Elapsed milliseconds for this step.</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>
/// Agent tool-call progress pushed to the React UI.
/// Maps from the existing <see cref="AgentProgress"/> model.
/// </summary>
public sealed class AgentProgressEvent
{
    /// <summary>Kind of progress: tool_start, tool_end, thinking, streaming.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Tool name being called (if applicable).</summary>
    public string? ToolName { get; init; }
    /// <summary>Human-readable status text.</summary>
    public string Message { get; init; } = "";
    /// <summary>Partial streaming text (for incremental display).</summary>
    public string? PartialText { get; init; }
}

/// <summary>
/// Pack pipeline progress pushed to the React UI.
/// </summary>
public sealed class PipelineProgressEvent
{
    /// <summary>Pipeline phase: planning, compiling, validating, executing, reporting.</summary>
    public string Phase { get; init; } = "";
    /// <summary>Human-readable status.</summary>
    public string Message { get; init; } = "";
    /// <summary>0.0 to 1.0 progress fraction.</summary>
    public double Progress { get; init; }
    /// <summary>Current flow index (during execution phase).</summary>
    public int? CurrentFlowIndex { get; init; }
    /// <summary>Total flows in the pack.</summary>
    public int? TotalFlows { get; init; }
}

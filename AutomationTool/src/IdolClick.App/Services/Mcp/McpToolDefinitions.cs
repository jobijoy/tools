using System.ComponentModel;
using System.Text.Json;
using IdolClick.Models;
using ModelContextProtocol.Server;

namespace IdolClick.Services.Mcp;

// ═══════════════════════════════════════════════════════════════════════════════════
// MCP TOOL DEFINITIONS — [McpServerToolType] wrappers over existing AgentTools.
//
// Architecture:
//   MCP SDK v0.8.0-preview.1 discovers these methods via [McpServerTool] attribute.
//   Each method delegates to the existing AgentTools / PackAgentTools implementations
//   via the ServiceHost singleton registered in DI.
//
// Why wrappers (not direct attribute on AgentTools)?
//   • AgentTools is also consumed by M.E.AI function calling (IChatClient).
//   • MCP tool names use idolclick_* convention; M.E.AI uses method names.
//   • Wrappers let each consumer evolve independently.
//   • MCP tools can add MCP-specific behaviour (progress, logging to stderr).
//
// Adding a new MCP tool:
//   1. Add [McpServerTool] method here with matching Name from ToolCatalog.
//   2. Implement the logic (or delegate to AgentTools/PackAgentTools).
//   3. Tool is automatically registered when --mcp starts the server.
//   4. Update ToolCatalog.cs to keep the registry in sync.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// MCP-exposed discovery and execution tools.
/// Wraps existing AgentTools methods for the MCP stdio transport.
/// </summary>
[McpServerToolType]
public class McpDiscoveryTools
{
    private readonly ServiceHost _host;

    public McpDiscoveryTools(ServiceHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    [McpServerTool(Name = "idolclick_list_windows"),
     Description("Lists all visible windows on the desktop with process name, window title, and native handle. Use this to discover what applications are running and find targets for automation.")]
    public string ListWindows()
    {
        var tools = CreateAgentTools();
        return tools.ListWindows();
    }

    [McpServerTool(Name = "idolclick_inspect_window"),
     Description("Inspects a window's UI automation tree and returns elements with type, name, automationId, selector, and bounding rectangle. Use this to find exact selectors for test flow steps.")]
    public string InspectWindow(
        [Description("Process name or partial window title to inspect (e.g., 'notepad', 'chrome', 'Settings')")] string processName,
        [Description("Maximum depth to traverse the element tree")] int maxDepth = 3,
        [Description("Maximum number of elements to return")] int maxElements = 50)
    {
        var tools = CreateAgentTools();
        return tools.InspectWindow(processName, maxDepth, maxElements);
    }

    [McpServerTool(Name = "idolclick_list_processes"),
     Description("Lists running processes that have visible windows, with PID, process name, and main window title.")]
    public string ListProcesses()
    {
        var tools = CreateAgentTools();
        return tools.ListProcesses();
    }

    [McpServerTool(Name = "idolclick_get_capabilities"),
     Description("Returns all supported step actions, assertion types, selector formats, and active automation backend capabilities.")]
    public string GetCapabilities()
    {
        var tools = CreateAgentTools();
        return tools.GetCapabilities();
    }

    [McpServerTool(Name = "idolclick_capture_screenshot"),
     Description("Captures a screenshot of the current desktop and returns the file path.")]
    public string CaptureScreenshot()
    {
        var tools = CreateAgentTools();
        return tools.CaptureScreenshot();
    }

    [McpServerTool(Name = "idolclick_locate_by_vision"),
     Description("Uses LLM vision to locate a UI element by visual description when UIA selectors fail. Strictly a fallback — always try UIA selectors first.")]
    public async Task<string> LocateByVision(
        [Description("Natural language description of the element to find")] string description,
        [Description("Optional process name or window title to scope search")] string windowHint = "")
    {
        var tools = CreateAgentTools();
        return await tools.LocateByVision(description, windowHint);
    }

    private AgentTools CreateAgentTools() =>
        new(_host.Config, _host.Log, _host.FlowValidator,
            _host.FlowExecutor, _host.Reports, _host.Vision);
}

/// <summary>
/// MCP-exposed execution tools.
/// Wraps existing AgentTools flow execution methods.
/// </summary>
[McpServerToolType]
public class McpExecutionTools
{
    private readonly ServiceHost _host;

    public McpExecutionTools(ServiceHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    [McpServerTool(Name = "idolclick_validate_flow"),
     Description("Validates a test flow JSON string against the IdolClick schema and returns any errors or warnings. Always validate before execution.")]
    public string ValidateFlow(
        [Description("Complete test flow JSON string to validate")] string flowJson)
    {
        var tools = CreateAgentTools();
        return tools.ValidateFlow(flowJson);
    }

    [McpServerTool(Name = "idolclick_run_flow"),
     Description("Executes a validated test flow and returns the structured execution report with per-step results, timing, and element snapshots.")]
    public async Task<string> RunFlow(
        [Description("Complete test flow JSON string to execute")] string flowJson)
    {
        var tools = CreateAgentTools();
        return await tools.RunFlow(flowJson);
    }

    [McpServerTool(Name = "idolclick_list_reports"),
     Description("Lists recent execution reports saved on disk, most recent first.")]
    public string ListReports(
        [Description("Maximum number of reports to return")] int maxCount = 10)
    {
        var tools = CreateAgentTools();
        return tools.ListReports(maxCount);
    }

    private AgentTools CreateAgentTools() =>
        new(_host.Config, _host.Log, _host.FlowValidator,
            _host.FlowExecutor, _host.Reports, _host.Vision);
}

/// <summary>
/// MCP-exposed TestSpec tools — the primary interface for coding agent integration.
/// These are the new tools that enable the "coding agent → test → score → fix" loop.
/// </summary>
[McpServerToolType]
public class McpTestSpecTools
{
    private readonly ServiceHost _host;
    private readonly TestSpecRunner _runner;

    public McpTestSpecTools(ServiceHost host, TestSpecRunner runner)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    [McpServerTool(Name = "idolclick_run_test_spec"),
     Description("Executes a TestSpec — a high-level test specification in natural language. " +
         "The coding agent generates a TestSpec with steps described in plain English (no selectors needed). " +
         "IdolClick compiles it to an executable TestFlow using its LLM, runs it against the real UI, " +
         "and returns a scored report with per-step results and fix suggestions. " +
         "This is the PRIMARY tool for the coding agent → test → fix loop.")]
    public async Task<string> RunTestSpec(
                [Description("Complete TestSpec JSON string with specName, targetApp, steps[], and optional tags/timeout")] string specJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<TestSpec>(specJson, FlowJson.Options);
            if (spec == null)
                return JsonSerializer.Serialize(new { error = "Failed to parse TestSpec JSON. See schema at schemas/test-spec.schema.json." });

            if (string.IsNullOrWhiteSpace(spec.SpecName))
                return JsonSerializer.Serialize(new { error = "specName is required." });
            if (spec.Steps.Count == 0)
                return JsonSerializer.Serialize(new { error = "At least one step is required." });

            var report = await _runner.RunAsync(spec, cancellationToken);
            return JsonSerializer.Serialize(report, FlowJson.Options);
        }
        catch (OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = "TestSpec execution was cancelled." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"TestSpec execution failed: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "idolclick_generate_test_spec"),
     Description("Generates a TestSpec from a natural language feature description. " +
         "Provide an app target and describe what to test — IdolClick's LLM creates a structured TestSpec " +
         "that you can review, edit, and then pass to idolclick_run_test_spec.")]
    public async Task<string> GenerateTestSpec(
                [Description("Target application process name or window title")] string targetApp,
        [Description("Natural language description of what to test (e.g., 'Test the checkout flow with a valid credit card')")] string featureDescription,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var spec = await _runner.GenerateSpecAsync(targetApp, featureDescription, cancellationToken);
            return JsonSerializer.Serialize(spec, FlowJson.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to generate TestSpec: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "idolclick_get_last_spec_report"),
     Description("Returns the last TestSpec execution report. Use this for follow-up queries like " +
         "'what failed?' or 'show me the fix suggestions'. Returns the full scored report with per-step " +
         "results, screenshots, timing, and machine-readable fix suggestions.")]
    public string GetLastSpecReport()
    {
        var report = _runner.LastReport;
        if (report == null)
            return JsonSerializer.Serialize(new { error = "No TestSpec has been executed yet. Run idolclick_run_test_spec first." });

        return JsonSerializer.Serialize(report, FlowJson.Options);
    }
}

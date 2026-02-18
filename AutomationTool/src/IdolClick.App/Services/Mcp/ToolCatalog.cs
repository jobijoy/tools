using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdolClick.Services.Mcp;

// ═══════════════════════════════════════════════════════════════════════════════════
// TOOL CATALOG — Single source of truth for all IdolClick tool definitions.
//
// Architecture bridge between:
//   • AgentService (M.E.AI function calling — consumes [Description] methods)
//   • REST API (ToolEndpoints — GET /api/tools)
//   • Future MCP Server (ModelContextProtocol C# SDK — [McpServerTool])
//   • Future Copilot Extension (GitHub Skillset — tool manifest)
//
// Why this exists:
//   IdolClick has 17+ tools defined across AgentTools, PackAgentTools, and
//   the Template/Intent layer. Each consumer (LLM agent, REST API, MCP, Copilot)
//   needs the same metadata in a different format. ToolCatalog is the canonical
//   registry that all consumers read from.
//
// MCP compatibility:
//   Tool descriptors use MCP-compatible naming and JSON Schema format.
//   When the C# MCP SDK (`ModelContextProtocol` NuGet) is added in Phase 2,
//   each ToolDescriptor maps 1:1 to an [McpServerTool] registration.
//
// Design rules:
//   • ToolCatalog describes tools. It does NOT execute them.
//   • Execution stays in AgentTools / PackAgentTools / existing services.
//   • Tool names are "idolclick_{snake_case}" — globally unique per MCP spec.
//   • Categories group tools by capability for UI/discovery purposes.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single tool parameter with JSON Schema metadata.
/// </summary>
public sealed class ToolParameter
{
    /// <summary>Parameter name (camelCase for JSON).</summary>
    public string Name { get; init; } = "";

    /// <summary>Human-readable description for LLM/UI consumption.</summary>
    public string Description { get; init; } = "";

    /// <summary>JSON Schema type: "string", "integer", "boolean", "object", "array".</summary>
    public string JsonType { get; init; } = "string";

    /// <summary>Whether this parameter is required. Default: true.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Default value as string, if any.</summary>
    public string? Default { get; init; }
}

/// <summary>
/// Transport-agnostic tool descriptor. Describes a single IdolClick tool
/// in MCP-compatible format (name, description, JSON Schema input).
/// </summary>
public sealed class ToolDescriptor
{
    /// <summary>
    /// Globally unique tool name. Convention: "idolclick_{snake_case}".
    /// Maps to MCP tool name and AgentTools method name.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>Human-readable description. Used by LLMs to decide when to invoke.</summary>
    public string Description { get; init; } = "";

    /// <summary>Tool parameters (input schema).</summary>
    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];

    /// <summary>Whether the tool is async (returns Task).</summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// Tool category for grouping. Values: "discovery", "execution", "pack", "template".
    /// </summary>
    public string Category { get; init; } = "general";

    /// <summary>
    /// MCP-compatible risk annotation. "none", "low", "high".
    /// Used by MCP clients to prompt for confirmation on destructive operations.
    /// </summary>
    public string Risk { get; init; } = "none";

    /// <summary>
    /// Source class where this tool is implemented (for traceability).
    /// Not exposed to consumers — internal bookkeeping.
    /// </summary>
    [JsonIgnore]
    public string ImplementedIn { get; init; } = "";

    /// <summary>
    /// Generates MCP-compatible JSON Schema for this tool's input parameters.
    /// Used by: GET /api/tools, MCP tools/list response, Copilot Extension manifest.
    /// </summary>
    public JsonElement ToInputSchema()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in Parameters)
        {
            var prop = new Dictionary<string, object>
            {
                ["type"] = p.JsonType,
                ["description"] = p.Description
            };
            if (p.Default != null) prop["default"] = p.Default;

            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0) schema["required"] = required;

        return JsonSerializer.SerializeToElement(schema);
    }
}

/// <summary>
/// Central registry of all IdolClick tool definitions.
/// Single source of truth consumed by REST API, future MCP server, and Copilot Extension.
/// </summary>
public static class ToolCatalog
{
    /// <summary>All registered tool descriptors.</summary>
    public static IReadOnlyList<ToolDescriptor> All => _tools;

    /// <summary>Get a tool by its MCP-compatible name.</summary>
    public static ToolDescriptor? GetByName(string name) =>
        _tools.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Get all tools in a specific category.</summary>
    public static IReadOnlyList<ToolDescriptor> GetByCategory(string category) =>
        _tools.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Total number of registered tools.</summary>
    public static int Count => _tools.Length;

    /// <summary>
    /// Generates the MCP-compatible tools/list response payload.
    /// Can be serialized directly as the response to MCP "tools/list" JSON-RPC call.
    /// </summary>
    public static JsonElement ToMcpToolsList()
    {
        var tools = _tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.ToInputSchema()
        });

        return JsonSerializer.SerializeToElement(new { tools });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOOL REGISTRY — canonical definitions of all 17 IdolClick tools.
    //
    // Adding a tool:
    //   1. Implement the method in AgentTools/PackAgentTools/etc.
    //   2. Add descriptor here with matching name + parameters.
    //   3. Tool is automatically exposed via REST, and future MCP/Copilot.
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly ToolDescriptor[] _tools =
    [
        // ── Discovery Tools ─────────────────────────────────────────────────

        new()
        {
            Name = "idolclick_list_windows",
            Description = "Lists all visible windows on the desktop with process name, window title, and native handle. Use this to discover what applications are running and find targets for automation.",
            Parameters = [],
            Category = "discovery",
            ImplementedIn = "AgentTools.ListWindows"
        },

        new()
        {
            Name = "idolclick_inspect_window",
            Description = "Inspects a window's UI automation tree and returns elements with type, name, automationId, selector, and bounding rectangle. Use this to find exact selectors for test flow steps.",
            Parameters =
            [
                new() { Name = "processName", Description = "Process name or partial window title to inspect (e.g., 'notepad', 'chrome', 'Settings')", JsonType = "string" },
                new() { Name = "maxDepth", Description = "Maximum depth to traverse the element tree", JsonType = "integer", Required = false, Default = "3" },
                new() { Name = "maxElements", Description = "Maximum number of elements to return", JsonType = "integer", Required = false, Default = "50" }
            ],
            Category = "discovery",
            ImplementedIn = "AgentTools.InspectWindow"
        },

        new()
        {
            Name = "idolclick_list_processes",
            Description = "Lists running processes that have visible windows, with PID, process name, and main window title.",
            Parameters = [],
            Category = "discovery",
            ImplementedIn = "AgentTools.ListProcesses"
        },

        new()
        {
            Name = "idolclick_get_capabilities",
            Description = "Returns all supported step actions, assertion types, selector formats, and active automation backend capabilities.",
            Parameters = [],
            Category = "discovery",
            ImplementedIn = "AgentTools.GetCapabilities"
        },

        new()
        {
            Name = "idolclick_capture_screenshot",
            Description = "Captures a screenshot of the current desktop and returns the file path.",
            Parameters = [],
            Category = "discovery",
            ImplementedIn = "AgentTools.CaptureScreenshot"
        },

        new()
        {
            Name = "idolclick_locate_by_vision",
            Description = "Uses LLM vision to locate a UI element by visual description when UIA selectors fail. Strictly a fallback — always try UIA selectors first.",
            Parameters =
            [
                new() { Name = "description", Description = "Natural language description of the element to find" },
                new() { Name = "windowHint", Description = "Optional process name or window title to scope search", Required = false, Default = "" }
            ],
            IsAsync = true,
            Category = "discovery",
            ImplementedIn = "AgentTools.LocateByVision"
        },

        // ── Execution Tools ─────────────────────────────────────────────────

        new()
        {
            Name = "idolclick_validate_flow",
            Description = "Validates a test flow JSON string against the IdolClick schema and returns any errors or warnings. Always validate before execution.",
            Parameters =
            [
                new() { Name = "flowJson", Description = "Complete test flow JSON string to validate" }
            ],
            Category = "execution",
            ImplementedIn = "AgentTools.ValidateFlow"
        },

        new()
        {
            Name = "idolclick_run_flow",
            Description = "Executes a validated test flow and returns the structured execution report with per-step results, timing, and element snapshots.",
            Parameters =
            [
                new() { Name = "flowJson", Description = "Complete test flow JSON string to execute" }
            ],
            IsAsync = true,
            Risk = "high",
            Category = "execution",
            ImplementedIn = "AgentTools.RunFlow"
        },

        new()
        {
            Name = "idolclick_list_reports",
            Description = "Lists recent execution reports saved on disk, most recent first.",
            Parameters =
            [
                new() { Name = "maxCount", Description = "Maximum number of reports to return", JsonType = "integer", Required = false, Default = "10" }
            ],
            Category = "execution",
            ImplementedIn = "AgentTools.ListReports"
        },

        // ── Pack Orchestration Tools ────────────────────────────────────────

        new()
        {
            Name = "idolclick_run_pipeline",
            Description = "Runs the complete TestPack pipeline: Plan → Compile → Execute → Report. Returns confidence score, failures, fix queue, and coverage map.",
            Parameters =
            [
                new() { Name = "packJson", Description = "Complete TestPack JSON with packName, targets, inputs, guardrails" }
            ],
            IsAsync = true,
            Risk = "high",
            Category = "pack",
            ImplementedIn = "PackAgentTools.RunFullPipeline"
        },

        new()
        {
            Name = "idolclick_plan_test_pack",
            Description = "Plans test journeys and coverage from a TestPack input without executing. Preview before committing to execution.",
            Parameters =
            [
                new() { Name = "packJson", Description = "TestPack JSON with inputs (instructions, features, routes, risks) and targets" }
            ],
            IsAsync = true,
            Category = "pack",
            ImplementedIn = "PackAgentTools.PlanTestPack"
        },

        new()
        {
            Name = "idolclick_get_fix_queue",
            Description = "Returns the ranked fix queue from the latest TestPack execution with categories, causes, and FixPackets for coding agents.",
            Parameters = [],
            Category = "pack",
            ImplementedIn = "PackAgentTools.GetFixQueue"
        },

        new()
        {
            Name = "idolclick_confidence",
            Description = "Returns the confidence score breakdown: journey pass rate, coverage completion, perception reliability, and warning impact.",
            Parameters = [],
            Category = "pack",
            ImplementedIn = "PackAgentTools.GetConfidenceBreakdown"
        },

        new()
        {
            Name = "idolclick_analyze_report",
            Description = "Returns comprehensive analysis of latest TestPack execution: coverage map, failure summary, warning patterns, perception stats, and improvement suggestions.",
            Parameters = [],
            Category = "pack",
            ImplementedIn = "PackAgentTools.AnalyzeReport"
        },

        // ── Template / Intent Tools ─────────────────────────────────────────

        new()
        {
            Name = "idolclick_list_templates",
            Description = "Lists all registered flow templates with their intent kind, required/optional slots, risk level, and maturity.",
            Parameters = [],
            Category = "template",
            ImplementedIn = "TemplateEndpoints.ListAll"
        },

        new()
        {
            Name = "idolclick_resolve_intent",
            Description = "Classifies natural language input into an intent, scores template candidates, applies escalation policy, and returns a draft TestFlow if confidence is sufficient.",
            Parameters =
            [
                new() { Name = "input", Description = "Natural language automation request" },
                new() { Name = "interactive", Description = "Whether user is interactive (affects escalation)", JsonType = "boolean", Required = false, Default = "true" },
                new() { Name = "trustMode", Description = "Trust mode skips confirmation for Normal risk", JsonType = "boolean", Required = false, Default = "false" }
            ],
            Category = "template",
            ImplementedIn = "IntentSplitterService.Split"
        },

        new()
        {
            Name = "idolclick_build_from_template",
            Description = "Builds a TestFlow from a specific template using provided slot values. Manual override — bypasses intent classification.",
            Parameters =
            [
                new() { Name = "templateId", Description = "Template ID (e.g., 'browser-search', 'login-basic')" },
                new() { Name = "slots", Description = "Key-value pairs for template slots", JsonType = "object" },
                new() { Name = "input", Description = "Original user input for context", Required = false }
            ],
            Category = "template",
            ImplementedIn = "TemplateEndpoints.Build"
        },

        // ── TestSpec Tools (coding agent integration) ───────────────────

        new()
        {
            Name = "idolclick_run_test_spec",
            Description = "Executes a TestSpec — a high-level test specification in natural language. The coding agent generates a TestSpec with steps described in plain English (no selectors needed). IdolClick compiles it to an executable TestFlow using its LLM, runs it against the real UI, and returns a scored report with per-step results and fix suggestions. This is the PRIMARY tool for the coding agent → test → fix loop.",
            Parameters =
            [
                new() { Name = "specJson", Description = "Complete TestSpec JSON string with specName, targetApp, steps[] (action, description, expected), and optional tags/timeoutMs" }
            ],
            IsAsync = true,
            Risk = "high",
            Category = "testspec",
            ImplementedIn = "McpTestSpecTools.RunTestSpec"
        },

        new()
        {
            Name = "idolclick_generate_test_spec",
            Description = "Generates a TestSpec from a natural language feature description. Provide an app target and describe what to test — IdolClick's LLM creates a structured TestSpec that you can review, edit, and then pass to idolclick_run_test_spec.",
            Parameters =
            [
                new() { Name = "targetApp", Description = "Target application process name or window title" },
                new() { Name = "featureDescription", Description = "Natural language description of what to test (e.g., 'Test the checkout flow with a valid credit card')" }
            ],
            IsAsync = true,
            Category = "testspec",
            ImplementedIn = "McpTestSpecTools.GenerateTestSpec"
        },

        new()
        {
            Name = "idolclick_get_last_spec_report",
            Description = "Returns the last TestSpec execution report. Use this for follow-up queries like 'what failed?' or 'show me the fix suggestions'. Returns the full scored report with per-step results, screenshots, timing, and machine-readable fix suggestions.",
            Parameters = [],
            Category = "testspec",
            ImplementedIn = "McpTestSpecTools.GetLastSpecReport"
        }
    ];
}

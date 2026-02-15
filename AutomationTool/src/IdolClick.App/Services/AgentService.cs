using Microsoft.Extensions.AI;
using IdolClick.Models;
using System.Text.Json;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// AGENT SERVICE — LLM integration for the AI agent mode.
//
// Responsibilities:
//   • Manages the IChatClient lifecycle (create/dispose/reconfigure)
//   • Maintains conversation history per session
//   • Runs the manual tool-calling loop with progress callbacks
//   • Detects structured test flows in LLM output (JSON blocks)
//   • Provides connection status for the UI
//
// Architecture:
//   Uses a manual tool-calling loop (NOT FunctionInvokingChatClient) so the UI
//   can display real-time progress: which tool is executing, intermediate text,
//   and per-step status during long-running flow executions.
//
// Uses Microsoft.Extensions.AI abstraction — works with:
//   Azure OpenAI, GitHub Models, OpenAI, Ollama, any OpenAI-compatible endpoint
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Interface for the AI agent service that powers the Agent mode.
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Whether the agent has a valid configuration and is ready to chat.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Human-readable status string for the UI (e.g., "Connected · gpt-4o").
    /// </summary>
    string StatusText { get; }

    /// <summary>
    /// Send a user message and get the agent's response.
    /// </summary>
    Task<AgentResponse> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fired when the agent starts or completes a tool call, allowing the UI
    /// to show real-time progress instead of a static "thinking" indicator.
    /// </summary>
    event Action<AgentProgress>? OnProgress;

    /// <summary>
    /// Clear conversation history and start fresh.
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// Reconfigure the client (called after settings change).
    /// </summary>
    void Reconfigure();
}

/// <summary>
/// Response from the agent, containing the text reply and optionally a parsed test flow.
/// </summary>
public class AgentResponse
{
    /// <summary>
    /// The full text response from the LLM.
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// If the response contains a structured test flow JSON, it's parsed here.
    /// </summary>
    public TestFlow? Flow { get; set; }

    /// <summary>
    /// Whether the response contains a runnable test flow.
    /// </summary>
    public bool HasFlow => Flow != null;

    /// <summary>
    /// True if the response was an error (client not configured, API error, etc.).
    /// </summary>
    public bool IsError { get; set; }
}

/// <summary>
/// Progress update emitted during the tool-calling loop.
/// </summary>
public class AgentProgress
{
    /// <summary>The kind of progress event.</summary>
    public AgentProgressKind Kind { get; set; }

    /// <summary>Human-readable summary (e.g. "Running flow 'Open YouTube'...").</summary>
    public string Message { get; set; } = "";

    /// <summary>Name of the tool being called (e.g. "RunFlow", "InspectWindow").</summary>
    public string? ToolName { get; set; }

    /// <summary>Intermediate text from the LLM (before tool calls).</summary>
    public string? IntermediateText { get; set; }

    /// <summary>The current iteration number in the tool-calling loop.</summary>
    public int Iteration { get; set; }
}

public enum AgentProgressKind
{
    /// <summary>LLM produced intermediate text before making tool calls.</summary>
    IntermediateText,
    /// <summary>A tool call is starting.</summary>
    ToolCallStarting,
    /// <summary>A tool call completed.</summary>
    ToolCallCompleted,
    /// <summary>Starting a new LLM round (sending tool results back).</summary>
    NewIteration
}

/// <summary>
/// AI agent service powered by Microsoft.Extensions.AI.
/// Manages LLM communication, conversation state, and test flow detection.
/// </summary>
public class AgentService : IAgentService, IDisposable
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private IChatClient? _client;
    private readonly List<ChatMessage> _history = [];
    private List<AITool> _tools = [];
    private AgentTools? _agentTools;
    private bool _configured;
    private StepExecutor? _executor;
    private ReportService? _reportService;
    private VisionService? _visionService;
    private Dictionary<string, AIFunction> _toolMap = new(); // name → function for invocation

    /// <summary>Max tool-calling iterations before forcing a final response.</summary>
    private const int MaxToolIterations = 15;

    public event Action<AgentProgress>? OnProgress;

    public AgentService(ConfigService config, LogService log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        TryCreateClient();
    }

    /// <summary>
    /// Sets the flow executor, report service, and vision service for closed-loop execution.
    /// Called after App.FlowExecutor is initialized.
    /// </summary>
    public void SetExecutionServices(StepExecutor executor, ReportService reportService, VisionService? visionService = null)
    {
        _executor = executor;
        _reportService = reportService;
        _visionService = visionService;
        // Recreate tools with execution + vision capability
        if (_configured)
        {
            var validator = new FlowValidatorService(_log);
            _agentTools = new AgentTools(_config, _log, validator, _executor, _reportService, _visionService);
            _tools = CreateToolsAndMap(_agentTools);
            _log.Info("Agent", "Execution services connected — closed-loop enabled" +
                (_visionService != null ? " + vision fallback" : ""));
        }
    }

    public bool IsConfigured => _configured;

    public string StatusText
    {
        get
        {
            if (!_configured) return "Not configured";
            var settings = _config.GetConfig().AgentSettings;
            return $"Connected · {settings.ModelId}";
        }
    }

    public async Task<AgentResponse> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (!_configured || _client == null)
        {
            return new AgentResponse
            {
                Text = "Agent is not configured. Go to Settings → Agent and set your LLM endpoint and API key.",
                IsError = true
            };
        }

        try
        {
            // Add user message to history
            _history.Add(new ChatMessage(ChatRole.User, userMessage));
            _log.Debug("Agent", $"Sending message ({userMessage.Length} chars, {_history.Count} messages in history)");

            var settings = _config.GetConfig().AgentSettings;

            var options = new ChatOptions
            {
                MaxOutputTokens = settings.MaxTokens,
                Temperature = settings.Temperature is > 0 and not 1.0 ? (float)settings.Temperature : null,
                Tools = [.. _tools],
            };

            // ── Manual tool-calling loop ────────────────────────────────────
            // Instead of FunctionInvokingChatClient (opaque, no progress),
            // we drive the loop ourselves so we can emit progress events.
            string finalText = "";
            int iteration = 0;

            while (iteration < MaxToolIterations)
            {
                iteration++;
                cancellationToken.ThrowIfCancellationRequested();

                _log.Debug("Agent", $"Tool loop iteration {iteration}");

                var response = await _client.GetResponseAsync(_history, options, cancellationToken);

                // Collect all messages from the response
                var toolCallMessages = new List<ChatMessage>();
                bool hasToolCalls = false;
                string intermediateText = "";

                foreach (var msg in response.Messages)
                {
                    _history.Add(msg);
                    toolCallMessages.Add(msg);

                    // Check for tool call content
                    if (msg.Role == ChatRole.Assistant)
                    {
                        foreach (var content in msg.Contents)
                        {
                            if (content is FunctionCallContent)
                                hasToolCalls = true;
                            else if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                                intermediateText += tc.Text;
                        }
                    }
                }

                // If the LLM produced intermediate text alongside tool calls, emit it
                if (!string.IsNullOrWhiteSpace(intermediateText) && hasToolCalls)
                {
                    OnProgress?.Invoke(new AgentProgress
                    {
                        Kind = AgentProgressKind.IntermediateText,
                        Message = intermediateText,
                        IntermediateText = intermediateText,
                        Iteration = iteration
                    });
                }

                // No tool calls → we have the final response
                if (!hasToolCalls)
                {
                    finalText = response.Text ?? "";
                    break;
                }

                // ── Execute tool calls ──────────────────────────────────────
                var toolResultMessages = new List<ChatMessage>();

                foreach (var msg in toolCallMessages)
                {
                    foreach (var content in msg.Contents)
                    {
                        if (content is FunctionCallContent fcc)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var toolDisplayName = MapToolDisplayName(fcc.Name);
                            _log.Info("Agent", $"Calling tool: {fcc.Name}");

                            OnProgress?.Invoke(new AgentProgress
                            {
                                Kind = AgentProgressKind.ToolCallStarting,
                                ToolName = fcc.Name,
                                Message = $"⚙ {toolDisplayName}...",
                                Iteration = iteration
                            });

                            // Invoke the tool
                            string toolResult;
                            try
                            {
                                if (_toolMap.TryGetValue(fcc.Name, out var aiFunc))
                                {
                                    var args = fcc.Arguments != null
                                        ? new AIFunctionArguments(fcc.Arguments)
                                        : new AIFunctionArguments();
                                    var result = await aiFunc.InvokeAsync(args, cancellationToken);
                                    toolResult = result?.ToString() ?? "null";
                                }
                                else
                                {
                                    toolResult = JsonSerializer.Serialize(new { error = $"Unknown tool: {fcc.Name}" });
                                }
                            }
                            catch (Exception ex)
                            {
                                toolResult = JsonSerializer.Serialize(new { error = ex.Message });
                                _log.Error("Agent", $"Tool '{fcc.Name}' threw: {ex.Message}");
                            }

                            _log.Debug("Agent", $"Tool '{fcc.Name}' returned {toolResult.Length} chars");

                            OnProgress?.Invoke(new AgentProgress
                            {
                                Kind = AgentProgressKind.ToolCallCompleted,
                                ToolName = fcc.Name,
                                Message = $"✓ {toolDisplayName} done",
                                Iteration = iteration
                            });

                            // Build tool result message
                            var resultContent = new FunctionResultContent(fcc.CallId, toolResult);
                            var resultMsg = new ChatMessage(ChatRole.Tool, [resultContent]);
                            _history.Add(resultMsg);
                        }
                    }
                }

                // Emit progress for the next LLM round
                OnProgress?.Invoke(new AgentProgress
                {
                    Kind = AgentProgressKind.NewIteration,
                    Message = "🤖 Analyzing results...",
                    Iteration = iteration + 1
                });
            }

            if (iteration >= MaxToolIterations)
            {
                _log.Warn("Agent", $"Reached max tool iterations ({MaxToolIterations})");
                if (string.IsNullOrWhiteSpace(finalText))
                    finalText = "(Agent reached the maximum number of tool-calling iterations. Please try a simpler request or break it into steps.)";
            }

            _log.Debug("Agent", $"Response received ({finalText.Length} chars, {_history.Count} messages in history)");

            // Try to detect a test flow in the response
            var flow = TryParseTestFlow(finalText);
            if (flow != null)
            {
                _log.Info("Agent", $"Detected test flow: '{flow.TestName}' with {flow.Steps.Count} steps");
            }

            return new AgentResponse
            {
                Text = finalText,
                Flow = flow
            };
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Agent", "Request cancelled");
            return new AgentResponse { Text = "Request was cancelled.", IsError = true };
        }
        catch (Exception ex)
        {
            _log.Error("Agent", $"LLM request failed: {ex.Message}");

            // Remove the user message we just added since the request failed
            if (_history.Count > 0 && _history[^1].Role == ChatRole.User)
                _history.RemoveAt(_history.Count - 1);

            return new AgentResponse
            {
                Text = $"Error communicating with LLM: {ex.Message}",
                IsError = true
            };
        }
    }

    /// <summary>Maps internal tool names to user-friendly display names.</summary>
    private static string MapToolDisplayName(string toolName) => toolName switch
    {
        "ListWindows" => "Listing windows",
        "InspectWindow" => "Inspecting window UI",
        "ListProcesses" => "Listing processes",
        "ValidateFlow" => "Validating flow",
        "RunFlow" => "Running automation flow",
        "ListReports" => "Checking reports",
        "CaptureScreenshot" => "Capturing screenshot",
        "GetCapabilities" => "Reading capabilities",
        "LocateByVision" => "Vision locate",
        _ => toolName
    };

    public void ClearHistory()
    {
        _history.Clear();
        // Re-add system prompt
        AddSystemPrompt();
        _log.Info("Agent", "Conversation history cleared");
    }

    public void Reconfigure()
    {
        DisposeClient();
        TryCreateClient();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // CLIENT LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════════════════

    private void TryCreateClient()
    {
        var settings = _config.GetConfig().AgentSettings;

        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _configured = false;
            _log.Debug("Agent", "Agent not configured — missing endpoint or API key");
            return;
        }

        try
        {
            // Auto-detect Azure OpenAI vs generic OpenAI-compatible endpoint
            var credential = new System.ClientModel.ApiKeyCredential(settings.ApiKey);
            OpenAI.Chat.ChatClient chatClient;

            if (IsAzureOpenAIEndpoint(settings.Endpoint))
            {
                // Azure OpenAI: uses AzureOpenAIClient with different URL structure + API versioning
                var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
                    new Uri(settings.Endpoint), credential);
                chatClient = azureClient.GetChatClient(settings.ModelId);
                _log.Info("Agent", "Using Azure OpenAI client");
            }
            else
            {
                // Generic OpenAI-compatible (GitHub Models, Ollama, OpenAI, etc.)
                var openAiClient = new OpenAI.OpenAIClient(credential, new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(settings.Endpoint)
                });
                chatClient = openAiClient.GetChatClient(settings.ModelId);
                _log.Info("Agent", "Using OpenAI-compatible client");
            }

            var innerClient = chatClient.AsIChatClient();
            // Use the raw client — we drive the tool-calling loop manually for progress visibility
            _client = innerClient;

            // Create agent tools for function calling
            var validator = new FlowValidatorService(_log);
            _agentTools = new AgentTools(_config, _log, validator, _executor, _reportService, _visionService);
            _tools = CreateToolsAndMap(_agentTools);

            _configured = true;
            _history.Clear();
            AddSystemPrompt();

            _log.Info("Agent", $"Agent configured: {settings.Endpoint} / {settings.ModelId}");
        }
        catch (Exception ex)
        {
            _configured = false;
            _log.Error("Agent", $"Failed to create LLM client: {ex.Message}");
        }
    }

    /// <summary>
    /// Detects Azure OpenAI endpoints by hostname pattern.
    /// Azure OpenAI uses AzureOpenAIClient (handles API versioning + different URL structure).
    /// </summary>
    private static bool IsAzureOpenAIEndpoint(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private void AddSystemPrompt()
    {
        var settings = _config.GetConfig().AgentSettings;
        var systemPrompt = settings.SystemPrompt;

        // Append tool-aware DSL instructions
        systemPrompt += @"

## You are the IdolClick AI Agent — an intelligent UI automation assistant.

### Tools Available
You have function-calling tools to explore the desktop:
- **ListWindows**: See all visible windows (process name, title, handle)
- **InspectWindow**: Examine UI elements inside a window (type, name, automationId, bounds)
- **ListProcesses**: See running processes with window titles
- **ValidateFlow**: Validate a test flow JSON before execution
- **RunFlow**: Execute a validated test flow and get structured report (CLOSED LOOP)
- **ListReports**: See recent execution report history
- **CaptureScreenshot**: Take a screenshot of the current desktop
- **GetCapabilities**: See all supported actions, assertions, and selector format
- **LocateByVision**: [FALLBACK] Use LLM vision to find elements when UIA selectors fail

### Element Resolution Chain (STRICT PRIORITY)
1. **UIA Selector** (primary) — Always try InspectWindow + UIA selectors first
2. **Vision Fallback** — Only when UIA selectors provably fail, use LocateByVision
   - Requires vision to be enabled in settings
   - Returns bounding box coordinates + confidence score
   - Confidence must meet threshold to be usable
   - NEVER skip UIA and go straight to vision

### Workflow
1. When asked to automate something, first use tools to explore the target UI
2. Use InspectWindow to find exact selectors before generating flows
3. Generate a structured test flow JSON
4. Validate with ValidateFlow
5. Execute with RunFlow — get structured report back
6. If steps failed due to selector resolution, try LocateByVision as fallback
7. Fix the flow and re-run until all steps pass

### Test Flow DSL (JSON v1)
```json
{
  ""schemaVersion"": 1,
  ""testName"": ""Descriptive test name"",
  ""targetApp"": ""processname"",
  ""steps"": [
    {
      ""order"": 1,
      ""action"": ""launch"",
      ""processPath"": ""notepad.exe"",
      ""description"": ""Open Notepad"",
      ""delayAfterMs"": 2000
    },
    {
      ""order"": 2,
      ""action"": ""click"",
      ""selector"": ""Button#Save"",
      ""description"": ""Click the Save button"",
      ""delayAfterMs"": 500
    },
    {
      ""order"": 3,
      ""action"": ""assert_text"",
      ""selector"": ""Label#Status"",
      ""contains"": ""Saved"",
      ""assertions"": [
        { ""type"": ""exists"", ""selector"": ""Button#OK"", ""description"": ""OK button appeared"" }
      ]
    }
  ]
}
```

### Actions (snake_case in JSON):
click, type, send_keys, wait, assert_exists, assert_not_exists, assert_text, assert_window, navigate, screenshot, scroll, focus_window, launch

### Selector Format: ""ElementType#TextOrAutomationId""
Examples: ""Button#Save"", ""TextBox#SearchField"", ""Window#Untitled - Notepad""

### Post-Step Assertions (optional per step):
Types: exists, not_exists, text_contains, text_equals, window_title, process_running

### Critical Rules
- **ALWAYS call InspectWindow BEFORE generating any flow** — you MUST discover actual UIA selectors, never guess
- Prefer AutomationId over text in selectors (stable across languages)
- Include a description for every step
- **Always add delayAfterMs: 2000 after launch steps** — apps need time to render their UIA tree
- **Add delayAfterMs: 500 after click steps** that trigger UI transitions (new windows, dialogs, page loads)
- For send_keys, use the ""keys"" field with key names: ""Enter"", ""Tab"", ""Escape"", etc.
- For navigate, use the ""url"" field. **Set the ""app"" field to the browser process name** (e.g. ""chrome"", ""msedge"", ""firefox"") to open in a specific browser. If ""app"" is omitted, the OS default browser is used. The navigate action waits for the page to load before continuing.
- **After navigate, set windowTitle on subsequent steps** to match the page (e.g. ""YouTube"") so the correct browser tab/window is targeted — otherwise an existing browser window may be picked instead of the new tab
- For Windows keyboard shortcuts with the Windows key, use ""Win+D"", ""Win+R"", etc. in keys
- When the flow needs to interact with browser content (YouTube search, web forms), use send_keys with ""/"" or ""Ctrl+L"" to focus the search/address bar FIRST, then type the query, then send_keys ""Enter"" to submit
- Validate flows with ValidateFlow before execution
- Execute flows with RunFlow for closed-loop automation
- If a flow fails, read the error, call InspectWindow again to find correct selectors, and re-run
- Keep explanations concise";

        _history.Add(new ChatMessage(ChatRole.System, systemPrompt));
    }

    private void DisposeClient()
    {
        if (_client is IDisposable disposable)
            disposable.Dispose();
        _client = null;
        _tools = [];
        _toolMap.Clear();
        _agentTools = null;
        _configured = false;
    }

    /// <summary>
    /// Creates AITool definitions from AgentTools methods for LLM function calling,
    /// and populates the _toolMap for manual invocation.
    /// </summary>
    private List<AITool> CreateToolsAndMap(AgentTools tools)
    {
        var type = typeof(AgentTools);
        var functions = new[]
        {
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.ListWindows))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.InspectWindow))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.ListProcesses))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.ValidateFlow))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.RunFlow))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.ListReports))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.CaptureScreenshot))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.GetCapabilities))!, tools),
            AIFunctionFactory.Create(type.GetMethod(nameof(AgentTools.LocateByVision))!, tools),
        };

        // Build name→function map for manual invocation
        _toolMap.Clear();
        var toolList = new List<AITool>();
        foreach (var f in functions)
        {
            toolList.Add(f);
            if (f is AIFunction af)
                _toolMap[af.Name] = af;
        }

        return toolList;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TEST FLOW DETECTION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to extract and parse a TestFlow JSON from the LLM response text.
    /// Looks for ```json ... ``` code blocks containing a "testName" and "steps" array.
    /// </summary>
    private static TestFlow? TryParseTestFlow(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return null;

        try
        {
            // Look for JSON code blocks
            var jsonStart = responseText.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (jsonStart < 0) jsonStart = responseText.IndexOf("```JSON", StringComparison.OrdinalIgnoreCase);
            if (jsonStart < 0) return null;

            // Find the actual JSON start (after ```json\n)
            var contentStart = responseText.IndexOf('\n', jsonStart);
            if (contentStart < 0) return null;
            contentStart++;

            // Find the closing ```
            var contentEnd = responseText.IndexOf("```", contentStart);
            if (contentEnd < 0) return null;

            var jsonBlock = responseText[contentStart..contentEnd].Trim();

            // Quick sanity check — must look like a test flow
            if (!jsonBlock.Contains("\"steps\"", StringComparison.OrdinalIgnoreCase))
                return null;

            var flow = System.Text.Json.JsonSerializer.Deserialize<TestFlow>(jsonBlock, FlowJson.Options);

            // Validate minimum structure
            if (flow == null || flow.Steps.Count == 0)
                return null;

            return flow;
        }
        catch
        {
            // Not valid JSON or not a test flow — that's fine
            return null;
        }
    }

    public void Dispose()
    {
        DisposeClient();
        GC.SuppressFinalize(this);
    }
}

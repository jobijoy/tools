using Microsoft.Extensions.AI;
using IdolClick.Models;
using IdolClick.Services.Packs;
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

    /// <summary>
    /// Stateless single-shot completion: sends a prompt to the LLM without history,
    /// system prompt, or tool definitions. Returns raw text. Used for structured
    /// JSON generation tasks (e.g., TestSpec → TestFlow compilation).
    /// </summary>
    Task<string> CompletionAsync(string prompt, CancellationToken cancellationToken = default);
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
    private PackAgentTools? _packTools;
    private bool _configured;
    private StepExecutor? _executor;
    private ReportService? _reportService;
    private VisionService? _visionService;
    private Dictionary<string, AIFunction> _toolMap = new(); // name → function for invocation
    private string? _cachedSystemPrompt; // R3.1: Avoid rebuilding every call

    /// <summary>Max tool-calling iterations before forcing a final response.</summary>
    private const int MaxToolIterations = 30;

    /// <summary>Running token estimate maintained incrementally (chars / 4).</summary>
    private long _estimatedTokens;

    public event Action<AgentProgress>? OnProgress;

    /// <summary>
    /// Exposes the current IChatClient for use by the API layer (e.g., Pack endpoints).
    /// Returns null if the agent is not configured.
    /// </summary>
    public IChatClient? GetChatClient() => _client;

    public AgentService(ConfigService config, LogService log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        TryCreateClient();
    }

    /// <inheritdoc />
    public async Task<string> CompletionAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // Auto-retry configuration
        if (!_configured || _client == null)
        {
            _log.Debug("Agent", "Agent not configured on CompletionAsync — retrying TryCreateClient()");
            TryCreateClient();
        }
        if (!_configured || _client == null)
            throw new InvalidOperationException("Agent is not configured. Set LLM endpoint/key in config.json.");

        var settings = _config.GetConfig().AgentSettings;
        var options = new ChatOptions
        {
            MaxOutputTokens = settings.MaxTokens,
            Temperature = settings.Temperature is > 0 and < 1.0 ? (float)settings.Temperature : null,
            // No tools — stateless, JSON-only completion
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt)
        };

        _log.Debug("Agent", $"CompletionAsync: sending {prompt.Length} chars (no tools, no history)");

        var response = await _client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var text = response.Text ?? "";

        _log.Debug("Agent", $"CompletionAsync: received {text.Length} chars");
        return text;
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
            // Create PackAgentTools if PackOrchestrator is available
            if (App.PackOrchestrator != null)
            {
                _packTools = new PackAgentTools(_log, App.PackOrchestrator, () => _client);
            }
            _tools = CreateToolsAndMap(_agentTools, _packTools);
            _log.Info("Agent", "Execution services connected — closed-loop enabled" +
                (_visionService != null ? " + vision fallback" : "") +
                (_packTools != null ? " + pack orchestration" : ""));
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
    {        // Auto-retry configuration — handles race conditions during startup
        // where config may not have been fully loaded on first TryCreateClient()
        if (!_configured || _client == null)
        {
            _log.Debug("Agent", "Agent not configured on call — retrying TryCreateClient()");
            TryCreateClient();
        }
        if (!_configured || _client == null)
        {
            return new AgentResponse
            {
                Text = "Agent is not configured. Go to Settings → Agent and set your LLM endpoint and API key.",
                IsError = true
            };
        }

        // Track history length before this turn for rollback on failure
        int preLoopHistoryCount = _history.Count;

        try
        {
            // Add user message to history
            AddMessage(new ChatMessage(ChatRole.User, userMessage));
            _log.Debug("Agent", $"Sending message ({userMessage.Length} chars, {_history.Count} messages in history)");

            // Compact history if it exceeds the sliding window limit
            CompactHistory();

            var settings = _config.GetConfig().AgentSettings;

            var options = new ChatOptions
            {
                MaxOutputTokens = settings.MaxTokens,
                // Some models (gpt-5.2, o-series) only accept the default temperature.
                // Only send a custom value when explicitly set and non-default.
                Temperature = settings.Temperature is > 0 and < 1.0 ? (float)settings.Temperature : null,
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

                // Token budget check — compact aggressively if nearing limit
                _estimatedTokens = EstimateTokens();
                var maxTokens = settings.MaxSessionTokens;
                if (maxTokens > 0 && _estimatedTokens > maxTokens * 0.85)
                {
                    _log.Warn("Agent", $"Token budget warning: ~{_estimatedTokens} / {maxTokens} (~{_estimatedTokens * 100 / maxTokens}%)");
                    CompactHistory();
                }

                _log.Debug("Agent", $"Tool loop iteration {iteration} (~{_estimatedTokens} tokens, {_history.Count} msgs)");

                var response = await GetResponseWithRetryAsync(_history, options, cancellationToken).ConfigureAwait(false);

                // Collect all messages from the response
                var toolCallMessages = new List<ChatMessage>();
                bool hasToolCalls = false;
                string intermediateText = "";

                foreach (var msg in response.Messages)
                {
                    AddMessage(msg);
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
                                    var result = await aiFunc.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
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
                            AddMessage(resultMsg);
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
            // Rollback any partial messages added during the aborted turn
            if (_history.Count > preLoopHistoryCount)
            {
                _history.RemoveRange(preLoopHistoryCount, _history.Count - preLoopHistoryCount);
                _estimatedTokens = EstimateTokens();
            }
            return new AgentResponse { Text = "Request was cancelled.", IsError = true };
        }
        catch (Exception ex)
        {
            _log.Error("Agent", $"LLM request failed: {ex.Message}");

            // Rollback ALL messages added during this turn (user message + any partial LLM/tool messages)
            // to prevent history corruption from half-completed tool-call sequences.
            if (_history.Count > preLoopHistoryCount)
            {
                _history.RemoveRange(preLoopHistoryCount, _history.Count - preLoopHistoryCount);
                _estimatedTokens = EstimateTokens();
            }
            else if (_history.Count > 0 && _history[^1].Role == ChatRole.User)
            {
                _history.RemoveAt(_history.Count - 1);
                _estimatedTokens = EstimateTokens();
            }

            return new AgentResponse
            {
                Text = $"Error communicating with LLM: {ex.Message}",
                IsError = true
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // LLM RETRY — Exponential backoff with jitter for transient failures
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Max retry attempts for transient LLM failures.</summary>
    private const int MaxLlmRetries = 3;

    /// <summary>Base delays for exponential backoff: 1s, 3s, 8s.</summary>
    private static readonly int[] RetryDelaysMs = [1000, 3000, 8000];

    private static readonly Random _jitterRng = new();

    /// <summary>
    /// Calls <c>_client.GetResponseAsync</c> with exponential backoff retry on transient errors.
    /// Retries on: HttpRequestException, TaskCanceledException (timeout, not user cancel), rate-limit responses.
    /// </summary>
    private async Task<ChatResponse> GetResponseWithRetryAsync(
        List<ChatMessage> messages, ChatOptions options, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _client!.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxLlmRetries && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var baseDelay = attempt < RetryDelaysMs.Length ? RetryDelaysMs[attempt] : RetryDelaysMs[^1];
                var jitter = _jitterRng.Next(-500, 501);
                var delayMs = Math.Max(200, baseDelay + jitter);

                _log.Warn("Agent", $"LLM request failed (attempt {attempt + 1}/{MaxLlmRetries + 1}): {ex.GetType().Name}: {ex.Message} — retrying in {delayMs}ms");
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Determines if an exception is transient and worth retrying.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => true,
        TaskCanceledException tce when tce.InnerException is TimeoutException => true,
        _ when ex.Message.Contains("429", StringComparison.Ordinal) => true, // Rate limit
        _ when ex.Message.Contains("503", StringComparison.Ordinal) => true, // Service unavailable
        _ when ex.Message.Contains("502", StringComparison.Ordinal) => true, // Bad gateway
        _ when ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) => true,
        _ => false
    };

    // ═══════════════════════════════════════════════════════════════════════════════
    // HISTORY COMPACTION — Sliding window + rule-based trimming
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compacts conversation history when it exceeds <c>MaxHistoryMessages</c>.
    /// Strategy: preserve system prompt + last N messages; summarize removed tool results.
    /// </summary>
    private void CompactHistory()
    {
        var settings = _config.GetConfig().AgentSettings;
        var maxMessages = Math.Max(settings.MaxHistoryMessages, 10);

        if (_history.Count <= maxMessages) return;

        // Always keep index 0 (system prompt) and the tail
        int keepTail = maxMessages / 2; // Keep the most recent half
        int removeCount = _history.Count - 1 - keepTail; // How many from index 1..N to remove
        if (removeCount <= 0) return;

        // Count tool results being removed for the summary + deduct tokens
        int toolResultsRemoved = 0;
        int assistantMsgsRemoved = 0;
        for (int i = 1; i <= removeCount; i++)
        {
            _estimatedTokens -= EstimateMessageTokens(_history[i]);
            if (_history[i].Role == ChatRole.Tool)
                toolResultsRemoved++;
            else if (_history[i].Role == ChatRole.Assistant)
                assistantMsgsRemoved++;
        }

        _log.Debug("Agent", $"Compacting history: {_history.Count} → ~{1 + keepTail + 1} messages " +
            $"(removing {removeCount}: {toolResultsRemoved} tool results, {assistantMsgsRemoved} assistant msgs)");

        // Insert a summary message at position 1 (after system prompt)
        var summaryMsg = new ChatMessage(ChatRole.System,
            $"[History compacted: {removeCount} earlier messages removed. " +
            $"{toolResultsRemoved} tool results and {assistantMsgsRemoved} assistant messages were trimmed. " +
            $"The most recent conversation continues below.]");

        // Remove old messages (from index 1 to removeCount)
        _history.RemoveRange(1, removeCount);

        // Insert summary at position 1
        _history.Insert(1, summaryMsg);
        _estimatedTokens += EstimateMessageTokens(summaryMsg);

        _log.Debug("Agent", $"History compacted to {_history.Count} messages (~{_estimatedTokens} tokens)");
    }

    /// <summary>
    /// Adds a message to history and updates the running token estimate.
    /// </summary>
    private void AddMessage(ChatMessage msg)
    {
        _history.Add(msg);
        _estimatedTokens += EstimateMessageTokens(msg);
    }

    /// <summary>
    /// Estimates tokens for a single message (chars / 4).
    /// </summary>
    private static long EstimateMessageTokens(ChatMessage msg)
    {
        long chars = 0;
        foreach (var content in msg.Contents)
        {
            if (content is TextContent tc)
                chars += tc.Text?.Length ?? 0;
            else if (content is FunctionCallContent fcc)
                chars += (fcc.Name?.Length ?? 0) + 50;
            else if (content is FunctionResultContent frc)
                chars += frc.Result?.ToString()?.Length ?? 0;
        }
        return chars / 4;
    }

    /// <summary>
    /// Full recount of tokens from history (used after bulk operations like clear/rollback).
    /// </summary>
    private long EstimateTokens()
    {
        long total = 0;
        foreach (var msg in _history)
            total += EstimateMessageTokens(msg);
        return total;
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
        "LocateByVision" => "Vision: reading screen content",
        "RunFullPipeline" => "Running full test pipeline",
        "PlanTestPack" => "Planning test pack",
        "GetFixQueue" => "Checking fix queue",
        "GetConfidenceBreakdown" => "Analyzing confidence",
        "AnalyzeReport" => "Analyzing report",
        _ => toolName
    };

    public void ClearHistory()
    {
        _history.Clear();
        _estimatedTokens = 0;
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

            // Extract base URL — users often paste the full deployment/chat-completions URL
            // e.g. "https://myresource.cognitiveservices.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=..."
            // The SDK expects just: "https://myresource.cognitiveservices.azure.com"
            var endpointUrl = settings.Endpoint.TrimEnd('/');
            if (Uri.TryCreate(endpointUrl, UriKind.Absolute, out var parsedUri))
                endpointUrl = $"{parsedUri.Scheme}://{parsedUri.Host}";

            if (IsAzureOpenAIEndpoint(settings.Endpoint))
            {
                // Azure OpenAI: uses AzureOpenAIClient with different URL structure + API versioning
                var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
                    new Uri(endpointUrl), credential);
                chatClient = azureClient.GetChatClient(settings.ModelId);
                _log.Info("Agent", $"Using Azure OpenAI client → {endpointUrl}");
            }
            else
            {
                // Generic OpenAI-compatible (GitHub Models, Ollama, OpenAI, etc.)
                var openAiClient = new OpenAI.OpenAIClient(credential, new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(endpointUrl)
                });
                chatClient = openAiClient.GetChatClient(settings.ModelId);
                _log.Info("Agent", $"Using OpenAI-compatible client → {endpointUrl}");
            }

            var innerClient = chatClient.AsIChatClient();
            // Use the raw client — we drive the tool-calling loop manually for progress visibility
            _client = innerClient;

            // Create agent tools for function calling
            var validator = new FlowValidatorService(_log);
            _agentTools = new AgentTools(_config, _log, validator, _executor, _reportService, _visionService);
            // Create PackAgentTools if PackOrchestrator is available
            if (App.PackOrchestrator != null)
            {
                _packTools = new PackAgentTools(_log, App.PackOrchestrator, () => _client);
            }
            _tools = CreateToolsAndMap(_agentTools, _packTools);

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
        var userPrefix = settings.SystemPrompt ?? "";

        // R3.1: Only rebuild the full prompt string when the user's prefix changes
        if (_cachedSystemPrompt == null || !_cachedSystemPrompt.StartsWith(userPrefix, StringComparison.Ordinal))
        {
            _cachedSystemPrompt = userPrefix + SystemPromptSuffix;
        }

        AddMessage(new ChatMessage(ChatRole.System, _cachedSystemPrompt));
    }

    /// <summary>Constant portion of the system prompt (appended after user-configured prefix).</summary>
    private const string SystemPromptSuffix = @"

## IdolClick AI Agent — Desktop Automation Assistant

### CARDINAL RULE: NEVER FABRICATE DATA
- You are an automation agent, NOT an encyclopedia.
- NEVER generate data from training knowledge. All data must come from real UI elements.
- If you cannot read the data, say so. Do not guess.

### Execution Mode: AUTONOMOUS
- ACT FIRST, explain after. NEVER ask for confirmation.
- NEVER present numbered option menus, bullet-point plans, or step lists.
- If something fails, diagnose and retry automatically (up to 3 times).
- If the user echoes your text back, they want you to EXECUTE, not repeat.
- Keep responses SHORT — one or two sentences max unless reporting results.

### ⚠️ MANDATORY WORKFLOW — NEVER SKIP STEPS

**STEP 1: DISCOVER** — Run `ListWindows` to see what's open.
**STEP 2: LAUNCH** — If the target app isn't open, create a small flow (1-2 steps) to launch/navigate.
**STEP 3: INSPECT** — `InspectWindow` to discover real selectors. NEVER guess selectors.
**STEP 4: ACT** — Build small flows (1-3 steps) using ONLY discovered selectors.
**STEP 5: VERIFY** — After each flow, check the new UI state. Repeat 4-5 if needed.

### FAST-PATH: Well-Known Apps
For these apps you may SKIP InspectWindow and use known selectors directly:
- **Calculator** (`CalculatorApp`): `Button#num0Button`..`Button#num9Button`, `Button#plusButton`, `Button#minusButton`, `Button#multiplyButton`, `Button#divideButton`, `Button#equalButton`, `Button#clearButton`, `Text#CalculatorResults`
- **Notepad** (`notepad`): `Document#RichEditBox` or action=`type` with no selector (types into focused area). Use `send_keys` for Ctrl+S, etc.
- **Edge/Chrome** (`msedge`/`chrome`): `send_keys` Ctrl+L to focus address bar, then `type` the URL + `send_keys` Enter.
For any other app, follow the full 5-step workflow.

### SMALL FLOWS ONLY
- Maximum 3 steps per flow. Break complex tasks into multiple small flows.
- Run → check result → build next flow (InspectWindow only if needed).
- Example: launch calc (1 step) → click 4,0,+,3,2,= (1 flow) → read result

### Process Name Rules
- ALWAYS get process names from `ListWindows` — NEVER guess.
- Common traps: Calculator = `CalculatorApp`, Paint = `mspaint`, Edge = `msedge`, Chrome = `chrome`
- When `focus_window` fails, immediately run `ListWindows` to find the correct name.

### Flow JSON Rules
- ALWAYS set `targetApp` at the FLOW level (the app process name from ListWindows).
  This tells the executor which window ALL steps in the flow target.
  Example: { ""testName"": ""..."", ""targetApp"": ""CalculatorApp"", ""steps"": [...] }
- `navigate`: requires `url` + `app` (browser process). Opens a URL.
- `click`: requires `selector`. Get from InspectWindow FIRST.
- `type`: requires `text`. Optional `selector` (types into focused element if omitted).
- `send_keys`: requires `keys` (`Enter`, `Tab`, `Ctrl+S`, `Ctrl+L`, `Alt+PrintScreen`).
- `scroll`: requires `direction` (""up""/""down"") + optional `scrollAmount`.
- `launch`: requires `processPath` (e.g., `calc.exe`, `mspaint.exe`).
- `focus_window`: requires `app` or `windowTitle` — use exact names from ListWindows.
- ALWAYS `ValidateFlow` BEFORE `RunFlow`.
- `delayAfterMs`: 5000 after navigate, 3000 after launch, 500 after click/type.
- If a flow operates on multiple apps, set `app` on each step that targets a different window.

### Selector Format
`ElementType#TextOrAutomationId` — get from InspectWindow, never guess.
Examples: `Button#num4Button`, `Button#plusButton`, `Edit#SearchBox`

### Resolution Chain
1. **UIA** (InspectWindow + selectors) — primary for ALL apps including web browsers.
   Modern browsers expose full UIA trees: headings, links, buttons, text, edits.
2. **Vision** (LocateByVision) — ONLY for canvas apps, PDFs, or after 2+ UIA failures on same element.

### Failure Recovery
- If a flow fails: read the error → run `ListWindows` → run `InspectWindow` → fix → retry.
- NEVER give up after 1-2 failures. You have 30 tool iterations — use them.
- NEVER present ""what went wrong"" summaries with options. Just fix it and continue.

### Reporting Results
- When reporting observed data, note: ""Data read from live UI on [date]""
- Keep it concise — report the data, not the process.";

    private void DisposeClient()
    {
        if (_client is IDisposable disposable)
            disposable.Dispose();
        _client = null;
        _tools = [];
        _toolMap.Clear();
        _agentTools = null;
        _packTools = null;
        _configured = false;
        _cachedSystemPrompt = null; // Force rebuild on next configure
    }

    /// <summary>
    /// Creates AITool definitions from AgentTools methods for LLM function calling,
    /// and populates the _toolMap for manual invocation.
    /// </summary>
    private List<AITool> CreateToolsAndMap(AgentTools tools, PackAgentTools? packTools = null)
    {
        var type = typeof(AgentTools);
        var functions = new List<AIFunction>
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

        // Register Pack orchestration tools (Sprint 1 — TestPack pipeline)
        if (packTools != null)
        {
            var packType = typeof(PackAgentTools);
            functions.Add(AIFunctionFactory.Create(packType.GetMethod(nameof(PackAgentTools.RunFullPipeline))!, packTools));
            functions.Add(AIFunctionFactory.Create(packType.GetMethod(nameof(PackAgentTools.PlanTestPack))!, packTools));
            functions.Add(AIFunctionFactory.Create(packType.GetMethod(nameof(PackAgentTools.GetFixQueue))!, packTools));
            functions.Add(AIFunctionFactory.Create(packType.GetMethod(nameof(PackAgentTools.GetConfidenceBreakdown))!, packTools));
            functions.Add(AIFunctionFactory.Create(packType.GetMethod(nameof(PackAgentTools.AnalyzeReport))!, packTools));
            _log.Debug("Agent", $"Registered {5} Pack orchestration tools");
        }

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

namespace IdolClick.Models;

/// <summary>
/// Application operating mode.
/// </summary>
public enum AppMode
{
    /// <summary>Classic rule-based automation engine.</summary>
    Classic,
    /// <summary>AI agent mode with natural language interaction.</summary>
    Agent
}

/// <summary>
/// Root application configuration containing global settings and automation rules.
/// Serialized to/from config.json in the application directory.
/// </summary>
/// <remarks>
/// <para>Configuration is auto-reloaded when the file changes on disk.</para>
/// <para>Schema versioning is handled at the <see cref="Rule"/> level.</para>
/// </remarks>
public class AppConfig
{
    /// <summary>
    /// Global application settings affecting all rules and behaviors.
    /// </summary>
    public GlobalSettings Settings { get; set; } = new();
    
    /// <summary>
    /// AI agent configuration for LLM-backed automation.
    /// </summary>
    public AgentSettings AgentSettings { get; set; } = new();
    
    /// <summary>
    /// Collection of automation rules to evaluate each polling cycle.
    /// </summary>
    public List<Rule> Rules { get; set; } = [];
}

/// <summary>
/// Global settings controlling application behavior, UI preferences, and feature toggles.
/// </summary>
public class GlobalSettings
{
    // === Mode ===
    
    /// <summary>
    /// Current application operating mode: Classic (rules) or Agent (AI).
    /// </summary>
    public AppMode Mode { get; set; } = AppMode.Classic;
    
    // === Core Automation ===
    
    /// <summary>
    /// Master switch for automation. When false, no rules are evaluated.
    /// </summary>
    public bool AutomationEnabled { get; set; } = true;
    
    /// <summary>
    /// Interval in milliseconds between rule evaluation cycles. Minimum 5000ms (5 seconds).
    /// </summary>
    public int PollingIntervalMs { get; set; } = 10000;
    
    /// <summary>
    /// Global hotkey to toggle automation (e.g., "Ctrl+Alt+T").
    /// </summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+T";
    
    // === Window Behavior ===
    
    /// <summary>
    /// Show the control panel window when the application starts.
    /// </summary>
    public bool ShowPanelOnStart { get; set; } = true;
    
    /// <summary>
    /// Periodically move the mouse to prevent system sleep.
    /// </summary>
    public bool GlobalMouseNudge { get; set; }
    
    // === Logging & Theme ===
    
    /// <summary>
    /// Minimum log level: Debug, Info, Warning, Error.
    /// </summary>
    public string LogLevel { get; set; } = "Info";
    
    /// <summary>
    /// UI theme: "Dark" or "Light". Currently only Dark is implemented.
    /// </summary>
    public string Theme { get; set; } = "Dark";
    
    // === UI Preferences ===
    
    /// <summary>
    /// Show the session execution count column in the rules list.
    /// </summary>
    public bool ShowExecutionCount { get; set; } = true;
    
    /// <summary>
    /// Show a "radar pulse" animation at the click point so you can observe automation actions.
    /// The overlay is fully click-through and never steals focus.
    /// </summary>
    public bool ClickRadar { get; set; } = true;
    
    // === Scripting ===
    
    /// <summary>
    /// Enable PowerShell and C# script execution for RunScript actions.
    /// </summary>
    public bool ScriptingEnabled { get; set; } = true;
    
    /// <summary>
    /// Default timeout for script execution in milliseconds.
    /// </summary>
    public int DefaultScriptTimeoutMs { get; set; } = 5000;
    
    // === Plugins ===
    
    /// <summary>
    /// Enable loading and execution of plugins from the Plugins directory.
    /// </summary>
    public bool PluginsEnabled { get; set; } = true;
    
    /// <summary>
    /// List of plugin IDs to disable even if present.
    /// </summary>
    public string[] DisabledPlugins { get; set; } = [];
    
    // === Event Timeline ===
    
    /// <summary>
    /// Enable the event timeline feature for tracking rule matches and actions.
    /// </summary>
    public bool TimelineEnabled { get; set; } = true;
    
    /// <summary>
    /// Maximum number of events to keep in memory.
    /// </summary>
    public int MaxTimelineEvents { get; set; } = 1000;
    
    /// <summary>
    /// Persist timeline events to SQLite database (future feature).
    /// </summary>
    public bool PersistTimeline { get; set; }
    
    // === Notifications ===
    
    /// <summary>
    /// Default settings for notifications.
    /// </summary>
    public NotificationDefaults NotificationDefaults { get; set; } = new();

    // === Safety ===

    /// <summary>
    /// Global kill switch hotkey (e.g., "Ctrl+Alt+Escape").
    /// Immediately stops all automation (classic engine + running flows).
    /// </summary>
    public string KillSwitchHotkey { get; set; } = "Ctrl+Alt+Escape";

    /// <summary>
    /// Process allowlist. When non-empty, automation ONLY targets these processes.
    /// Both the classic engine and flow executor enforce this.
    /// Empty list = no restriction (all processes allowed).
    /// </summary>
    public List<string> AllowedProcesses { get; set; } = [];
}

/// <summary>
/// Settings for the AI agent backend (LLM endpoint, model, credentials).
/// </summary>
public class AgentSettings
{
    /// <summary>
    /// LLM API endpoint URL (e.g., Azure OpenAI, GitHub Models, local Ollama).
    /// </summary>
    public string Endpoint { get; set; } = "";
    
    /// <summary>
    /// Model deployment/ID to use (e.g., "gpt-4o", "gpt-4o-mini").
    /// </summary>
    public string ModelId { get; set; } = "gpt-4o";
    
    /// <summary>
    /// API key for the LLM endpoint. Stored in config; future: encrypted via DPAPI.
    /// </summary>
    public string ApiKey { get; set; } = "";
    
    /// <summary>
    /// Maximum tokens per LLM response.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;
    
    /// <summary>
    /// Sampling temperature. Set to 0 to omit (use model default).
    /// Some models (e.g. o-series, gpt-5.2) only support the default value.
    /// </summary>
    public double Temperature { get; set; } = 0;
    
    /// <summary>
    /// System prompt prepended to every agent conversation.
    /// </summary>
    public string SystemPrompt { get; set; } = "You are IdolClick Agent, a desktop automation assistant. You can create automation rules, click UI elements, type text, and inspect windows. Be concise and action-oriented.";

    /// <summary>
    /// Enable vision-based fallback for element location when UIA selectors fail.
    /// Requires a vision-capable LLM (gpt-4o, claude-3.5-sonnet, gemini-pro-vision, etc.).
    /// </summary>
    public bool VisionFallbackEnabled { get; set; } = false;

    /// <summary>
    /// Minimum confidence threshold (0.0–1.0) for vision results to be used.
    /// Below this, the element is treated as "not found".
    /// </summary>
    public double VisionConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// Optional separate model ID for vision requests. If empty, uses the main ModelId.
    /// Useful when the main model doesn't support vision (e.g., text-only) but you have
    /// a vision model available at the same endpoint.
    /// </summary>
    public string VisionModelId { get; set; } = "";
}

/// <summary>
/// Default notification settings applied when rules don't specify their own.
/// </summary>
public class NotificationDefaults
{
    /// <summary>
    /// Show a toast notification when any rule matches.
    /// </summary>
    public bool ToastOnRuleMatch { get; set; }
    
    /// <summary>
    /// Default webhook URL for notification routing.
    /// </summary>
    public string? DefaultWebhookUrl { get; set; }
    
    /// <summary>
    /// Include timestamp in notification messages.
    /// </summary>
    public bool IncludeTimestamp { get; set; } = true;
}

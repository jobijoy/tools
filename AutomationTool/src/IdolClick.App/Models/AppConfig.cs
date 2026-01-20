namespace IdolClick.Models;

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
    /// Collection of automation rules to evaluate each polling cycle.
    /// </summary>
    public List<Rule> Rules { get; set; } = [];
}

/// <summary>
/// Global settings controlling application behavior, UI preferences, and feature toggles.
/// </summary>
public class GlobalSettings
{
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

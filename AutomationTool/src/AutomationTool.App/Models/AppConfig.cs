namespace AutomationTool.Models;

/// <summary>
/// Application configuration with global settings and rules.
/// </summary>
public class AppConfig
{
    public GlobalSettings Settings { get; set; } = new();
    public List<Rule> Rules { get; set; } = [];
}

public class GlobalSettings
{
    public bool AutomationEnabled { get; set; }
    public int PollingIntervalMs { get; set; } = 3000;
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+T";
    public bool ShowPanelOnStart { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool GlobalMouseNudge { get; set; }            // Prevent sleep by nudging mouse
    public string LogLevel { get; set; } = "Info";
    public string Theme { get; set; } = "Dark";           // Light or Dark
    
    // Scripting
    public bool ScriptingEnabled { get; set; } = true;    // Enable/disable scripting
    public int DefaultScriptTimeoutMs { get; set; } = 5000;
    
    // Plugins
    public bool PluginsEnabled { get; set; } = true;      // Enable/disable plugins
    public string[] DisabledPlugins { get; set; } = [];   // Plugin IDs to disable
    
    // Event Timeline
    public bool TimelineEnabled { get; set; } = true;
    public int MaxTimelineEvents { get; set; } = 1000;
    public bool PersistTimeline { get; set; }             // Save to SQLite
    
    // Notifications
    public NotificationDefaults NotificationDefaults { get; set; } = new();
}

/// <summary>
/// Default notification settings.
/// </summary>
public class NotificationDefaults
{
    public bool ToastOnRuleMatch { get; set; }
    public string? DefaultWebhookUrl { get; set; }
    public bool IncludeTimestamp { get; set; } = true;
}

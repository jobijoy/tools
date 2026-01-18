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
}

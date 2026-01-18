namespace VsCodeAllowClicker.App.Models;

/// <summary>
/// Application configuration with rules and global settings.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Automation rules to execute</summary>
    public List<Rule> Rules { get; set; } = [];
    
    /// <summary>Global settings</summary>
    public GlobalSettings Settings { get; set; } = new();
}

public sealed class GlobalSettings
{
    /// <summary>Polling interval in milliseconds</summary>
    public int PollingIntervalMs { get; set; } = 3000;
    
    /// <summary>Start automation when app launches</summary>
    public bool AutoStart { get; set; }
    
    /// <summary>Show control panel on startup</summary>
    public bool ShowPanelOnStart { get; set; } = true;
    
    /// <summary>Global hotkey to toggle automation (Ctrl+Alt+A)</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+A";
    
    /// <summary>Log level: Debug, Info, Warning, Error</summary>
    public string LogLevel { get; set; } = "Info";
}

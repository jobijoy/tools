using AutomationTool.Models;

namespace AutomationTool.Services;

/// <summary>
/// Interface for plugin actions that can be executed by rules.
/// Implement this interface in external DLLs to create custom actions.
/// </summary>
public interface IPluginAction
{
    /// <summary>
    /// Unique identifier for this plugin action.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name shown in the UI.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description of what this action does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Plugin version.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Execute the plugin action.
    /// </summary>
    /// <param name="rule">The rule that triggered this action.</param>
    /// <param name="context">Automation context with runtime information.</param>
    /// <returns>True if action succeeded, false otherwise.</returns>
    Task<bool> ExecuteAsync(Rule rule, AutomationContext context);

    /// <summary>
    /// Validate plugin configuration (optional).
    /// </summary>
    bool Validate(Rule rule) => true;

    /// <summary>
    /// Called when the plugin is loaded.
    /// </summary>
    void OnLoad() { }

    /// <summary>
    /// Called when the plugin is unloaded.
    /// </summary>
    void OnUnload() { }
}

/// <summary>
/// Context passed to plugins during execution.
/// </summary>
public class AutomationContext
{
    public IntPtr WindowHandle { get; set; }
    public string? WindowTitle { get; set; }
    public string? ProcessName { get; set; }
    public string? MatchedText { get; set; }
    public object? MatchedElement { get; set; }
    public DateTime TriggerTime { get; set; } = DateTime.Now;
    public Dictionary<string, object> Variables { get; set; } = new();

    /// <summary>
    /// Log a message through the automation tool's logging system.
    /// </summary>
    public Action<string, string>? Log { get; set; }

    /// <summary>
    /// Execute a click at screen coordinates.
    /// </summary>
    public Action<int, int>? Click { get; set; }

    /// <summary>
    /// Send keystrokes.
    /// </summary>
    public Action<string>? SendKeys { get; set; }

    /// <summary>
    /// Show a notification.
    /// </summary>
    public Action<string, string>? ShowNotification { get; set; }
}

/// <summary>
/// Metadata for discovered plugins.
/// </summary>
public class PluginInfo
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Source { get; set; } = "";              // DLL path or script path
    public PluginType Type { get; set; }
    public bool Enabled { get; set; } = true;
    public IPluginAction? Instance { get; set; }
}

public enum PluginType
{
    DotNet,
    PowerShell
}

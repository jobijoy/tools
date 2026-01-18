namespace VsCodeAllowClicker.App.Models;

/// <summary>
/// A rule defines what to look for and what action to take.
/// </summary>
public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New Rule";
    public bool Enabled { get; set; } = true;
    
    // What to target
    public TargetMatch Target { get; set; } = new();
    
    // What to do when found
    public RuleAction Action { get; set; } = new();
    
    // Safety settings
    public SafetySettings Safety { get; set; } = new();
}

public sealed class TargetMatch
{
    /// <summary>Process names to monitor (e.g., "Code", "Code - Insiders")</summary>
    public string[] ProcessNames { get; set; } = ["Code"];
    
    /// <summary>Window title must contain this text (optional)</summary>
    public string? WindowTitleContains { get; set; }
    
    /// <summary>Element type to find: Button, ListItem, Text, Any</summary>
    public string ElementType { get; set; } = "Button";
    
    /// <summary>Text patterns to match (supports prefix matching)</summary>
    public string[] TextPatterns { get; set; } = ["Allow"];
    
    /// <summary>Patterns to exclude from matching</summary>
    public string[] ExcludePatterns { get; set; } = [];
    
    /// <summary>Screen region to search (normalized 0-1)</summary>
    public ScreenRegion? Region { get; set; }
}

public sealed class ScreenRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1.0;
    public double Height { get; set; } = 1.0;
}

public sealed class RuleAction
{
    /// <summary>Action type: Click, SendKeys, Alert, ReadAndAlert</summary>
    public string Type { get; set; } = "Click";
    
    /// <summary>Keys to send (for SendKeys action)</summary>
    public string[]? Keys { get; set; }
    
    /// <summary>Message to show (for Alert action)</summary>
    public string? AlertMessage { get; set; }
    
    /// <summary>Delay after action (ms)</summary>
    public int DelayAfterMs { get; set; } = 200;
}

public sealed class SafetySettings
{
    /// <summary>Show confirmation before acting</summary>
    public bool ConfirmBeforeAction { get; set; }
    
    /// <summary>Read nearby text and alert if it contains these patterns</summary>
    public string[]? AlertIfContains { get; set; }
    
    /// <summary>Maximum clicks per minute (0 = unlimited)</summary>
    public int MaxClicksPerMinute { get; set; }
    
    /// <summary>Cooldown between actions (ms)</summary>
    public int CooldownMs { get; set; } = 1500;
}

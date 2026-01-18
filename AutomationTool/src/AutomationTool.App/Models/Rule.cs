using System.Text.Json.Serialization;

namespace AutomationTool.Models;

/// <summary>
/// Defines an automation rule with target, conditions, and actions.
/// </summary>
public class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New Rule";
    public bool Enabled { get; set; } = true;

    // === Target ===
    public string TargetApp { get; set; } = "";           // Process name(s), comma-separated
    public string? WindowTitle { get; set; }              // Window title contains
    public string ElementType { get; set; } = "Button";   // Button, ListItem, Text, Link, Any
    public string MatchText { get; set; } = "";           // Text patterns, comma-separated
    public bool UseRegex { get; set; }                    // Treat MatchText as regex
    public string[] ExcludeTexts { get; set; } = [];      // Patterns to exclude

    // === Region Filter ===
    public ScreenRegion? Region { get; set; }             // Normalized screen region (0-1)

    // === Action ===
    public string Action { get; set; } = "Click";         // Click, SendKeys, RunScript, ShowNotification, Alert
    public string? Keys { get; set; }                     // For SendKeys action
    public string? Script { get; set; }                   // Inline script content
    public string ScriptLanguage { get; set; } = "powershell"; // powershell, csharp
    public string? NotificationMessage { get; set; }      // For ShowNotification

    // === Safety & Timing ===
    public int CooldownSeconds { get; set; } = 2;         // Min seconds between actions
    public string? TimeWindow { get; set; }               // e.g., "09:00-17:00"
    public bool RequireFocus { get; set; }                // Only act if window is focused
    public bool ConfirmBeforeAction { get; set; }         // Show confirmation dialog
    public string[] AlertIfContains { get; set; } = [];   // Alert instead of act if text found nearby

    // === Metadata ===
    public DateTime? LastTriggered { get; set; }
    public int TriggerCount { get; set; }
}

/// <summary>
/// Normalized screen region (0.0 to 1.0 coordinates relative to window).
/// </summary>
public class ScreenRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1.0;
    public double Height { get; set; } = 1.0;

    public override string ToString() => $"({X:P0}, {Y:P0}) {Width:P0}x{Height:P0}";
}

using System.Text.Json.Serialization;

namespace IdolClick.Models;

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
    public string Action { get; set; } = "Click";         // Click, SendKeys, RunScript, ShowNotification, Alert, Plugin
    public string? Keys { get; set; }                     // For SendKeys action
    public string? Script { get; set; }                   // Inline script content or file path
    public string ScriptLanguage { get; set; } = "powershell"; // powershell, csharp
    public string? NotificationMessage { get; set; }      // For ShowNotification
    public string? PluginId { get; set; }                 // For Plugin action

    // === Notification Hooks ===
    public NotificationConfig? Notification { get; set; } // Optional notification routing

    // === Safety & Timing ===
    public int CooldownSeconds { get; set; } = 2;         // Min seconds between actions
    public string? TimeWindow { get; set; }               // e.g., "09:00-17:00"
    public bool RequireFocus { get; set; }                // Only act if window is focused
    public bool ConfirmBeforeAction { get; set; }         // Show confirmation dialog
    public string[] AlertIfContains { get; set; } = [];   // Alert instead of act if text found nearby
    public bool DryRun { get; set; }                      // Log but don't execute action

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

/// <summary>
/// Configuration for notification routing.
/// </summary>
public class NotificationConfig
{
    public string Type { get; set; } = "toast";           // toast, webhook, script
    public string? Title { get; set; }                    // For toast
    public string? Message { get; set; }                  // Message template with placeholders
    public string? Url { get; set; }                      // For webhook
    public string? ScriptPath { get; set; }               // For script hook
    public string? ScriptLanguage { get; set; } = "powershell";
    public int TimeoutMs { get; set; } = 5000;            // Script timeout
    public bool OnSuccess { get; set; } = true;           // Send on successful action
    public bool OnFailure { get; set; }                   // Send on failed action
}

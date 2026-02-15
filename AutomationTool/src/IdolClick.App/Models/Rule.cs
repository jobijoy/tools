using System.Text.Json.Serialization;

namespace IdolClick.Models;

/// <summary>
/// Defines an automation rule with target, conditions, and actions.
/// </summary>
public class Rule : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isRunning = true;
    private int _sessionExecutionCount;

    /// <summary>
    /// Schema version for future-proofing config migrations.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New Rule";
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Whether this rule is actively running (can be toggled per-rule).
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning != value)
            {
                _isRunning = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRunning)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RunStateIcon)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusColor)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusTooltip)));
            }
        }
    }
    
    /// <summary>
    /// Icon for the run state button (▶ or ⏸).
    /// </summary>
    [JsonIgnore]
    public string RunStateIcon => IsRunning ? "⏸" : "▶";
    
    /// <summary>
    /// Tooltip for the run state button.
    /// </summary>
    [JsonIgnore]
    public string RunStateTooltip => IsRunning 
        ? "Pause this rule – keep enabled but stop triggering" 
        : "Run this rule – will trigger when target matches";
    
    /// <summary>
    /// Color for the status indicator dot.
    /// </summary>
    [JsonIgnore]
    public string StatusColor => !Enabled ? "#6B7280" : (IsRunning ? "#10B981" : "#F59E0B");
    
    /// <summary>
    /// Tooltip for the status indicator.
    /// </summary>
    [JsonIgnore]
    public string StatusTooltip => !Enabled 
        ? "Disabled – this rule will not run" 
        : (IsRunning 
            ? "Active – this rule will auto-execute when matched" 
            : "Paused – this rule is enabled but currently not running");
    
    /// <summary>
    /// In-memory execution count for this session (resets on app restart).
    /// </summary>
    [JsonIgnore]
    public int SessionExecutionCount
    {
        get => _sessionExecutionCount;
        set
        {
            if (_sessionExecutionCount != value)
            {
                _sessionExecutionCount = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SessionExecutionCount)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SessionExecutionDisplay)));
            }
        }
    }
    
    /// <summary>
    /// Display string for session execution count.
    /// </summary>
    [JsonIgnore]
    public string SessionExecutionDisplay => _sessionExecutionCount > 0 ? $"🔁 {_sessionExecutionCount}" : "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    
    /// <summary>
    /// Notify that Enabled changed (for status color binding).
    /// </summary>
    public void NotifyEnabledChanged()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusColor)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusTooltip)));
    }
    
    /// <summary>
    /// Raises PropertyChanged for the specified property name.
    /// </summary>
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    // ═══════════════════════════════════════════════════════════════════════════
    // TARGET CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Process name(s) to target, comma-separated (e.g., "Code, Code - Insiders").
    /// </summary>
    public string TargetApp { get; set; } = "";
    
    /// <summary>
    /// Optional window title filter. Rule only applies if window title contains this text.
    /// </summary>
    public string? WindowTitle { get; set; }
    
    /// <summary>
    /// UI element type to match: Button, ListItem, Text, Link, or Any.
    /// </summary>
    public string ElementType { get; set; } = "Button";
    
    /// <summary>
    /// Text patterns to match, comma-separated. Supports prefix matching for buttons with shortcuts.
    /// </summary>
    public string MatchText { get; set; } = "";
    
    /// <summary>
    /// Treat MatchText as regular expressions instead of literal text.
    /// </summary>
    public bool UseRegex { get; set; }
    
    /// <summary>
    /// Patterns to exclude from matching. If element text contains any of these, it's skipped.
    /// </summary>
    public string[] ExcludeTexts { get; set; } = [];

    // ═══════════════════════════════════════════════════════════════════════════
    // REGION FILTER
    // ═══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Optional normalized screen region (0.0 to 1.0) to constrain element matching.
    /// </summary>
    public ScreenRegion? Region { get; set; }

    // ═══════════════════════════════════════════════════════════════════════════
    // ACTION CONFIGURATION  
    // ═══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Action to perform: Click, SendKeys, RunScript, ShowNotification, Alert, or Plugin.
    /// </summary>
    public string Action { get; set; } = "Click";
    
    /// <summary>
    /// Keys to send for SendKeys action (e.g., "Tab", "Enter", "Ctrl+A").
    /// </summary>
    public string? Keys { get; set; }
    
    /// <summary>
    /// Inline script content or file path for RunScript action.
    /// </summary>
    public string? Script { get; set; }
    
    /// <summary>
    /// Script language: "powershell" or "csharp".
    /// </summary>
    public string ScriptLanguage { get; set; } = "powershell";
    
    /// <summary>
    /// Message to display for ShowNotification action.
    /// </summary>
    public string? NotificationMessage { get; set; }
    
    /// <summary>
    /// Plugin identifier for Plugin action.
    /// </summary>
    public string? PluginId { get; set; }

    // ═══════════════════════════════════════════════════════════════════════════
    // NOTIFICATION HOOKS
    // ═══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Optional notification configuration for webhooks, toasts, or script hooks.
    /// </summary>
    public NotificationConfig? Notification { get; set; }

    // ═══════════════════════════════════════════════════════════════════════════
    // SAFETY & TIMING
    // ═══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Minimum seconds between consecutive triggers of this rule.
    /// </summary>
    public int CooldownSeconds { get; set; } = 2;
    
    /// <summary>
    /// Time window when rule is active (e.g., "09:00-17:00"). Empty = always active.
    /// </summary>
    public string? TimeWindow { get; set; }
    
    /// <summary>
    /// Only trigger if the target window has keyboard focus.
    /// </summary>
    public bool RequireFocus { get; set; }
    
    /// <summary>
    /// Show confirmation dialog before executing the action.
    /// </summary>
    public bool ConfirmBeforeAction { get; set; }
    
    /// <summary>
    /// Show alert instead of executing if any of these patterns are found near the element.
    /// </summary>
    public string[] AlertIfContains { get; set; } = [];
    
    /// <summary>
    /// Log actions without actually executing them. Useful for testing rules.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Maximum number of times this rule can fire per session. 0 = unlimited.
    /// Prevents runaway loops from misconfigured rules.
    /// </summary>
    public int MaxExecutionsPerSession { get; set; } = 0;

    // ═══════════════════════════════════════════════════════════════════════════
    // METADATA (Persisted)
    // ═══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Timestamp of last successful trigger. Persisted to config.
    /// </summary>
    public DateTime? LastTriggered { get; set; }
    
    /// <summary>
    /// Total trigger count across all sessions. Persisted to config.
    /// </summary>
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

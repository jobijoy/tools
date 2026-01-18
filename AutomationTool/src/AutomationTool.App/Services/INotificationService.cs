using AutomationTool.Models;

namespace AutomationTool.Services;

/// <summary>
/// Service for sending notifications through various channels.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Send a notification using the specified configuration.
    /// </summary>
    Task<bool> SendAsync(NotificationConfig config, NotificationContext context);

    /// <summary>
    /// Send a simple toast notification.
    /// </summary>
    void ShowToast(string title, string message);

    /// <summary>
    /// Send a webhook notification.
    /// </summary>
    Task<bool> SendWebhookAsync(string url, object payload);
}

/// <summary>
/// Context passed to notification handlers.
/// </summary>
public class NotificationContext
{
    public Rule? Rule { get; set; }
    public string? MatchedText { get; set; }
    public string? WindowTitle { get; set; }
    public string? ProcessName { get; set; }
    public DateTime TriggerTime { get; set; } = DateTime.Now;
    public string? ActionTaken { get; set; }

    /// <summary>
    /// Replace placeholders in a message template.
    /// </summary>
    public string FormatMessage(string template)
    {
        return template
            .Replace("{RuleName}", Rule?.Name ?? "Unknown")
            .Replace("{MatchedText}", MatchedText ?? "")
            .Replace("{WindowTitle}", WindowTitle ?? "")
            .Replace("{ProcessName}", ProcessName ?? "")
            .Replace("{TriggerTime}", TriggerTime.ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{Action}", ActionTaken ?? Rule?.Action ?? "");
    }
}

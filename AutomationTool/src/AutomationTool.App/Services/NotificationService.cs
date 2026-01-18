using System.Net.Http;
using System.Text;
using System.Text.Json;
using AutomationTool.Models;

namespace AutomationTool.Services;

/// <summary>
/// Sends notifications via toast, webhook, or script.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly HttpClient _httpClient;

    public NotificationService(LogService log, ConfigService config)
    {
        _log = log;
        _config = config;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<bool> SendAsync(NotificationConfig? config, NotificationContext context)
    {
        if (config == null) return false;

        try
        {
            var message = context.FormatMessage(config.Message ?? "Rule '{RuleName}' triggered");

            return config.Type?.ToLowerInvariant() switch
            {
                "toast" => SendToast(config.Title ?? "Automation Tool", message),
                "webhook" => await SendWebhookNotification(config, context, message),
                "script" => await RunNotificationScript(config, context),
                _ => false
            };
        }
        catch (Exception ex)
        {
            _log.Error("Notification", $"Failed to send notification: {ex.Message}");
            return false;
        }
    }

    public void ShowToast(string title, string message)
    {
        SendToast(title, message);
    }

    private bool SendToast(string title, string message)
    {
        try
        {
            // Use the tray service to show balloon notification
            App.Tray?.ShowBalloon(title, message);
            _log.Debug("Notification", $"Toast sent: {title}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Notification", $"Toast failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendWebhookAsync(string url, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _log.Debug("Notification", $"Webhook sent to {url}");
                return true;
            }

            _log.Warn("Notification", $"Webhook failed: {response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("Notification", $"Webhook error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SendWebhookNotification(NotificationConfig config, NotificationContext context, string message)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            _log.Warn("Notification", "Webhook URL is empty");
            return false;
        }

        var payload = new
        {
            timestamp = context.TriggerTime.ToString("o"),
            rule = new
            {
                id = context.Rule?.Id,
                name = context.Rule?.Name,
                action = context.Rule?.Action
            },
            trigger = new
            {
                matchedText = context.MatchedText,
                windowTitle = context.WindowTitle,
                processName = context.ProcessName,
                actionTaken = context.ActionTaken
            },
            message
        };

        return await SendWebhookAsync(config.Url, payload);
    }

    private async Task<bool> RunNotificationScript(NotificationConfig config, NotificationContext context)
    {
        if (string.IsNullOrWhiteSpace(config.ScriptPath))
        {
            _log.Warn("Notification", "Script path is empty");
            return false;
        }

        if (!File.Exists(config.ScriptPath))
        {
            _log.Warn("Notification", $"Script not found: {config.ScriptPath}");
            return false;
        }

        try
        {
            var result = await App.Scripts.ExecuteAsync(
                config.ScriptLanguage ?? "powershell",
                config.ScriptPath,
                new ScriptContext
                {
                    Rule = context.Rule,
                    MatchedText = context.MatchedText,
                    WindowTitle = context.WindowTitle,
                    ProcessName = context.ProcessName,
                    TriggerTime = context.TriggerTime,
                    Variables = new Dictionary<string, object>
                    {
                        ["NotificationMessage"] = context.FormatMessage(config.Message ?? ""),
                        ["ActionTaken"] = context.ActionTaken ?? ""
                    }
                },
                config.TimeoutMs);

            if (!result.Success)
            {
                _log.Warn("Notification", $"Script failed: {result.Error}");
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            _log.Error("Notification", $"Script execution error: {ex.Message}");
            return false;
        }
    }
}

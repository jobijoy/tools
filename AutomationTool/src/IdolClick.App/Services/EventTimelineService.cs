using System.Collections.ObjectModel;
using System.Text.Json;

namespace IdolClick.Services;

/// <summary>
/// Tracks automation events in a timeline for display and persistence.
/// </summary>
public class EventTimelineService : IDisposable
{
    private readonly LogService _log;
    private readonly ObservableCollection<AutomationEvent> _events = new();
    private readonly object _lock = new();
    private int _maxEvents = 1000;
    private string? _persistPath;
    private bool _isDisposed;

    public EventTimelineService(LogService log)
    {
        _log = log;
    }

    /// <summary>
    /// Observable collection of events for UI binding.
    /// </summary>
    public ObservableCollection<AutomationEvent> Events => _events;

    /// <summary>
    /// Event raised when a new event is added.
    /// </summary>
    public event Action<AutomationEvent>? OnEvent;

    /// <summary>
    /// Configure the timeline.
    /// </summary>
    public void Configure(int maxEvents, string? persistPath = null)
    {
        _maxEvents = maxEvents;
        _persistPath = persistPath;

        if (!string.IsNullOrEmpty(_persistPath) && File.Exists(_persistPath))
        {
            LoadFromFile();
        }
    }

    /// <summary>
    /// Record a rule match event.
    /// </summary>
    public void RecordMatch(string ruleId, string ruleName, string? matchedText, string? windowTitle)
    {
        AddEvent(new AutomationEvent
        {
            Type = EventType.RuleMatch,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = $"Matched: {matchedText}",
            Details = new Dictionary<string, string?>
            {
                ["MatchedText"] = matchedText,
                ["WindowTitle"] = windowTitle
            }
        });
    }

    /// <summary>
    /// Record an action execution event.
    /// </summary>
    public void RecordAction(string ruleId, string ruleName, string action, bool success, string? details = null)
    {
        AddEvent(new AutomationEvent
        {
            Type = success ? EventType.ActionSuccess : EventType.ActionFailure,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = $"{action}: {(success ? "Success" : "Failed")}",
            Details = new Dictionary<string, string?>
            {
                ["Action"] = action,
                ["Details"] = details
            },
            Status = success ? EventStatus.Success : EventStatus.Failed
        });
    }

    /// <summary>
    /// Record a notification event.
    /// </summary>
    public void RecordNotification(string ruleId, string ruleName, string notificationType, bool sent)
    {
        AddEvent(new AutomationEvent
        {
            Type = EventType.Notification,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = $"Notification ({notificationType}): {(sent ? "Sent" : "Failed")}",
            Status = sent ? EventStatus.Success : EventStatus.Failed
        });
    }

    /// <summary>
    /// Record a plugin execution event.
    /// </summary>
    public void RecordPlugin(string ruleId, string ruleName, string pluginId, bool success, string? output = null)
    {
        AddEvent(new AutomationEvent
        {
            Type = EventType.Plugin,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = $"Plugin '{pluginId}': {(success ? "Success" : "Failed")}",
            Details = new Dictionary<string, string?>
            {
                ["PluginId"] = pluginId,
                ["Output"] = output
            },
            Status = success ? EventStatus.Success : EventStatus.Failed
        });
    }

    /// <summary>
    /// Record a script execution event.
    /// </summary>
    public void RecordScript(string ruleId, string ruleName, string language, bool success, string? output = null)
    {
        AddEvent(new AutomationEvent
        {
            Type = EventType.Script,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = $"Script ({language}): {(success ? "Success" : "Failed")}",
            Details = new Dictionary<string, string?>
            {
                ["Language"] = language,
                ["Output"] = output
            },
            Status = success ? EventStatus.Success : EventStatus.Failed
        });
    }

    /// <summary>
    /// Record a system event (startup, shutdown, config change).
    /// </summary>
    public void RecordSystem(string message, EventStatus status = EventStatus.Info)
    {
        AddEvent(new AutomationEvent
        {
            Type = EventType.System,
            Message = message,
            Status = status
        });
    }

    /// <summary>
    /// Clear all events.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => _events.Clear());
        }
    }

    /// <summary>
    /// Get events filtered by type or rule.
    /// </summary>
    public IReadOnlyList<AutomationEvent> GetEvents(EventType? type = null, string? ruleId = null, DateTime? since = null)
    {
        lock (_lock)
        {
            var query = _events.AsEnumerable();

            if (type.HasValue)
                query = query.Where(e => e.Type == type.Value);

            if (!string.IsNullOrEmpty(ruleId))
                query = query.Where(e => e.RuleId == ruleId);

            if (since.HasValue)
                query = query.Where(e => e.Timestamp >= since.Value);

            return query.ToList();
        }
    }

    private void AddEvent(AutomationEvent evt)
    {
        lock (_lock)
        {
            // Add to collection on UI thread
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _events.Insert(0, evt);

                // Trim old events
                while (_events.Count > _maxEvents)
                {
                    _events.RemoveAt(_events.Count - 1);
                }
            });

            OnEvent?.Invoke(evt);

            // Persist if enabled
            if (!string.IsNullOrEmpty(_persistPath))
            {
                SaveToFile();
            }
        }
    }

    private void LoadFromFile()
    {
        try
        {
            var json = File.ReadAllText(_persistPath!);
            var events = JsonSerializer.Deserialize<List<AutomationEvent>>(json);
            if (events != null)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var evt in events.Take(_maxEvents))
                    {
                        _events.Add(evt);
                    }
                });
            }
            _log.Debug("Timeline", $"Loaded {_events.Count} events from {_persistPath}");
        }
        catch (Exception ex)
        {
            _log.Warn("Timeline", $"Failed to load timeline: {ex.Message}");
        }
    }

    private void SaveToFile()
    {
        try
        {
            var json = JsonSerializer.Serialize(_events.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_persistPath!, json);
        }
        catch (Exception ex)
        {
            _log.Warn("Timeline", $"Failed to save timeline: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (!string.IsNullOrEmpty(_persistPath))
        {
            SaveToFile();
        }
    }
}

/// <summary>
/// Represents an automation event in the timeline.
/// </summary>
public class AutomationEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public EventType Type { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Info;
    public string? RuleId { get; set; }
    public string? RuleName { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, string?>? Details { get; set; }

    public string Icon => Type switch
    {
        EventType.RuleMatch => "🎯",
        EventType.ActionSuccess => "✅",
        EventType.ActionFailure => "❌",
        EventType.Notification => "📣",
        EventType.Plugin => "🔌",
        EventType.Script => "📜",
        EventType.System => "⚙️",
        _ => "•"
    };

    public string StatusColor => Status switch
    {
        EventStatus.Success => "#4CAF50",
        EventStatus.Failed => "#F44336",
        EventStatus.Warning => "#FF9800",
        _ => "#2196F3"
    };
}

public enum EventType
{
    System,
    RuleMatch,
    ActionSuccess,
    ActionFailure,
    Notification,
    Plugin,
    Script
}

public enum EventStatus
{
    Info,
    Success,
    Warning,
    Failed
}

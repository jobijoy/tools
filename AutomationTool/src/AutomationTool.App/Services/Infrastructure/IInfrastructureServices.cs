using AutomationTool.Models;

namespace AutomationTool.Services.Infrastructure;

/// <summary>
/// Interface for evaluating preconditions before rule execution.
/// Implement to add custom condition checks like system idle, window focus, etc.
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Unique identifier for this condition type.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name for UI.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluate whether the condition is met.
    /// </summary>
    /// <param name="rule">The rule being evaluated.</param>
    /// <param name="parameters">Condition-specific parameters from rule config.</param>
    /// <returns>True if condition is met, false otherwise.</returns>
    Task<bool> EvaluateAsync(Rule rule, Dictionary<string, object>? parameters = null);
}

/// <summary>
/// Interface for publishing automation events to external message systems.
/// Implement to integrate with MQTT, Redis, RabbitMQ, etc.
/// </summary>
public interface IMessageBusClient : IDisposable
{
    /// <summary>
    /// Connect to the message bus.
    /// </summary>
    Task ConnectAsync(string connectionString);

    /// <summary>
    /// Disconnect from the message bus.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Publish an event to the message bus.
    /// </summary>
    /// <param name="topic">Topic/channel to publish to.</param>
    /// <param name="message">Message payload (will be serialized to JSON).</param>
    Task PublishAsync(string topic, object message);

    /// <summary>
    /// Subscribe to events from the message bus.
    /// </summary>
    /// <param name="topic">Topic/channel to subscribe to.</param>
    /// <param name="handler">Handler for received messages.</param>
    Task SubscribeAsync(string topic, Func<string, Task> handler);

    /// <summary>
    /// Check if connected.
    /// </summary>
    bool IsConnected { get; }
}

/// <summary>
/// Interface for synchronizing configuration from external sources.
/// Implement to pull config from Git repos, cloud storage, etc.
/// </summary>
public interface IConfigSyncService
{
    /// <summary>
    /// Fetch configuration from remote source.
    /// </summary>
    /// <returns>Merged configuration or null if unchanged.</returns>
    Task<AppConfig?> FetchAsync();

    /// <summary>
    /// Push local configuration to remote source.
    /// </summary>
    Task<bool> PushAsync(AppConfig config);

    /// <summary>
    /// Check if remote has newer configuration.
    /// </summary>
    Task<bool> HasUpdatesAsync();

    /// <summary>
    /// Last sync time.
    /// </summary>
    DateTime? LastSyncTime { get; }
}

/// <summary>
/// Interface for scheduling rule execution on timers.
/// Implement to run rules on schedules independent of UI automation.
/// </summary>
public interface IRuleScheduler : IDisposable
{
    /// <summary>
    /// Schedule a rule to run at intervals.
    /// </summary>
    /// <param name="ruleId">Rule ID to schedule.</param>
    /// <param name="interval">Interval between executions.</param>
    /// <param name="startTime">Optional start time (for cron-like scheduling).</param>
    void Schedule(string ruleId, TimeSpan interval, DateTime? startTime = null);

    /// <summary>
    /// Schedule a rule using cron expression.
    /// </summary>
    /// <param name="ruleId">Rule ID to schedule.</param>
    /// <param name="cronExpression">Cron expression (e.g., "0 9 * * *" for 9 AM daily).</param>
    void ScheduleCron(string ruleId, string cronExpression);

    /// <summary>
    /// Unschedule a rule.
    /// </summary>
    void Unschedule(string ruleId);

    /// <summary>
    /// Get all scheduled rules.
    /// </summary>
    IReadOnlyList<ScheduledRule> GetScheduledRules();

    /// <summary>
    /// Pause all scheduled rules.
    /// </summary>
    void PauseAll();

    /// <summary>
    /// Resume all scheduled rules.
    /// </summary>
    void ResumeAll();
}

/// <summary>
/// Information about a scheduled rule.
/// </summary>
public class ScheduledRule
{
    public required string RuleId { get; set; }
    public string? Schedule { get; set; }
    public DateTime? NextRun { get; set; }
    public DateTime? LastRun { get; set; }
    public bool IsPaused { get; set; }
}

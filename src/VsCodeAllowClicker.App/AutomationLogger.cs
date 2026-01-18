using System.Collections.Concurrent;
using System.IO;

namespace VsCodeAllowClicker.App;

internal sealed class AutomationLogger
{
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private readonly object _fileLock = new();
    private readonly string _logFilePath;
    private const int MaxBufferSize = 500;

    public event Action<LogEntry>? LogAdded;

    public AutomationLogger()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _logFilePath = Path.Combine(logDir, $"automation_{timestamp}.log");
    }

    public void Log(LogLevel level, string category, string message, Dictionary<string, string>? metadata = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            Metadata = metadata ?? new Dictionary<string, string>()
        };

        _buffer.Enqueue(entry);
        
        // Trim buffer if too large
        while (_buffer.Count > MaxBufferSize && _buffer.TryDequeue(out _)) { }

        // Write to file asynchronously
        Task.Run(() => WriteToFile(entry));

        // Notify listeners
        LogAdded?.Invoke(entry);
    }

    public void LogButtonClicked(string buttonName, string windowTitle)
    {
        Log(LogLevel.Info, "ButtonClick", $"Clicked button '{buttonName}' in window '{windowTitle}'", new Dictionary<string, string>
        {
            ["ButtonName"] = buttonName,
            ["WindowTitle"] = windowTitle
        });
    }

    public void LogWindowFound(string processName, string windowTitle)
    {
        Log(LogLevel.Debug, "WindowDetection", $"Found target window: {processName} - {windowTitle}", new Dictionary<string, string>
        {
            ["ProcessName"] = processName,
            ["WindowTitle"] = windowTitle
        });
    }

    public void LogWindowNotFound()
    {
        Log(LogLevel.Debug, "WindowDetection", "Target window not found");
    }

    public void LogButtonScan(int buttonCount, int matchCount)
    {
        Log(LogLevel.Debug, "ButtonScan", $"Scanned {buttonCount} buttons, found {matchCount} matches", new Dictionary<string, string>
        {
            ["TotalButtons"] = buttonCount.ToString(),
            ["MatchingButtons"] = matchCount.ToString()
        });
    }

    public void LogError(string category, string message, Exception? ex = null)
    {
        Log(LogLevel.Error, category, message + (ex != null ? $": {ex.Message}" : ""), ex != null ? new Dictionary<string, string>
        {
            ["Exception"] = ex.GetType().Name,
            ["StackTrace"] = ex.StackTrace ?? ""
        } : null);
    }

    public void LogStateChange(bool enabled)
    {
        Log(LogLevel.Info, "StateChange", enabled ? "Automation enabled" : "Automation disabled", new Dictionary<string, string>
        {
            ["Enabled"] = enabled.ToString()
        });
    }

    public void LogConfigReload()
    {
        Log(LogLevel.Info, "Configuration", "Configuration reloaded");
    }

    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100)
    {
        return _buffer.TakeLast(Math.Min(count, _buffer.Count)).ToList();
    }

    public string GetLogFilePath() => _logFilePath;

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            var line = FormatLogEntry(entry);
            lock (_fileLock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Fail silently for file writes
        }
    }

    private static string FormatLogEntry(LogEntry entry)
    {
        var levelStr = entry.Level.ToString().ToUpperInvariant().PadRight(5);
        var categoryStr = entry.Category.PadRight(20);
        var metadataStr = entry.Metadata.Count > 0 
            ? " | " + string.Join(", ", entry.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        
        return $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {levelStr} [{categoryStr}] {entry.Message}{metadataStr}";
    }
}

internal sealed class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new();
}

internal enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

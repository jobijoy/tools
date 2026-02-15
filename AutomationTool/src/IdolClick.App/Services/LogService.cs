using System.Collections.Concurrent;
using System.IO;

namespace IdolClick.Services;

/// <summary>
/// Log severity levels in ascending order of importance.
/// </summary>
public enum LogLevel 
{ 
    /// <summary>Detailed debugging information.</summary>
    Debug = 0, 
    /// <summary>General informational messages.</summary>
    Info = 1, 
    /// <summary>Potential issues or unexpected conditions.</summary>
    Warning = 2, 
    /// <summary>Error conditions that may affect functionality.</summary>
    Error = 3 
}

/// <summary>
/// Immutable log entry record.
/// </summary>
/// <param name="Time">Timestamp when the log entry was created.</param>
/// <param name="Level">Severity level of the log entry.</param>
/// <param name="Category">Source category (e.g., "Engine", "Action", "Config").</param>
/// <param name="Message">Human-readable log message.</param>
public record LogEntry(DateTime Time, LogLevel Level, string Category, string Message);

/// <summary>
/// Thread-safe logging service with file and memory output.
/// </summary>
/// <remarks>
/// <para>Logs are written to timestamped files in the logs/ directory.</para>
/// <para>Recent entries are kept in a circular buffer for UI display.</para>
/// <para>File writes are performed asynchronously to avoid blocking.</para>
/// </remarks>
public class LogService
{
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private readonly string _logPath;
    private readonly string _auditPath;
    private readonly object _fileLock = new();
    private readonly object _auditLock = new();
    private LogLevel _minLevel = LogLevel.Info;
    
    /// <summary>
    /// Maximum number of log entries to keep in the memory buffer.
    /// </summary>
    private const int MaxBufferSize = 500;

    /// <summary>
    /// Raised when a new log entry is created (after filtering by level).
    /// </summary>
    public event Action<LogEntry>? OnLog;

    /// <summary>
    /// Initializes the log service and creates the log file.
    /// </summary>
    public LogService()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        _auditPath = Path.Combine(logDir, "audit_log.txt");
    }

    /// <summary>
    /// Sets the minimum log level. Messages below this level are ignored.
    /// </summary>
    /// <param name="level">Level name: "Debug", "Info", "Warning", or "Error".</param>
    public void SetLevel(string level) => 
        _minLevel = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var l) ? l : LogLevel.Info;

    /// <summary>Logs a debug message.</summary>
    public void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
    
    /// <summary>Logs an informational message.</summary>
    public void Info(string category, string message) => Log(LogLevel.Info, category, message);
    
    /// <summary>Logs a warning message.</summary>
    public void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
    
    /// <summary>Logs an error message.</summary>
    public void Error(string category, string message) => Log(LogLevel.Error, category, message);

    /// <summary>
    /// Writes a safety audit entry. Always persists regardless of log level.
    /// Written to a separate audit_log.txt for enterprise compliance.
    /// Events: kill switch, target lock violations, allowlist blocks, vision fallback usage.
    /// </summary>
    public void Audit(string category, string message)
    {
        // Also log normally at Warning level
        Warn(category, message);

        // Write to dedicated audit log
        _ = Task.Run(() =>
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AUDIT [{category,-15}] {message}";
                lock (_auditLock)
                {
                    File.AppendAllText(_auditPath, line + Environment.NewLine);
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// Logs a message with the specified level and category.
    /// </summary>
    /// <param name="level">Severity level.</param>
    /// <param name="category">Source category for filtering.</param>
    /// <param name="message">Log message content.</param>
    public void Log(LogLevel level, string category, string message)
    {
        if (level < _minLevel) return;

        var entry = new LogEntry(DateTime.Now, level, category, message);
        
        // Circular buffer - remove oldest when full
        _buffer.Enqueue(entry);
        while (_buffer.Count > MaxBufferSize) 
            _buffer.TryDequeue(out _);

        // Async file write to avoid blocking
        _ = Task.Run(() => WriteToFile(entry));
        
        // Notify subscribers
        OnLog?.Invoke(entry);
    }

    /// <summary>
    /// Gets the most recent log entries from the memory buffer.
    /// </summary>
    /// <param name="count">Maximum number of entries to return.</param>
    /// <returns>List of recent log entries, newest last.</returns>
    public IReadOnlyList<LogEntry> GetRecent(int count = 100) => 
        _buffer.TakeLast(Math.Min(count, MaxBufferSize)).ToList();
    
    /// <summary>
    /// Gets the full path to the current log file.
    /// </summary>
    public string GetLogPath() => _logPath;

    private void WriteToFile(LogEntry e)
    {
        try
        {
            var line = $"[{e.Time:yyyy-MM-dd HH:mm:ss.fff}] {e.Level,-7} [{e.Category,-15}] {e.Message}";
            lock (_fileLock) 
            { 
                File.AppendAllText(_logPath, line + Environment.NewLine); 
            }
        }
        catch 
        { 
            // Silently ignore file write errors to prevent log spam
        }
    }
}

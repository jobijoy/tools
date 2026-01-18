using System.Collections.Concurrent;
using System.IO;

namespace VsCodeAllowClicker.App.Services;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message);

/// <summary>
/// Simple logging service with file and memory output.
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private readonly string _logPath;
    private readonly object _lock = new();
    private LogLevel _minLevel = LogLevel.Info;

    public event Action<LogEntry>? OnLog;

    public LogService()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
    }

    public void SetLevel(string level) => _minLevel = Enum.TryParse<LogLevel>(level, true, out var l) ? l : LogLevel.Info;

    public void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
    public void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Log(LogLevel.Error, category, message);

    public void Log(LogLevel level, string category, string message)
    {
        if (level < _minLevel) return;

        var entry = new LogEntry(DateTime.Now, level, category, message);
        _buffer.Enqueue(entry);
        while (_buffer.Count > 500) _buffer.TryDequeue(out _);

        Task.Run(() => WriteToFile(entry));
        OnLog?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> GetRecent(int count = 100) => _buffer.TakeLast(count).ToList();
    public string GetLogPath() => _logPath;

    private void WriteToFile(LogEntry e)
    {
        try
        {
            var line = $"[{e.Time:HH:mm:ss.fff}] {e.Level,-7} [{e.Category,-15}] {e.Message}";
            lock (_lock) { File.AppendAllText(_logPath, line + Environment.NewLine); }
        }
        catch { }
    }

    public void Dispose() { }
}

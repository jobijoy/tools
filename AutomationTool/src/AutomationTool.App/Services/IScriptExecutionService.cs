using AutomationTool.Models;

namespace AutomationTool.Services;

/// <summary>
/// Service for executing scripts in various languages.
/// </summary>
public interface IScriptExecutionService
{
    /// <summary>
    /// Execute a script with the specified language.
    /// </summary>
    /// <param name="language">Script language: "powershell" or "csharp"</param>
    /// <param name="script">Script content or file path</param>
    /// <param name="context">Automation context with rule and element info</param>
    /// <param name="timeoutMs">Timeout in milliseconds (0 = no timeout)</param>
    /// <returns>Script execution result</returns>
    Task<ScriptResult> ExecuteAsync(string language, string script, ScriptContext? context = null, int timeoutMs = 5000);

    /// <summary>
    /// Check if scripting is enabled in configuration.
    /// </summary>
    bool IsScriptingEnabled { get; }

    /// <summary>
    /// Get available script languages.
    /// </summary>
    IReadOnlyList<string> SupportedLanguages { get; }
}

/// <summary>
/// Context passed to scripts during execution.
/// </summary>
public class ScriptContext
{
    public Rule? Rule { get; set; }
    public string? MatchedText { get; set; }
    public string? WindowTitle { get; set; }
    public string? ProcessName { get; set; }
    public IntPtr WindowHandle { get; set; }
    public DateTime TriggerTime { get; set; } = DateTime.Now;
    public Dictionary<string, object> Variables { get; set; } = new();
}

/// <summary>
/// Result of script execution.
/// </summary>
public class ScriptResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public object? ReturnValue { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public bool TimedOut { get; set; }

    public static ScriptResult Succeeded(string? output = null, object? returnValue = null) => new()
    {
        Success = true,
        Output = output,
        ReturnValue = returnValue
    };

    public static ScriptResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };

    public static ScriptResult Timeout() => new()
    {
        Success = false,
        Error = "Script execution timed out",
        TimedOut = true
    };
}

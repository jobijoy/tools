using System.Diagnostics;
using System.Text;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Executes PowerShell scripts via process spawning (lightweight, no SDK dependency).
/// C# scripting is disabled in single-file publish mode.
/// </summary>
public class ScriptExecutionService : IScriptExecutionService
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private static readonly string[] _supportedLanguages = ["powershell"];

    public ScriptExecutionService(LogService log, ConfigService config)
    {
        _log = log;
        _config = config;
    }

    public bool IsScriptingEnabled => _config.GetConfig().Settings.ScriptingEnabled;
    public IReadOnlyList<string> SupportedLanguages => _supportedLanguages;

    public async Task<ScriptResult> ExecuteAsync(string language, string script, ScriptContext? context = null, int timeoutMs = 5000)
    {
        if (!IsScriptingEnabled)
        {
            _log.Warn("Script", "Scripting is disabled in settings");
            return ScriptResult.Failed("Scripting is disabled");
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return ScriptResult.Failed("Script is empty");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // Check if script is a file path
            var scriptContent = script;
            if (File.Exists(script))
            {
                scriptContent = await File.ReadAllTextAsync(script);
                _log.Debug("Script", $"Loaded script from file: {script}");
            }

            var result = language.ToLowerInvariant() switch
            {
                "powershell" => await ExecutePowerShellAsync(scriptContent, context, timeoutMs),
                "csharp" or "c#" => ScriptResult.Failed("C# scripting is not available in single-file mode. Use PowerShell instead."),
                _ => ScriptResult.Failed($"Unsupported script language: {language}")
            };

            result.ExecutionTime = sw.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            _log.Error("Script", $"Script execution failed: {ex.Message}");
            return ScriptResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Executes PowerShell via process spawn (lightweight, no SDK required).
    /// </summary>
    private async Task<ScriptResult> ExecutePowerShellAsync(string script, ScriptContext? context, int timeoutMs)
    {
        try
        {
            // Build script with context variables prepended
            var fullScript = new StringBuilder();
            
            if (context != null)
            {
                fullScript.AppendLine($"$RuleName = '{EscapeForPowerShell(context.Rule?.Name)}'");
                fullScript.AppendLine($"$MatchedText = '{EscapeForPowerShell(context.MatchedText)}'");
                fullScript.AppendLine($"$WindowTitle = '{EscapeForPowerShell(context.WindowTitle)}'");
                fullScript.AppendLine($"$ProcessName = '{EscapeForPowerShell(context.ProcessName)}'");
                fullScript.AppendLine($"$TriggerTime = [DateTime]::Parse('{context.TriggerTime:o}')");
                
                foreach (var kvp in context.Variables)
                {
                    fullScript.AppendLine($"${kvp.Key} = '{EscapeForPowerShell(kvp.Value?.ToString())}'");
                }
            }
            
            fullScript.Append(script);

            // Create temp script file for complex scripts
            var tempFile = Path.Combine(Path.GetTempPath(), $"idolclick_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tempFile, fullScript.ToString());

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pwsh.exe",  // Try PowerShell 7 first
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Fallback to Windows PowerShell if pwsh not found
                try
                {
                    using var testProc = Process.Start(new ProcessStartInfo { FileName = "pwsh.exe", Arguments = "-Version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                    testProc?.WaitForExit(1000);
                }
                catch
                {
                    psi.FileName = "powershell.exe";
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return ScriptResult.Failed("Failed to start PowerShell process");
                }

                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();

                var completed = proc.WaitForExit(timeoutMs);
                
                if (!completed)
                {
                    proc.Kill();
                    return ScriptResult.Timeout();
                }

                var output = await outputTask;
                var error = await errorTask;

                if (!string.IsNullOrEmpty(error))
                {
                    _log.Warn("Script", $"PowerShell stderr: {error.Trim()}");
                }

                return new ScriptResult
                {
                    Success = proc.ExitCode == 0,
                    Output = output.TrimEnd(),
                    Error = string.IsNullOrWhiteSpace(error) ? null : error.TrimEnd(),
                    ReturnValue = output.TrimEnd()
                };
            }
            finally
            {
                // Clean up temp file
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch (Exception ex)
        {
            return ScriptResult.Failed(ex.Message);
        }
    }

    private static string EscapeForPowerShell(string? value)
    {
        if (value == null) return "";
        return value.Replace("'", "''");
    }
}

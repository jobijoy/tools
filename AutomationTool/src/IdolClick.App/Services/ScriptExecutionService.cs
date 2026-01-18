using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Executes PowerShell scripts with safety features.
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

    private async Task<ScriptResult> ExecutePowerShellAsync(string script, ScriptContext? context, int timeoutMs)
    {
        return await Task.Run(() =>
        {
            try
            {
                var initialState = InitialSessionState.CreateDefault();
                using var runspace = RunspaceFactory.CreateRunspace(initialState);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                // Add context variables
                if (context != null)
                {
                    ps.AddCommand("Set-Variable").AddParameter("Name", "RuleName").AddParameter("Value", context.Rule?.Name);
                    ps.AddStatement();
                    ps.AddCommand("Set-Variable").AddParameter("Name", "MatchedText").AddParameter("Value", context.MatchedText);
                    ps.AddStatement();
                    ps.AddCommand("Set-Variable").AddParameter("Name", "WindowTitle").AddParameter("Value", context.WindowTitle);
                    ps.AddStatement();
                    ps.AddCommand("Set-Variable").AddParameter("Name", "ProcessName").AddParameter("Value", context.ProcessName);
                    ps.AddStatement();
                    ps.AddCommand("Set-Variable").AddParameter("Name", "TriggerTime").AddParameter("Value", context.TriggerTime);
                    ps.AddStatement();

                    foreach (var kvp in context.Variables)
                    {
                        ps.AddCommand("Set-Variable").AddParameter("Name", kvp.Key).AddParameter("Value", kvp.Value);
                        ps.AddStatement();
                    }
                }

                ps.AddScript(script);

                var output = new StringBuilder();
                var errors = new StringBuilder();
                object? lastResult = null;

                var results = ps.Invoke();

                foreach (var item in results)
                {
                    if (item != null)
                    {
                        output.AppendLine(item.ToString());
                        lastResult = item.BaseObject;
                    }
                }

                foreach (var error in ps.Streams.Error)
                {
                    errors.AppendLine(error.ToString());
                }

                if (errors.Length > 0)
                {
                    _log.Warn("Script", $"PowerShell errors: {errors}");
                }

                return new ScriptResult
                {
                    Success = !ps.HadErrors,
                    Output = output.ToString().TrimEnd(),
                    Error = errors.Length > 0 ? errors.ToString().TrimEnd() : null,
                    ReturnValue = lastResult
                };
            }
            catch (OperationCanceledException)
            {
                return ScriptResult.Timeout();
            }
            catch (Exception ex)
            {
                return ScriptResult.Failed(ex.Message);
            }
        });
    }
}

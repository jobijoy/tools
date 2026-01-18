using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Executes PowerShell and C# scripts with safety features.
/// </summary>
public class ScriptExecutionService : IScriptExecutionService
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private ScriptOptions? _csharpOptions;
    private bool _csharpInitialized;
    private string? _csharpInitError;
    private static readonly string[] _supportedLanguages = ["powershell", "csharp"];

    public ScriptExecutionService(LogService log, ConfigService config)
    {
        _log = log;
        _config = config;
        // Defer C# script options initialization to avoid single-file publish issues
    }

    /// <summary>
    /// Lazily initializes C# scripting options. Returns false if initialization fails.
    /// </summary>
    private bool EnsureCSharpInitialized()
    {
        if (_csharpInitialized) return _csharpOptions != null;

        _csharpInitialized = true;
        
        try
        {
            // Only add references that have a valid location (not single-file bundled)
            var assemblies = new[]
            {
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
                typeof(System.Text.Json.JsonSerializer).Assembly
            }.Where(a => !string.IsNullOrEmpty(a.Location)).ToArray();

            if (assemblies.Length == 0)
            {
                _csharpInitError = "C# scripting unavailable in single-file mode. Use PowerShell scripts instead.";
                _log.Warn("Script", _csharpInitError);
                return false;
            }

            _csharpOptions = ScriptOptions.Default
                .AddReferences(assemblies)
                .AddImports(
                    "System",
                    "System.IO",
                    "System.Linq",
                    "System.Text",
                    "System.Collections.Generic",
                    "System.Threading.Tasks",
                    "System.Net.Http",
                    "System.Text.Json");

            _log.Debug("Script", "C# scripting initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            _csharpInitError = $"C# scripting initialization failed: {ex.Message}";
            _log.Warn("Script", _csharpInitError);
            return false;
        }
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
                "csharp" or "c#" => await ExecuteCSharpAsync(scriptContent, context, timeoutMs),
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
            using var cts = new CancellationTokenSource(timeoutMs > 0 ? timeoutMs : Timeout.Infinite);

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

    private async Task<ScriptResult> ExecuteCSharpAsync(string script, ScriptContext? context, int timeoutMs)
    {
        // Lazy initialization of C# scripting
        if (!EnsureCSharpInitialized())
        {
            return ScriptResult.Failed(_csharpInitError ?? "C# scripting not available");
        }

        using var cts = new CancellationTokenSource(timeoutMs > 0 ? timeoutMs : Timeout.Infinite);

        try
        {
            // Create globals object for the script
            var globals = new ScriptGlobals
            {
                Context = context ?? new ScriptContext(),
                Log = (msg) => _log.Info("Script", msg)
            };

            // Run with timeout using Task.Run + cancellation
            var task = CSharpScript.EvaluateAsync<object?>(script, _csharpOptions!, globals);
            
            var completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs > 0 ? timeoutMs : Timeout.Infinite, cts.Token));
            
            if (completedTask != task)
            {
                return ScriptResult.Timeout();
            }

            var result = await task;
            return ScriptResult.Succeeded(result?.ToString(), result);
        }
        catch (OperationCanceledException)
        {
            return ScriptResult.Timeout();
        }
        catch (CompilationErrorException ex)
        {
            var errors = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
            return ScriptResult.Failed($"Compilation errors:\n{errors}");
        }
        catch (Exception ex)
        {
            return ScriptResult.Failed(ex.Message);
        }
    }
}

/// <summary>
/// Global variables available to C# scripts.
/// </summary>
public class ScriptGlobals
{
    public ScriptContext Context { get; set; } = new();
    public Action<string> Log { get; set; } = _ => { };
}

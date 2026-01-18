using System.Reflection;
using System.Text.RegularExpressions;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Discovers and manages plugin actions from DLLs and PowerShell scripts.
/// </summary>
public class PluginService
{
    private readonly LogService _log;
    private readonly Dictionary<string, PluginInfo> _plugins = new();
    private readonly object _lock = new();

    public PluginService(LogService log)
    {
        _log = log;
    }

    /// <summary>
    /// Get all discovered plugins.
    /// </summary>
    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            lock (_lock)
            {
                return _plugins.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Get a plugin by ID.
    /// </summary>
    public PluginInfo? GetPlugin(string id)
    {
        lock (_lock)
        {
            return _plugins.TryGetValue(id, out var plugin) ? plugin : null;
        }
    }

    /// <summary>
    /// Load all plugins from a directory.
    /// </summary>
    public void LoadPlugins(string pluginsPath)
    {
        if (!Directory.Exists(pluginsPath))
        {
            Directory.CreateDirectory(pluginsPath);
            _log.Info("Plugins", $"Created plugins directory: {pluginsPath}");
            return;
        }

        // Load .NET DLLs
        foreach (var dll in Directory.GetFiles(pluginsPath, "*.dll"))
        {
            try
            {
                LoadDotNetPlugin(dll);
            }
            catch (Exception ex)
            {
                _log.Error("Plugins", $"Failed to load {Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        // Load PowerShell scripts with plugin metadata
        foreach (var ps1 in Directory.GetFiles(pluginsPath, "*.ps1"))
        {
            try
            {
                LoadPowerShellPlugin(ps1);
            }
            catch (Exception ex)
            {
                _log.Error("Plugins", $"Failed to load {Path.GetFileName(ps1)}: {ex.Message}");
            }
        }

        _log.Info("Plugins", $"Loaded {_plugins.Count} plugins from {pluginsPath}");
    }

    private void LoadDotNetPlugin(string dllPath)
    {
        var assembly = Assembly.LoadFrom(dllPath);
        var pluginTypes = assembly.GetTypes()
            .Where(t => typeof(IPluginAction).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in pluginTypes)
        {
            var instance = Activator.CreateInstance(type) as IPluginAction;
            if (instance == null) continue;

            var info = new PluginInfo
            {
                Id = instance.Id,
                Name = instance.Name,
                Description = instance.Description,
                Version = instance.Version,
                Source = dllPath,
                Type = PluginType.DotNet,
                Instance = instance
            };

            lock (_lock)
            {
                _plugins[instance.Id] = info;
            }

            instance.OnLoad();
            _log.Debug("Plugins", $"Loaded .NET plugin: {instance.Name} ({instance.Id})");
        }
    }

    private void LoadPowerShellPlugin(string scriptPath)
    {
        var content = File.ReadAllText(scriptPath);

        // Parse metadata from comments
        // # ID: my-plugin-id
        // # Name: My Plugin
        // # Description: Does something cool
        // # Version: 1.0.0

        var idMatch = Regex.Match(content, @"^#\s*ID:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!idMatch.Success)
        {
            _log.Debug("Plugins", $"Skipping {Path.GetFileName(scriptPath)}: No plugin ID found");
            return;
        }

        var id = idMatch.Groups[1].Value.Trim();
        var nameMatch = Regex.Match(content, @"^#\s*Name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var descMatch = Regex.Match(content, @"^#\s*Description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var versionMatch = Regex.Match(content, @"^#\s*Version:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        var info = new PluginInfo
        {
            Id = id,
            Name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(scriptPath),
            Description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : "",
            Version = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : "1.0.0",
            Source = scriptPath,
            Type = PluginType.PowerShell,
            Instance = new PowerShellPluginWrapper(id, scriptPath, _log)
        };

        lock (_lock)
        {
            _plugins[id] = info;
        }

        _log.Debug("Plugins", $"Loaded PowerShell plugin: {info.Name} ({id})");
    }

    /// <summary>
    /// Execute a plugin action.
    /// </summary>
    public async Task<bool> ExecuteAsync(string pluginId, Rule rule, AutomationContext context)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin == null)
        {
            _log.Warn("Plugins", $"Plugin not found: {pluginId}");
            return false;
        }

        if (!plugin.Enabled)
        {
            _log.Debug("Plugins", $"Plugin is disabled: {pluginId}");
            return false;
        }

        if (plugin.Instance == null)
        {
            _log.Warn("Plugins", $"Plugin has no instance: {pluginId}");
            return false;
        }

        try
        {
            _log.Debug("Plugins", $"Executing plugin: {plugin.Name}");
            return await plugin.Instance.ExecuteAsync(rule, context);
        }
        catch (Exception ex)
        {
            _log.Error("Plugins", $"Plugin execution failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unload all plugins.
    /// </summary>
    public void UnloadAll()
    {
        lock (_lock)
        {
            foreach (var plugin in _plugins.Values)
            {
                plugin.Instance?.OnUnload();
            }
            _plugins.Clear();
        }
    }
}

/// <summary>
/// Wrapper to execute PowerShell scripts as plugins.
/// </summary>
internal class PowerShellPluginWrapper : IPluginAction
{
    private readonly string _scriptPath;
    private readonly LogService _log;

    public string Id { get; }
    public string Name => Path.GetFileNameWithoutExtension(_scriptPath);
    public string Description => $"PowerShell plugin from {Path.GetFileName(_scriptPath)}";
    public string Version => "1.0.0";

    public PowerShellPluginWrapper(string id, string scriptPath, LogService log)
    {
        Id = id;
        _scriptPath = scriptPath;
        _log = log;
    }

    public async Task<bool> ExecuteAsync(Rule rule, AutomationContext context)
    {
        var result = await App.Scripts.ExecuteAsync(
            "powershell",
            _scriptPath,
            new ScriptContext
            {
                Rule = rule,
                MatchedText = context.MatchedText,
                WindowTitle = context.WindowTitle,
                ProcessName = context.ProcessName,
                WindowHandle = context.WindowHandle,
                TriggerTime = context.TriggerTime,
                Variables = context.Variables
            });

        if (!result.Success)
        {
            _log.Warn("Plugin", $"PowerShell plugin failed: {result.Error}");
        }

        return result.Success;
    }
}

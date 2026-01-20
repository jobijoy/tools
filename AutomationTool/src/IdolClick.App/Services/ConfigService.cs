using System.IO;
using System.Text.Json;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Manages loading, saving, and hot-reloading of application configuration.
/// </summary>
/// <remarks>
/// <para>Configuration is stored in config.json in the application directory.</para>
/// <para>The service automatically detects file changes and reloads configuration.</para>
/// <para>Supports schema versioning via <see cref="Rule.SchemaVersion"/> for forward compatibility.</para>
/// </remarks>
public class ConfigService
{
    /// <summary>
    /// Current configuration schema version. Increment when making breaking changes.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
    
    private readonly string _configPath;
    private AppConfig? _config;
    private DateTime _lastModified;
    private readonly object _lock = new();
    
    /// <summary>
    /// Gets the path to the config file.
    /// </summary>
    public string ConfigPath => _configPath;
    
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes the configuration service with the specified config file path.
    /// </summary>
    /// <param name="configPath">Absolute path to config.json file.</param>
    public ConfigService(string configPath)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
    }

    /// <summary>
    /// Gets the current configuration, reloading from disk if the file has changed.
    /// </summary>
    /// <returns>The current <see cref="AppConfig"/> instance.</returns>
    public AppConfig GetConfig()
    {
        lock (_lock)
        {
            if (_config != null && File.Exists(_configPath))
            {
                var modified = File.GetLastWriteTimeUtc(_configPath);
                if (modified <= _lastModified)
                    return _config;
            }

            _config = Load();
            return _config;
        }
    }

    /// <summary>
    /// Saves the configuration to disk.
    /// </summary>
    /// <param name="config">The configuration to save.</param>
    public void SaveConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(_configPath, json);
            _config = config;
            _lastModified = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Forces a reload of the configuration from disk.
    /// </summary>
    public void ReloadConfig()
    {
        lock (_lock)
        {
            _config = null;
            _lastModified = DateTime.MinValue;
        }
        GetConfig(); // Trigger reload
    }

    private AppConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Config not found, creating default at: {_configPath}");
            var defaultConfig = CreateDefaultConfig();
            SaveConfig(defaultConfig);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            _lastModified = File.GetLastWriteTimeUtc(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions) ?? CreateDefaultConfig();
            
            // Upgrade old schemas if needed
            var upgradeCount = 0;
            foreach (var rule in config.Rules)
            {
                if (UpgradeOldSchemaIfNeeded(rule))
                    upgradeCount++;
            }
            
            if (upgradeCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[ConfigService] Upgraded {upgradeCount} rules to schema v{CurrentSchemaVersion}");
                SaveConfig(config); // Persist upgrades
            }
            
            return config;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Malformed config JSON: {ex.Message}");
            // Backup corrupted file before overwriting
            BackupCorruptedConfig();
            return CreateDefaultConfig();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Failed to load config: {ex.GetType().Name}: {ex.Message}");
            return CreateDefaultConfig();
        }
    }
    
    /// <summary>
    /// Backs up a corrupted config file for debugging.
    /// </summary>
    private void BackupCorruptedConfig()
    {
        try
        {
            var backupPath = _configPath + $".corrupted.{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(_configPath, backupPath, overwrite: true);
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Backed up corrupted config to: {backupPath}");
        }
        catch { /* Ignore backup errors */ }
    }
    
    /// <summary>
    /// Upgrades old rule schemas to the current version.
    /// </summary>
    /// <param name="rule">The rule to upgrade.</param>
    /// <returns>True if the rule was upgraded, false if already current.</returns>
    private static bool UpgradeOldSchemaIfNeeded(Rule rule)
    {
        var wasUpgraded = false;
        
        // v0 -> v1: Set default schema version if missing (0 is default int value)
        if (rule.SchemaVersion == 0)
        {
            rule.SchemaVersion = 1;
            wasUpgraded = true;
        }
        
        // Future migrations:
        // if (rule.SchemaVersion == 1) { ... migrate to v2 ... rule.SchemaVersion = 2; wasUpgraded = true; }
        
        return wasUpgraded;
    }

    private static AppConfig CreateDefaultConfig() => new()
    {
        Settings = new GlobalSettings
        {
            PollingIntervalMs = 10000,
            AutomationEnabled = false,
            ShowPanelOnStart = true,
            ToggleHotkey = "Ctrl+Alt+T",
            LogLevel = "Info",
            Theme = "Dark"
        },
        Rules =
        [
            new Rule
            {
                Id = "vscode-copilot-allow",
                Name = "VS Code Copilot Allow/Continue",
                Enabled = true,
                TargetApp = "Code, Code - Insiders",
                ElementType = "Button",
                MatchText = "Allow, Continue, OK, Yes",
                ExcludeTexts = ["Don't Allow", "Cancel", "No", "Continue Chat", "Continue in"],
                Action = "Click",
                CooldownSeconds = 5
            },
            new Rule
            {
                Id = "github-select-jobijoy",
                Name = "GitHub Select jobijoy Account",
                Enabled = true,
                TargetApp = "",
                WindowTitle = "Select an account",
                ElementType = "Any",
                MatchText = "jobijoy",
                Action = "Click",
                CooldownSeconds = 5
            },
            new Rule
            {
                Id = "github-continue",
                Name = "GitHub Continue Button",
                Enabled = true,
                TargetApp = "",
                WindowTitle = "Select an account",
                ElementType = "Button",
                MatchText = "Continue",
                Action = "Click",
                CooldownSeconds = 5
            }
        ]
    };
}

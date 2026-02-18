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
    private readonly string _secretsPath;
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

        // Store credentials in .kv/store.dat — the folder is gitignored.
        var configDir = Path.GetDirectoryName(configPath) ?? ".";
        var kvDir = Path.Combine(configDir, ".kv");
        Directory.CreateDirectory(kvDir);
        _secretsPath = Path.Combine(kvDir, "store.dat");

        // Migrate from legacy locations
        MigrateLegacy(Path.Combine(configDir, ".secrets", "secrets.json"));
        MigrateLegacy(Path.Combine(configDir, "secrets.json"));
    }

    private void MigrateLegacy(string legacyPath)
    {
        if (File.Exists(legacyPath) && !File.Exists(_secretsPath))
        {
            try { File.Move(legacyPath, _secretsPath); } catch { }
        }
    }

    /// <summary>
    /// Gets the path to the secrets file (next to config.json).
    /// </summary>
    public string SecretsPath => _secretsPath;

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
    /// Saves the configuration to disk. Secrets (API keys, endpoints) are written
    /// to a separate secrets.json file and stripped from config.json.
    /// </summary>
    /// <param name="config">The configuration to save.</param>
    public void SaveConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        
        lock (_lock)
        {
            // Extract secrets and save separately
            SaveSecrets(config.AgentSettings);

            // Write config.json WITHOUT secrets (empty placeholders)
            var agent = config.AgentSettings;
            var savedEndpoint = agent.Endpoint;
            var savedApiKey = agent.ApiKey;
            var savedWhisperEndpoint = agent.WhisperEndpoint;
            var savedWhisperApiKey = agent.WhisperApiKey;

            agent.Endpoint = "";
            agent.ApiKey = "";
            agent.WhisperEndpoint = "";
            agent.WhisperApiKey = "";

            var json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(_configPath, json);

            // Restore in-memory values so the running app still works
            agent.Endpoint = savedEndpoint;
            agent.ApiKey = savedApiKey;
            agent.WhisperEndpoint = savedWhisperEndpoint;
            agent.WhisperApiKey = savedWhisperApiKey;

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

            // Merge secrets from secrets.json (survives bin wipes)
            LoadSecrets(config.AgentSettings);
            
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
    
    // ═══════════════════════════════════════════════════════════════════════════════
    // SECRETS — stored in .kv/store.dat next to config.json, folder is .gitignored
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads secrets from secrets.json and merges into the AgentSettings.
    /// Secrets override empty config values — if a user has keys in secrets.json,
    /// they're applied even when config.json has empty placeholders.
    /// </summary>
    private void LoadSecrets(AgentSettings agent)
    {
        try
        {
            if (!File.Exists(_secretsPath)) return;

            var json = File.ReadAllText(_secretsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("endpoint", out var ep) && ep.GetString() is string endpoint && !string.IsNullOrWhiteSpace(endpoint))
                agent.Endpoint = endpoint;

            if (root.TryGetProperty("apiKey", out var ak) && ak.GetString() is string apiKey && !string.IsNullOrWhiteSpace(apiKey))
                agent.ApiKey = apiKey;

            if (root.TryGetProperty("whisperEndpoint", out var wep) && wep.GetString() is string whisperEndpoint && !string.IsNullOrWhiteSpace(whisperEndpoint))
                agent.WhisperEndpoint = whisperEndpoint;

            if (root.TryGetProperty("whisperApiKey", out var wak) && wak.GetString() is string whisperApiKey && !string.IsNullOrWhiteSpace(whisperApiKey))
                agent.WhisperApiKey = whisperApiKey;

            System.Diagnostics.Debug.WriteLine($"[ConfigService] Secrets loaded from: {_secretsPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Failed to load secrets: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves secrets to secrets.json. Only writes non-empty values.
    /// </summary>
    private void SaveSecrets(AgentSettings agent)
    {
        try
        {
            var secrets = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(agent.Endpoint))
                secrets["endpoint"] = agent.Endpoint;
            if (!string.IsNullOrWhiteSpace(agent.ApiKey))
                secrets["apiKey"] = agent.ApiKey;
            if (!string.IsNullOrWhiteSpace(agent.WhisperEndpoint))
                secrets["whisperEndpoint"] = agent.WhisperEndpoint;
            if (!string.IsNullOrWhiteSpace(agent.WhisperApiKey))
                secrets["whisperApiKey"] = agent.WhisperApiKey;

            if (secrets.Count > 0)
            {
                var json = JsonSerializer.Serialize(secrets, _jsonOptions);
                File.WriteAllText(_secretsPath, json);
                System.Diagnostics.Debug.WriteLine($"[ConfigService] Secrets saved to: {_secretsPath}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Failed to save secrets: {ex.Message}");
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
            Mode = AppMode.Classic,
            AutomationEnabled = false,
            PollingIntervalMs = 3000,
            ToggleHotkey = "Ctrl+Alt+T",
            ShowPanelOnStart = true,
            GlobalMouseNudge = false,
            LogLevel = "Info",
            Theme = "Dark",
            ShowExecutionCount = true,
            ClickRadar = true,
            ScriptingEnabled = true,
            DefaultScriptTimeoutMs = 5000,
            PluginsEnabled = true,
            DisabledPlugins = [],
            TimelineEnabled = true,
            MaxTimelineEvents = 1000,
            PersistTimeline = false,
            NotificationDefaults = new NotificationDefaults
            {
                ToastOnRuleMatch = false,
                IncludeTimestamp = true
            }
        },
        AgentSettings = new AgentSettings
        {
            Endpoint = "",
            ModelId = "gpt-4o",
            ApiKey = "",
            MaxTokens = 4096,
            Temperature = 0,
            SystemPrompt = "You are IdolClick Agent, a desktop automation assistant. You can create automation rules, click UI elements, type text, and inspect windows. Be concise and action-oriented."
        },
        Rules =
        [
            new Rule
            {
                SchemaVersion = 1,
                Id = "vscode-allow",
                Name = "VS Code Allow Button",
                Enabled = true,
                TargetApp = "Code, Code - Insiders",
                ElementType = "Button",
                MatchText = "Allow, Continue, Yes, OK, Accept",
                ExcludeTexts = ["Continue Chat in", "Continue in"],
                Action = "Click",
                CooldownSeconds = 2
            }
        ]
    };
}

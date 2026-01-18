using System.IO;
using System.Text.Json;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Manages loading and saving configuration.
/// </summary>
public class ConfigService
{
    private readonly string _configPath;
    private AppConfig? _config;
    private DateTime _lastModified;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigService(string configPath)
    {
        _configPath = configPath;
    }

    public AppConfig GetConfig()
    {
        if (_config != null && File.Exists(_configPath))
        {
            var modified = File.GetLastWriteTime(_configPath);
            if (modified <= _lastModified)
                return _config;
        }

        _config = Load();
        return _config;
    }

    public void SaveConfig(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        File.WriteAllText(_configPath, json);
        _config = config;
        _lastModified = DateTime.Now;
    }

    private AppConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            var defaultConfig = CreateDefaultConfig();
            SaveConfig(defaultConfig);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            _lastModified = File.GetLastWriteTime(_configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions) ?? CreateDefaultConfig();
        }
        catch
        {
            return CreateDefaultConfig();
        }
    }

    private static AppConfig CreateDefaultConfig() => new()
    {
        Settings = new GlobalSettings
        {
            PollingIntervalMs = 3000,
            AutomationEnabled = false,
            ShowPanelOnStart = true,
            MinimizeToTray = true,
            ToggleHotkey = "Ctrl+Alt+T",
            LogLevel = "Info",
            Theme = "Dark"
        },
        Rules =
        [
            new Rule
            {
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

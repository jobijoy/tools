using System.IO;
using System.Text.Json;

namespace VsCodeAllowClicker.App.Services;

/// <summary>
/// Manages loading and saving configuration.
/// </summary>
public sealed class ConfigService
{
    private readonly string _configPath;
    private Models.AppConfig? _config;
    private DateTime _lastModified;

    public ConfigService(string configPath)
    {
        _configPath = configPath;
    }

    public Models.AppConfig GetConfig()
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

    public void SaveConfig(Models.AppConfig config)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(_configPath, json);
        _config = config;
        _lastModified = DateTime.Now;
    }

    private Models.AppConfig Load()
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
            return JsonSerializer.Deserialize<Models.AppConfig>(json) ?? CreateDefaultConfig();
        }
        catch
        {
            return CreateDefaultConfig();
        }
    }

    private static Models.AppConfig CreateDefaultConfig() => new()
    {
        Settings = new Models.GlobalSettings
        {
            PollingIntervalMs = 3000,
            AutoStart = false,
            ShowPanelOnStart = true,
            ToggleHotkey = "Ctrl+Alt+A",
            LogLevel = "Info"
        },
        Rules =
        [
            new Models.Rule
            {
                Id = "vscode-allow",
                Name = "VS Code Allow Button",
                Enabled = true,
                Target = new Models.TargetMatch
                {
                    ProcessNames = ["Code", "Code - Insiders"],
                    ElementType = "Button",
                    TextPatterns = ["Allow", "Continue", "Yes", "OK", "Accept"],
                    ExcludePatterns = ["Continue Chat in", "Continue in"]
                },
                Action = new Models.RuleAction { Type = "Click" },
                Safety = new Models.SafetySettings { CooldownMs = 1500 }
            },
            new Models.Rule
            {
                Id = "github-account",
                Name = "GitHub Account Selection",
                Enabled = true,
                Target = new Models.TargetMatch
                {
                    ProcessNames = ["Code"],
                    WindowTitleContains = "Select an account",
                    ElementType = "ListItem",
                    TextPatterns = ["*"] // Match any list item
                },
                Action = new Models.RuleAction 
                { 
                    Type = "Click",
                    DelayAfterMs = 300
                },
                Safety = new Models.SafetySettings { CooldownMs = 2000 }
            }
        ]
    };
}

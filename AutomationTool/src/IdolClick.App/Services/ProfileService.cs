using System.IO;
using System.Text.Json;

namespace IdolClick.Services;

/// <summary>
/// Manages configuration profiles - multiple saved configs that can be switched.
/// </summary>
public class ProfileService
{
    private readonly string _profilesDir;
    private readonly ConfigService _configService;
    private readonly LogService _log;
    private string _activeProfile = "Default";
    
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string ActiveProfile => _activeProfile;
    public event Action? ProfileChanged;

    /// <summary>
    /// Saves the current config.json to the active profile file.
    /// Call this after any config changes to keep the profile in sync.
    /// </summary>
    public void SaveCurrentProfile()
    {
        SaveCurrentToProfile(_activeProfile);
    }

    public ProfileService(ConfigService configService, LogService log)
    {
        _configService = configService;
        _log = log;
        
        // Profiles stored in AppData\IdolClick\Profiles
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdolClick");
        _profilesDir = Path.Combine(appDir, "Profiles");
        
        if (!Directory.Exists(_profilesDir))
            Directory.CreateDirectory(_profilesDir);

        // Load saved active profile name
        var activeFile = Path.Combine(appDir, "active-profile.txt");
        if (File.Exists(activeFile))
        {
            var name = File.ReadAllText(activeFile).Trim();
            if (!string.IsNullOrEmpty(name))
                _activeProfile = name;
        }
    }

    /// <summary>
    /// Gets list of available profile names.
    /// </summary>
    public List<string> GetProfiles()
    {
        var profiles = new List<string> { "Default" };
        
        try
        {
            var files = Directory.GetFiles(_profilesDir, "*.json");
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                    profiles.Add(name);
            }
        }
        catch { }

        // Ensure active profile is in the list
        if (!profiles.Contains(_activeProfile, StringComparer.OrdinalIgnoreCase))
            profiles.Add(_activeProfile);

        return profiles.OrderBy(p => p == "Default" ? "" : p).ToList();
    }

    /// <summary>
    /// Switches to a different profile, loading its config.
    /// </summary>
    public void SwitchProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;
        if (profileName == _activeProfile) return;

        // Save current config to current profile
        SaveCurrentToProfile(_activeProfile);

        // Load new profile
        _activeProfile = profileName;
        LoadProfileConfig(profileName);

        // Persist active profile name
        SaveActiveProfileName();

        _log.Info("Profile", $"Switched to profile: {profileName}");
        ProfileChanged?.Invoke();
    }

    /// <summary>
    /// Creates a new profile (copies current config).
    /// </summary>
    public bool CreateProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;
        
        // Sanitize name
        profileName = SanitizeFileName(profileName);
        if (string.IsNullOrEmpty(profileName)) return false;

        // Check if already exists
        var profiles = GetProfiles();
        if (profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            return false;

        // Save current config as new profile
        SaveCurrentToProfile(profileName);
        _log.Info("Profile", $"Created profile: {profileName}");
        
        return true;
    }

    /// <summary>
    /// Renames a profile.
    /// </summary>
    public bool RenameProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
        if (oldName.Equals("Default", StringComparison.OrdinalIgnoreCase)) return false;
        
        newName = SanitizeFileName(newName);
        if (string.IsNullOrEmpty(newName)) return false;

        var oldPath = GetProfilePath(oldName);
        var newPath = GetProfilePath(newName);

        if (!File.Exists(oldPath)) return false;
        if (File.Exists(newPath)) return false;

        try
        {
            File.Move(oldPath, newPath);
            
            if (_activeProfile.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile = newName;
                SaveActiveProfileName();
            }

            _log.Info("Profile", $"Renamed profile: {oldName} -> {newName}");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes a profile.
    /// </summary>
    public bool DeleteProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;
        if (profileName.Equals("Default", StringComparison.OrdinalIgnoreCase)) return false;

        var path = GetProfilePath(profileName);
        if (!File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            
            if (_activeProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile = "Default";
                LoadProfileConfig("Default");
                SaveActiveProfileName();
            }

            _log.Info("Profile", $"Deleted profile: {profileName}");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Duplicates an existing profile.
    /// </summary>
    public bool DuplicateProfile(string sourceName, string newName)
    {
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(newName)) return false;
        
        newName = SanitizeFileName(newName);
        if (string.IsNullOrEmpty(newName)) return false;

        var sourcePath = GetProfilePath(sourceName);
        var newPath = GetProfilePath(newName);

        if (File.Exists(newPath)) return false;

        try
        {
            if (File.Exists(sourcePath))
                File.Copy(sourcePath, newPath);
            else
            {
                // Source is Default or doesn't exist - copy current config
                SaveCurrentToProfile(newName);
            }

            _log.Info("Profile", $"Duplicated profile: {sourceName} -> {newName}");
            return true;
        }
        catch { return false; }
    }

    private void SaveCurrentToProfile(string profileName)
    {
        var cfg = _configService.GetConfig();
        var path = GetProfilePath(profileName);
        
        try
        {
            var json = JsonSerializer.Serialize(cfg, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private void LoadProfileConfig(string profileName)
    {
        var path = GetProfilePath(profileName);
        
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Models.AppConfig>(json, _jsonOptions);
                if (cfg != null)
                {
                    // Save to main config location so ConfigService picks it up
                    var mainConfigPath = _configService.ConfigPath;
                    File.WriteAllText(mainConfigPath, json);
                    _configService.ReloadConfig();
                }
            }
            catch { }
        }
        else if (!profileName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            // Profile doesn't exist yet - create it from current
            SaveCurrentToProfile(profileName);
        }
    }

    private void SaveActiveProfileName()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdolClick");
        var activeFile = Path.Combine(appDir, "active-profile.txt");
        
        try
        {
            File.WriteAllText(activeFile, _activeProfile);
        }
        catch { }
    }

    private string GetProfilePath(string profileName)
    {
        return Path.Combine(_profilesDir, $"{SanitizeFileName(profileName)}.json");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(c => !invalid.Contains(c))).Trim();
    }
}

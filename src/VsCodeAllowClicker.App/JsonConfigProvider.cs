using System.IO;
using System.Text.Json;

namespace VsCodeAllowClicker.App;

internal sealed class JsonConfigProvider
{
    private readonly string _path;

    public JsonConfigProvider(string path)
    {
        _path = path;
    }

    public bool TryGetConfig(out AppConfig config)
    {
        config = new AppConfig();

        try
        {
            if (!File.Exists(_path))
            {
                return false;
            }

            var json = File.ReadAllText(_path);
            config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppConfig();

            return true;
        }
        catch
        {
            return false;
        }
    }
}

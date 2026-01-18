# Plugins Directory

Place your plugin files here. AutomationTool supports two types of plugins:

## PowerShell Plugins (`.ps1`)

PowerShell scripts with metadata in comments at the top:

```powershell
# ID: my-plugin-id
# Name: My Plugin Name
# Description: What this plugin does
# Version: 1.0.0

# Your script code here
# Available variables: $RuleName, $MatchedText, $WindowTitle, $ProcessName, $TriggerTime
```

## .NET Plugins (`.dll`)

Implement the `IPluginAction` interface:

```csharp
using AutomationTool.Services;
using AutomationTool.Models;

public class MyPlugin : IPluginAction
{
    public string Id => "my-plugin";
    public string Name => "My Plugin";
    public string Description => "Does something cool";
    public string Version => "1.0.0";

    public async Task<bool> ExecuteAsync(Rule rule, AutomationContext ctx)
    {
        // Your plugin logic here
        ctx.Log?.Invoke("MyPlugin", "Executed!");
        return true;
    }
}
```

Compile as a class library targeting `net8.0-windows` and reference `AutomationTool.exe`.

## Included Sample Plugins

- `sample-logger.ps1` - Logs rule triggers to a text file
- `discord-webhook.ps1` - Sends notifications to Discord (requires `DISCORD_WEBHOOK_URL` env var)

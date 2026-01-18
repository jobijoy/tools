using System.IO;
using System.Windows;
using AutomationTool.Services;

namespace AutomationTool;

public partial class App : Application
{
    public static ConfigService Config { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static AutomationEngine Engine { get; private set; } = null!;
    public static TrayService Tray { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize services
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        Config = new ConfigService(configPath);
        Log = new LogService();
        Log.SetLevel(Config.GetConfig().Settings.LogLevel);
        Engine = new AutomationEngine(Config, Log);
        Tray = new TrayService();

        Log.Info("App", "Automation Tool started");

        // Start engine
        Engine.Start();
        if (Config.GetConfig().Settings.AutomationEnabled)
            Engine.SetEnabled(true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App", "Shutting down");
        Tray.Dispose();
        Engine.Dispose();
        base.OnExit(e);
    }
}

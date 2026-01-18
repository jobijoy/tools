using System.Windows;
using AutomationTool.Services;

namespace AutomationTool;

public partial class App : Application
{
    public static ConfigService Config { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static AutomationEngine Engine { get; private set; } = null!;
    public static TrayService Tray { get; private set; } = null!;
    public static IRegionCaptureService RegionCapture { get; private set; } = null!;
    public static INotificationService Notifications { get; private set; } = null!;
    public static IScriptExecutionService Scripts { get; private set; } = null!;
    public static PluginService Plugins { get; private set; } = null!;
    public static EventTimelineService Timeline { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize services
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        Config = new ConfigService(configPath);
        Log = new LogService();
        Log.SetLevel(Config.GetConfig().Settings.LogLevel);
        
        // Initialize new services
        RegionCapture = new RegionCaptureService(Log);
        Notifications = new NotificationService(Log, Config);
        Scripts = new ScriptExecutionService(Log, Config);
        Plugins = new PluginService(Log);
        Timeline = new EventTimelineService(Log);
        
        Engine = new AutomationEngine(Config, Log);
        Tray = new TrayService();

        Log.Info("App", "Automation Tool started");

        // Load plugins
        var pluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        Plugins.LoadPlugins(pluginsPath);

        // Start engine
        Engine.Start();
        if (Config.GetConfig().Settings.AutomationEnabled)
            Engine.SetEnabled(true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App", "Shutting down");
        Timeline.Dispose();
        Tray.Dispose();
        Engine.Dispose();
        base.OnExit(e);
    }
}

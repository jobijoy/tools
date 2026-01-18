using System.Windows;
using System.Threading;
using IdolClick.Services;
using IdolClick.UI;

namespace IdolClick;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "IdolClick_SingleInstance_Mutex";
    
    public static ConfigService Config { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static AutomationEngine Engine { get; private set; } = null!;
    public static TrayService Tray { get; private set; } = null!;
    public static IRegionCaptureService RegionCapture { get; private set; } = null!;
    public static INotificationService Notifications { get; private set; } = null!;
    public static IScriptExecutionService Scripts { get; private set; } = null!;
    public static PluginService Plugins { get; private set; } = null!;
    public static EventTimelineService Timeline { get; private set; } = null!;

    private SplashWindow? _splash;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Idol Click is already running.\n\nCheck the system tray for the existing instance.",
                "Idol Click", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Show splash screen
        _splash = new SplashWindow();
        _splash.Show();

        // Run initialization on background thread
        Task.Run(async () =>
        {
            try
            {
                // Initialize services with progress updates
                UpdateSplash("Loading configuration...", 0.1);
                await Task.Delay(150);
                
                var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
                Config = new ConfigService(configPath);
                
                UpdateSplash("Starting log service...", 0.2);
                await Task.Delay(100);
                
                Log = new LogService();
                Log.SetLevel(Config.GetConfig().Settings.LogLevel);
                
                UpdateSplash("Initializing region capture...", 0.3);
                await Task.Delay(100);
                
                RegionCapture = new RegionCaptureService(Log);
                
                UpdateSplash("Setting up notifications...", 0.4);
                await Task.Delay(100);
                
                Notifications = new NotificationService(Log, Config);
                
                UpdateSplash("Loading script engine...", 0.5);
                await Task.Delay(150);
                
                Scripts = new ScriptExecutionService(Log, Config);
                
                UpdateSplash("Discovering plugins...", 0.6);
                await Task.Delay(100);
                
                Plugins = new PluginService(Log);
                var pluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
                Plugins.LoadPlugins(pluginsPath);
                
                UpdateSplash("Starting event timeline...", 0.7);
                await Task.Delay(100);
                
                Timeline = new EventTimelineService(Log);
                
                UpdateSplash("Initializing automation engine...", 0.85);
                await Task.Delay(150);
                
                // These need to be created on UI thread
                Dispatcher.Invoke(() =>
                {
                    Engine = new AutomationEngine(Config, Log);
                    Tray = new TrayService();
                });
                
                UpdateSplash("Starting up...", 1.0);
                await Task.Delay(400);

                // Show main window on UI thread
                Dispatcher.Invoke(() =>
                {
                    Log.Info("App", "Idol Click started");
                    
                    // Start engine
                    Engine.Start();
                    if (Config.GetConfig().Settings.AutomationEnabled)
                        Engine.SetEnabled(true);

                    // Create and show main window
                    _mainWindow = new MainWindow();
                    MainWindow = _mainWindow;
                    
                    // Fade out splash
                    _splash.FadeOut(() =>
                    {
                        Dispatcher.Invoke(() => _mainWindow.Show());
                    });
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show($"Startup error: {ex.Message}\n\n{ex.StackTrace}", 
                        "Idol Click Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                });
            }
        });
    }

    private void UpdateSplash(string status, double progress)
    {
        Dispatcher.Invoke(() => _splash?.UpdateStatus(status, progress));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log?.Info("App", "Shutting down");
        Timeline?.Dispose();
        Tray?.Dispose();
        Engine?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

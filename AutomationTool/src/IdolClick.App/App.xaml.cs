using System.Windows;
using System.Threading;
using IdolClick.Services;
using IdolClick.UI;

namespace IdolClick;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showWindowEvent;
    private static Thread? _signalListenerThread;
    private const string MutexName = "IdolClick_SingleInstance_Mutex";
    private const string EventName = "IdolClick_ShowWindow_Event";
    
    public static ConfigService Config { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static AutomationEngine Engine { get; private set; } = null!;
    public static HotkeyService Hotkey { get; private set; } = null!;
    public static ProfileService Profiles { get; private set; } = null!;
    public static IRegionCaptureService RegionCapture { get; private set; } = null!;
    public static INotificationService Notifications { get; private set; } = null!;
    public static IScriptExecutionService Scripts { get; private set; } = null!;
    public static PluginService Plugins { get; private set; } = null!;
    public static EventTimelineService Timeline { get; private set; } = null!;

    private SplashWindow? _splash;
    private MainWindow? _mainWindow;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        
        if (!createdNew)
        {
            // Another instance is running - signal it to show window
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // Start listening for signals from other instances
        StartSignalListener();

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
                
                // Use executable directory for config (works with single-file publish)
                var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
                var configPath = Path.Combine(appDir, "config.json");
                Config = new ConfigService(configPath);
                
                UpdateSplash("Loading profiles...", 0.15);
                await Task.Delay(100);
                
                Log = new LogService(); // Temporary for ProfileService
                Profiles = new ProfileService(Config, Log);
                
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
                    Hotkey = new HotkeyService();
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

    /// <summary>
    /// Signal the existing instance to show its window.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(EventName);
            evt.Set();
        }
        catch
        {
            // Event doesn't exist yet, instance might still be starting
        }
    }

    /// <summary>
    /// Start listening for signals from other instances.
    /// </summary>
    private void StartSignalListener()
    {
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        
        _signalListenerThread = new Thread(() =>
        {
            while (!_isShuttingDown)
            {
                try
                {
                    // Wait for signal with timeout so we can check shutdown flag
                    if (_showWindowEvent.WaitOne(500))
                    {
                        // Signal received - show main window
                        Dispatcher.BeginInvoke(() => ShowMainWindow());
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "IdolClick_SignalListener"
        };
        _signalListenerThread.Start();
    }

    /// <summary>
    /// Show and activate the main window.
    /// </summary>
    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;  // Bring to front
        _mainWindow.Topmost = false; // Reset topmost
        _mainWindow.Focus();
    }

    private void UpdateSplash(string status, double progress)
    {
        Dispatcher.Invoke(() => _splash?.UpdateStatus(status, progress));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        Log?.Info("App", "Shutting down");
        
        // Clean up signal listener
        _showWindowEvent?.Set(); // Wake up listener thread
        _signalListenerThread?.Join(1000);
        _showWindowEvent?.Dispose();
        
        Timeline?.Dispose();
        Hotkey?.Dispose();
        Engine?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

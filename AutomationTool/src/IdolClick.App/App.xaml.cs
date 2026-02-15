using System.Windows;
using System.Threading;
using IdolClick.Services;
using IdolClick.Services.Infrastructure;
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
    public static IAgentService Agent { get; private set; } = null!;
    public static StepExecutor FlowExecutor { get; private set; } = null!;
    public static IAutomationBackend DesktopBackend { get; private set; } = null!;
    public static ReportService Reports { get; private set; } = null!;
    public static VisionService Vision { get; private set; } = null!;

    /// <summary>
    /// When true, the kill switch has been activated and the engine cannot be
    /// re-enabled until the user manually resets it from the UI.
    /// Prevents restart loops after emergency stop.
    /// </summary>
    public static bool KillSwitchActive { get; private set; }

    /// <summary>
    /// Cancellation source for the currently running agent flow, if any.
    /// Set by the agent chat panel when a flow starts; cancelled by kill switch.
    /// </summary>
    public static CancellationTokenSource? ActiveFlowCts { get; set; }

    private SplashWindow? _splash;
    private MainWindow? _mainWindow;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── CLI arg stubs (v1.1 will expand) ─────────────────────────────────
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--version":
                case "-v":
                    ServiceHost.PrintVersion();
                    Shutdown(0);
                    return;
                case "--help":
                case "-h":
                case "/?":
                    ServiceHost.PrintHelp();
                    Shutdown(0);
                    return;
            }
        }

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
                
                UpdateSplash("Initializing agent service...", 0.8);
                await Task.Delay(100);
                
                Agent = new AgentService(Config, Log);
                
                UpdateSplash("Initializing flow executor...", 0.82);
                await Task.Delay(100);
                
                var flowValidator = new FlowValidatorService(Log);
                var ruleExecutor = new ActionExecutor(Log);
                var flowActionExecutor = new FlowActionExecutor(Log, ruleExecutor);
                var assertionEvaluator = new AssertionEvaluator(Log);
                var selectorParser = new SelectorParser(Log);
                
                // Vision service (fallback for UIA resolution failures)
                Vision = new VisionService(Config, Log);
                
                DesktopBackend = new Services.Infrastructure.DesktopBackend(Log, flowActionExecutor, assertionEvaluator, selectorParser, Vision);
                FlowExecutor = new StepExecutor(Log, flowValidator, DesktopBackend);
                
                Reports = new ReportService(Log);
                
                // Connect execution services to agent for closed-loop
                if (Agent is AgentService agentSvc)
                    agentSvc.SetExecutionServices(FlowExecutor, Reports, Vision);
                
                UpdateSplash("Initializing automation engine...", 0.85);
                await Task.Delay(150);
                
                // These need to be created on UI thread
                Dispatcher.Invoke(() =>
                {
                    Engine = new AutomationEngine(Config, Log);
                    Hotkey = new HotkeyService();

                    // Wire kill switch — emergency stop for all automation
                    Hotkey.OnKillSwitchActivated += () =>
                    {
                        KillSwitchActive = true;
                        Engine.SetEnabled(false);
                        ActiveFlowCts?.Cancel();
                        Log.Audit("KillSwitch", "EMERGENCY STOP activated — all automation disabled until manual reset");
                    };
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

    /// <summary>
    /// Resets the kill switch, allowing the engine to be re-enabled.
    /// Called from the UI when the user explicitly acknowledges the emergency stop.
    /// </summary>
    public static void ResetKillSwitch()
    {
        KillSwitchActive = false;
        Log.Audit("KillSwitch", "Kill switch reset by user — automation can be re-enabled");
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
        
        (Agent as IDisposable)?.Dispose();
        Timeline?.Dispose();
        Hotkey?.Dispose();
        Engine?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

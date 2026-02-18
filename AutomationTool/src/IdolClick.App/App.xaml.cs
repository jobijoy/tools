using System.Windows;
using System.Threading;
using System.Windows.Interop;
using IdolClick.Models;
using IdolClick.Services;
using IdolClick.Services.Api;
using IdolClick.Services.Backend;
using IdolClick.Services.Mcp;
using IdolClick.Services.Packs;
using IdolClick.Services.Templates;
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
    public static PackOrchestrator PackOrchestrator { get; private set; } = null!;
    public static TemplateRegistry Templates { get; private set; } = null!;
    public static IntentSplitterService IntentSplitter { get; private set; } = null!;
    public static ApiHostService ApiHost { get; private set; } = null!;
    public static VoiceInputService? Voice { get; private set; }

    /// <summary>
    /// The port Kestrel is listening on. 0 until <see cref="IsApiReady"/> is true.
    /// WebView2 connects to http://localhost:{ApiPort}/.
    /// </summary>
    public static int ApiPort => ApiHost?.Port ?? 0;

    /// <summary>
    /// True once the Kestrel API host is running and ready for connections.
    /// UI can show "Starting engine…" state until this is true.
    /// </summary>
    public static bool IsApiReady => ApiHost?.Port > 0;

    /// <summary>
    /// When true, the kill switch has been activated and the engine cannot be
    /// re-enabled until the user manually resets it from the UI.
    /// Prevents restart loops after emergency stop.
    /// Volatile: read from engine thread, written from hotkey/UI thread.
    /// </summary>
    private static volatile bool _killSwitchActive;
    public static bool KillSwitchActive
    {
        get => _killSwitchActive;
        private set => _killSwitchActive = value;
    }

    /// <summary>
    /// Cancellation source for the currently running agent flow, if any.
    /// Set by the agent chat panel when a flow starts; cancelled by kill switch.
    /// </summary>
    public static CancellationTokenSource? ActiveFlowCts { get; set; }

    private SplashWindow? _splash;
    private MainWindow? _mainWindow;
    private HomeWindow? _homeWindow;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── CLI arg handling ─────────────────────────────────────────────────
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
                case "--smoke":
                    RunHeadlessSmoke(args.Skip(1).ToArray());
                    return;
                case "--mcp":
                    RunMcpServer(args.Skip(1).ToArray());
                    return;
                case "--test-classic":
                    RunClassicTests();
                    return;
                case "--demo":
                    RunDemo();
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

        // ── Phase 1: Core services (instant — Config + Log only) ─────────
        InitCoreServices();

        // ── Phase 2: Route to Home screen or direct launch ───────────────
        var settings = Config.GetConfig().Settings;
        if (settings.SkipHomeScreen)
        {
            LaunchMode(settings.Mode);
        }
        else
        {
            _homeWindow = new HomeWindow();
            _homeWindow.Show();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // LAZY INITIALIZATION (Home → Splash → Mode-specific services → MainWindow)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Phase 1: Config + Log only. Runs synchronously, completes in milliseconds.
    /// Called before deciding whether to show <see cref="HomeWindow"/> or launch directly.
    /// </summary>
    private void InitCoreServices()
    {
        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(appDir, "config.json");
        Config = new ConfigService(configPath);
        Log = new LogService();
        Log.SetLevel(Config.GetConfig().Settings.LogLevel);
    }

    /// <summary>
    /// Called by <see cref="HomeWindow"/> after the user picks a mode, or directly
    /// from <see cref="OnStartup"/> when <see cref="GlobalSettings.SkipHomeScreen"/> is true.
    /// Shows the splash, initializes services for the chosen mode on a background thread,
    /// then opens <see cref="MainWindow"/>.
    /// </summary>
    public void LaunchMode(AppMode mode)
    {
        _homeWindow?.Close();
        _homeWindow = null;

        _splash = new SplashWindow();
        _splash.Show();

        Task.Run(async () =>
        {
            try
            {
                await InitServicesForModeAsync(mode);

                Dispatcher.Invoke(() =>
                {
                    Log.Info("App", $"IdolClick started in {mode} mode");

                    Engine.Start();
                    if (Config.GetConfig().Settings.AutomationEnabled)
                        Engine.SetEnabled(true);

                    _mainWindow = new MainWindow();
                    MainWindow = _mainWindow;

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
    /// Phase 2: Initializes shared services (always needed) plus optional Agent/Teach services.
    /// Classic mode skips the heavy AI / Kestrel / Vision / Template stack entirely.
    /// </summary>
    private async Task InitServicesForModeAsync(AppMode mode)
    {
        // ── Shared services (all modes) ──────────────────────────────────
        UpdateSplash("Loading profiles...", 0.10);
        Profiles = new ProfileService(Config, Log);

        UpdateSplash("Setting up notifications...", 0.20);
        Notifications = new NotificationService(Log, Config);

        UpdateSplash("Loading script engine...", 0.30);
        Scripts = new ScriptExecutionService(Log, Config);

        UpdateSplash("Discovering plugins...", 0.40);
        Plugins = new PluginService(Log);
        Plugins.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "Plugins"));

        UpdateSplash("Starting event timeline...", 0.50);
        Timeline = new EventTimelineService(Log);

        // ── Agent / Teach: heavy services ────────────────────────────────
        if (mode is AppMode.Agent or AppMode.Teach)
        {
            await InitAgentServicesAsync();
        }

        // ── Engine + Hotkey (all modes, must be UI thread) ───────────────
        UpdateSplash("Initializing automation engine...", 0.95);
        Dispatcher.Invoke(() =>
        {
            Engine = new AutomationEngine(Config, Log);
            Hotkey = new HotkeyService();

            Hotkey.OnKillSwitchActivated += () =>
            {
                KillSwitchActive = true;
                Engine.SetEnabled(false);
                ActiveFlowCts?.Cancel();
                Log.Audit("KillSwitch", "EMERGENCY STOP activated — all automation disabled until manual reset");
            };
        });

        UpdateSplash("Starting up...", 1.0);
        await Task.Delay(100);
    }

    /// <summary>
    /// Initializes Agent/Teach-only services: RegionCapture, Agent, FlowExecutor chain,
    /// Vision, DesktopBackend, PackOrchestrator, Templates, Voice, and API Host (Kestrel).
    /// </summary>
    private async Task InitAgentServicesAsync()
    {
        UpdateSplash("Initializing region capture...", 0.55);
        RegionCapture = new RegionCaptureService(Log);

        UpdateSplash("Initializing agent service...", 0.60);
        Agent = new AgentService(Config, Log);

        UpdateSplash("Initializing flow executor...", 0.65);
        var flowValidator = new FlowValidatorService(Log);
        var timing = Config.GetConfig().Timing;
        var ruleExecutor = new ActionExecutor(Log);
        ruleExecutor.SetTiming(timing);
        var flowActionExecutor = new FlowActionExecutor(Log, ruleExecutor);
        flowActionExecutor.SetTiming(timing);
        var assertionEvaluator = new AssertionEvaluator(Log);
        var selectorParser = new SelectorParser(Log);
        selectorParser.SetCacheTtl(timing.SelectorCacheTtlMs);
        selectorParser.SetTiming(timing);

        Vision = new VisionService(Config, Log);

        var desktopBackend = new Services.Backend.DesktopBackend(Log, flowActionExecutor, assertionEvaluator, selectorParser, Vision);
        desktopBackend.SetTiming(timing);
        DesktopBackend = desktopBackend;
        FlowExecutor = new StepExecutor(Log, flowValidator, DesktopBackend);

        Reports = new ReportService(Log);

        // ── Pack Orchestrator (Hand-Eye-Brain pipeline) ──────────────
        UpdateSplash("Initializing Pack orchestrator...", 0.75);
        var packRunner = new PackRunnerService(Log, flowValidator, backendName =>
            backendName switch
            {
                "desktop-uia" => DesktopBackend,
                _ => null
            });
        PackOrchestrator = new PackOrchestrator(Config, Log, flowValidator, packRunner);

        // Connect execution services to agent for closed-loop
        if (Agent is AgentService agentSvc)
        {
            agentSvc.SetExecutionServices(FlowExecutor, Reports, Vision);
            if (!agentSvc.IsConfigured)
                agentSvc.Reconfigure();
        }

        // ── Template Registry + Intent Splitter (compiler layer) ─────
        UpdateSplash("Loading template registry...", 0.80);
        Templates = TemplateRegistry.CreateDefault();
        IntentSplitter = new IntentSplitterService(Templates, Log);
        Log.Info("App", $"Template registry loaded: {Templates.Count} templates (" +
            $"{Templates.GetByMaturity(TemplateMaturity.Core).Count} core, " +
            $"{Templates.GetByMaturity(TemplateMaturity.Experimental).Count} experimental)");

        // ── Voice Input (Azure Whisper push-to-talk) ─────────────────
        UpdateSplash("Initializing voice input...", 0.85);
        Voice = new VoiceInputService(Config, Log);
        if (Voice.IsConfigured)
            Log.Info("App", "Voice input enabled (Azure Whisper)");
        else
            Log.Debug("App", "Voice input not configured — mic button hidden");

        // ── API Host (Kestrel + SignalR) ─────────────────────────────
        UpdateSplash("Starting API host...", 0.90);
        ApiHost = new ApiHostService(Log);
        await ApiHost.StartAsync();
        Log.Info("App", $"API host ready on port {ApiPort}");
    }

    /// <summary>
    /// Lazy-initializes Agent-tier services if not already loaded.
    /// Called when switching from Classic → Agent/Teach mode in MainWindow.
    /// </summary>
    public async Task EnsureAgentServicesAsync()
    {
        if (Agent != null) return;
        await Task.Run(async () => await InitAgentServicesAsync());
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DEMO MODE (--demo CLI)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs Demo Mode — launches the DemoWindow which showcases IdolClick capabilities
    /// using real Windows apps. No LLM or API key required.
    /// </summary>
    private void RunDemo()
    {
        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(appDir, "config.json");

        Config = new ConfigService(configPath);
        Log = new LogService();
        Log.SetLevel("Debug");
        Timeline = new EventTimelineService(Log);
        Notifications = new NotificationService(Log, Config);
        Scripts = new ScriptExecutionService(Log, Config);
        Plugins = new PluginService(Log);

        Console.WriteLine($"IdolClick Demo Mode v{ServiceHost.Version}");
        Console.WriteLine();

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var demoWindow = new UI.DemoWindow();
                demoWindow.Closed += (_, _) => Shutdown(0);
                demoWindow.Show();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: {ex.Message}");
                Shutdown(1);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // CLASSIC ENGINE INTEGRATION TESTS (--test-classic CLI)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs classic rule engine integration tests with a real WPF test window.
    /// Requires a UI thread (not headless). Exits with code 0 (all passed) or 1.
    ///
    /// Usage:
    ///   --test-classic    Run all classic engine integration tests
    /// </summary>
    private void RunClassicTests()
    {
        // Initialize minimal services needed for the classic engine
        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(appDir, "config.json");

        Config = new ConfigService(configPath);
        Log = new LogService();
        Log.SetLevel("Debug");
        Timeline = new EventTimelineService(Log);
        Notifications = new NotificationService(Log, Config);
        Scripts = new ScriptExecutionService(Log, Config);
        Plugins = new PluginService(Log);

        Console.WriteLine($"IdolClick Classic Engine Tests v{ServiceHost.Version}");
        Console.WriteLine();

        var runner = new ClassicTestRunner(Log, msg =>
        {
            Console.WriteLine(msg);
            Log.Info("ClassicTest", msg);
        });

        // Run tests on the UI thread (WPF windows need STA)
        var allPassed = false;
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                allPassed = await runner.RunAllAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
            finally
            {
                Shutdown(allPassed ? 0 : 1);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HEADLESS SMOKE TEST RUNNER (--smoke CLI)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs smoke tests headlessly (no GUI), writes incremental log to a file,
    /// and exits with code 0 (all passed) or 1 (failures/errors).
    /// 
    /// Usage:
    ///   --smoke                              Run all built-in tests
    ///   --smoke ST-01,ST-15                  Run specific test IDs
    /// <summary>
    /// Starts IdolClick as an MCP server on stdio transport (no WPF, no UI).
    /// Used by IDEs and coding agents to interact with IdolClick via the Model Context Protocol.
    ///
    /// Usage:
    ///   --mcp                     Start with default config
    ///   --mcp --config path.json  Start with custom config path
    /// </summary>
    private void RunMcpServer(string[] mcpArgs)
    {
        // Parse optional --config arg
        string? configPath = null;
        for (int i = 0; i < mcpArgs.Length; i++)
        {
            if (mcpArgs[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < mcpArgs.Length)
            {
                configPath = mcpArgs[++i];
            }
        }

        // Default config path
        if (string.IsNullOrEmpty(configPath))
        {
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            configPath = Path.Combine(appDir, "config.json");
        }

        // Run the MCP server (blocks until client disconnects)
        Task.Run(async () =>
        {
            try
            {
                await McpServerService.RunAsync(configPath, mcpArgs);
                Dispatcher.Invoke(() => Shutdown(0));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MCP server error: {ex.Message}");
                Dispatcher.Invoke(() => Shutdown(1));
            }
        });
    }

    /// <summary>
    /// Runs smoke tests in headless mode (no WPF).
    ///
    /// Usage:
    ///   --smoke --file C:\tests\my-suite.json  Run tests from external JSON file
    ///   --smoke --log C:\out\result.txt      Custom log path
    ///   --smoke --file suite.json --log C:\out\result.txt
    /// </summary>
    private void RunHeadlessSmoke(string[] smokeArgs)
    {
        // Parse arguments
        string? testFilter = null;
        string? logPath = null;
        string? testFilePath = null;

        for (int i = 0; i < smokeArgs.Length; i++)
        {
            if (smokeArgs[i].Equals("--log", StringComparison.OrdinalIgnoreCase) && i + 1 < smokeArgs.Length)
            {
                logPath = smokeArgs[++i];
            }
            else if (smokeArgs[i].Equals("--file", StringComparison.OrdinalIgnoreCase) && i + 1 < smokeArgs.Length)
            {
                testFilePath = smokeArgs[++i];
            }
            else if (!smokeArgs[i].StartsWith("--"))
            {
                testFilter = smokeArgs[i];
            }
        }

        // Default log path
        logPath ??= Path.Combine(AppContext.BaseDirectory, "logs",
            $"smoke_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        Console.WriteLine($"IdolClick Smoke Test Runner v{ServiceHost.Version}");
        Console.WriteLine($"Log file: {logPath}");

        // Boot services headlessly via ServiceHost (no WPF/UI thread needed)
        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(appDir, "config.json");

        ServiceHost? host = null;
        int exitCode = 1;

        try
        {
            host = ServiceHost.Create(configPath);

            if (!host.Agent.IsConfigured)
            {
                Console.Error.WriteLine("ERROR: Agent is not configured. Set LLM endpoint/key in config.json first.");
                Shutdown(2);
                return;
            }

            // Select tests: from file or built-in
            List<SmokeTest> tests;

            if (!string.IsNullOrEmpty(testFilePath))
            {
                // Load from external JSON file
                try
                {
                    var resolvedPath = Path.GetFullPath(testFilePath);
                    Console.WriteLine($"Loading tests from: {resolvedPath}");
                    tests = SmokeTestFile.LoadFromFile(resolvedPath);
                    Console.WriteLine($"Loaded {tests.Count} test(s) from file");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR: Failed to load test file: {ex.Message}");
                    Shutdown(3);
                    return;
                }

                // Apply optional ID filter on file-based tests too
                if (!string.IsNullOrEmpty(testFilter))
                {
                    var ids = testFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => id.ToUpperInvariant())
                        .ToHashSet();
                    tests = tests.Where(t => ids.Contains(t.Id.ToUpperInvariant())).ToList();
                    if (tests.Count == 0)
                    {
                        Console.Error.WriteLine($"ERROR: No tests in file matched filter '{testFilter}'.");
                        Shutdown(3);
                        return;
                    }
                }
            }
            else
            {
                // Use built-in tests
                var allTests = SmokeTestService.GetAllTests();

                if (!string.IsNullOrEmpty(testFilter))
                {
                    var ids = testFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => id.ToUpperInvariant())
                        .ToHashSet();

                    tests = allTests.Where(t => ids.Contains(t.Id.ToUpperInvariant())).ToList();

                    if (tests.Count == 0)
                    {
                        Console.Error.WriteLine($"ERROR: No tests matched filter '{testFilter}'.");
                        Console.Error.WriteLine($"Available IDs: {string.Join(", ", allTests.Select(t => t.Id))}");
                        Shutdown(3);
                        return;
                    }

                    Console.WriteLine($"Running {tests.Count} test(s): {string.Join(", ", tests.Select(t => t.Id))}");
                }
                else
                {
                    tests = allTests;
                    Console.WriteLine($"Running all {tests.Count} test(s)");
                }
            }

            Console.WriteLine();

            // Create the service and wire incremental log + screenshot/vision services
            var svc = new SmokeTestService(host.Agent, host.Log, host.Reports, host.Vision);
            svc.SetLogFile(logPath);

            // Also echo to console
            svc.OnLogMessage += msg => Console.WriteLine(msg);
            svc.OnTestStatusChanged += (id, status, result) =>
            {
                if (result != null)
                {
                    var icon = status switch
                    {
                        SmokeTestStatus.Passed => "PASS",
                        SmokeTestStatus.Failed => "FAIL",
                        SmokeTestStatus.Error => "ERR ",
                        _ => status.ToString()
                    };
                    Console.WriteLine($"  [{icon}] {id} ({result.ElapsedMs / 1000.0:F1}s)");
                }
            };

            // Run synchronously (we're not on a UI thread)
            var suite = svc.RunAllAsync(tests, CancellationToken.None).GetAwaiter().GetResult();
            svc.CloseLogFile();

            Console.WriteLine();
            Console.WriteLine($"Result: {suite.PassedCount}/{suite.TotalCount} passed ({suite.TotalElapsedMs / 1000.0:F1}s)");
            Console.WriteLine($"Log: {logPath}");

            exitCode = suite.AllPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
        }
        finally
        {
            host?.Dispose();
            Shutdown(exitCode);
        }
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
    /// Show and activate the main window using native Win32 APIs
    /// for reliable foreground activation across Windows focus restrictions.
    /// </summary>
    public void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            // Still in splash or home phase — bring the visible window to front
            if (_homeWindow != null)
            {
                _homeWindow.Activate();
                _homeWindow.Topmost = true;
                _homeWindow.Topmost = false;
            }
            else if (_splash != null)
            {
                _splash.Activate();
                _splash.Topmost = true;
                _splash.Topmost = false;
            }
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;

        // Use Win32 ForceActivateWindow for reliable bring-to-front
        var hwnd = new WindowInteropHelper(_mainWindow).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32.ForceActivateWindow(hwnd);
        }
        else
        {
            // Fallback: WPF-only activation (window not yet rendered)
            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        }
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
        try { _showWindowEvent?.Set(); } catch { /* best-effort */ }
        _signalListenerThread?.Join(1000);
        try { _showWindowEvent?.Dispose(); } catch { /* best-effort */ }
        
        // Dispose all services — each wrapped individually so one failure
        // cannot prevent the others (especially mutex release) from running.
        try { ApiHost?.Dispose(); }             catch { /* logged internally */ }
        try { Voice?.Dispose(); }               catch { /* best-effort */ }
        try { (Agent as IDisposable)?.Dispose(); } catch { /* best-effort */ }
        try { Timeline?.Dispose(); }            catch { /* best-effort */ }
        try { Hotkey?.Dispose(); }              catch { /* best-effort */ }
        try { Engine?.Dispose(); }              catch { /* best-effort */ }
        try { (Log as IDisposable)?.Dispose(); } catch { /* flush log */ }
        
        // ALWAYS release the single-instance mutex so the next launch succeeds.
        try { _mutex?.ReleaseMutex(); } catch { /* non-owner is OK */ }
        try { _mutex?.Dispose(); }      catch { /* best-effort */ }
        
        base.OnExit(e);
        
        // Failsafe: if a background thread (Kestrel, SignalR listener, etc.)
        // is keeping the process alive after WPF shutdown, force-terminate.
        Environment.Exit(e.ApplicationExitCode);
    }
}

using System.IO;
using IdolClick.Models;
using IdolClick.Services.Infrastructure;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// SERVICE HOST — WPF-free core service container.
//
// Contains all services that have NO dependency on WPF, UI thread, or System.Windows.
// This is the extraction point for future CLI runner (v1.1).
//
// Services owned here:
//   Config, Log, Profiles, FlowValidator, SelectorParser, ActionExecutor,
//   FlowActionExecutor, AssertionEvaluator, DesktopBackend, StepExecutor,
//   Agent, Reports, Vision, Scripts, Plugins, Timeline
//
// Services NOT owned (WPF-dependent):
//   HotkeyService (Win32 window message pump)
//   AutomationEngine (creates ActionExecutor which uses Win32)
//   RegionCaptureService (WPF overlay window)
//   NotificationService (WPF toast)
//
// Usage (future CLI):
//   var host = ServiceHost.Create(configPath);
//   var report = await host.FlowExecutor.ExecuteFlowAsync(flow, ct: cts.Token);
//   Environment.ExitCode = report.Result == "passed" ? 0 : 1;
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// WPF-free core service container. Provides all services needed to
/// validate and execute test flows without any UI dependencies.
/// Designed for extraction into a CLI runner in v1.1.
/// </summary>
public class ServiceHost : IDisposable
{
    public ConfigService Config { get; }
    public LogService Log { get; }
    public ProfileService Profiles { get; }
    public FlowValidatorService FlowValidator { get; }
    public StepExecutor FlowExecutor { get; }
    public IAutomationBackend Backend { get; }
    public IAgentService Agent { get; }
    public ReportService Reports { get; }
    public VisionService Vision { get; }
    public PluginService Plugins { get; }
    public EventTimelineService Timeline { get; }
    public IScriptExecutionService Scripts { get; }

    private ServiceHost(
        ConfigService config,
        LogService log,
        ProfileService profiles,
        FlowValidatorService flowValidator,
        StepExecutor flowExecutor,
        IAutomationBackend backend,
        IAgentService agent,
        ReportService reports,
        VisionService vision,
        PluginService plugins,
        EventTimelineService timeline,
        IScriptExecutionService scripts)
    {
        Config = config;
        Log = log;
        Profiles = profiles;
        FlowValidator = flowValidator;
        FlowExecutor = flowExecutor;
        Backend = backend;
        Agent = agent;
        Reports = reports;
        Vision = vision;
        Plugins = plugins;
        Timeline = timeline;
        Scripts = scripts;
    }

    /// <summary>
    /// Creates a fully initialized ServiceHost from a config file path.
    /// No WPF or UI thread required.
    /// </summary>
    /// <param name="configPath">Absolute path to config.json.</param>
    /// <returns>Initialized service host ready for flow execution.</returns>
    public static ServiceHost Create(string configPath)
    {
        var config = new ConfigService(configPath);
        var log = new LogService();
        log.SetLevel(config.GetConfig().Settings.LogLevel);

        var profiles = new ProfileService(config, log);
        var scripts = new ScriptExecutionService(log, config);

        var plugins = new PluginService(log);
        var pluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        if (Directory.Exists(pluginsPath))
            plugins.LoadPlugins(pluginsPath);

        var timeline = new EventTimelineService(log);
        var vision = new VisionService(config, log);

        var flowValidator = new FlowValidatorService(log);
        var ruleExecutor = new ActionExecutor(log);
        var flowActionExecutor = new FlowActionExecutor(log, ruleExecutor);
        var assertionEvaluator = new AssertionEvaluator(log);
        var selectorParser = new SelectorParser(log);

        var backend = new DesktopBackend(log, flowActionExecutor, assertionEvaluator, selectorParser, vision);
        var flowExecutor = new StepExecutor(log, flowValidator, backend);

        var reports = new ReportService(log);
        var agent = new AgentService(config, log);

        if (agent is AgentService agentSvc)
            agentSvc.SetExecutionServices(flowExecutor, reports, vision);

        log.Info("ServiceHost", "Core services initialized (WPF-free)");

        return new ServiceHost(
            config, log, profiles, flowValidator, flowExecutor,
            backend, agent, reports, vision, plugins, timeline, scripts);
    }

    /// <summary>
    /// Application version string.
    /// </summary>
    public static string Version => "1.0.0";

    /// <summary>
    /// Prints version info to stdout. For CLI --version support.
    /// </summary>
    public static void PrintVersion()
    {
        Console.WriteLine($"IdolClick v{Version}");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {Environment.OSVersion}");
    }

    /// <summary>
    /// Prints help text to stdout. For CLI --help support.
    /// </summary>
    public static void PrintHelp()
    {
        Console.WriteLine($"IdolClick v{Version} — AI-Compatible Deterministic UI Execution Runtime");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  IdolClick.exe                     Launch GUI (default)");
        Console.WriteLine("  IdolClick.exe --version           Print version info");
        Console.WriteLine("  IdolClick.exe --help              Print this help message");
        Console.WriteLine();
        Console.WriteLine("Future (v1.1):");
        Console.WriteLine("  IdolClick.exe run <flow.json>     Execute a flow and exit");
        Console.WriteLine("  IdolClick.exe validate <flow.json> Validate flow without executing");
        Console.WriteLine();
        Console.WriteLine("Documentation: See PRODUCT.md for full specification.");
    }

    public void Dispose()
    {
        (Agent as IDisposable)?.Dispose();
        Timeline?.Dispose();
        Log?.Info("ServiceHost", "Disposed");
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// AGENT TOOLS — Functions exposed to the LLM via function calling.
//
// Each public method becomes a callable tool that the LLM can invoke to:
//   • Explore the desktop environment (windows, elements, processes)
//   • Validate test flows before presenting them to the user
//   • Understand IdolClick's capabilities
//
// The LLM decides when to call these tools based on the user's request.
// Function calling is handled by FunctionInvokingChatClient (Microsoft.Extensions.AI).
//
// Thread safety: all methods are safe to call from thread pool threads.
// UI Automation COM objects are queried fresh per call — no stale references.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Tools exposed to the LLM agent via Microsoft.Extensions.AI function calling.
/// </summary>
public class AgentTools
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly FlowValidatorService _validator;
    private readonly StepExecutor? _executor;
    private readonly ReportService? _reportService;
    private readonly VisionService? _visionService;
    private string? _cachedCapabilities; // R3.4: Cached serialized capabilities

    public AgentTools(ConfigService config, LogService log, FlowValidatorService validator,
        StepExecutor? executor = null, ReportService? reportService = null,
        VisionService? visionService = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _executor = executor;
        _reportService = reportService;
        _visionService = visionService;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WINDOW DISCOVERY
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Lists all visible windows on the desktop with process name, window title, and native handle. Use this to discover what applications are running and find targets for automation.")]
    public string ListWindows()
    {
        try
        {
            var windows = new List<object>();
            var root = AutomationElement.RootElement;
            var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);

            for (int i = 0; i < children.Count; i++)
            {
                try
                {
                    var w = children[i];
                    var name = w.Current.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var handle = w.Current.NativeWindowHandle;
                    var processId = w.Current.ProcessId;
                    string processName = "";
                    try
                    {
                        using var proc = Process.GetProcessById(processId);
                        processName = proc.ProcessName;
                    }
                    catch { }

                    // Filter out invisible/zero-area windows to reduce noise
                    try
                    {
                        var rect = w.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                            continue;
                    }
                    catch { }

                    windows.Add(new
                    {
                        processName,
                        windowTitle = name,
                        handle,
                        processId
                    });
                }
                catch (ElementNotAvailableException) { }
            }

            _log.Debug("Tools", $"ListWindows: found {windows.Count} windows");
            return JsonSerializer.Serialize(windows, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("Tools", $"ListWindows failed: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ELEMENT INSPECTION
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Inspects a window's UI automation tree and returns elements with their type, name, automationId, suggested selector, and bounding rectangle. Use this to find exact selectors for test flow steps. The processName can also be a partial window title for matching.")]
    public string InspectWindow(
        [Description("Process name or partial window title to inspect (e.g., 'notepad', 'chrome', 'Settings')")] string processName,
        [Description("Maximum depth to traverse the element tree. Default: 3")] int maxDepth = 3,
        [Description("Maximum number of elements to return. Default: 30")] int maxElements = 30)
    {
        try
        {
            // Clamp parameters — keep element count bounded to limit token usage
            maxDepth = Math.Clamp(maxDepth, 1, 5);
            maxElements = Math.Clamp(maxElements, 5, 60);

            // Find the window — try by process name first
            AutomationElement? window = null;

            var procs = Array.Empty<Process>();
            try { procs = Process.GetProcessesByName(processName); } catch { }

            foreach (var proc in procs)
            {
                using (proc)
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    try
                    {
                        window = AutomationElement.FromHandle(proc.MainWindowHandle);
                        break;
                    }
                    catch { }
                }
            }

            // Fallback: try by window title
            if (window == null)
            {
                var root = AutomationElement.RootElement;
                var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < children.Count; i++)
                {
                    try
                    {
                        var w = children[i];
                        if ((w.Current.Name ?? "").Contains(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            window = w;
                            break;
                        }
                    }
                    catch (ElementNotAvailableException) { }
                }
            }

            if (window == null)
                return JsonSerializer.Serialize(new { error = $"No window found for '{processName}'. Use ListWindows to see available windows." });

            var elements = new List<object>();
            CollectElements(window, elements, 0, maxDepth, maxElements);

            _log.Debug("Tools", $"InspectWindow({processName}): found {elements.Count} elements");

            return JsonSerializer.Serialize(new
            {
                windowTitle = window.Current.Name,
                processName = GetProcessName(window),
                elementCount = elements.Count,
                maxDepthUsed = maxDepth,
                elements
            }, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("Tools", $"InspectWindow failed: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string GetProcessName(AutomationElement window)
    {
        try
        {
            using var proc = Process.GetProcessById(window.Current.ProcessId);
            return proc.ProcessName;
        }
        catch { return ""; }
    }

    private static void CollectElements(AutomationElement parent, List<object> elements,
        int depth, int maxDepth, int maxElements)
    {
        if (depth > maxDepth || elements.Count >= maxElements) return;

        AutomationElementCollection children;
        try
        {
            children = parent.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch { return; }

        for (int i = 0; i < children.Count && elements.Count < maxElements; i++)
        {
            try
            {
                var elem = children[i];
                var current = elem.Current;
                var controlType = current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "Unknown";
                var name = current.Name ?? "";
                var automationId = current.AutomationId ?? "";
                var isEnabled = current.IsEnabled;
                var rect = current.BoundingRectangle;

                // Build selector suggestion
                var selector = "";
                if (!string.IsNullOrEmpty(automationId))
                    selector = $"{controlType}#{automationId}";
                else if (!string.IsNullOrEmpty(name))
                    selector = $"{controlType}#{name}";

                // Include elements that have a name or automationId (skip anonymous containers)
                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(automationId))
                {
                    elements.Add(new
                    {
                        type = controlType,
                        name,
                        automationId,
                        selector,
                        isEnabled,
                        bounds = rect.IsEmpty ? null : new
                        {
                            x = (int)rect.X,
                            y = (int)rect.Y,
                            width = (int)rect.Width,
                            height = (int)rect.Height
                        },
                        depth
                    });
                }

                // Recurse into children
                CollectElements(elem, elements, depth + 1, maxDepth, maxElements);
            }
            catch (ElementNotAvailableException) { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PROCESS DISCOVERY
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Lists running processes that have visible windows, with their PID, process name, and main window title. Use this to find process names for automation targets.")]
    public string ListProcesses()
    {
        try
        {
            // R4.2: Ensure ALL Process handles are disposed (not just those passing the filter)
            var allProcs = Process.GetProcesses();
            var results = new List<(int pid, string name, string windowTitle)>();
            
            foreach (var p in allProcs)
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        results.Add((p.Id, p.ProcessName, p.MainWindowTitle));
                    }
                    catch { }
                }
            }

            results.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            _log.Debug("Tools", $"ListProcesses: found {results.Count} windowed processes");
            return JsonSerializer.Serialize(
                results.Select(r => new { pid = r.pid, name = r.name, windowTitle = r.windowTitle }),
                FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("Tools", $"ListProcesses failed: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FLOW VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Validates a test flow JSON string against the IdolClick schema and returns any errors or warnings. ALWAYS validate a flow before presenting it to the user. The input should be the complete JSON string of a TestFlow object.")]
    public string ValidateFlow(
        [Description("The complete test flow JSON string to validate")] string flowJson)
    {
        try
        {
            var flow = JsonSerializer.Deserialize<TestFlow>(flowJson, FlowJson.Options);
            if (flow == null)
                return JsonSerializer.Serialize(new { valid = false, errors = new[] { "Failed to parse JSON as TestFlow" } });

            var result = _validator.Validate(flow);

            return JsonSerializer.Serialize(new
            {
                valid = result.IsValid,
                errors = result.Errors,
                warnings = result.Warnings,
                stepCount = flow.Steps.Count,
                testName = flow.TestName
            }, FlowJson.Options);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { valid = false, errors = new[] { $"JSON parse error: {ex.Message}" } });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // CAPABILITIES
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Returns IdolClick's capabilities: all supported step actions with their required fields, assertion types, selector formats, and active automation backend. Use this when you need to know what automation primitives are available.")]
    public string GetCapabilities()
    {
        // R3.4: Return cached result if available (capabilities are stable per session)
        if (_cachedCapabilities != null)
            return _cachedCapabilities;
        // Query active backend capabilities if available
        var backend = _executor?.Backend;
        var backendInfo = backend != null
            ? new
            {
                name = backend.Name,
                version = backend.Version,
                supportsTracing = backend.Capabilities.SupportsTracing,
                supportsScreenshots = backend.Capabilities.SupportsScreenshots,
                supportsActionabilityChecks = backend.Capabilities.SupportsActionabilityChecks,
                supportedSelectorKinds = backend.Capabilities.SupportedSelectorKinds.Select(k => k.ToString()).ToArray()
            }
            : null;

        var capabilities = new
        {
            schemaVersion = 2,
            activeBackend = backendInfo,
            visionFallback = _visionService != null ? new
            {
                enabled = _visionService.IsEnabled,
                confidenceThreshold = _visionService.ConfidenceThreshold,
                note = "Vision is STRICTLY a fallback — always use UIA selectors first"
            } : null,
            backends = new object[]
            {
                new { name = "desktop-uia", description = "Windows UI Automation + Win32", selectorKinds = new[] { "DesktopUia" } }
            },
            actions = new object[]
            {
                new { name = "click", description = "Click a UI element by selector", required = new[] { "selector" }, actionabilityChecks = new[] { "exists", "visible", "stable", "enabled", "receives_events" } },
                new { name = "hover", description = "Move cursor over a UI element (triggers tooltips, dropdowns, hover states)", required = new[] { "selector" }, actionabilityChecks = new[] { "exists", "visible", "stable" } },
                new { name = "type", description = "Type text into an element or focused control", required = new[] { "text" }, optional = new[] { "selector" }, actionabilityChecks = new[] { "exists", "visible", "enabled", "editable" } },
                new { name = "send_keys", description = "Send keyboard shortcuts (e.g., Ctrl+S, Tab, Enter)", required = new[] { "keys" } },
                new { name = "wait", description = "Wait for an element to appear or fixed delay", required = Array.Empty<string>(), optional = new[] { "selector", "timeoutMs" } },
                new { name = "assert_exists", description = "Verify a UI element exists", required = new[] { "selector" } },
                new { name = "assert_not_exists", description = "Verify a UI element does NOT exist", required = new[] { "selector" } },
                new { name = "assert_text", description = "Verify element text contains or equals a value", required = new[] { "selector", "contains" }, optional = new[] { "exact" } },
                new { name = "assert_window", description = "Verify a window title matches", required = new[] { "windowTitle or contains" } },
                new { name = "navigate", description = "Open a URL via shell execute (opens default browser)", required = new[] { "url" } },
                new { name = "screenshot", description = "Capture a screenshot for the report", required = Array.Empty<string>() },
                new { name = "scroll", description = "Scroll within an element or window", required = new[] { "direction" }, optional = new[] { "scrollAmount", "selector" } },
                new { name = "focus_window", description = "Bring a window to the foreground", required = new[] { "app or windowTitle" } },
                new { name = "launch", description = "Start a process", required = new[] { "processPath" } }
            },
            assertionTypes = new object[]
            {
                new { name = "exists", description = "Verify element exists", required = new[] { "selector" } },
                new { name = "not_exists", description = "Verify element does NOT exist", required = new[] { "selector" } },
                new { name = "text_contains", description = "Verify element text contains value", required = new[] { "selector", "expected" } },
                new { name = "text_equals", description = "Verify element text exactly equals value", required = new[] { "selector", "expected" } },
                new { name = "window_title", description = "Verify window title", required = new[] { "expected" } },
                new { name = "process_running", description = "Verify a process is running", required = new[] { "expected" } }
            },
            selectorFormat = new
            {
                desktopUia = new
                {
                    pattern = "ElementType#TextOrAutomationId",
                    examples = new[]
                    {
                        "Button#Save — click a button with text 'Save'",
                        "TextBox#SearchField — type into a text box with AutomationId 'SearchField'",
                        "#AutomationId — any element type with the given AutomationId",
                        "Window#Untitled - Notepad — target a window by title"
                    },
                    tips = new[]
                    {
                        "Prefer AutomationId over text — it's stable across languages",
                        "Use InspectWindow to discover exact selectors before generating flows",
                        "Element types map to UI Automation ControlTypes (Button, TextBox, etc.)"
                    }
                }
            }
        };

        return _cachedCapabilities = JsonSerializer.Serialize(capabilities, FlowJson.Options);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FLOW EXECUTION (CLOSED LOOP)
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Executes a validated test flow and returns the structured execution report. The flow JSON must pass validation first. This is the closed-loop execution tool — use it after ValidateFlow confirms the flow is valid. The report includes per-step pass/fail, timing, element snapshots, and error details.")]
    public async Task<string> RunFlow(
        [Description("The complete test flow JSON string to execute")] string flowJson)
    {
        if (_executor == null)
            return JsonSerializer.Serialize(new { error = "Flow executor not available" });

        try
        {
            var flow = JsonSerializer.Deserialize<TestFlow>(flowJson, FlowJson.Options);
            if (flow == null)
                return JsonSerializer.Serialize(new { error = "Failed to parse flow JSON" });

            // Validate first
            var validation = _validator.Validate(flow);
            if (!validation.IsValid)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Flow validation failed",
                    validationErrors = validation.Errors
                }, FlowJson.Options);
            }

            _log.Info("Tools", $"RunFlow: executing '{flow.TestName}' ({flow.Steps.Count} steps)");

            // Execute the flow
            var report = await _executor.ExecuteFlowAsync(flow);

            // Auto-save report to disk
            string? reportPath = null;
            if (_reportService != null)
            {
                try { reportPath = _reportService.SaveReport(report); }
                catch { /* non-fatal */ }
            }

            _log.Info("Tools", $"RunFlow: '{flow.TestName}' {report.Result} — {report.PassedCount}/{report.Steps.Count} passed");

            // Return a compact summary for AI consumption (full report saved to disk).
            // Only include failed/warning step details to reduce token usage.
            var failedSteps = report.Steps
                .Where(s => s.Status is StepStatus.Failed or StepStatus.Error or StepStatus.Warning)
                .Select(s => new
                {
                    step = s.Step,
                    action = s.Action,
                    selector = s.Selector,
                    status = s.Status,
                    error = s.Error,
                    warningCode = s.WarningCode
                }).ToList();

            var result = new
            {
                testName = report.TestName,
                result = report.Result,
                passed = report.PassedCount,
                failed = report.FailedCount,
                skipped = report.SkippedCount,
                warnings = report.WarningCount,
                totalSteps = report.Steps.Count,
                totalTimeMs = report.TotalTimeMs,
                failedStep = report.FailedStep,
                failedStepDetails = failedSteps,
                savedTo = reportPath,
                summary = report.Summary
            };
            return JsonSerializer.Serialize(result, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("Tools", $"RunFlow failed: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // REPORT HISTORY
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Lists recent execution reports saved on disk, most recent first. Each entry has folder name, result (passed/failed/error), and test name. Use this after RunFlow to check historical results.")]
    public string ListReports(
        [Description("Maximum number of reports to return. Default: 10")] int maxCount = 10)
    {
        if (_reportService == null)
            return JsonSerializer.Serialize(new { error = "Report service not available" });

        var reports = _reportService.ListReports(Math.Clamp(maxCount, 1, 50));
        return JsonSerializer.Serialize(reports.Select(r => new
        {
            folder = r.Folder,
            testName = r.TestName,
            result = r.Result,
            path = r.Path
        }), FlowJson.Options);
    }

    [Description("Captures a screenshot of the current desktop and returns the file path. Essential for web data extraction: capture the screen state after navigation, then pass the path to LocateByVision to read visible content. Also useful for visual debugging.")]
    public string CaptureScreenshot()
    {
        if (_reportService == null)
            return JsonSerializer.Serialize(new { error = "Report service not available" });

        var path = _reportService.CaptureScreenshot();
        if (path == null)
            return JsonSerializer.Serialize(new { error = "Screenshot capture failed" });

        return JsonSerializer.Serialize(new { path, message = "Screenshot saved" }, FlowJson.Options);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // VISION FALLBACK
    // ═══════════════════════════════════════════════════════════════════════════════

    [Description("Uses LLM vision to locate a UI element OR read text/data from the screen. Two use cases: (1) Locate an element by visual description when UIA selectors fail — returns bounding box coordinates. (2) Read visible content from a screenshot — describe what data you need (e.g., 'list all product names and prices visible on this page'). Captures a screenshot, sends it to a vision-capable LLM, and returns the result. For web pages, this is the PRIMARY way to read page content since UIA cannot see inside browser-rendered content. Requires vision fallback to be enabled in settings.")]
    public async Task<string> LocateByVision(
        [Description("Natural language description of what to find or read (e.g., 'the blue Save button in the toolbar', 'list all product names and prices on this page', 'read the table of search results')")] string description,
        [Description("Optional: process name or window title to scope the search to a specific window. If empty, captures full screen.")] string windowHint = "")
    {
        if (_visionService == null || !_visionService.IsEnabled)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Vision fallback is not enabled. Enable it in Settings → Agent → Vision Fallback Enabled.",
                hint = "Use InspectWindow with UIA selectors instead — they are faster and more reliable."
            }, FlowJson.Options);
        }

        try
        {
            // Optionally scope to a specific window
            Models.ElementBounds? windowBounds = null;
            if (!string.IsNullOrWhiteSpace(windowHint))
            {
                var root = AutomationElement.RootElement;
                var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < children.Count; i++)
                {
                    try
                    {
                        var w = children[i];
                        var name = w.Current.Name ?? "";
                        string processName = "";
                        try
                        {
                            using var proc = Process.GetProcessById(w.Current.ProcessId);
                            processName = proc.ProcessName;
                        }
                        catch { }

                        if (name.Contains(windowHint, StringComparison.OrdinalIgnoreCase)
                            || processName.Equals(windowHint, StringComparison.OrdinalIgnoreCase))
                        {
                            var rect = w.Current.BoundingRectangle;
                            if (!rect.IsEmpty)
                            {
                                windowBounds = new Models.ElementBounds
                                {
                                    X = (int)rect.X,
                                    Y = (int)rect.Y,
                                    Width = (int)rect.Width,
                                    Height = (int)rect.Height
                                };
                            }
                            break;
                        }
                    }
                    catch (ElementNotAvailableException) { }
                }
            }

            var result = await _visionService.LocateElementAsync(description, windowBounds);

            return JsonSerializer.Serialize(new
            {
                found = result.Found,
                centerX = result.CenterX,
                centerY = result.CenterY,
                bounds = result.Bounds != null ? new
                {
                    x = result.Bounds.X,
                    y = result.Bounds.Y,
                    width = result.Bounds.Width,
                    height = result.Bounds.Height
                } : null,
                confidence = result.Confidence,
                description = result.Description,
                screenshotPath = result.ScreenshotPath,
                error = result.Error,
                threshold = _visionService.ConfidenceThreshold
            }, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("Tools", $"LocateByVision failed: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

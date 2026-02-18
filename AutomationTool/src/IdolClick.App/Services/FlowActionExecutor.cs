using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// FLOW ACTION EXECUTOR — Executes TestStep actions against live UI.
//
// Layer 1 of the 3-layer execution architecture:
//   FlowActionExecutor → AssertionEvaluator → StepExecutor
//
// Each action type maps to a concrete method. No free-form strings.
// Reuses ActionExecutor's click/sendkeys infrastructure via composition.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Executes a single test step action and reports the result.
/// </summary>
public interface IFlowActionExecutor
{
    /// <summary>
    /// Executes the action defined in a TestStep against a resolved UI element.
    /// </summary>
    /// <param name="step">The step to execute.</param>
    /// <param name="element">The resolved UI element (null for window-level or non-element actions).</param>
    /// <param name="window">The target window.</param>
    /// <returns>Action result with status and diagnostics.</returns>
    Task<ActionResult> ExecuteAsync(TestStep step, AutomationElement? element, AutomationElement? window);
}

/// <summary>
/// Result of executing a single step action (before assertion evaluation).
/// </summary>
public class ActionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Diagnostics { get; set; }
    public string? ScreenshotPath { get; set; }
    /// <summary>Actual value found (for assertion-type actions like assert_text).</summary>
    public string? Found { get; set; }
    /// <summary>Expected value (echoed back for reporting).</summary>
    public string? Expected { get; set; }
}

/// <summary>
/// Executes FlowDSL step actions using Windows UI Automation.
/// Delegates physical click/sendkeys to the existing ActionExecutor infrastructure.
/// </summary>
public class FlowActionExecutor : IFlowActionExecutor
{
    private readonly LogService _log;
    private readonly ActionExecutor _ruleExecutor;
    private TimingSettings _timing = new();

    public FlowActionExecutor(LogService log, ActionExecutor ruleExecutor)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ruleExecutor = ruleExecutor ?? throw new ArgumentNullException(nameof(ruleExecutor));
    }

    /// <summary>Injects configurable timing settings.</summary>
    public void SetTiming(TimingSettings timing) => _timing = timing ?? new TimingSettings();

    public async Task<ActionResult> ExecuteAsync(TestStep step, AutomationElement? element, AutomationElement? window)
    {
        try
        {
            return step.Action switch
            {
                StepAction.Click => ExecuteClick(step, element),
                StepAction.Type => await ExecuteType(step, element).ConfigureAwait(false),
                StepAction.SendKeys => await ExecuteSendKeysAsync(step).ConfigureAwait(false),
                StepAction.Wait => await ExecuteWaitAsync(step, element).ConfigureAwait(false),
                StepAction.AssertExists => ExecuteAssertExists(element, step.Selector),
                StepAction.AssertNotExists => ExecuteAssertNotExists(element, step.Selector),
                StepAction.AssertText => ExecuteAssertText(step, element),
                StepAction.AssertWindow => ExecuteAssertWindow(step, window),
                StepAction.Navigate => await ExecuteNavigateAsync(step).ConfigureAwait(false),
                StepAction.Screenshot => ExecuteScreenshot(step),
                StepAction.Scroll => ExecuteScroll(step, element),
                StepAction.FocusWindow => await ExecuteFocusWindowAsync(step, window).ConfigureAwait(false),
                StepAction.Launch => await ExecuteLaunchAsync(step).ConfigureAwait(false),
                StepAction.Hover => ExecuteHover(step, element),
                _ => new ActionResult { Success = false, Error = $"Unknown action: {step.Action}" }
            };
        }
        catch (ElementNotAvailableException ex)
        {
            return new ActionResult { Success = false, Error = $"Element became unavailable: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ACTION IMPLEMENTATIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    private ActionResult ExecuteClick(TestStep step, AutomationElement? element)
    {
        if (element == null)
            return new ActionResult { Success = false, Error = $"Cannot click: element '{step.Selector}' not found." };

        _ruleExecutor.ClickElement(element);
        _log.Debug("FlowAction", $"Click: '{step.Selector}'");
        return new ActionResult { Success = true, Diagnostics = $"Clicked '{step.Selector}'" };
    }

    private async Task<ActionResult> ExecuteType(TestStep step, AutomationElement? element)
    {
        if (string.IsNullOrEmpty(step.Text))
            return new ActionResult { Success = false, Error = "Type action requires text." };

        // Primary method: Click to focus the element, then send keystrokes.
        // This APPENDS text naturally (like a real user), unlike ValuePattern.SetValue
        // which REPLACES the entire content — breaking multi-step type sequences.
        if (element != null)
        {
            _ruleExecutor.ClickElement(element);
            await Task.Delay(_timing.PostClickFocusDelayMs).ConfigureAwait(false);
        }

        // Send text character by character via SendInput Unicode (OS-level injection)
        // Uses KEYEVENTF_UNICODE which correctly handles ALL characters
        // including '.', '/', '@', ':', '#', etc.
        foreach (var ch in step.Text)
        {
            Win32.SendChar(ch);
            await Task.Delay(_timing.TypeCharDelayMs).ConfigureAwait(false);
        }

        _log.Debug("FlowAction", $"Type: sent '{step.Text}' via keyboard");
        return new ActionResult { Success = true, Diagnostics = $"Typed '{step.Text}' via keyboard" };
    }

    private async Task<ActionResult> ExecuteSendKeysAsync(TestStep step)
    {
        if (string.IsNullOrEmpty(step.Keys))
            return new ActionResult { Success = false, Error = "SendKeys action requires keys." };

        await _ruleExecutor.SendKeysAsync(step.Keys).ConfigureAwait(false);
        _log.Debug("FlowAction", $"SendKeys: '{step.Keys}'");
        return new ActionResult { Success = true, Diagnostics = $"Sent keys: '{step.Keys}'" };
    }

    private async Task<ActionResult> ExecuteWaitAsync(TestStep step, AutomationElement? element)
    {
        if (element != null || string.IsNullOrWhiteSpace(step.Selector))
        {
            // Element already found, or fixed delay wait
            var delayMs = step.TimeoutMs > 0 ? step.TimeoutMs : 1000;
            if (string.IsNullOrWhiteSpace(step.Selector))
            {
                // Fixed delay
                await Task.Delay(delayMs).ConfigureAwait(false);
                _log.Debug("FlowAction", $"Wait: fixed delay {delayMs}ms");
                return new ActionResult { Success = true, Diagnostics = $"Waited {delayMs}ms" };
            }

            _log.Debug("FlowAction", $"Wait: element '{step.Selector}' already present");
            return new ActionResult { Success = true, Diagnostics = $"Element '{step.Selector}' already present" };
        }

        // Element not found yet — this will be handled by the retry logic in StepExecutor
        return new ActionResult { Success = false, Error = $"Element '{step.Selector}' not found within timeout." };
    }

    private static ActionResult ExecuteAssertExists(AutomationElement? element, string? selector)
    {
        return element != null
            ? new ActionResult { Success = true, Diagnostics = $"Element '{selector}' exists" }
            : new ActionResult { Success = false, Error = $"Element '{selector}' not found.", Expected = "exists", Found = "not found" };
    }

    private static ActionResult ExecuteAssertNotExists(AutomationElement? element, string? selector)
    {
        return element == null
            ? new ActionResult { Success = true, Diagnostics = $"Element '{selector}' correctly does not exist" }
            : new ActionResult { Success = false, Error = $"Element '{selector}' exists but should not.", Expected = "not exists", Found = "exists" };
    }

    private static ActionResult ExecuteAssertText(TestStep step, AutomationElement? element)
    {
        if (element == null)
            return new ActionResult { Success = false, Error = $"Element '{step.Selector}' not found for text assertion." };

        var actual = SelectorParser.GetElementText(element);
        var expected = step.Contains ?? "";

        bool match = step.Exact
            ? actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            : actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

        return new ActionResult
        {
            Success = match,
            Expected = expected,
            Found = actual,
            Error = match ? null : $"Text mismatch: expected '{expected}', found '{actual}'",
            Diagnostics = match ? $"Text matched: '{expected}'" : null
        };
    }

    private static ActionResult ExecuteAssertWindow(TestStep step, AutomationElement? window)
    {
        if (window == null)
            return new ActionResult { Success = false, Error = "No window context for AssertWindow." };

        string actual;
        try { actual = window.Current.Name ?? ""; }
        catch { actual = ""; }

        var expected = step.WindowTitle ?? step.Contains ?? "";

        bool match = actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
        return new ActionResult
        {
            Success = match,
            Expected = expected,
            Found = actual,
            Error = match ? null : $"Window title mismatch: expected '{expected}', found '{actual}'"
        };
    }

    private async Task<ActionResult> ExecuteNavigateAsync(TestStep step)
    {
        if (string.IsNullOrEmpty(step.Url))
            return new ActionResult { Success = false, Error = "Navigate action requires URL." };

        try
        {
            var targetApp = step.App;
            Process? proc = null;

            // If a specific browser is requested, launch it directly with the URL
            if (!string.IsNullOrWhiteSpace(targetApp))
            {
                var browserExe = ResolveBrowserExecutable(targetApp);
                if (browserExe != null)
                {
                    var psi = new ProcessStartInfo(browserExe, step.Url) { UseShellExecute = false };
                    proc = Process.Start(psi);
                    _log.Debug("FlowAction", $"Navigate: launched '{browserExe}' with URL '{step.Url}'");
                }
            }

            // Fallback: open with default browser via shell
            if (proc == null)
            {
                Process.Start(new ProcessStartInfo(step.Url) { UseShellExecute = true });
                _log.Debug("FlowAction", $"Navigate: opened '{step.Url}' via default browser");
            }

            // Smart wait: poll for a browser window whose title contains domain or page keywords
            var domainHint = ExtractDomainHint(step.Url);
            var sw = Stopwatch.StartNew();
            int maxWaitMs = Math.Max(step.TimeoutMs, _timing.NavigateMaxWaitMs);
            int pollIntervalMs = _timing.NavigatePollIntervalMs;
            bool windowFound = false;
            string? matchedTitle = null;

            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                await Task.Delay(pollIntervalMs).ConfigureAwait(false);

                // Check if any browser window title contains the domain hint
                try
                {
                    var children = System.Windows.Automation.AutomationElement.RootElement.FindAll(
                        System.Windows.Automation.TreeScope.Children,
                        System.Windows.Automation.Condition.TrueCondition);

                    for (int i = 0; i < children.Count; i++)
                    {
                        try
                        {
                            var title = children[i].Current.Name ?? "";
                            if (!string.IsNullOrEmpty(domainHint) &&
                                title.Contains(domainHint, StringComparison.OrdinalIgnoreCase))
                            {
                                windowFound = true;
                                matchedTitle = title;
                                _log.Debug("FlowAction", $"Navigate: window with '{domainHint}' in title found after {sw.ElapsedMilliseconds}ms");
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (windowFound) break;
            }

            if (!windowFound)
            {
                _log.Debug("FlowAction", $"Navigate: no window with '{domainHint}' found after {sw.ElapsedMilliseconds}ms, continuing anyway");
            }

            // Learn from successful navigation: persist domain→title mapping
            if (windowFound && matchedTitle != null)
            {
                try
                {
                    var uri = new Uri(step.Url);
                    DomainHintStore.Learn(uri.Host, matchedTitle);
                }
                catch { /* best-effort learning */ }
            }

            // Give the page a moment to finish initial rendering
            await Task.Delay(_timing.NavigatePostRenderDelayMs).ConfigureAwait(false);

            return new ActionResult { Success = true, Diagnostics = $"Opened URL: '{step.Url}'" + (windowFound ? $" (page loaded in {sw.ElapsedMilliseconds}ms)" : " (page load timeout, continuing)") };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Error = $"Failed to open URL: {ex.Message}" };
        }
    }

    /// <summary>
    /// Maps a process/app name to the actual browser executable path.
    /// </summary>
    private static string? ResolveBrowserExecutable(string appName)
    {
        var name = appName.Trim().ToLowerInvariant();

        // Direct executable names
        if (name.EndsWith(".exe")) return name;

        // Common browser name aliases → executable
        return name switch
        {
            "chrome" or "google chrome" or "googlechrome"
                => FindExecutable("chrome.exe",
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"),

            "msedge" or "edge" or "microsoft edge"
                => FindExecutable("msedge.exe",
                    @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                    @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),

            "firefox" or "mozilla firefox"
                => FindExecutable("firefox.exe",
                    @"C:\Program Files\Mozilla Firefox\firefox.exe",
                    @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"),

            "brave" or "brave browser"
                => FindExecutable("brave.exe",
                    @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
                    @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe"),

            _ => null // Not a known browser — caller uses shell fallback
        };
    }

    private static string? FindExecutable(string exeName, params string[] knownPaths)
    {
        foreach (var path in knownPaths)
            if (System.IO.File.Exists(path)) return path;

        // Try PATH
        try
        {
            var psi = new ProcessStartInfo("where.exe", exeName)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(output) && System.IO.File.Exists(output))
                    return output;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Extracts a domain hint from a URL for window-title matching.
    /// e.g. "https://www.youtube.com/watch?v=xxx" → "YouTube"
    /// </summary>
    private static string ExtractDomainHint(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();

            // 1. Check learned domain hints (persisted from successful navigations)
            var learned = DomainHintStore.GetHint(host);
            if (!string.IsNullOrEmpty(learned)) return learned;

            // 2. Localhost/loopback: use the first path segment as hint
            //    e.g., "http://localhost:5000/photos" → "photos"
            //    This helps match browser titles for locally-running apps like "Photos - MyApp"
            if (host is "localhost" or "127.0.0.1" || host.StartsWith("192.168."))
            {
                var pathHint = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
                if (!string.IsNullOrEmpty(pathHint) && pathHint.Length >= 2)
                    return pathHint;
                // No path hint — return port-based hint so at least we try
                return uri.Port > 0 ? $"localhost:{uri.Port}" : "localhost";
            }

            // 3. Well-known sites → friendly name for title matching
            if (host.Contains("youtube")) return "YouTube";
            if (host.Contains("google")) return "Google";
            if (host.Contains("github")) return "GitHub";
            if (host.Contains("stackoverflow")) return "Stack Overflow";
            if (host.Contains("bing")) return "Bing";
            if (host.Contains("reddit")) return "Reddit";
            if (host.Contains("twitter") || host.Contains("x.com")) return "X";
            if (host.Contains("facebook")) return "Facebook";
            if (host.Contains("linkedin")) return "LinkedIn";
            if (host.Contains("wikipedia")) return "Wikipedia";

            // 4. Generic: use the domain name (e.g. "example.com" → "example")
            var parts = host.Replace("www.", "").Split('.');
            return parts.Length > 0 ? parts[0] : host;
        }
        catch
        {
            return "";
        }
    }

    private ActionResult ExecuteScreenshot(TestStep step)
    {
        try
        {
            var path = App.Reports?.CaptureScreenshot();
            if (path != null)
            {
                _log.Debug("FlowAction", $"Screenshot captured: {path}");
                return new ActionResult { Success = true, ScreenshotPath = path, Diagnostics = $"Screenshot saved: {path}" };
            }
            return new ActionResult { Success = true, Diagnostics = "Screenshot capture returned null" };
        }
        catch (Exception ex)
        {
            _log.Warn("FlowAction", $"Screenshot failed: {ex.Message}");
            return new ActionResult { Success = true, Diagnostics = $"Screenshot failed: {ex.Message}" };
        }
    }

    private ActionResult ExecuteHover(TestStep step, AutomationElement? element)
    {
        if (element == null)
            return new ActionResult { Success = false, Error = $"Cannot hover: element '{step.Selector}' not found." };

        try
        {
            // Get element bounding rectangle and compute center point
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty)
                return new ActionResult { Success = false, Error = $"Cannot hover: element '{step.Selector}' has no bounding rectangle." };

            int centerX = (int)(rect.Left + rect.Width / 2);
            int centerY = (int)(rect.Top + rect.Height / 2);

            Win32.MoveCursor(centerX, centerY);
            _log.Debug("FlowAction", $"Hover: moved cursor to ({centerX},{centerY}) over '{step.Selector}'");
            return new ActionResult { Success = true, Diagnostics = $"Hovered over '{step.Selector}' at ({centerX},{centerY})" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Error = $"Hover failed: {ex.Message}" };
        }
    }

    private ActionResult ExecuteScroll(TestStep step, AutomationElement? element)
    {
        if (string.IsNullOrEmpty(step.Direction))
            return new ActionResult { Success = false, Error = "Scroll action requires direction." };

        // Try ScrollPattern on the element
        if (element != null)
        {
            try
            {
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp) && sp is ScrollPattern scroll)
                {
                    var amount = step.Direction.ToLowerInvariant() switch
                    {
                        "up" => ScrollAmount.SmallDecrement,
                        "down" => ScrollAmount.SmallIncrement,
                        _ => ScrollAmount.SmallIncrement
                    };

                    for (int i = 0; i < step.ScrollAmount; i++)
                    {
                        if (step.Direction is "up" or "down")
                            scroll.ScrollVertical(amount);
                        else
                            scroll.ScrollHorizontal(amount);
                    }

                    _log.Debug("FlowAction", $"Scroll: {step.Direction} x{step.ScrollAmount} via ScrollPattern");
                    return new ActionResult { Success = true, Diagnostics = $"Scrolled {step.Direction} x{step.ScrollAmount}" };
                }
            }
            catch (Exception ex)
            {
                _log.Debug("FlowAction", $"ScrollPattern failed, falling back to mouse wheel: {ex.Message}");
            }
        }

        // Fallback: mouse wheel
        var clicks = step.ScrollAmount;
        var delta = step.Direction.ToLowerInvariant() switch
        {
            "up" => 120 * clicks,
            "down" => -120 * clicks,
            _ => 0
        };

        if (delta != 0)
        {
            Win32.MouseWheel(delta);
            _log.Debug("FlowAction", $"Scroll: {step.Direction} x{clicks} via mouse wheel");
            return new ActionResult { Success = true, Diagnostics = $"Scrolled {step.Direction} x{clicks} via mouse wheel" };
        }

        return new ActionResult { Success = true, Diagnostics = $"Scroll {step.Direction} (no-op for horizontal without ScrollPattern)" };
    }

    private async Task<ActionResult> ExecuteFocusWindowAsync(TestStep step, AutomationElement? window)
    {
        if (window == null)
            return new ActionResult { Success = false, Error = "Window not found for FocusWindow action." };

        await _ruleExecutor.ActivateWindowAsync(window).ConfigureAwait(false);
        _log.Debug("FlowAction", $"FocusWindow: activated");
        return new ActionResult { Success = true, Diagnostics = "Window activated" };
    }

    private async Task<ActionResult> ExecuteLaunchAsync(TestStep step)
    {
        if (string.IsNullOrEmpty(step.ProcessPath))
            return new ActionResult { Success = false, Error = "Launch action requires processPath." };

        try
        {
            var psi = new ProcessStartInfo(step.ProcessPath) { UseShellExecute = true };
            var proc = Process.Start(psi);

            if (proc != null && !proc.HasExited)
            {
                // Layer 1: Wait for process message loop to be idle (OS-level readiness signal)
                try
                {
                    proc.WaitForInputIdle(5000);
                    _log.Debug("FlowAction", $"Launch: process '{step.ProcessPath}' is input-idle");
                }
                catch (InvalidOperationException)
                {
                    // Process may not have a message loop yet (e.g. console apps) — continue
                    _log.Debug("FlowAction", $"Launch: WaitForInputIdle not supported for '{step.ProcessPath}'");
                }

                // Layer 2: Poll for the window to appear in the UIA tree (max 10s)
                var sw = Stopwatch.StartNew();
                int maxWaitMs = _timing.LaunchWindowTimeoutMs;
                const int pollIntervalMs = 200;
                AutomationElement? window = null;
                string procName = "";

                try { procName = proc.ProcessName; } catch { }

                while (sw.ElapsedMilliseconds < maxWaitMs)
                {
                    try
                    {
                        if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                        {
                            window = AutomationElement.FromHandle(proc.MainWindowHandle);
                            if (window != null)
                            {
                                var rect = window.Current.BoundingRectangle;
                                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                                {
                                    _log.Debug("FlowAction", $"Launch: window appeared after {sw.ElapsedMilliseconds}ms");
                                    break;
                                }
                            }
                        }
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException) { }

                    await Task.Delay(pollIntervalMs).ConfigureAwait(false);

                    // Refresh process info (MainWindowHandle can change)
                    try { proc.Refresh(); } catch { }
                }

                // Layer 3: Wait for the UIA tree to populate (window has interactive children)
                if (window != null)
                {
                    var stabilityStart = Stopwatch.StartNew();
                    while (stabilityStart.ElapsedMilliseconds < _timing.LaunchUiaStabilityMs)
                    {
                        try
                        {
                            var children = window.FindAll(TreeScope.Children, Condition.TrueCondition);
                            if (children.Count > 0)
                            {
                                _log.Debug("FlowAction", $"Launch: UIA tree ready ({children.Count} children) after {sw.ElapsedMilliseconds}ms total");
                                break;
                            }
                        }
                        catch (ElementNotAvailableException) { }

                        await Task.Delay(_timing.LaunchPollIntervalMs).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Window never appeared — fall back to a short delay
                    _log.Debug("FlowAction", $"Launch: window not detected via UIA, waited {sw.ElapsedMilliseconds}ms");
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            else
            {
                // Process exited immediately or Start returned null (UseShellExecute)
                await Task.Delay(1500).ConfigureAwait(false);
            }

            _log.Debug("FlowAction", $"Launch: '{step.ProcessPath}' ready");
            return new ActionResult { Success = true, Diagnostics = $"Launched '{step.ProcessPath}'" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Error = $"Failed to launch: {ex.Message}" };
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// DOMAIN HINT STORE — Learns domain→title mappings from successful navigations.
// Persists to %LOCALAPPDATA%\IdolClick\domain-hints.json.
// Thread-safe, lazy-loaded, write-through.
// ═══════════════════════════════════════════════════════════════════════════════════

internal static class DomainHintStore
{
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdolClick", "domain-hints.json");

    private static readonly object _lock = new();
    private static Dictionary<string, string>? _hints;

    /// <summary>
    /// Looks up a learned title hint for the given host (e.g., "youtube.com" → "YouTube").
    /// Returns null if no learned mapping exists.
    /// </summary>
    public static string? GetHint(string host)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _hints!.TryGetValue(NormalizeHost(host), out var hint) ? hint : null;
        }
    }

    /// <summary>
    /// Learns a domain→title mapping from a successful navigation.
    /// Extracts the meaningful part of the window title (before " - " browser suffix).
    /// </summary>
    public static void Learn(string host, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(windowTitle)) return;

        var key = NormalizeHost(host);
        var hint = ExtractTitleHint(windowTitle);
        if (string.IsNullOrEmpty(hint) || hint.Length < 2) return;

        EnsureLoaded();
        lock (_lock)
        {
            // Don't overwrite if the same hint already exists
            if (_hints!.TryGetValue(key, out var existing) &&
                existing.Equals(hint, StringComparison.OrdinalIgnoreCase))
                return;

            _hints[key] = hint;
            SaveAsync(); // fire-and-forget
        }
    }

    private static string NormalizeHost(string host)
    {
        host = host.ToLowerInvariant().Trim();
        if (host.StartsWith("www.")) host = host[4..];
        return host;
    }

    /// <summary>
    /// Extracts a useful hint from a browser window title.
    /// E.g., "GitHub: Let's build from here · GitHub - Google Chrome" → "GitHub"
    /// Strips browser names from the end, takes the first segment.
    /// </summary>
    private static string ExtractTitleHint(string title)
    {
        // Remove trailing browser identifiers
        string[] browserSuffixes = [" - Google Chrome", " - Mozilla Firefox", " - Microsoft Edge",
                                    " — Mozilla Firefox", " - Brave", " - Opera", " - Vivaldi"];
        foreach (var suffix in browserSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length];
                break;
            }
        }

        // Take the first meaningful segment (before " - ", " · ", " | ", " — ")
        string[] separators = [" - ", " · ", " | ", " — "];
        foreach (var sep in separators)
        {
            var idx = title.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                title = title[..idx].Trim();
                break;
            }
        }

        return title.Trim();
    }

    private static void EnsureLoaded()
    {
        if (_hints != null) return;
        lock (_lock)
        {
            if (_hints != null) return;
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _hints = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
                }
                else
                {
                    _hints = [];
                }
            }
            catch
            {
                _hints = [];
            }
        }
    }

    private static void SaveAsync()
    {
        // Serialize inside the lock to avoid race conditions
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_hints, new JsonSerializerOptions { WriteIndented = true });
        }

        // Write to disk async outside the lock
        _ = Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Best-effort persistence — don't crash the app
            }
        });
    }
}

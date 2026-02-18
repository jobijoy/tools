using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Core automation engine that evaluates and executes rules against Windows UI.
/// </summary>
/// <remarks>
/// <para>The engine runs a background polling loop that:</para>
/// <list type="number">
///   <item>Iterates through enabled rules that are currently running</item>
///   <item>Finds matching windows by process name and/or title</item>
///   <item>Searches for UI elements matching the rule criteria</item>
///   <item>Executes configured actions (click, keys, script, etc.)</item>
/// </list>
/// <para>Safety features include cooldown timers, time windows, and confirmation dialogs.</para>
/// </remarks>
public class AutomationEngine : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // DEPENDENCIES
    // ═══════════════════════════════════════════════════════════════════════════════
    
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ActionExecutor _executor;

    // R4.3: Compiled regex cache to avoid recompilation per polling cycle
    private static readonly ConcurrentDictionary<string, Regex?> _regexCache = new();
    
    // ═══════════════════════════════════════════════════════════════════════════════
    // STATE
    // ═══════════════════════════════════════════════════════════════════════════════
    
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile bool _enabled;
    private bool _disposed;
    
    /// <summary>
    /// Tracks last trigger time per rule ID to enforce cooldown periods.
    /// Thread-safe: accessed from the background worker thread.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastTrigger = new();
    
    /// <summary>
    /// Debounce timer for config saves to avoid writing to disk on every trigger.
    /// </summary>
    private DateTime _lastConfigSave = DateTime.MinValue;
    private const int ConfigSaveDebounceMs = 5000;

    // ═══════════════════════════════════════════════════════════════════════════════
    // PROPERTIES
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>Gets whether the automation engine is actively processing rules.</summary>
    public bool IsEnabled => _enabled;
    
    /// <summary>Gets human-readable status for UI display.</summary>
    public string Status => _enabled ? "Running" : "Paused";

    // ═══════════════════════════════════════════════════════════════════════════════
    // EVENTS
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>Raised when a rule's AlertIfContains pattern matches or ShowNotification action fires.</summary>
    public event Action<Rule, string>? OnAlert;
    
    /// <summary>Raised when ConfirmBeforeAction is true. Return false to cancel execution.</summary>
    public event Func<Rule, string, bool>? OnConfirmRequired;

    // ═══════════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Initializes a new automation engine instance.
    /// </summary>
    /// <param name="config">Configuration service for accessing rules and settings.</param>
    /// <param name="log">Logging service for diagnostics.</param>
    public AutomationEngine(ConfigService config, LogService log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _executor = new ActionExecutor(log);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PUBLIC METHODS
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Starts the background polling loop. Safe to call multiple times.
    /// </summary>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AutomationEngine));
        if (_worker != null) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunLoop(_cts.Token));
        _log.Info("Engine", "Automation engine started");
    }

    /// <summary>
    /// Enables or disables rule processing without stopping the polling loop.
    /// Blocked when the global kill switch is active (requires manual reset first).
    /// </summary>
    /// <param name="enabled">True to process rules, false to pause.</param>
    public void SetEnabled(bool enabled)
    {
        if (enabled && App.KillSwitchActive)
        {
            _log.Warn("Engine", "Cannot enable — kill switch is active. Reset from Settings or UI first.");
            return;
        }
        if (_enabled == enabled) return;
        _enabled = enabled;
        _log.Info("Engine", enabled ? "Automation enabled" : "Automation disabled");
    }

    /// <summary>Toggles the enabled state.</summary>
    public void Toggle() => SetEnabled(!_enabled);

    // ═══════════════════════════════════════════════════════════════════════════════
    // MAIN LOOP
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>Monotonically increasing cycle counter for log correlation.</summary>
    private long _cycleNumber;

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config.GetConfig();
            var interval = Math.Max(5000, cfg.Settings.PollingIntervalMs); // Minimum 5 seconds to prevent rapid loops

            if (_enabled)
            {
                await ProcessRulesOnceAsync().ConfigureAwait(false);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes all active rules exactly once (one cycle).  
    /// Called by the internal polling loop and also exposed for integration tests
    /// that need deterministic, single-cycle execution without waiting for timers.
    /// </summary>
    public async Task<(int Evaluated, int Triggered)> ProcessRulesOnceAsync()
    {
        var cfg = _config.GetConfig();
        var cycle = Interlocked.Increment(ref _cycleNumber);
        var sw = Stopwatch.StartNew();
        var rulesEvaluated = 0;
        var rulesTriggered = 0;

        try
        {
            var activeRules = cfg.Rules.Where(r => r.Enabled && r.IsRunning).ToList();
            _log.Debug("Cycle", $"[C{cycle}] Begin — {activeRules.Count} active rules");

            foreach (var rule in activeRules)
            {
                rulesEvaluated++;
                if (await ProcessRuleAsync(rule, cycle).ConfigureAwait(false))
                    rulesTriggered++;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Engine", $"[C{cycle}] Unhandled: {ex.GetType().Name}: {ex.Message}");
        }

        sw.Stop();
        _log.Debug("Cycle", $"[C{cycle}] End — {rulesEvaluated} evaluated, {rulesTriggered} triggered, {sw.ElapsedMilliseconds}ms");
        return (rulesEvaluated, rulesTriggered);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RULE PROCESSING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Processes a single rule: finds matching elements and executes actions.
    /// Returns true if the rule triggered an action this cycle.
    /// </summary>
    private async Task<bool> ProcessRuleAsync(Rule rule, long cycle)
    {
        var tag = $"[C{cycle}][{rule.Name}]";

        // Check cooldown
        if (_lastTrigger.TryGetValue(rule.Id, out var lastTime))
        {
            var elapsed = (DateTime.UtcNow - lastTime).TotalSeconds;
            if (elapsed < rule.CooldownSeconds)
            {
                _log.Debug("Skip", $"{tag} Cooldown ({elapsed:F1}s / {rule.CooldownSeconds}s)");
                return false;
            }
        }

        // Check time window
        if (!IsInTimeWindow(rule.TimeWindow))
        {
            _log.Debug("Skip", $"{tag} Outside time window '{rule.TimeWindow}'");
            return false;
        }

        // Safety: Process allowlist
        var allowedProcesses = _config.GetConfig().Settings.AllowedProcesses;
        if (allowedProcesses.Count > 0 && !string.IsNullOrEmpty(rule.TargetApp))
        {
            var ruleProcesses = rule.TargetApp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!ruleProcesses.Any(p => allowedProcesses.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                _log.Warn("Safety", $"{tag} Blocked — process '{rule.TargetApp}' not in AllowedProcesses");
                return false;
            }
        }

        // Safety: Max executions per session
        if (rule.MaxExecutionsPerSession > 0 && rule.SessionExecutionCount >= rule.MaxExecutionsPerSession)
        {
            _log.Debug("Skip", $"{tag} Max executions reached ({rule.SessionExecutionCount}/{rule.MaxExecutionsPerSession})");
            return false;
        }

        // Find target windows
        var windows = FindWindows(rule);
        if (windows.Count == 0)
        {
            _log.Debug("Skip", $"{tag} No windows (app='{rule.TargetApp}', title='{rule.WindowTitle}')");
            return false;
        }

        _log.Debug("Scan", $"{tag} Found {windows.Count} candidate window(s)");

        foreach (var window in windows)
        {
            // Check focus requirement
            if (rule.RequireFocus && !IsWindowFocused(window))
            {
                _log.Debug("Skip", $"{tag} Window not focused (RequireFocus=true)");
                continue;
            }

            AutomationElement? element;
            try
            {
                element = FindElement(window, rule);

                // PreScroll: if no element found and PreScrollDirection is set,
                // scroll the window and re-scan. This forces Electron/Chromium apps
                // to render off-viewport content into the UIA tree.
                if (element == null && !string.IsNullOrEmpty(rule.PreScrollDirection))
                {
                    element = PreScrollAndRescan(window, rule, tag);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Scan", $"{tag} FindElement error: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (element == null)
            {
                _log.Debug("Skip", $"{tag} No matching element (type='{rule.ElementType}', match='{rule.MatchText}')");
                continue;
            }

            var elementName = "(unnamed)";
            try { elementName = element.Current.Name ?? "(unnamed)"; } catch { }
            _log.Debug("Match", $"{tag} Matched element: '{elementName}'");

            // Safety: Check for alert patterns in nearby text
            if (rule.AlertIfContains.Length > 0)
            {
                var nearbyText = GetNearbyText(window, element);
                foreach (var pattern in rule.AlertIfContains)
                {
                    if (nearbyText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn("Safety", $"{tag} Alert triggered: '{pattern}' found near '{elementName}'");
                        OnAlert?.Invoke(rule, $"Found '{pattern}' - Please review before proceeding");
                        return false;
                    }
                }
            }

            // Safety: Confirm before action (dispatch to UI thread)
            if (rule.ConfirmBeforeAction)
            {
                bool confirmed = true;
                try
                {
                    confirmed = System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        OnConfirmRequired?.Invoke(rule, elementName) ?? true) ?? true;
                }
                catch (Exception ex)
                {
                    _log.Warn("Safety", $"{tag} Confirm dialog error: {ex.Message}; proceeding");
                }

                if (!confirmed)
                {
                    _log.Info("Safety", $"{tag} Action cancelled by user for '{elementName}'");
                    return false;
                }
            }

            // Execute action
            var actionSw = Stopwatch.StartNew();
            await ExecuteActionAsync(rule, element, window).ConfigureAwait(false);
            actionSw.Stop();
            _log.Debug("Perf", $"{tag} Action '{rule.Action}' took {actionSw.ElapsedMilliseconds}ms");
            
            // Update tracking
            _lastTrigger[rule.Id] = DateTime.UtcNow;
            rule.LastTriggered = DateTime.Now;
            rule.TriggerCount++;
            rule.SessionExecutionCount++;  // In-memory session count

            // Fire PropertyChanged on UI thread so WPF bindings update correctly
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    rule.OnPropertyChanged(nameof(Rule.TriggerCount));
                    rule.OnPropertyChanged(nameof(Rule.LastTriggered));
                });
            }
            catch { /* App shutting down — safe to ignore */ }

            // Debounce config saves — avoid disk I/O on every trigger
            if ((DateTime.UtcNow - _lastConfigSave).TotalMilliseconds > ConfigSaveDebounceMs)
            {
                try
                {
                    _config.SaveConfig(_config.GetConfig());
                }
                catch (Exception ex)
                {
                    _log.Warn("Engine", $"Config save failed (will retry next cycle): {ex.Message}");
                }
                _lastConfigSave = DateTime.UtcNow;
            }
            
            return true; // One action per rule per cycle
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS: TIME & FOCUS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if current time is within the rule's time window.
    /// </summary>
    /// <param name="timeWindow">Time range in format "HH:mm-HH:mm" or null for always active.</param>
    private static bool IsInTimeWindow(string? timeWindow)
    {
        if (string.IsNullOrEmpty(timeWindow)) return true;

        try
        {
            var parts = timeWindow.Split('-');
            if (parts.Length != 2) return true;

            var start = TimeSpan.Parse(parts[0].Trim());
            var end = TimeSpan.Parse(parts[1].Trim());
            var now = DateTime.Now.TimeOfDay;

            return now >= start && now <= end;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Checks if the specified window has keyboard focus.
    /// </summary>
    private static bool IsWindowFocused(AutomationElement window)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            return Win32.GetForegroundWindow() == hwnd;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WINDOW DISCOVERY
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds all windows matching the rule's target criteria.
    /// </summary>
    private List<AutomationElement> FindWindows(Rule rule)
    {
        var results = new List<AutomationElement>();
        var seen = new HashSet<long>();

        // Search by process name if specified
        if (!string.IsNullOrEmpty(rule.TargetApp))
        {
            var processNames = rule.TargetApp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            foreach (var procName in processNames)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(procName);
                }
                catch
                {
                    continue;
                }

                foreach (var proc in procs)
                {
                    using (proc) // Dispose Process handle to prevent leaks
                    {
                        try
                        {
                            if (proc.MainWindowHandle == IntPtr.Zero) continue;
                            var handle = proc.MainWindowHandle.ToInt64();
                            if (seen.Contains(handle)) continue;

                            var elem = AutomationElement.FromHandle(proc.MainWindowHandle);
                            var title = elem.Current.Name ?? "";

                            if (!string.IsNullOrEmpty(rule.WindowTitle) &&
                                !title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase))
                                continue;

                            results.Add(elem);
                            seen.Add(handle);
                        }
                        catch (System.Windows.Automation.ElementNotAvailableException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }
        }

        // Also search all top-level windows by title
        if (!string.IsNullOrEmpty(rule.WindowTitle))
        {
            try
            {
                var all = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < all.Count; i++)
                {
                    try
                    {
                        var w = all[i];
                        var handle = (long)w.Current.NativeWindowHandle;
                        if (seen.Contains(handle)) continue;

                        var title = w.Current.Name ?? "";
                        if (title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(w);
                            seen.Add(handle);
                        }
                    }
                    catch (System.Windows.Automation.ElementNotAvailableException) { }
                }
            }
            catch { }
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ELEMENT DISCOVERY
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Searches a window for the first UI element matching the rule criteria.
    /// </summary>
    private AutomationElement? FindElement(AutomationElement window, Rule rule)
    {
        var controlType = rule.ElementType.ToLowerInvariant() switch
        {
            "button" => ControlType.Button,
            "listitem" => ControlType.ListItem,
            "text" => ControlType.Text,
            "link" => ControlType.Hyperlink,
            "edit" or "textbox" => ControlType.Edit,
            "checkbox" => ControlType.CheckBox,
            "radiobutton" => ControlType.RadioButton,
            "combobox" or "dropdown" => ControlType.ComboBox,
            "menuitem" => ControlType.MenuItem,
            "menu" => ControlType.Menu,
            "tab" or "tabitem" => ControlType.TabItem,
            "treeitem" => ControlType.TreeItem,
            "dataitem" or "row" => ControlType.DataItem,
            "slider" => ControlType.Slider,
            "window" => ControlType.Window,
            "document" => ControlType.Document,
            "image" => ControlType.Image,
            "toolbar" => ControlType.ToolBar,
            "group" => ControlType.Group,
            _ => null
        };

        var condition = controlType != null
            ? new PropertyCondition(AutomationElement.ControlTypeProperty, controlType)
            : Condition.TrueCondition;

        var elements = window.FindAll(TreeScope.Descendants, condition);
        var windowRect = window.Current.BoundingRectangle;
        var patterns = rule.MatchText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        int scanned = 0, excluded = 0, regionSkipped = 0, disabledSkipped = 0;

        for (int i = 0; i < elements.Count; i++)
        {
            AutomationElement elem;
            try
            {
                elem = elements[i];
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                continue; // Element disappeared between enumeration and access
            }

            string name;
            try
            {
                name = (elem.Current.Name ?? "").Trim();
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(name)) continue;
            scanned++;

            // Check exclusions
            if (rule.ExcludeTexts.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase)))
            {
                excluded++;
                continue;
            }

            // Check pattern match
            if (!MatchesPatterns(name, patterns, rule.UseRegex))
                continue;

            // Check region
            if (rule.Region != null)
            {
                try
                {
                    var rect = elem.Current.BoundingRectangle;
                    if (!IsInRegion(rect, windowRect, rule.Region))
                    {
                        regionSkipped++;
                        continue;
                    }
                }
                catch (System.Windows.Automation.ElementNotAvailableException)
                {
                    continue;
                }
            }

            // Check visibility (offscreen or invisible elements should not match)
            try
            {
                if (elem.Current.IsOffscreen)
                {
                    if (!rule.ScrollIntoView)
                        continue;

                    // Attempt to scroll the element into view
                    if (!TryScrollIntoView(elem, window, name))
                        continue;
                }
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                continue;
            }

            // Check enabled
            try
            {
                if (!elem.Current.IsEnabled)
                {
                    disabledSkipped++;
                    continue;
                }
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                continue;
            }

            _log.Debug("Scan", $"FindElement: scanned={scanned}, excluded={excluded}, regionSkip={regionSkipped}, disabled={disabledSkipped} → matched '{name}'");
            return elem;
        }

        _log.Debug("Scan", $"FindElement: scanned={scanned}/{elements.Count} named, excluded={excluded}, regionSkip={regionSkipped}, disabled={disabledSkipped} → no match");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PRE-SCROLL (for Electron/Chromium apps)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scrolls the target window in the configured direction, then re-scans for the element.
    /// This forces Electron/Chromium apps to render off-viewport content into the UIA tree.
    /// </summary>
    private AutomationElement? PreScrollAndRescan(AutomationElement window, Rule rule, string tag)
    {
        const int maxScrollPasses = 5;
        const int settleMs = 400;

        var direction = rule.PreScrollDirection?.ToLowerInvariant();
        if (direction is not ("down" or "up")) return null;

        var amount = Math.Clamp(rule.PreScrollAmount, 1, 50);
        var delta = direction == "down" ? -120 * amount : 120 * amount;

        try
        {
            var rect = window.Current.BoundingRectangle;
            if (rect.IsEmpty) return null;

            var hwnd = new IntPtr(window.Current.NativeWindowHandle);

            for (int pass = 1; pass <= maxScrollPasses; pass++)
            {
                // Move cursor to window center and scroll
                Win32.MoveCursor((int)(rect.Left + rect.Width / 2),
                                 (int)(rect.Top + rect.Height / 2));
                Win32.MouseWheel(delta);
                Thread.Sleep(settleMs);

                // Re-scan after scroll
                var element = FindElement(window, rule);
                if (element != null)
                {
                    _log.Info("PreScroll", $"{tag} Found '{rule.MatchText}' after {pass} scroll pass(es) {direction}");
                    return element;
                }
            }

            _log.Debug("PreScroll", $"{tag} No match after {maxScrollPasses} scroll passes {direction}");
        }
        catch (Exception ex)
        {
            _log.Warn("PreScroll", $"{tag} PreScroll error: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SCROLL INTO VIEW
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to scroll an off-screen element into view using a three-tier fallback.
    /// Returns true if the element is now on-screen.
    /// </summary>
    private bool TryScrollIntoView(AutomationElement element, AutomationElement window, string elementName)
    {
        const int maxAttempts = 3;
        const int settleMs = 350;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Tier 1: ScrollItemPattern on the element itself (cleanest UIA approach)
            try
            {
                if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var sip) && sip is ScrollItemPattern scrollItem)
                {
                    scrollItem.ScrollIntoView();
                    Thread.Sleep(settleMs);
                    if (!element.Current.IsOffscreen)
                    {
                        _log.Info("Scroll", $"Scrolled '{elementName}' into view via ScrollItemPattern (attempt {attempt})");
                        return true;
                    }
                }
            }
            catch { }

            // Tier 2: ScrollPattern on nearest scrollable ancestor — scroll to bottom
            try
            {
                var ancestor = FindScrollableAncestor(element);
                if (ancestor != null &&
                    ancestor.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp) && sp is ScrollPattern scroll)
                {
                    scroll.ScrollVertical(ScrollAmount.LargeIncrement);
                    Thread.Sleep(settleMs);
                    if (!element.Current.IsOffscreen)
                    {
                        _log.Info("Scroll", $"Scrolled '{elementName}' into view via ancestor ScrollPattern (attempt {attempt})");
                        return true;
                    }
                }
            }
            catch { }

            // Tier 3: Mouse wheel on the window center
            try
            {
                var windowRect = window.Current.BoundingRectangle;
                if (!windowRect.IsEmpty)
                {
                    var hwnd = new IntPtr(window.Current.NativeWindowHandle);
                    Win32.SetForegroundWindow(hwnd);
                    Thread.Sleep(100);
                    Win32.MoveCursor((int)(windowRect.Left + windowRect.Width / 2),
                                    (int)(windowRect.Top + windowRect.Height / 2));
                    Win32.MouseWheel(-120 * 5); // scroll down ~5 notches
                    Thread.Sleep(settleMs);
                    if (!element.Current.IsOffscreen)
                    {
                        _log.Info("Scroll", $"Scrolled '{elementName}' into view via mouse wheel (attempt {attempt})");
                        return true;
                    }
                }
            }
            catch { }
        }

        _log.Debug("Scroll", $"Could not scroll '{elementName}' into view after {maxAttempts} attempts");
        return false;
    }

    /// <summary>
    /// Walks up the UIA tree to find the nearest ancestor that supports ScrollPattern.
    /// </summary>
    private static AutomationElement? FindScrollableAncestor(AutomationElement element)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var current = walker.GetParent(element);
            int depth = 0;
            while (current != null && !AutomationElement.RootElement.Equals(current) && depth < 15)
            {
                if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
                    return current;
                current = walker.GetParent(current);
                depth++;
            }
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PATTERN MATCHING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if element name matches any of the patterns.
    /// </summary>
    /// <remarks>
    /// For non-regex patterns, supports prefix matching for buttons with shortcuts
    /// like "Allow (Ctrl+Enter)".
    /// </remarks>
    private static bool MatchesPatterns(string name, string[] patterns, bool useRegex)
    {
        foreach (var pattern in patterns)
        {
            if (useRegex)
            {
                // R4.3: Use cached compiled Regex
                var regex = _regexCache.GetOrAdd(pattern, p =>
                {
                    try { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
                    catch { return null; }
                });
                if (regex != null && regex.IsMatch(name)) return true;
            }
            else
            {
                // Support prefix matching for buttons with shortcuts like "Allow (Ctrl+Enter)"
                if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
                if (name.StartsWith(pattern + " ", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.StartsWith(pattern + "(", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if element center is within the normalized screen region.
    /// </summary>
    private static bool IsInRegion(System.Windows.Rect elem, System.Windows.Rect window, ScreenRegion region)
    {
        var regionRect = new System.Windows.Rect(
            window.Left + window.Width * region.X,
            window.Top + window.Height * region.Y,
            window.Width * region.Width,
            window.Height * region.Height);

        var cx = elem.Left + elem.Width / 2;
        var cy = elem.Top + elem.Height / 2;
        return cx >= regionRect.Left && cx <= regionRect.Right && cy >= regionRect.Top && cy <= regionRect.Bottom;
    }

    /// <summary>
    /// Collects text content near the specified element for safety pattern checking.
    /// Guarded against stale elements and UI Automation exceptions.
    /// </summary>
    private static string GetNearbyText(AutomationElement window, AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            var textCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
            var texts = window.FindAll(TreeScope.Descendants, textCondition);

            var nearby = new List<string>();
            for (int i = 0; i < texts.Count; i++)
            {
                try
                {
                    var t = texts[i];
                    var tRect = t.Current.BoundingRectangle;
                    var distance = Math.Sqrt(Math.Pow(rect.Left - tRect.Left, 2) + Math.Pow(rect.Top - tRect.Top, 2));
                    if (distance < 200)
                        nearby.Add(t.Current.Name ?? "");
                }
                catch (System.Windows.Automation.ElementNotAvailableException) { }
            }
            return string.Join(" ", nearby);
        }
        catch { return ""; }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ACTION EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes the configured action for a matched element via the unified async path.
    /// Routes through ExecuteRuleActionAsync for consistent DryRun, Timeline, Notification, and Plugin support.
    /// </summary>
    private async Task ExecuteActionAsync(Rule rule, AutomationElement element, AutomationElement window)
    {
        var name = "(unknown)";
        try { name = element.Current.Name ?? "(unknown)"; } catch { }

        // Build context for the full pipeline
        var context = new AutomationContext
        {
            MatchedText = name,
            WindowHandle = IntPtr.Zero,
            WindowTitle = "",
            ProcessName = rule.TargetApp
        };

        try
        {
            context.WindowTitle = window.Current.Name ?? "";
            context.WindowHandle = new IntPtr(window.Current.NativeWindowHandle);
        }
        catch { }

        // Pre-action: activate window if SendKeys (needs focus)
        if (rule.Action.Equals("sendkeys", StringComparison.OrdinalIgnoreCase))
            await _executor.ActivateWindowAsync(window).ConfigureAwait(false);

        // Execute through the unified async path directly (no sync-over-async)
        var success = await _executor.ExecuteRuleActionAsync(rule, element, context).ConfigureAwait(false);
        _log.Info("Action", $"{(success ? "✓" : "✗")} '{rule.Action}' on '{name}' (Rule: {rule.Name})");
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DISPOSAL
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stops the polling loop and releases resources.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        try { _worker?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}

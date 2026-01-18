using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AutomationTool.Models;

namespace AutomationTool.Services;

/// <summary>
/// Core automation engine that evaluates and executes rules.
/// </summary>
public class AutomationEngine : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ActionExecutor _executor;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile bool _enabled;
    private readonly Dictionary<string, DateTime> _lastTrigger = new();

    public bool IsEnabled => _enabled;
    public string Status => _enabled ? "Running" : "Paused";

    public event Action<Rule, string>? OnAlert;
    public event Func<Rule, string, bool>? OnConfirmRequired;

    public AutomationEngine(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
        _executor = new ActionExecutor(log);
    }

    public void Start()
    {
        if (_worker != null) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunLoop(_cts.Token));
        _log.Info("Engine", "Automation engine started");
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _log.Info("Engine", enabled ? "Automation enabled" : "Automation disabled");
    }

    public void Toggle() => SetEnabled(!_enabled);

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config.GetConfig();
            var interval = Math.Max(500, cfg.Settings.PollingIntervalMs);

            if (_enabled)
            {
                try
                {
                    foreach (var rule in cfg.Rules.Where(r => r.Enabled))
                    {
                        if (ct.IsCancellationRequested) break;
                        ProcessRule(rule);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Engine", $"Error: {ex.Message}");
                }
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private void ProcessRule(Rule rule)
    {
        // Check cooldown
        if (_lastTrigger.TryGetValue(rule.Id, out var lastTime))
        {
            var elapsed = (DateTime.UtcNow - lastTime).TotalSeconds;
            if (elapsed < rule.CooldownSeconds) return;
        }

        // Check time window
        if (!IsInTimeWindow(rule.TimeWindow)) return;

        // Find target windows
        var windows = FindWindows(rule);
        if (windows.Count == 0) return;

        foreach (var window in windows)
        {
            // Check focus requirement
            if (rule.RequireFocus && !IsWindowFocused(window)) continue;

            var element = FindElement(window, rule);
            if (element == null) continue;

            var elementName = element.Current.Name ?? "(unnamed)";
            _log.Debug("Match", $"Rule '{rule.Name}' matched: {elementName}");

            // Safety: Check for alert patterns in nearby text
            if (rule.AlertIfContains.Length > 0)
            {
                var nearbyText = GetNearbyText(window, element);
                foreach (var pattern in rule.AlertIfContains)
                {
                    if (nearbyText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn("Safety", $"Alert triggered: '{pattern}' found near '{elementName}'");
                        OnAlert?.Invoke(rule, $"Found '{pattern}' - Please review before proceeding");
                        return;
                    }
                }
            }

            // Safety: Confirm before action
            if (rule.ConfirmBeforeAction)
            {
                var confirmed = OnConfirmRequired?.Invoke(rule, elementName) ?? true;
                if (!confirmed)
                {
                    _log.Info("Safety", $"Action cancelled by user for '{elementName}'");
                    return;
                }
            }

            // Execute action
            ExecuteAction(rule, element, window);
            
            // Update tracking
            _lastTrigger[rule.Id] = DateTime.UtcNow;
            rule.LastTriggered = DateTime.Now;
            rule.TriggerCount++;
            _config.SaveConfig(_config.GetConfig());
            
            return; // One action per rule per cycle
        }
    }

    private bool IsInTimeWindow(string? timeWindow)
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

    private bool IsWindowFocused(AutomationElement window)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            return Win32.GetForegroundWindow() == hwnd;
        }
        catch { return false; }
    }

    private List<AutomationElement> FindWindows(Rule rule)
    {
        var results = new List<AutomationElement>();
        var seen = new HashSet<int>();

        var processNames = rule.TargetApp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var procName in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(procName))
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                var handle = proc.MainWindowHandle.ToInt32();
                if (seen.Contains(handle)) continue;

                try
                {
                    var elem = AutomationElement.FromHandle(proc.MainWindowHandle);
                    var title = elem.Current.Name ?? "";

                    if (!string.IsNullOrEmpty(rule.WindowTitle) &&
                        !title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(elem);
                    seen.Add(handle);
                }
                catch { }
            }
        }

        // Also search by window title
        if (!string.IsNullOrEmpty(rule.WindowTitle))
        {
            try
            {
                var all = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < all.Count; i++)
                {
                    var w = all[i];
                    var handle = w.Current.NativeWindowHandle;
                    if (seen.Contains(handle)) continue;

                    var title = w.Current.Name ?? "";
                    if (title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(w);
                        seen.Add(handle);
                    }
                }
            }
            catch { }
        }

        return results;
    }

    private AutomationElement? FindElement(AutomationElement window, Rule rule)
    {
        var controlType = rule.ElementType.ToLowerInvariant() switch
        {
            "button" => ControlType.Button,
            "listitem" => ControlType.ListItem,
            "text" => ControlType.Text,
            "link" => ControlType.Hyperlink,
            _ => null
        };

        var condition = controlType != null
            ? new PropertyCondition(AutomationElement.ControlTypeProperty, controlType)
            : Condition.TrueCondition;

        var elements = window.FindAll(TreeScope.Descendants, condition);
        var windowRect = window.Current.BoundingRectangle;
        var patterns = rule.MatchText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            var name = (elem.Current.Name ?? "").Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // Check exclusions
            if (rule.ExcludeTexts.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Check pattern match
            if (!MatchesPatterns(name, patterns, rule.UseRegex))
                continue;

            // Check region
            if (rule.Region != null)
            {
                var rect = elem.Current.BoundingRectangle;
                if (!IsInRegion(rect, windowRect, rule.Region))
                    continue;
            }

            // Check enabled
            if (!elem.Current.IsEnabled) continue;

            return elem;
        }

        return null;
    }

    private static bool MatchesPatterns(string name, string[] patterns, bool useRegex)
    {
        foreach (var pattern in patterns)
        {
            if (useRegex)
            {
                try { if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase)) return true; }
                catch { }
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
                var t = texts[i];
                var tRect = t.Current.BoundingRectangle;
                var distance = Math.Sqrt(Math.Pow(rect.Left - tRect.Left, 2) + Math.Pow(rect.Top - tRect.Top, 2));
                if (distance < 200)
                    nearby.Add(t.Current.Name ?? "");
            }
            return string.Join(" ", nearby);
        }
        catch { return ""; }
    }

    private void ExecuteAction(Rule rule, AutomationElement element, AutomationElement window)
    {
        var name = element.Current.Name ?? "(unknown)";

        switch (rule.Action.ToLowerInvariant())
        {
            case "click":
                _executor.ClickElement(element);
                _log.Info("Action", $"Clicked '{name}' (Rule: {rule.Name})");
                break;

            case "sendkeys":
                if (!string.IsNullOrEmpty(rule.Keys))
                {
                    _executor.ActivateWindow(window);
                    _executor.SendKeys(rule.Keys);
                    _log.Info("Action", $"Sent keys to '{name}' (Rule: {rule.Name})");
                }
                break;

            case "runscript":
                if (!string.IsNullOrEmpty(rule.Script))
                {
                    _executor.RunScript(rule.ScriptLanguage, rule.Script);
                    _log.Info("Action", $"Ran {rule.ScriptLanguage} script (Rule: {rule.Name})");
                }
                break;

            case "shownotification":
                var msg = rule.NotificationMessage ?? name;
                OnAlert?.Invoke(rule, msg);
                _log.Info("Action", $"Showed notification: {msg}");
                break;

            case "alert":
                _log.Warn("Alert", $"Alert triggered for '{name}' (Rule: {rule.Name})");
                OnAlert?.Invoke(rule, $"Found: {name}");
                break;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _worker?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}

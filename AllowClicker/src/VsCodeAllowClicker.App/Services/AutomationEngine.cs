using System.Diagnostics;
using System.Windows.Automation;
using VsCodeAllowClicker.App.Models;

namespace VsCodeAllowClicker.App.Services;

/// <summary>
/// Core automation engine that executes rules.
/// </summary>
public sealed class AutomationEngine : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile bool _enabled;
    private readonly Dictionary<string, DateTime> _lastActionTime = new();

    public event Action<Rule, string>? OnAlert;
    public event Func<Rule, string, bool>? OnConfirmRequired;

    public bool IsEnabled => _enabled;
    public string Status { get; private set; } = "Stopped";

    public AutomationEngine(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
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
        Status = enabled ? "Running" : "Paused";
        _log.Info("Engine", enabled ? "Automation enabled" : "Automation disabled");
    }

    public void Toggle() => SetEnabled(!_enabled);

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config.GetConfig();
            var interval = Math.Max(100, cfg.Settings.PollingIntervalMs);

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
        if (_lastActionTime.TryGetValue(rule.Id, out var lastTime))
        {
            var elapsed = (DateTime.UtcNow - lastTime).TotalMilliseconds;
            if (elapsed < rule.Safety.CooldownMs) return;
        }

        // Find target windows
        var windows = FindWindows(rule.Target);
        if (windows.Count == 0) return;

        foreach (var window in windows)
        {
            var element = FindElement(window, rule.Target);
            if (element == null) continue;

            var elementName = element.Current.Name ?? "(unnamed)";
            _log.Debug("Match", $"Rule '{rule.Name}' matched: {elementName}");

            // Safety: Check for alert patterns
            if (rule.Safety.AlertIfContains?.Length > 0)
            {
                var nearbyText = GetNearbyText(window, element);
                foreach (var pattern in rule.Safety.AlertIfContains)
                {
                    if (nearbyText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn("Safety", $"Alert triggered: '{pattern}' found near '{elementName}'");
                        OnAlert?.Invoke(rule, $"Found '{pattern}' - {elementName}");
                        return;
                    }
                }
            }

            // Safety: Confirm before action
            if (rule.Safety.ConfirmBeforeAction)
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
            _lastActionTime[rule.Id] = DateTime.UtcNow;
            return;
        }
    }

    private List<AutomationElement> FindWindows(TargetMatch target)
    {
        var results = new List<AutomationElement>();
        var seen = new HashSet<int>();

        foreach (var procName in target.ProcessNames)
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

                    if (!string.IsNullOrEmpty(target.WindowTitleContains) &&
                        !title.Contains(target.WindowTitleContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(elem);
                    seen.Add(handle);
                }
                catch { }
            }
        }

        // Also search top-level windows by title
        if (!string.IsNullOrEmpty(target.WindowTitleContains))
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
                    if (title.Contains(target.WindowTitleContains, StringComparison.OrdinalIgnoreCase))
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

    private AutomationElement? FindElement(AutomationElement window, TargetMatch target)
    {
        var controlType = target.ElementType.ToLowerInvariant() switch
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

        for (int i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            var name = (elem.Current.Name ?? "").Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // Check exclusions
            if (target.ExcludePatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Check pattern match
            if (!MatchesPatterns(name, target.TextPatterns))
                continue;

            // Check region
            if (target.Region != null)
            {
                var rect = elem.Current.BoundingRectangle;
                if (!IsInRegion(rect, windowRect, target.Region))
                    continue;
            }

            // Check enabled
            if (!elem.Current.IsEnabled) continue;

            return elem;
        }

        return null;
    }

    private static bool MatchesPatterns(string name, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern == "*") return true;
            if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(pattern + " ", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(pattern + "(", StringComparison.OrdinalIgnoreCase)) return true;
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
        var action = rule.Action;
        var name = element.Current.Name ?? "(unknown)";

        switch (action.Type.ToLowerInvariant())
        {
            case "click":
                ClickElement(element);
                _log.Info("Action", $"Clicked '{name}' (Rule: {rule.Name})");
                break;

            case "sendkeys":
                if (action.Keys?.Length > 0)
                {
                    ActivateWindow(window);
                    foreach (var key in action.Keys)
                        SendKey(key);
                    _log.Info("Action", $"Sent keys to '{name}' (Rule: {rule.Name})");
                }
                break;

            case "alert":
                _log.Warn("Alert", $"{action.AlertMessage ?? name}");
                OnAlert?.Invoke(rule, action.AlertMessage ?? name);
                break;

            case "readandalert":
                var text = GetNearbyText(window, element);
                _log.Info("Read", $"Content near '{name}': {text}");
                OnAlert?.Invoke(rule, $"Found: {text}");
                break;
        }

        if (action.DelayAfterMs > 0)
            Thread.Sleep(action.DelayAfterMs);
    }

    private static void ClickElement(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
            {
                inv.Invoke();
                return;
            }
        }
        catch { }

        try
        {
            var pt = element.GetClickablePoint();
            Win32.Click((int)pt.X, (int)pt.Y);
            return;
        }
        catch { }

        try
        {
            var r = element.Current.BoundingRectangle;
            if (!r.IsEmpty)
                Win32.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        }
        catch { }
    }

    private static void ActivateWindow(AutomationElement window)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            Win32.SetForegroundWindow(hwnd);
        }
        catch { }
    }

    private static void SendKey(string keyName)
    {
        var vk = keyName.ToLowerInvariant() switch
        {
            "enter" or "return" => Win32.VK_RETURN,
            "tab" => Win32.VK_TAB,
            "escape" or "esc" => Win32.VK_ESCAPE,
            "down" => Win32.VK_DOWN,
            "up" => Win32.VK_UP,
            "space" => Win32.VK_SPACE,
            _ => (ushort)0
        };
        if (vk != 0) Win32.SendKey(vk);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _worker?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}

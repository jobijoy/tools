using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

/// <summary>
/// Executes automation actions on matched UI elements.
/// </summary>
/// <remarks>
/// <para>Supported actions:</para>
/// <list type="table">
///   <item><term>Click</term><description>Invokes UI element via pattern or simulated click</description></item>
///   <item><term>SendKeys</term><description>Sends keyboard input to active window</description></item>
///   <item><term>RunScript</term><description>Executes PowerShell or C# script</description></item>
///   <item><term>ShowNotification</term><description>Displays toast notification</description></item>
///   <item><term>Plugin</term><description>Runs external plugin</description></item>
///   <item><term>Alert</term><description>Shows modal alert dialog</description></item>
/// </list>
/// </remarks>
public class ActionExecutor
{
    private readonly LogService _log;

    /// <summary>
    /// Initializes a new action executor instance.
    /// </summary>
    /// <param name="log">Logging service for diagnostics.</param>
    public ActionExecutor(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // MAIN ENTRY POINT
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes the action defined in a rule against the matched element.
    /// </summary>
    /// <param name="rule">Rule defining the action to execute.</param>
    /// <param name="element">Matched UI automation element.</param>
    /// <param name="context">Optional context with window and process info.</param>
    /// <returns>True if action succeeded, false otherwise.</returns>
    public async Task<bool> ExecuteRuleActionAsync(Rule rule, AutomationElement element, AutomationContext? context = null)
    {
        if (rule.DryRun)
        {
            _log.Info("Action", $"[DRY RUN] Would execute '{rule.Action}' for rule '{rule.Name}'");
            App.Timeline.RecordAction(rule.Id, rule.Name, rule.Action, true, "Dry run - no action taken");
            return true;
        }

        context ??= new AutomationContext();

        try
        {
            var success = rule.Action.ToLowerInvariant() switch
            {
                "click" => ExecuteClick(element),
                "sendkeys" => ExecuteSendKeys(rule.Keys),
                "runscript" => await ExecuteScriptAsync(rule, context),
                "shownotification" => ExecuteShowNotification(rule),
                "plugin" => await ExecutePluginAsync(rule, context),
                "alert" => ExecuteAlert(rule),
                _ => false
            };

            // Record to timeline
            App.Timeline.RecordAction(rule.Id, rule.Name, rule.Action, success);

            // Send notification if configured
            if (rule.Notification != null)
            {
                await SendNotificationIfNeeded(rule, context, success);
            }

            return success;
        }
        catch (Exception ex)
        {
            _log.Error("Action", $"Action execution failed: {ex.Message}");
            App.Timeline.RecordAction(rule.Id, rule.Name, rule.Action, false, ex.Message);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // NOTIFICATION HELPER
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task SendNotificationIfNeeded(Rule rule, AutomationContext context, bool success)
    {
        if (rule.Notification == null) return;
        
        var shouldNotify = (success && rule.Notification.OnSuccess) || (!success && rule.Notification.OnFailure);
        if (!shouldNotify) return;

        var notifyContext = new NotificationContext
        {
            Rule = rule,
            MatchedText = context.MatchedText,
            WindowTitle = context.WindowTitle,
            ProcessName = context.ProcessName,
            TriggerTime = DateTime.Now,
            ActionTaken = rule.Action
        };

        var sent = await App.Notifications.SendAsync(rule.Notification, notifyContext);
        App.Timeline.RecordNotification(rule.Id, rule.Name, rule.Notification.Type ?? "unknown", sent);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ACTION IMPLEMENTATIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    private bool ExecuteClick(AutomationElement element)
    {
        ClickElement(element);
        return true;
    }

    private bool ExecuteSendKeys(string? keys)
    {
        if (string.IsNullOrEmpty(keys)) return false;
        SendKeys(keys);
        return true;
    }

    private async Task<bool> ExecuteScriptAsync(Rule rule, AutomationContext context)
    {
        if (string.IsNullOrEmpty(rule.Script))
        {
            _log.Warn("Action", "Script is empty");
            return false;
        }

        var scriptContext = new ScriptContext
        {
            Rule = rule,
            MatchedText = context.MatchedText,
            WindowTitle = context.WindowTitle,
            ProcessName = context.ProcessName,
            WindowHandle = context.WindowHandle,
            TriggerTime = DateTime.Now
        };

        var result = await App.Scripts.ExecuteAsync(rule.ScriptLanguage, rule.Script, scriptContext);

        if (!string.IsNullOrEmpty(result.Output))
            _log.Debug("Script", $"Output: {result.Output}");

        App.Timeline.RecordScript(rule.Id, rule.Name, rule.ScriptLanguage, result.Success, result.Output);
        return result.Success;
    }

    private bool ExecuteShowNotification(Rule rule)
    {
        var message = rule.NotificationMessage ?? $"Rule '{rule.Name}' triggered";
        App.Notifications.ShowToast("Automation Tool", message);
        return true;
    }

    private async Task<bool> ExecutePluginAsync(Rule rule, AutomationContext context)
    {
        if (string.IsNullOrEmpty(rule.PluginId))
        {
            _log.Warn("Action", "Plugin ID is empty");
            return false;
        }

        var success = await App.Plugins.ExecuteAsync(rule.PluginId, rule, context);
        App.Timeline.RecordPlugin(rule.Id, rule.Name, rule.PluginId, success);
        return success;
    }

    private bool ExecuteAlert(Rule rule)
    {
        var message = rule.NotificationMessage ?? $"Alert: Rule '{rule.Name}' triggered";
        System.Windows.MessageBox.Show(message, "Automation Alert", 
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // CLICK IMPLEMENTATION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clicks a UI element using the most reliable method available.
    /// </summary>
    /// <remarks>
    /// Attempts methods in order: InvokePattern → ClickablePoint → BoundingRect center.
    /// </remarks>
    public void ClickElement(AutomationElement element)
    {
        // Try InvokePattern first (most reliable)
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
            {
                inv.Invoke();
                return;
            }
        }
        catch { }

        // Try GetClickablePoint
        try
        {
            var pt = element.GetClickablePoint();
            Win32.Click((int)pt.X, (int)pt.Y);
            return;
        }
        catch { }

        // Fallback to center of bounding rect
        try
        {
            var r = element.Current.BoundingRectangle;
            if (!r.IsEmpty)
                Win32.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WINDOW & KEYBOARD
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Brings a window to the foreground and activates it.
    /// </summary>
    public void ActivateWindow(AutomationElement window)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            Win32.SetForegroundWindow(hwnd);
            Thread.Sleep(100);
        }
        catch { }
    }

    /// <summary>
    /// Sends keyboard input to the active window.
    /// </summary>
    /// <param name="keys">Comma-separated key names (e.g., "Tab, Enter, Ctrl+A").</param>
    public void SendKeys(string keys)
    {
        var keyList = keys.Split(',', StringSplitOptions.TrimEntries);
        foreach (var key in keyList)
        {
            SendKey(key);
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Sends a single key or key combination.
    /// </summary>
    private void SendKey(string keyName)
    {
        var lower = keyName.ToLowerInvariant();
        ushort vk = lower switch
        {
            "enter" or "return" => Win32.VK_RETURN,
            "tab" => Win32.VK_TAB,
            "escape" or "esc" => Win32.VK_ESCAPE,
            "space" => Win32.VK_SPACE,
            "up" => Win32.VK_UP,
            "down" => Win32.VK_DOWN,
            "left" => Win32.VK_LEFT,
            "right" => Win32.VK_RIGHT,
            "backspace" => Win32.VK_BACK,
            "delete" => Win32.VK_DELETE,
            "home" => Win32.VK_HOME,
            "end" => Win32.VK_END,
            _ => 0
        };

        if (vk != 0)
        {
            Win32.SendKey(vk);
            return;
        }

        // Handle Ctrl+X, Alt+X combinations
        if (lower.StartsWith("ctrl+") || lower.StartsWith("alt+") || lower.StartsWith("shift+"))
        {
            var parts = lower.Split('+');
            var modifiers = new List<ushort>();
            ushort mainKey = 0;

            foreach (var part in parts)
            {
                switch (part)
                {
                    case "ctrl": modifiers.Add(Win32.VK_CONTROL); break;
                    case "alt": modifiers.Add(Win32.VK_MENU); break;
                    case "shift": modifiers.Add(Win32.VK_SHIFT); break;
                    default:
                        if (part.Length == 1)
                            mainKey = (ushort)char.ToUpperInvariant(part[0]);
                        break;
                }
            }

            if (mainKey != 0)
            {
                Win32.SendKeyCombo(modifiers.ToArray(), mainKey);
                return;
            }
        }

        // Single letter
        if (keyName.Length == 1)
        {
            Win32.SendKey((ushort)char.ToUpperInvariant(keyName[0]));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SCRIPT EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs a script in the specified language.
    /// </summary>
    /// <param name="language">"powershell" or "csharp".</param>
    /// <param name="script">Script content to execute.</param>
    public void RunScript(string language, string script)
    {
        try
        {
            switch (language.ToLowerInvariant())
            {
                case "powershell":
                    RunPowerShell(script);
                    break;
                case "csharp":
                    _log.Warn("Script", "C# scripting requires Roslyn - coming in Phase 4");
                    break;
                default:
                    _log.Warn("Script", $"Unknown script language: {language}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Script", $"Script execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a PowerShell script via process spawn.
    /// </summary>
    private void RunPowerShell(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null)
        {
            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (!string.IsNullOrEmpty(output))
                _log.Debug("Script", $"Output: {output.Trim()}");
            if (!string.IsNullOrEmpty(error))
                _log.Warn("Script", $"Error: {error.Trim()}");
        }
    }
}

using System.Windows.Automation;

namespace AutomationTool.Services;

/// <summary>
/// Executes automation actions (click, sendkeys, script).
/// </summary>
public class ActionExecutor
{
    private readonly LogService _log;

    public ActionExecutor(LogService log)
    {
        _log = log;
    }

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

    public void SendKeys(string keys)
    {
        // Parse key string like "Tab, Enter, Ctrl+A"
        var keyList = keys.Split(',', StringSplitOptions.TrimEntries);
        foreach (var key in keyList)
        {
            SendKey(key);
            Thread.Sleep(50);
        }
    }

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

    private void RunPowerShell(string script)
    {
        // Run PowerShell script via process
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

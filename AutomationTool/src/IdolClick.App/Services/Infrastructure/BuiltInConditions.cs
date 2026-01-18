namespace IdolClick.Services.Infrastructure;

/// <summary>
/// Built-in condition evaluators for common preconditions.
/// </summary>
public static class BuiltInConditions
{
    /// <summary>
    /// Check if the system has been idle for a specified duration.
    /// </summary>
    public class SystemIdleCondition : IConditionEvaluator
    {
        public string Id => "system-idle";
        public string Name => "System Idle";

        public Task<bool> EvaluateAsync(Models.Rule rule, Dictionary<string, object>? parameters = null)
        {
            var idleMs = parameters?.TryGetValue("idleMs", out var val) == true && val is int ms ? ms : 5000;
            var lastInput = GetLastInputTime();
            return Task.FromResult(lastInput > idleMs);
        }

        private static int GetLastInputTime()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref info))
            {
                return Environment.TickCount - (int)info.dwTime;
            }
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
    }

    /// <summary>
    /// Check if a specific window is in the foreground.
    /// </summary>
    public class WindowFocusedCondition : IConditionEvaluator
    {
        public string Id => "window-focused";
        public string Name => "Window Focused";

        public Task<bool> EvaluateAsync(Models.Rule rule, Dictionary<string, object>? parameters = null)
        {
            var targetTitle = parameters?.TryGetValue("windowTitle", out var val) == true ? val?.ToString() : rule.WindowTitle;
            if (string.IsNullOrEmpty(targetTitle)) return Task.FromResult(true);

            var foregroundWindow = Win32.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return Task.FromResult(false);

            var title = GetWindowTitle(foregroundWindow);
            return Task.FromResult(title?.Contains(targetTitle, StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string? GetWindowTitle(IntPtr hwnd)
        {
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return null;

            var builder = new System.Text.StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    }

    /// <summary>
    /// Check if a specific process is running.
    /// </summary>
    public class ProcessRunningCondition : IConditionEvaluator
    {
        public string Id => "process-running";
        public string Name => "Process Running";

        public Task<bool> EvaluateAsync(Models.Rule rule, Dictionary<string, object>? parameters = null)
        {
            var processName = parameters?.TryGetValue("processName", out var val) == true ? val?.ToString() : rule.TargetApp;
            if (string.IsNullOrEmpty(processName)) return Task.FromResult(true);

            var processes = System.Diagnostics.Process.GetProcessesByName(processName.Replace(".exe", ""));
            return Task.FromResult(processes.Length > 0);
        }
    }

    /// <summary>
    /// Check if current time is within a specified window.
    /// </summary>
    public class TimeWindowCondition : IConditionEvaluator
    {
        public string Id => "time-window";
        public string Name => "Time Window";

        public Task<bool> EvaluateAsync(Models.Rule rule, Dictionary<string, object>? parameters = null)
        {
            var window = parameters?.TryGetValue("window", out var val) == true ? val?.ToString() : rule.TimeWindow;
            if (string.IsNullOrEmpty(window)) return Task.FromResult(true);

            // Parse "09:00-17:00" format
            var parts = window.Split('-');
            if (parts.Length != 2) return Task.FromResult(true);

            if (TimeOnly.TryParse(parts[0].Trim(), out var start) &&
                TimeOnly.TryParse(parts[1].Trim(), out var end))
            {
                var now = TimeOnly.FromDateTime(DateTime.Now);
                return Task.FromResult(now >= start && now <= end);
            }

            return Task.FromResult(true);
        }
    }
}

using System.Runtime.InteropServices;

namespace IdolClick.Services;

/// <summary>
/// Win32 API helpers for mouse/keyboard input with multi-monitor support.
/// </summary>
internal static class Win32
{
    // Virtual key codes
    public const ushort VK_TAB = 0x09;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;     // Alt
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_END = 0x23;
    public const ushort VK_HOME = 0x24;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_UP = 0x26;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_BACK = 0x08;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_F1 = 0x70;
    public const ushort VK_F2 = 0x71;
    public const ushort VK_F3 = 0x72;
    public const ushort VK_F4 = 0x73;
    public const ushort VK_F5 = 0x74;
    public const ushort VK_F6 = 0x75;
    public const ushort VK_F7 = 0x76;
    public const ushort VK_F8 = 0x77;
    public const ushort VK_F9 = 0x78;
    public const ushort VK_F10 = 0x79;
    public const ushort VK_F11 = 0x7A;
    public const ushort VK_F12 = 0x7B;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;  // For multi-monitor support
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    /// <summary>Move the mouse cursor to the specified screen coordinates (used by Hover action).</summary>
    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Multi-monitor support
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;   // Left edge of virtual screen
    private const int SM_YVIRTUALSCREEN = 77;   // Top edge of virtual screen  
    private const int SM_CXVIRTUALSCREEN = 78;  // Width of virtual screen
    private const int SM_CYVIRTUALSCREEN = 79;  // Height of virtual screen

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    /// <summary>
    /// Clicks at the specified screen coordinates with multi-monitor support.
    /// Coordinates are in virtual screen space (can be negative for monitors left of primary).
    /// </summary>
    public static void Click(int x, int y)
    {
        // Get virtual screen bounds (handles multi-monitor setups)
        int vsLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vsTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vsWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vsHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // Convert screen coordinates to normalized absolute coordinates (0-65535 range)
        // relative to the virtual desktop (all monitors combined)
        int normalizedX = (int)(((x - vsLeft) * 65535.0) / vsWidth);
        int normalizedY = (int)(((y - vsTop) * 65535.0) / vsHeight);

        // First move the cursor physically to ensure correct position
        SetCursorPos(x, y);
        Thread.Sleep(10);  // Small delay to ensure cursor is positioned

        var inputs = new[]
        {
            // Mouse down at current cursor position
            new INPUT 
            { 
                type = INPUT_MOUSE, 
                U = new InputUnion 
                { 
                    mi = new MOUSEINPUT 
                    { 
                        dx = normalizedX, 
                        dy = normalizedY, 
                        dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN
                    } 
                } 
            },
            // Mouse up
            new INPUT 
            { 
                type = INPUT_MOUSE, 
                U = new InputUnion 
                { 
                    mi = new MOUSEINPUT 
                    { 
                        dx = normalizedX, 
                        dy = normalizedY, 
                        dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTUP 
                    } 
                } 
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendKey(ushort vk)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Sends a Unicode character via SendInput using KEYEVENTF_UNICODE.
    /// Works for ALL characters including '.', '/', '@', ':', etc.
    /// Unlike SendKey (VK codes), this sends the actual character code.
    /// </summary>
    public static void SendChar(char c)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendKeyCombo(ushort[] modifiers, ushort mainKey)
    {
        var inputs = new List<INPUT>();

        // Press modifiers
        foreach (var mod in modifiers)
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = mod } } });

        // Press and release main key
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = mainKey } } });
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = mainKey, dwFlags = KEYEVENTF_KEYUP } } });

        // Release modifiers
        foreach (var mod in modifiers)
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = mod, dwFlags = KEYEVENTF_KEYUP } } });

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Sends a mouse wheel event. Positive delta = scroll up, negative = scroll down.
    /// Standard wheel click = 120 units.
    /// </summary>
    public static void MouseWheel(int delta)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL }
                }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FOCUS-PRESERVING CLICK
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Saves the current foreground window handle and cursor position.
    /// Call before performing any click that may steal focus.
    /// </summary>
    public static FocusState SaveFocusState()
    {
        GetCursorPos(out var cursorPos);
        return new FocusState
        {
            ForegroundWindow = GetForegroundWindow(),
            CursorX = cursorPos.X,
            CursorY = cursorPos.Y
        };
    }

    /// <summary>
    /// Restores the foreground window and cursor position captured by SaveFocusState.
    /// Uses AttachThreadInput to reliably restore focus even across processes.
    /// </summary>
    public static void RestoreFocusState(FocusState state)
    {
        if (state.ForegroundWindow == IntPtr.Zero) return;

        // Restore cursor position first (fast, no side effects)
        SetCursorPos(state.CursorX, state.CursorY);

        // Restore foreground window using thread-attach trick for reliability
        var currentFg = GetForegroundWindow();
        if (currentFg == state.ForegroundWindow) return; // Already correct

        try
        {
            var currentThreadId = GetCurrentThreadId();
            var fgThreadId = GetWindowThreadProcessId(currentFg, out _);
            var targetThreadId = GetWindowThreadProcessId(state.ForegroundWindow, out _);

            // Attach our thread to the current foreground thread to gain SetForegroundWindow rights
            bool attached1 = false, attached2 = false;
            if (currentThreadId != fgThreadId)
                attached1 = AttachThreadInput(currentThreadId, fgThreadId, true);
            if (currentThreadId != targetThreadId)
                attached2 = AttachThreadInput(currentThreadId, targetThreadId, true);

            SetForegroundWindow(state.ForegroundWindow);
            BringWindowToTop(state.ForegroundWindow);

            // Detach threads
            if (attached1) AttachThreadInput(currentThreadId, fgThreadId, false);
            if (attached2) AttachThreadInput(currentThreadId, targetThreadId, false);
        }
        catch
        {
            // Best-effort: try simple SetForegroundWindow as fallback
            SetForegroundWindow(state.ForegroundWindow);
        }
    }

    /// <summary>
    /// Performs a click at screen coordinates and immediately restores focus/cursor.
    /// The entire operation is designed to be imperceptibly fast.
    /// </summary>
    public static void ClickAndRestoreFocus(int x, int y)
    {
        var state = SaveFocusState();

        // Get virtual screen bounds (handles multi-monitor setups)
        int vsLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vsTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vsWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vsHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        int normalizedX = (int)(((x - vsLeft) * 65535.0) / vsWidth);
        int normalizedY = (int)(((y - vsTop) * 65535.0) / vsHeight);

        // Move cursor, click down, click up — all in a single SendInput batch for speed
        SetCursorPos(x, y);

        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = normalizedX,
                        dy = normalizedY,
                        dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN
                    }
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = normalizedX,
                        dy = normalizedY,
                        dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTUP
                    }
                }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        // Tiny delay to let the click register, then immediately restore
        Thread.Sleep(30);
        RestoreFocusState(state);
    }

    /// <summary>
    /// Captured focus state for save/restore operations.
    /// </summary>
    public class FocusState
    {
        public IntPtr ForegroundWindow;
        public int CursorX;
        public int CursorY;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WINDOW ACTIVATION (single-instance bring-to-front)
    // ═══════════════════════════════════════════════════════════════════════════════

    private const int SW_RESTORE = 9;
    private const uint FLASHW_ALL = 3;   // Flash both caption and taskbar
    private const uint FLASHW_TIMERNOFG = 12; // Flash until window comes to foreground

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Aggressively brings a window to the foreground, working around
    /// Windows restrictions that prevent background processes from stealing focus.
    /// Uses AttachThreadInput trick + FlashWindowEx as fallback.
    /// </summary>
    public static void ForceActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        // If minimized, restore first
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        // Try the AttachThreadInput trick to gain foreground rights
        var foreground = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var fgThreadId = GetWindowThreadProcessId(foreground, out _);
        var targetThreadId = GetWindowThreadProcessId(hWnd, out _);

        bool attachedFg = false, attachedTarget = false;
        try
        {
            if (currentThreadId != fgThreadId)
                attachedFg = AttachThreadInput(currentThreadId, fgThreadId, true);
            if (currentThreadId != targetThreadId)
                attachedTarget = AttachThreadInput(currentThreadId, targetThreadId, true);

            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
        }
        finally
        {
            if (attachedFg)  AttachThreadInput(currentThreadId, fgThreadId, false);
            if (attachedTarget) AttachThreadInput(currentThreadId, targetThreadId, false);
        }

        // If we still don't have foreground, flash the taskbar to attract attention
        if (GetForegroundWindow() != hWnd)
        {
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hWnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 5,
                dwTimeout = 0
            };
            FlashWindowEx(ref fi);
        }
    }

    // Hotkey registration
    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

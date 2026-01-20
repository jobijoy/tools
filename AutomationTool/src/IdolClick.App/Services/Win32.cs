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

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;  // For multi-monitor support
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

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

        var inputs = new[]
        {
            // Move mouse to absolute position on virtual desktop
            new INPUT 
            { 
                type = INPUT_MOUSE, 
                U = new InputUnion 
                { 
                    mi = new MOUSEINPUT 
                    { 
                        dx = normalizedX, 
                        dy = normalizedY, 
                        dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK 
                    } 
                } 
            },
            // Mouse down
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } },
            // Mouse up
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } }
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

    // Hotkey registration
    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

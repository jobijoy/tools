using System.Runtime.InteropServices;

namespace VsCodeAllowClicker.App;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly Action _onHotkey;
    private bool _registered;
    private int _hotkeyId = 1;

    public HotkeyWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    public bool TryRegister(HotkeyConfig cfg)
    {
        try
        {
            var modifiers = 0u;
            foreach (var m in cfg.Modifiers ?? [])
            {
                modifiers |= ParseModifier(m);
            }

            var vk = ParseVirtualKey(cfg.Key);
            if (vk == 0)
            {
                return false;
            }

            _registered = RegisterHotKey(Handle, _hotkeyId, modifiers, vk);
            return _registered;
        }
        catch
        {
            return false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            _onHotkey();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered)
        {
            _ = UnregisterHotKey(Handle, _hotkeyId);
        }

        DestroyHandle();
    }

    private static uint ParseModifier(string? modifier)
    {
        if (modifier is null) return 0;
        return modifier.Trim().ToUpperInvariant() switch
        {
            "ALT" => 0x0001,
            "CTRL" or "CONTROL" => 0x0002,
            "SHIFT" => 0x0004,
            "WIN" or "WINDOWS" => 0x0008,
            _ => 0
        };
    }

    private static uint ParseVirtualKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        var k = key.Trim();

        // Letters and digits
        if (k.Length == 1)
        {
            var ch = char.ToUpperInvariant(k[0]);
            if (ch >= 'A' && ch <= 'Z') return ch;
            if (ch >= '0' && ch <= '9') return ch;
        }

        // Minimal named keys
        return k.ToUpperInvariant() switch
        {
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => 0
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

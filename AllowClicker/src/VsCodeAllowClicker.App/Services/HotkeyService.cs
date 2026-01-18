using System.Runtime.InteropServices;

namespace VsCodeAllowClicker.App.Services;

/// <summary>
/// Registers a global hotkey and fires callback when pressed.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HotkeyWindow _window;

    public HotkeyService(string hotkey, Action callback)
    {
        _window = new HotkeyWindow(callback);
        if (!string.IsNullOrEmpty(hotkey))
            _window.Register(hotkey);
    }

    public void Dispose() => _window.Dispose();

    private sealed class HotkeyWindow : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;
        private readonly Action _callback;
        private bool _registered;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotkeyWindow(Action callback)
        {
            _callback = callback;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(0, 0);
            Show();
            Hide();
        }

        public bool Register(string hotkey)
        {
            // Parse "Ctrl+Alt+A" format
            var parts = hotkey.Split('+').Select(p => p.Trim().ToLowerInvariant()).ToList();
            uint mods = 0;
            uint vk = 0;

            foreach (var part in parts)
            {
                switch (part)
                {
                    case "ctrl" or "control": mods |= 0x0002; break;
                    case "alt": mods |= 0x0001; break;
                    case "shift": mods |= 0x0004; break;
                    case "win" or "windows": mods |= 0x0008; break;
                    default:
                        if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                            vk = (uint)char.ToUpperInvariant(part[0]);
                        else if (part.StartsWith("f") && int.TryParse(part[1..], out var fn))
                            vk = (uint)(0x70 + fn - 1); // F1 = 0x70
                        break;
                }
            }

            if (vk == 0) return false;
            _registered = RegisterHotKey(Handle, HOTKEY_ID, mods, vk);
            return _registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
                _callback();
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (_registered) UnregisterHotKey(Handle, HOTKEY_ID);
            base.Dispose(disposing);
        }
    }
}

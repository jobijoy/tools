using System.Windows;
using System.Windows.Interop;
using IdolClick.UI;

namespace IdolClick.Services;

/// <summary>
/// Manages global hotkey registration for toggling automation.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int HOTKEY_ID = 1;
    private const int WM_HOTKEY = 0x0312;
    
    private MainWindow? _mainWindow;
    private HwndSource? _hwndSource;

    /// <summary>
    /// Associates the main window and registers the global hotkey.
    /// </summary>
    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        RegisterHotkey();
    }

    /// <summary>
    /// Registers the global hotkey from configuration (e.g., "Ctrl+Alt+T").
    /// </summary>
    private void RegisterHotkey()
    {
        if (_mainWindow == null) return;

        var cfg = App.Config.GetConfig();
        var hotkey = cfg.Settings.ToggleHotkey;

        // Parse hotkey string like "Ctrl+Alt+T"
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
                    break;
            }
        }

        if (vk == 0) return;

        var helper = new WindowInteropHelper(_mainWindow);
        _hwndSource = HwndSource.FromHwnd(helper.EnsureHandle());
        _hwndSource.AddHook(WndProc);

        Win32.RegisterHotKey(helper.Handle, HOTKEY_ID, mods, vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Toggles main window: minimize if visible, restore if minimized.
    /// </summary>
    private void ToggleWindow()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Minimized;
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.Focus();
        }
    }

    public void Dispose()
    {
        if (_mainWindow != null)
        {
            var helper = new WindowInteropHelper(_mainWindow);
            Win32.UnregisterHotKey(helper.Handle, HOTKEY_ID);
        }
        _hwndSource?.RemoveHook(WndProc);
    }
}

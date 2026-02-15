using System.Windows;
using System.Windows.Interop;
using IdolClick.UI;

namespace IdolClick.Services;

/// <summary>
/// Manages global hotkey registration for toggling automation and emergency stop.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_KILLSWITCH = 2;
    private const int WM_HOTKEY = 0x0312;
    
    private MainWindow? _mainWindow;
    private HwndSource? _hwndSource;

    /// <summary>
    /// Raised when the kill switch hotkey is pressed. Subscribers should
    /// immediately stop all automation (engine + running flows).
    /// </summary>
    public event Action? OnKillSwitchActivated;

    /// <summary>
    /// Associates the main window and registers all global hotkeys.
    /// </summary>
    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        RegisterHotkeys();
    }

    /// <summary>
    /// Registers the toggle hotkey and kill switch hotkey from configuration.
    /// </summary>
    private void RegisterHotkeys()
    {
        if (_mainWindow == null) return;

        var cfg = App.Config.GetConfig();
        var helper = new WindowInteropHelper(_mainWindow);
        _hwndSource = HwndSource.FromHwnd(helper.EnsureHandle());
        _hwndSource.AddHook(WndProc);

        // Register toggle hotkey (e.g., Ctrl+Alt+T)
        var (toggleMods, toggleVk) = ParseHotkey(cfg.Settings.ToggleHotkey);
        if (toggleVk != 0)
            Win32.RegisterHotKey(helper.Handle, HOTKEY_TOGGLE, toggleMods, toggleVk);

        // Register kill switch hotkey (e.g., Ctrl+Alt+Escape)
        var (killMods, killVk) = ParseHotkey(cfg.Settings.KillSwitchHotkey);
        if (killVk != 0)
            Win32.RegisterHotKey(helper.Handle, HOTKEY_KILLSWITCH, killMods, killVk);
    }

    /// <summary>
    /// Parses a hotkey string like "Ctrl+Alt+T" into modifier flags and virtual key code.
    /// </summary>
    private static (uint mods, uint vk) ParseHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return (0, 0);

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
                case "escape" or "esc": vk = 0x1B; break;
                case "f1": vk = 0x70; break;
                case "f2": vk = 0x71; break;
                case "f12": vk = 0x7B; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                        vk = (uint)char.ToUpperInvariant(part[0]);
                    break;
            }
        }

        return (mods, vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HOTKEY_TOGGLE:
                    ToggleWindow();
                    handled = true;
                    break;
                case HOTKEY_KILLSWITCH:
                    OnKillSwitchActivated?.Invoke();
                    handled = true;
                    break;
            }
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
            Win32.UnregisterHotKey(helper.Handle, HOTKEY_TOGGLE);
            Win32.UnregisterHotKey(helper.Handle, HOTKEY_KILLSWITCH);
        }
        _hwndSource?.RemoveHook(WndProc);
    }
}

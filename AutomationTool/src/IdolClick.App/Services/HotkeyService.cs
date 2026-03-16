using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
    private const int HOTKEY_CAPTURE = 3;
    private const int HOTKEY_ANNOTATION_TOGGLE = 4;
    private const int HOTKEY_REVIEW_SAVE = 5;
    private const int WM_HOTKEY = 0x0312;
    
    private MainWindow? _mainWindow;
    private HwndSource? _hwndSource;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private Win32.LowLevelKeyboardProc? _keyboardProc;
    private HotkeyBinding? _annotationBinding;
    private bool _annotationActive;

    /// <summary>
    /// Raised when the kill switch hotkey is pressed. Subscribers should
    /// immediately stop all automation (engine + running flows).
    /// </summary>
    public event Action? OnKillSwitchActivated;

    /// <summary>
    /// Raised when the capture hotkey is pressed.
    /// Subscribers can route the request to the active capture profile.
    /// </summary>
    public event Action? OnCaptureRequested;

    /// <summary>
    /// Raised when the annotation push-to-talk hotkey is pressed.
    /// </summary>
    public event Action? OnCaptureAnnotationPressed;

    /// <summary>
    /// Raised when the annotation push-to-talk hotkey is released.
    /// </summary>
    public event Action? OnCaptureAnnotationReleased;

    /// <summary>
    /// Raised when the rolling review buffer should be saved.
    /// </summary>
    public event Action? OnReviewBufferSaveRequested;

    /// <summary>
    /// Associates the main window and registers all global hotkeys.
    /// </summary>
    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        RegisterHotkeys();
    }

    public void ReloadHotkeys()
    {
        UnregisterHotkeys();
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
        _hwndSource ??= HwndSource.FromHwnd(helper.EnsureHandle());
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.AddHook(WndProc);

        // Register toggle hotkey (e.g., Ctrl+Alt+T)
        var (toggleMods, toggleVk) = ParseHotkey(cfg.Settings.ToggleHotkey);
        if (toggleVk != 0)
            Win32.RegisterHotKey(helper.Handle, HOTKEY_TOGGLE, toggleMods, toggleVk);

        // Register kill switch hotkey (e.g., Ctrl+Alt+Escape)
        var (killMods, killVk) = ParseHotkey(cfg.Settings.KillSwitchHotkey);
        if (killVk != 0)
            Win32.RegisterHotKey(helper.Handle, HOTKEY_KILLSWITCH, killMods, killVk);

        // Register capture hotkey (e.g., Ctrl+Alt+S)
        if (cfg.Capture.HotkeyEnabled)
        {
            var (captureMods, captureVk) = ParseHotkey(cfg.Capture.Hotkey);
            if (captureVk != 0)
                Win32.RegisterHotKey(helper.Handle, HOTKEY_CAPTURE, captureMods, captureVk);
        }

        if (cfg.Review.Enabled)
        {
            var (reviewMods, reviewVk) = ParseHotkey(cfg.Review.SaveBufferHotkey);
            if (reviewVk != 0)
                Win32.RegisterHotKey(helper.Handle, HOTKEY_REVIEW_SAVE, reviewMods, reviewVk);
        }

        _annotationBinding = null;
        if (cfg.Capture.AnnotationHotkeyEnabled)
        {
            var (annotationMods, annotationVk) = ParseHotkey(cfg.Capture.AnnotationHotkey);
            if (annotationVk != 0)
            {
                _annotationBinding = new HotkeyBinding(annotationMods, annotationVk);
                InstallKeyboardHook();
            }
        }
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
                case HOTKEY_CAPTURE:
                    OnCaptureRequested?.Invoke();
                    handled = true;
                    break;
                case HOTKEY_REVIEW_SAVE:
                    OnReviewBufferSaveRequested?.Invoke();
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
        UnregisterHotkeys();
        _hwndSource?.RemoveHook(WndProc);
        if (_keyboardHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
            return;

        _keyboardProc = KeyboardHookCallback;
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, Win32.GetModuleHandle(null), 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _annotationBinding != null)
        {
            var keyboardData = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            var message = wParam.ToInt32();

            if ((message == Win32.WM_KEYDOWN || message == Win32.WM_SYSKEYDOWN) &&
                keyboardData.vkCode == _annotationBinding.VirtualKey &&
                !_annotationActive &&
                AreAnnotationModifiersPressed(_annotationBinding.Modifiers))
            {
                _annotationActive = true;
                OnCaptureAnnotationPressed?.Invoke();
            }
            else if ((message == Win32.WM_KEYUP || message == Win32.WM_SYSKEYUP) &&
                     keyboardData.vkCode == _annotationBinding.VirtualKey &&
                     _annotationActive)
            {
                _annotationActive = false;
                OnCaptureAnnotationReleased?.Invoke();
            }
        }

        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool AreAnnotationModifiersPressed(uint modifiers)
    {
        bool ctrlRequired = (modifiers & 0x0002) != 0;
        bool altRequired = (modifiers & 0x0001) != 0;
        bool shiftRequired = (modifiers & 0x0004) != 0;
        bool winRequired = (modifiers & 0x0008) != 0;

        bool ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool altPressed = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool winPressed = (Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows;

        return (!ctrlRequired || ctrlPressed)
            && (!altRequired || altPressed)
            && (!shiftRequired || shiftPressed)
            && (!winRequired || winPressed);
    }

    private void UnregisterHotkeys()
    {
        if (_mainWindow == null)
            return;

        var helper = new WindowInteropHelper(_mainWindow);
        Win32.UnregisterHotKey(helper.Handle, HOTKEY_TOGGLE);
        Win32.UnregisterHotKey(helper.Handle, HOTKEY_KILLSWITCH);
        Win32.UnregisterHotKey(helper.Handle, HOTKEY_CAPTURE);
        Win32.UnregisterHotKey(helper.Handle, HOTKEY_REVIEW_SAVE);
    }

    private sealed record HotkeyBinding(uint Modifiers, uint VirtualKey);
}

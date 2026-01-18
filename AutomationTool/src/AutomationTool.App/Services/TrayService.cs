using System.Windows;
using System.Windows.Interop;
using AutomationTool.UI;
using Hardcodet.Wpf.TaskbarNotification;

namespace AutomationTool.Services;

/// <summary>
/// System tray icon and global hotkey management.
/// </summary>
public class TrayService : IDisposable
{
    private TaskbarIcon? _tray;
    private MainWindow? _mainWindow;
    private HwndSource? _hwndSource;
    private const int HOTKEY_ID = 1;

    public TrayService()
    {
        Application.Current.Dispatcher.Invoke(InitializeTray);
    }

    private void InitializeTray()
    {
        _tray = new TaskbarIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            ToolTipText = "Automation Tool",
            Visibility = Visibility.Visible
        };

        // Context menu
        var menu = new System.Windows.Controls.ContextMenu();
        
        var toggleItem = new System.Windows.Controls.MenuItem { Header = "Toggle Automation" };
        toggleItem.Click += (s, e) => _mainWindow?.Toggle();
        menu.Items.Add(toggleItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show Control Panel" };
        showItem.Click += (s, e) => ShowMainWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        _tray.ContextMenu = menu;
        _tray.TrayMouseDoubleClick += (s, e) => ShowMainWindow();
    }

    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        RegisterHotkey();
    }

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
        if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID) // WM_HOTKEY
        {
            _mainWindow?.Toggle();
            ShowMainWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ShowBalloon(string title, string message)
    {
        _tray?.ShowBalloonTip(title, message, BalloonIcon.Info);
    }

    public void Dispose()
    {
        if (_mainWindow != null)
        {
            var helper = new WindowInteropHelper(_mainWindow);
            Win32.UnregisterHotKey(helper.Handle, HOTKEY_ID);
        }
        _hwndSource?.RemoveHook(WndProc);
        _tray?.Dispose();
    }
}

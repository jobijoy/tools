using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using IdolClick.UI;
using Hardcodet.Wpf.TaskbarNotification;

namespace IdolClick.Services;

/// <summary>
/// Manages the system tray icon, context menu, and global hotkey registration.
/// </summary>
/// <remarks>
/// <para>Provides:</para>
/// <list type="bullet">
///   <item>System tray icon with context menu (Toggle, Show, Exit)</item>
///   <item>Double-click tray to show main window</item>
///   <item>Global hotkey (default Ctrl+Alt+T) for window toggle</item>
///   <item>Balloon notifications</item>
/// </list>
/// </remarks>
public class TrayService : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // CONSTANTS
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>Win32 hotkey identifier.</summary>
    private const int HOTKEY_ID = 1;
    
    /// <summary>Win32 WM_HOTKEY message constant.</summary>
    private const int WM_HOTKEY = 0x0312;
    
    // ═══════════════════════════════════════════════════════════════════════════════
    // STATE
    // ═══════════════════════════════════════════════════════════════════════════════
    
    private TaskbarIcon? _tray;
    private MainWindow? _mainWindow;
    private HwndSource? _hwndSource;

    // ═══════════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Initializes the tray service and creates the tray icon.
    /// </summary>
    public TrayService()
    {
        Application.Current.Dispatcher.Invoke(InitializeTray);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // INITIALIZATION
    // ═══════════════════════════════════════════════════════════════════════════════

    private void InitializeTray()
    {
        _tray = new TaskbarIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "Idol Click",
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

    /// <summary>
    /// Loads the application icon from embedded PNG resource.
    /// </summary>
    /// <returns>Application icon, or system default on failure.</returns>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            // Load PNG from resources and convert to Icon
            var uri = new Uri("pack://application:,,,/Assets/idol-click.png", UriKind.Absolute);
            var bitmap = new BitmapImage(uri);
            
            // Convert to System.Drawing.Bitmap
            using var memoryStream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(memoryStream);
            memoryStream.Position = 0;
            
            using var drawingBitmap = new System.Drawing.Bitmap(memoryStream);
            
            // Resize to 32x32 for tray icon
            using var resized = new System.Drawing.Bitmap(drawingBitmap, new System.Drawing.Size(32, 32));
            var hIcon = resized.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }
        catch
        {
            // Fallback to system icon
            return System.Drawing.SystemIcons.Application;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // MAIN WINDOW INTEGRATION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Associates the main window and registers the global hotkey.
    /// </summary>
    /// <param name="window">Main application window.</param>
    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        RegisterHotkey();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // GLOBAL HOTKEY
    // ═══════════════════════════════════════════════════════════════════════════════

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

    /// <summary>
    /// Window procedure hook to intercept WM_HOTKEY messages.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleWindowVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WINDOW MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Toggles main window visibility: hide if visible, show if hidden.
    /// </summary>
    private void ToggleWindowVisibility()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
        {
            // Window is visible - hide to tray
            _mainWindow.Hide();
        }
        else
        {
            // Window is hidden or minimized - show and focus
            ShowMainWindow();
        }
    }

    /// <summary>
    /// Shows the main window, restores from minimized state, and activates it.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;  // Force to front
        _mainWindow.Topmost = false; // Reset
        _mainWindow.Focus();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PUBLIC METHODS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Displays a balloon tooltip notification from the tray icon.
    /// </summary>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification message body.</param>
    public void ShowBalloon(string title, string message)
    {
        _tray?.ShowBalloonTip(title, message, BalloonIcon.Info);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DISPOSAL
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Unregisters the hotkey and disposes the tray icon.
    /// </summary>
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

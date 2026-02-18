using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using IdolClick.Models;

namespace IdolClick.UI;

/// <summary>
/// Mode selection screen shown on first launch (or when <see cref="GlobalSettings.SkipHomeScreen"/> is false).
/// Lets the user pick Classic, Agent, or Teach — only the services for that mode are loaded.
/// </summary>
public partial class HomeWindow : Window
{
    public HomeWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{Services.ServiceHost.Version}";
        SourceInitialized += (_, _) => EnableResizeBorder();
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        var mode = Enum.Parse<AppMode>(tag);

        if (RememberCheck.IsChecked == true)
        {
            var config = App.Config.GetConfig();
            config.Settings.Mode = mode;
            config.Settings.SkipHomeScreen = true;
            App.Config.SaveConfig(config);
        }

        ((App)Application.Current).LaunchMode(mode);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        var mode = Enum.Parse<AppMode>(tag);

        if (RememberCheck.IsChecked == true)
        {
            var config = App.Config.GetConfig();
            config.Settings.Mode = mode;
            config.Settings.SkipHomeScreen = true;
            App.Config.SaveConfig(config);
        }

        ((App)Application.Current).LaunchMode(mode);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Adapt card layout: switch from 3 columns to 1 when the window is narrow.
    /// </summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CardsGrid == null) return;
        CardsGrid.Columns = ActualWidth < 600 ? 1 : 3;
    }

    // ── Native resize for borderless window ─────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_SYSCOMMAND = 0x0112;

    private void EnableResizeBorder()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            var pt = PointFromScreen(new Point(
                (short)(lParam.ToInt32() & 0xFFFF),
                (short)(lParam.ToInt32() >> 16)));

            const int grip = 8;
            bool left = pt.X < grip, right = pt.X > ActualWidth - grip;
            bool top = pt.Y < grip, bottom = pt.Y > ActualHeight - grip;

            if (top && left) { handled = true; return (IntPtr)13; }      // HTTOPLEFT
            if (top && right) { handled = true; return (IntPtr)14; }     // HTTOPRIGHT
            if (bottom && left) { handled = true; return (IntPtr)16; }   // HTBOTTOMLEFT
            if (bottom && right) { handled = true; return (IntPtr)17; }  // HTBOTTOMRIGHT
            if (left) { handled = true; return (IntPtr)10; }             // HTLEFT
            if (right) { handled = true; return (IntPtr)11; }            // HTRIGHT
            if (top) { handled = true; return (IntPtr)12; }              // HTTOP
            if (bottom) { handled = true; return (IntPtr)15; }           // HTBOTTOM
        }
        return IntPtr.Zero;
    }
}

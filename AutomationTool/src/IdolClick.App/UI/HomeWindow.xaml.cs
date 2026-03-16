using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using IdolClick.Models;

namespace IdolClick.UI;

/// <summary>
/// Welcome launcher for IdolClick's utility families.
/// Lets the user choose a workspace or continue with the last-used utility.
/// </summary>
public partial class HomeWindow : Window
{
    private AppMode _lastMode;

    public HomeWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{Services.ServiceHost.Version}";
        _lastMode = App.Config.GetConfig().Settings.Mode;
        UpdateLastUsedUi();
        SourceInitialized += (_, _) => EnableResizeBorder();
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        var mode = Enum.Parse<AppMode>(tag);
        LaunchSelectedMode(mode);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        var mode = Enum.Parse<AppMode>(tag);
        LaunchSelectedMode(mode);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        LaunchSelectedMode(_lastMode);
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
        CardsGrid.Columns = ActualWidth < 700 ? 1 : ActualWidth < 1040 ? 2 : 3;
    }

    private void LaunchSelectedMode(AppMode mode)
    {
        var config = App.Config.GetConfig();
        config.Settings.Mode = mode;
        config.Settings.SkipHomeScreen = false;
        App.Config.SaveConfig(config);

        ((App)Application.Current).LaunchMode(mode);
    }

    private void UpdateLastUsedUi()
    {
        LastUsedText.Text = $"Last used: {GetModeTitle(_lastMode)}";
        ContinueButton.Content = $"Continue with {GetModeTitle(_lastMode)}  →";

        LastUsedClassicBadge.Visibility = _lastMode == AppMode.Classic ? Visibility.Visible : Visibility.Collapsed;
        LastUsedAgentBadge.Visibility = _lastMode == AppMode.Agent ? Visibility.Visible : Visibility.Collapsed;
        LastUsedTeachBadge.Visibility = _lastMode == AppMode.Teach ? Visibility.Visible : Visibility.Collapsed;
        LastUsedCaptureBadge.Visibility = _lastMode == AppMode.Capture ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetModeTitle(AppMode mode) => mode switch
    {
        AppMode.Agent => "Reason",
        AppMode.Teach => "Teach",
        AppMode.Capture => "Capture",
        _ => "Instinct"
    };

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

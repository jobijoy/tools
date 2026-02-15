using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IdolClick.UI;

/// <summary>
/// A transparent, click-through overlay that displays an expanding concentric circle
/// "radar pulse" animation at the point where IdolClick performed a click.
/// </summary>
/// <remarks>
/// <para>The overlay is fully non-interactive: WS_EX_TRANSPARENT makes mouse events
/// pass through to the window underneath, so it never steals focus or intercepts input.</para>
/// <para>Usage: call <see cref="ShowAt"/> from any thread — it marshals to the UI thread,
/// plays the animation, and auto-closes.</para>
/// </remarks>
public partial class ClickRadarOverlay : Window
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Number of concentric rings in the radar pulse.</summary>
    private const int RingCount = 3;

    /// <summary>Maximum radius the outermost ring expands to (pixels).</summary>
    private const double MaxRadius = 45;

    /// <summary>Duration of the full expand+fade animation.</summary>
    private static readonly Duration AnimDuration = new(TimeSpan.FromMilliseconds(500));

    /// <summary>Stagger delay between successive rings.</summary>
    private static readonly TimeSpan RingStagger = TimeSpan.FromMilliseconds(70);

    /// <summary>Ring stroke color — a calm blue that matches the app accent.</summary>
    private static readonly Color RingColor = Color.FromRgb(0, 120, 212); // #0078D4

    /// <summary>Optional center dot color.</summary>
    private static readonly Color DotColor = Color.FromRgb(16, 185, 129); // #10B981 accent green

    // ═══════════════════════════════════════════════════════════════════════════════
    // WIN32 — MAKE WINDOW CLICK-THROUGH
    // ═══════════════════════════════════════════════════════════════════════════════

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ═══════════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════════════════════

    public ClickRadarOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Make fully click-through at the Win32 level
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shows the radar pulse animation centered on the given screen coordinates.
    /// Safe to call from any thread.
    /// </summary>
    /// <param name="screenX">Screen X coordinate (pixels).</param>
    /// <param name="screenY">Screen Y coordinate (pixels).</param>
    public static void Pulse(int screenX, int screenY)
    {
        var app = Application.Current;
        if (app == null) return;

        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var overlay = new ClickRadarOverlay();
                overlay.ShowAt(screenX, screenY);
            }
            catch
            {
                // Never let a visual flicker crash the automation engine
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ANIMATION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Positions the overlay centered on the screen point and kicks off the animation.
    /// </summary>
    private void ShowAt(int screenX, int screenY)
    {
        // Position window so its center is at the click point
        // Account for DPI: WPF uses device-independent units
        var dpiScale = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
        var dpiX = dpiScale.DpiScaleX > 0 ? dpiScale.DpiScaleX : 1.0;
        var dpiY = dpiScale.DpiScaleY > 0 ? dpiScale.DpiScaleY : 1.0;

        var windowSize = (MaxRadius * 2) + 20; // padding
        Width = windowSize;
        Height = windowSize;

        Left = (screenX / dpiX) - (windowSize / 2);
        Top = (screenY / dpiY) - (windowSize / 2);

        var centerX = windowSize / 2;
        var centerY = windowSize / 2;

        // Center dot
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(DotColor),
            Opacity = 1.0
        };
        Canvas.SetLeft(dot, centerX - 4);
        Canvas.SetTop(dot, centerY - 4);
        RadarCanvas.Children.Add(dot);

        // Concentric expanding rings
        int completedAnimations = 0;
        int totalAnimations = RingCount + 1; // rings + dot

        for (int i = 0; i < RingCount; i++)
        {
            var ring = new Ellipse
            {
                Width = 0,
                Height = 0,
                Stroke = new SolidColorBrush(RingColor),
                StrokeThickness = 2.0 - (i * 0.4), // thinner outer rings
                Fill = Brushes.Transparent,
                Opacity = 0.9
            };

            Canvas.SetLeft(ring, centerX);
            Canvas.SetTop(ring, centerY);
            RadarCanvas.Children.Add(ring);

            var targetSize = MaxRadius * 2 * ((i + 1.0) / RingCount);
            var delay = RingStagger * i;

            // Size animation (expand)
            var sizeAnim = new DoubleAnimation(0, targetSize, AnimDuration)
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            // Position animation (keep centered)
            var posAnim = new DoubleAnimation(centerX, centerX - targetSize / 2, AnimDuration)
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            // Opacity animation (fade out)
            var opacityAnim = new DoubleAnimation(0.85, 0.0, AnimDuration)
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            opacityAnim.Completed += (s, e) =>
            {
                completedAnimations++;
                if (completedAnimations >= totalAnimations)
                    Close();
            };

            ring.BeginAnimation(WidthProperty, sizeAnim);
            ring.BeginAnimation(HeightProperty, sizeAnim);
            ring.BeginAnimation(Canvas.LeftProperty, posAnim);
            ring.BeginAnimation(Canvas.TopProperty, posAnim);
            ring.BeginAnimation(OpacityProperty, opacityAnim);
        }

        // Fade out the center dot slightly later
        var dotFade = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(350)))
        {
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        dotFade.Completed += (s, e) =>
        {
            completedAnimations++;
            if (completedAnimations >= totalAnimations)
                Close();
        };
        dot.BeginAnimation(OpacityProperty, dotFade);

        Show();
    }
}

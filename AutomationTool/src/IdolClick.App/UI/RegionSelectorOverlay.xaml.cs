using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdolClick.Services;
using WinForms = System.Windows.Forms;

namespace IdolClick.UI;

/// <summary>
/// Transparent overlay window for selecting screen regions.
/// </summary>
public partial class RegionSelectorOverlay : Window
{
    private Point _startPoint;
    private bool _isSelecting;
    private bool _hasSelection;

    public CapturedRegion? SelectedRegion { get; private set; }

    public RegionSelectorOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Ensure full screen coverage
        var screen = WinForms.Screen.PrimaryScreen;
        if (screen != null)
        {
            Left = screen.Bounds.Left;
            Top = screen.Bounds.Top;
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
        }

        Activate();
        Focus();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SelectedRegion = null;
                DialogResult = false;
                Close();
                break;

            case Key.Enter when _hasSelection:
                DialogResult = true;
                Close();
                break;
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startPoint = e.GetPosition(this);
            _isSelecting = true;
            _hasSelection = false;

            SelectionRect.Visibility = Visibility.Visible;
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            Canvas.SetLeft(SelectionRect, _startPoint.X);
            Canvas.SetTop(SelectionRect, _startPoint.Y);

            CaptureMouse();
        }
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var currentPos = e.GetPosition(this);
        CoordinatesText.Text = $"Position: ({(int)currentPos.X}, {(int)currentPos.Y})";

        if (!_isSelecting) return;

        var x = Math.Min(currentPos.X, _startPoint.X);
        var y = Math.Min(currentPos.Y, _startPoint.Y);
        var width = Math.Abs(currentPos.X - _startPoint.X);
        var height = Math.Abs(currentPos.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = width;
        SelectionRect.Height = height;

        // Update dimension label
        DimensionLabel.Visibility = Visibility.Visible;
        DimensionText.Text = $"{(int)width} × {(int)height}";
        Canvas.SetLeft(DimensionLabel, x + width + 10);
        Canvas.SetTop(DimensionLabel, y);

        // Keep label on screen
        if (x + width + DimensionLabel.ActualWidth + 20 > ActualWidth)
        {
            Canvas.SetLeft(DimensionLabel, x - DimensionLabel.ActualWidth - 10);
        }

        _hasSelection = width > 5 && height > 5;

        // Update selection color based on validity
        SelectionRect.Stroke = _hasSelection 
            ? new SolidColorBrush(Color.FromRgb(0, 170, 255)) 
            : new SolidColorBrush(Color.FromRgb(255, 100, 100));
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;

        _isSelecting = false;
        ReleaseMouseCapture();

        if (_hasSelection)
        {
            var currentPos = e.GetPosition(this);
            var x = (int)Math.Min(currentPos.X, _startPoint.X);
            var y = (int)Math.Min(currentPos.Y, _startPoint.Y);
            var width = (int)Math.Abs(currentPos.X - _startPoint.X);
            var height = (int)Math.Abs(currentPos.Y - _startPoint.Y);

            SelectedRegion = new CapturedRegion
            {
                X = x,
                Y = y,
                Width = width,
                Height = height
            };

            // Auto-confirm on mouse up
            DialogResult = true;
            Close();
        }
    }
}

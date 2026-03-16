using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using IdolClick.Services;

namespace IdolClick.UI;

public partial class SnapOrbWindow : Window
{
    private const double CollapsedWindowWidth = 128;
    private const double ExpandedWindowWidth = 560;
    private const double DefaultHorizontalMargin = 28;
    private const double DefaultVerticalMargin = 34;
    private const int DefaultIntervalSeconds = 30;
    private readonly ObservableCollection<CaptureGalleryItem> _items = [];
    private readonly System.Windows.Threading.DispatcherTimer _collapseTimer;
    private readonly System.Windows.Threading.DispatcherTimer _statusResetTimer;
    private readonly System.Windows.Threading.DispatcherTimer _intervalCaptureTimer;
    private CapturePreviewWindow? _previewWindow;
    private bool _isExpanded;
    private bool _annotationPressed;
    private bool _isApplyingPlacement;
    private bool _isCaptureInProgress;
    private bool _isLoadingIntervalState;
    private Point _collapsedOrigin;

    public SnapOrbWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        _collapseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver && !_annotationPressed)
                ExpandPanel(false);
        };

        _statusResetTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.4)
        };
        _statusResetTimer.Tick += (_, _) =>
        {
            _statusResetTimer.Stop();
            SetReadyState();
        };

        _intervalCaptureTimer = new System.Windows.Threading.DispatcherTimer();
        _intervalCaptureTimer.Tick += OnIntervalCaptureTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Width = CollapsedWindowWidth;
        PanelColumn.Width = new GridLength(0);
        GalleryPanel.Opacity = 0;
        Topmost = true;
        ApplyPlacementFromSettings();
        TilesListBox.ItemsSource = _items;
        TimelineListView.ItemsSource = _items;
        LoadIntervalCaptureSettings();
        App.SnapCapture.CaptureCompleted += OnCaptureCompleted;
        if (App.CaptureAnnotations.IsAvailable)
        {
            AnnotationButton.Visibility = Visibility.Visible;
            App.CaptureAnnotations.AnnotationAdded += OnAnnotationAdded;
            App.CaptureAnnotations.StatusChanged += OnAnnotationStatusChanged;
        }
        else
        {
            HoverTalkButton.IsEnabled = false;
            HoverTalkButton.Content = "Talk unavailable";
        }
        RefreshGallery();
        SetReadyState();
    }

    public void FocusOrb()
    {
        _collapseTimer.Stop();
        Show();
        Topmost = true;
        ExpandPanel(true);
    }

    public void ReloadPlacement()
    {
        _collapseTimer.Stop();
        _isExpanded = false;
        Width = CollapsedWindowWidth;
        PanelColumn.Width = new GridLength(0);
        GalleryPanel.Opacity = 0;
        Topmost = true;
        ApplyPlacementFromSettings();
        LoadIntervalCaptureSettings();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        ExpandPanel(true);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!IsMouseOver)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    private async void OrbButton_Click(object sender, RoutedEventArgs e)
    {
        await TriggerCaptureAsync();
    }

    private async Task TriggerCaptureAsync()
    {
        if (_isCaptureInProgress)
            return;

        var profile = App.SnapCapture.GetSelectedProfile();
        if (profile == null)
        {
            OrbStatusTitle.Text = "No profile";
            OrbStatusSubtitle.Text = "Create or select one in Capture";
            return;
        }

        _isCaptureInProgress = true;
        try
        {
            OrbStatusTitle.Text = "Snapping...";
            OrbStatusSubtitle.Text = profile.Name;
            BeginPulse();

            var result = await App.SnapCapture.CaptureSelectedProfileAsync();
            if (result == null)
            {
                OrbStatusTitle.Text = "No profile";
                OrbStatusSubtitle.Text = "Nothing was captured";
                return;
            }

            OrbStatusTitle.Text = result.Failures.Count == 0 ? "Snapped" : "Captured with warnings";
            OrbStatusSubtitle.Text = result.TimeLabel;
            OrbGlyphText.Text = result.Failures.Count == 0 ? "✓" : "!";
            _statusResetTimer.Stop();
            _statusResetTimer.Start();
        }
        finally
        {
            _isCaptureInProgress = false;
        }
    }

    private void OrbButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = false;

    private void ViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (TilesToggle == null || TimelineToggle == null || TilesScrollViewer == null || TimelineListView == null)
            return;

        if (sender == TilesToggle)
            TimelineToggle.IsChecked = false;
        else if (sender == TimelineToggle)
            TilesToggle.IsChecked = false;

        ApplyViewToggleState();
    }

    private void ApplyViewToggleState()
    {
        var showTiles = TilesToggle.IsChecked == true || TimelineToggle.IsChecked != true;
        TilesScrollViewer.Visibility = showTiles ? Visibility.Visible : Visibility.Collapsed;
        TimelineListView.Visibility = showTiles ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshGallery()
    {
        _items.Clear();
        foreach (var item in App.SnapCapture.ListRecentCaptureEvents(18).Select(CaptureGalleryItem.FromResult))
            _items.Add(item);

        var activeProfile = App.SnapCapture.GetSelectedProfile();
        ActiveProfileText.Text = activeProfile != null
            ? $"Active profile: {activeProfile.Name}"
            : "No active profile selected";
    }

    private void OnCaptureCompleted(CaptureEventResult result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshGallery();
            BeginPulse();
        });
    }

    private void ExpandPanel(bool expanded)
    {
        if (_isExpanded == expanded)
            return;

        _collapseTimer.Stop();
        _isExpanded = expanded;
        var targetWidth = expanded ? ExpandedWindowWidth : CollapsedWindowWidth;

        var widthAnimation = new GridLengthAnimation
        {
            From = PanelColumn.Width,
            To = expanded ? new GridLength(350) : new GridLength(0),
            Duration = new Duration(TimeSpan.FromMilliseconds(expanded ? 240 : 180))
        };
        PanelColumn.BeginAnimation(ColumnDefinition.WidthProperty, widthAnimation);

        var opacityAnimation = new DoubleAnimation
        {
            To = expanded ? 1.0 : 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(expanded ? 220 : 150))
        };
        GalleryPanel.BeginAnimation(OpacityProperty, opacityAnimation);

        Width = targetWidth;
        Left = _collapsedOrigin.X - (targetWidth - CollapsedWindowWidth);
        Top = _collapsedOrigin.Y;
    }

    private void ApplyPlacementFromSettings()
    {
        var capture = App.Config.GetConfig().Capture;
        var origin = ResolveCollapsedOrigin(capture);
        ApplyCollapsedOrigin(origin.X, origin.Y);
    }

    private Point ResolveCollapsedOrigin(Models.CaptureWorkspaceSettings capture)
    {
        var placement = string.IsNullOrWhiteSpace(capture.OrbPlacement) ? "BottomRight" : capture.OrbPlacement;
        if (capture.RememberOrbLocation && string.Equals(placement, "Custom", StringComparison.OrdinalIgnoreCase) &&
            capture.OrbLeft.HasValue && capture.OrbTop.HasValue)
        {
            return ClampCollapsedOrigin(capture.OrbLeft.Value, capture.OrbTop.Value);
        }

        var workArea = SystemParameters.WorkArea;
        var left = placement switch
        {
            "BottomLeft" or "TopLeft" => workArea.Left + DefaultHorizontalMargin,
            _ => workArea.Right - CollapsedWindowWidth - DefaultHorizontalMargin
        };
        var top = placement switch
        {
            "TopLeft" or "TopRight" => workArea.Top + DefaultVerticalMargin,
            _ => workArea.Bottom - Height - DefaultVerticalMargin
        };

        return ClampCollapsedOrigin(left, top);
    }

    private void ApplyCollapsedOrigin(double left, double top)
    {
        _isApplyingPlacement = true;
        try
        {
            _collapsedOrigin = new Point(left, top);
            var targetWidth = _isExpanded ? ExpandedWindowWidth : CollapsedWindowWidth;
            Left = _collapsedOrigin.X - (targetWidth - CollapsedWindowWidth);
            Top = _collapsedOrigin.Y;
        }
        finally
        {
            _isApplyingPlacement = false;
        }
    }

    private Point ClampCollapsedOrigin(double left, double top)
    {
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - CollapsedWindowWidth;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height;

        return new Point(
            Math.Min(Math.Max(left, minLeft), maxLeft),
            Math.Min(Math.Max(top, minTop), maxTop));
    }

    private void DragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var collapsedLeft = _collapsedOrigin.X + e.HorizontalChange;
        var top = _collapsedOrigin.Y + e.VerticalChange;
        var clamped = ClampCollapsedOrigin(collapsedLeft, top);

        _isApplyingPlacement = true;
        try
        {
            _collapsedOrigin = clamped;
            var targetWidth = _isExpanded ? ExpandedWindowWidth : CollapsedWindowWidth;
            Left = _collapsedOrigin.X - (targetWidth - CollapsedWindowWidth);
            Top = _collapsedOrigin.Y;
        }
        finally
        {
            _isApplyingPlacement = false;
        }
    }

    private void DragThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        PersistCurrentOrbLocation();
    }

    private void PersistCurrentOrbLocation()
    {
        if (_isApplyingPlacement)
            return;

        var cfg = App.Config.GetConfig();
        var capture = cfg.Capture;
        if (!capture.RememberOrbLocation)
            return;

        var origin = ClampCollapsedOrigin(_collapsedOrigin.X, _collapsedOrigin.Y);
        _collapsedOrigin = origin;
        capture.OrbPlacement = "Custom";
        capture.OrbLeft = origin.X;
        capture.OrbTop = origin.Y;
        App.Config.SaveConfig(cfg);
    }

    private void BeginPulse()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(360));
        OuterRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.22, duration) { AutoReverse = true });
        OuterRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.22, duration) { AutoReverse = true });
        InnerRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.12, duration) { AutoReverse = true });
        InnerRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.12, duration) { AutoReverse = true });
    }

    private void SetReadyState()
    {
        OrbGlyphText.Text = "●";
        OrbStatusTitle.Text = "Ready";
        OrbStatusSubtitle.Text = _intervalCaptureTimer.IsEnabled
            ? $"Interval every {GetSelectedIntervalSeconds()}s"
            : "Hover for gallery";
        ActiveProfileText.Text = App.SnapCapture.GetSelectedProfile() is { } profile
            ? $"Active profile: {profile.Name}"
            : "No active profile selected";
        var recentNote = App.CaptureAnnotations.ListRecentAnnotations(1).FirstOrDefault();
        LastAnnotationText.Text = recentNote != null
            ? $"Last note {recentNote.TimeLabel}: {Truncate(recentNote.Text, 72)}"
            : App.CaptureAnnotations.IsAvailable
                ? "Hold the mic to add a timestamped voice note."
                : "Voice notes are unavailable until voice input is configured.";
    }

    private void AnnotationButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!App.CaptureAnnotations.IsAvailable)
            return;

        _annotationPressed = true;
        AnnotationButton.Style = (Style)FindResource("MicButtonRecording");
        AnnotationButton.ToolTip = "Recording voice note... release to save";
        App.CaptureAnnotations.StartPushToTalk();
        e.Handled = true;
    }

    private async void AnnotationButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_annotationPressed)
            return;

        _annotationPressed = false;
        AnnotationButton.Style = (Style)FindResource("MicButtonTranscribing");
        AnnotationButton.IsEnabled = false;
        try
        {
            await App.CaptureAnnotations.StopPushToTalkAsync();
        }
        finally
        {
            ResetAnnotationButton();
        }
        e.Handled = true;
    }

    private async void AnnotationButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_annotationPressed && e.LeftButton != MouseButtonState.Pressed)
        {
            _annotationPressed = false;
            AnnotationButton.Style = (Style)FindResource("MicButtonTranscribing");
            AnnotationButton.IsEnabled = false;
            try
            {
                await App.CaptureAnnotations.StopPushToTalkAsync();
            }
            finally
            {
                ResetAnnotationButton();
            }
        }
    }

    private void OnAnnotationAdded(CaptureAnnotationEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LastAnnotationText.Text = $"Last note {entry.TimeLabel}: {Truncate(entry.Text, 72)}";
            OrbStatusTitle.Text = "Note saved";
            OrbStatusSubtitle.Text = entry.TimeLabel;
            _statusResetTimer.Stop();
            _statusResetTimer.Start();
        });
    }

    private void OnAnnotationStatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            OrbStatusTitle.Text = App.CaptureAnnotations.IsRecording ? "Listening" : "Voice note";
            OrbStatusSubtitle.Text = status;
        });
    }

    private void ResetAnnotationButton()
    {
        AnnotationButton.Style = (Style)FindResource("MicButton");
        AnnotationButton.ToolTip = "Hold to record a voice note";
        AnnotationButton.IsEnabled = true;
    }

    private void LoadIntervalCaptureSettings()
    {
        _isLoadingIntervalState = true;
        try
        {
            var capture = App.Config.GetConfig().Capture;
            SelectIntervalCaptureSeconds(capture.OrbIntervalSeconds > 0 ? capture.OrbIntervalSeconds : DefaultIntervalSeconds);
            IntervalCaptureToggle.IsChecked = capture.OrbIntervalCaptureEnabled;
            ApplyIntervalCaptureState();
        }
        finally
        {
            _isLoadingIntervalState = false;
        }
    }

    private void SelectIntervalCaptureSeconds(int seconds)
    {
        var target = IntervalCaptureComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), seconds.ToString(), StringComparison.OrdinalIgnoreCase));

        IntervalCaptureComboBox.SelectedItem = target ?? IntervalCaptureComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), DefaultIntervalSeconds.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private int GetSelectedIntervalSeconds()
    {
        return IntervalCaptureComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var seconds)
            && seconds > 0
            ? seconds
            : DefaultIntervalSeconds;
    }

    private void ApplyIntervalCaptureState()
    {
        var enabled = IntervalCaptureToggle.IsChecked == true;
        var intervalSeconds = GetSelectedIntervalSeconds();
        IntervalCaptureComboBox.IsEnabled = true;

        if (enabled)
        {
            _intervalCaptureTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
            if (!_intervalCaptureTimer.IsEnabled)
                _intervalCaptureTimer.Start();
            IntervalStatusText.Text = $"Auto-capturing every {intervalSeconds}s";
        }
        else
        {
            _intervalCaptureTimer.Stop();
            IntervalStatusText.Text = "Manual capture and push-to-talk";
        }

        if (!_isLoadingIntervalState)
            PersistIntervalCaptureSettings(enabled, intervalSeconds);
    }

    private void PersistIntervalCaptureSettings(bool enabled, int intervalSeconds)
    {
        var cfg = App.Config.GetConfig();
        cfg.Capture.OrbIntervalCaptureEnabled = enabled;
        cfg.Capture.OrbIntervalSeconds = intervalSeconds;
        App.Config.SaveConfig(cfg);
    }

    private async void OnIntervalCaptureTick(object? sender, EventArgs e)
    {
        if (_annotationPressed || _isCaptureInProgress)
            return;

        await TriggerCaptureAsync();
    }

    private void IntervalCaptureToggle_Changed(object sender, RoutedEventArgs e)
    {
        ApplyIntervalCaptureState();
        SetReadyState();
    }

    private void IntervalCaptureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCaptureComboBox.SelectedItem == null)
            return;

        ApplyIntervalCaptureState();
        SetReadyState();
    }

    private async void HoverCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await TriggerCaptureAsync();
    }

    private void HoverTalkButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AnnotationButton_PreviewMouseLeftButtonDown(sender, e);
    }

    private void HoverTalkButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        AnnotationButton_PreviewMouseLeftButtonUp(sender, e);
    }

    private void HoverTalkButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnnotationButton_MouseLeave(sender, e);
    }

    private void CaptureItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Selector)?.SelectedItem is not CaptureGalleryItem item)
            return;

        _previewWindow?.Close();
        _previewWindow = new CapturePreviewWindow(item.Result) { Owner = this };
        _previewWindow.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _collapseTimer.Stop();
        _intervalCaptureTimer.Stop();
        if (App.SnapCapture != null)
            App.SnapCapture.CaptureCompleted -= OnCaptureCompleted;
        if (App.CaptureAnnotations != null)
        {
            App.CaptureAnnotations.AnnotationAdded -= OnAnnotationAdded;
            App.CaptureAnnotations.StatusChanged -= OnAnnotationStatusChanged;
        }
        base.OnClosed(e);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}

public class CaptureGalleryItem
{
    public CaptureEventResult Result { get; init; } = new();
    public string ProfileName { get; init; } = "";
    public string TimeLabel { get; init; } = "";
    public string Summary { get; init; } = "";
    public string PreviewPath { get; init; } = "";
    public BitmapImage? PreviewImage { get; init; }

    public static CaptureGalleryItem FromResult(CaptureEventResult result)
    {
        BitmapImage? image = null;
        if (!string.IsNullOrWhiteSpace(result.PreviewPath) && File.Exists(result.PreviewPath))
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(result.PreviewPath, UriKind.Absolute);
            image.DecodePixelWidth = 240;
            image.EndInit();
            image.Freeze();
        }

        return new CaptureGalleryItem
        {
            Result = result,
            ProfileName = result.ProfileName,
            TimeLabel = result.TimeLabel,
            Summary = $"{result.Artifacts.Count} item(s) • {result.CapturedAt:MMM dd, HH:mm:ss.fff}",
            PreviewPath = result.PreviewPath,
            PreviewImage = image
        };
    }
}

public class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    public GridLength From { get; set; }
    public GridLength To { get; set; }

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var fromValue = From.Value;
        var toValue = To.Value;

        if (animationClock.CurrentProgress == null)
            return From;

        var progress = animationClock.CurrentProgress.Value;
        var current = ((toValue - fromValue) * progress) + fromValue;
        return new GridLength(current, GridUnitType.Pixel);
    }

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
}
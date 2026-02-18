using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using IdolClick.Services;

namespace IdolClick.UI;

/// <summary>
/// Demo Mode window — runs live demonstrations of IdolClick capabilities
/// using real Windows apps, with narrated progress.
/// </summary>
public partial class DemoWindow : Window
{
    private readonly ObservableCollection<DemoViewModel> _scenarios = [];
    private DemoService? _service;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public DemoWindow()
    {
        InitializeComponent();

        foreach (var scenario in DemoService.GetScenarios())
            _scenarios.Add(new DemoViewModel(scenario));

        ScenarioList.ItemsSource = _scenarios;
        UpdateSummary();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BUTTON HANDLERS
    // ═══════════════════════════════════════════════════════════════════════════

    private async void RunAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _scenarios) vm.IsSelected = true;
        await RunDemosAsync(_scenarios.ToList());
    }

    private async void RunSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _scenarios.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("No demos selected.\n\nUse the checkboxes to choose which demos to run.",
                "Demo Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunDemosAsync(selected);
    }

    private async Task RunDemosAsync(List<DemoViewModel> toRun)
    {
        if (_isRunning) return;

        _isRunning = true;
        RunAllBtn.IsEnabled = false;
        RunSelectedBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        LogOutput.Clear();

        foreach (var vm in toRun) vm.Reset();

        _cts = new CancellationTokenSource();
        _service = new DemoService(App.Log);

        _service.OnNarrate += OnNarrate;
        _service.OnScenarioStatusChanged += OnScenarioStatusChanged;

        var models = toRun.Select(vm => vm.Model).ToList();

        try
        {
            var result = await Task.Run(() => _service.RunAsync(models, _cts.Token));

            Dispatcher.Invoke(() =>
            {
                var icon = result.AllPassed ? "\u2705" : "\u274C";
                SummaryText.Text = $"{icon} {result.Passed}/{result.Scenarios.Count} demos passed";
                SummaryText.Foreground = result.AllPassed
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("ErrorBrush");
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("\n\u23F9 Demo cancelled by user.");
            SummaryText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog($"\n\u274C Error: {ex.Message}");
            SummaryText.Text = "Error";
        }
        finally
        {
            _service.OnNarrate -= OnNarrate;
            _service.OnScenarioStatusChanged -= OnScenarioStatusChanged;
            _isRunning = false;
            Dispatcher.Invoke(() =>
            {
                RunAllBtn.IsEnabled = true;
                RunSelectedBtn.IsEnabled = true;
                StopBtn.IsEnabled = false;
            });
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        AppendLog("\n\u23F9 Cancellation requested...");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(LogOutput.Text))
            Clipboard.SetText(LogOutput.Text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogOutput.Clear();

    // ═══════════════════════════════════════════════════════════════════════════
    // EVENT HANDLERS (from DemoService — background thread)
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnNarrate(string message) =>
        Dispatcher.BeginInvoke(() => AppendLog(message));

    private void OnScenarioStatusChanged(string scenarioId, DemoStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var vm = _scenarios.FirstOrDefault(s => s.Id == scenarioId);
            if (vm != null) vm.Status = status;
            UpdateSummary();
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private void AppendLog(string message)
    {
        LogOutput.AppendText(message + "\n");
        LogOutput.ScrollToEnd();
    }

    private void UpdateSummary()
    {
        var passed = _scenarios.Count(s => s.Status == DemoStatus.Passed);
        var failed = _scenarios.Count(s => s.Status is DemoStatus.Failed or DemoStatus.Error);
        var running = _scenarios.Count(s => s.Status == DemoStatus.Running);

        if (running > 0)
            SummaryText.Text = $"Running... ({passed} passed, {failed} failed)";
        else if (passed + failed == 0)
            SummaryText.Text = $"{_scenarios.Count} demos ready";
        else
            SummaryText.Text = $"{passed}/{_scenarios.Count} demos passed";
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// VIEW MODEL
// ═══════════════════════════════════════════════════════════════════════════════════

public class DemoViewModel : INotifyPropertyChanged
{
    private DemoStatus _status = DemoStatus.NotStarted;
    private bool _isSelected = true;
    private long _elapsedMs;

    public DemoScenario Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Description => Model.Description;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    public DemoStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    public string StatusIcon => _status switch
    {
        DemoStatus.NotStarted => "\u2B1C",
        DemoStatus.Running    => "\uD83D\uDD04",
        DemoStatus.Passed     => "\u2705",
        DemoStatus.Failed     => "\u274C",
        DemoStatus.Error      => "\uD83D\uDCA5",
        DemoStatus.Skipped    => "\u23ED",
        _ => "?"
    };

    public Brush RowBackground => _status switch
    {
        DemoStatus.Running => new SolidColorBrush(Color.FromArgb(25, 0, 120, 212)),
        DemoStatus.Passed  => new SolidColorBrush(Color.FromArgb(15, 76, 175, 80)),
        DemoStatus.Failed  => new SolidColorBrush(Color.FromArgb(15, 244, 67, 54)),
        DemoStatus.Error   => new SolidColorBrush(Color.FromArgb(15, 244, 67, 54)),
        _ => Brushes.Transparent
    };

    public string ElapsedText => _elapsedMs > 0 ? $"{_elapsedMs / 1000.0:F1}s" : "\u2014";

    public DemoViewModel(DemoScenario model) => Model = model;

    public void Reset()
    {
        Status = DemoStatus.NotStarted;
        _elapsedMs = 0;
        OnPropertyChanged(nameof(ElapsedText));
    }

    public void SetElapsed(long ms)
    {
        _elapsedMs = ms;
        OnPropertyChanged(nameof(ElapsedText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

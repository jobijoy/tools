using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using IdolClick.Models;
using IdolClick.Services;

namespace IdolClick.UI;

/// <summary>
/// Smoke test runner window — runs end-to-end agent integration tests
/// and displays results with live log output.  Supports multi-select, filtering
/// by difficulty, and running a subset of tests.
/// </summary>
public partial class SmokeTestWindow : Window
{
    private readonly ObservableCollection<SmokeTestViewModel> _tests = [];
    private SmokeTestService? _service;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public SmokeTestWindow()
    {
        InitializeComponent();

        // Load ALL tests (basic 5 + advanced 10)
        foreach (var test in SmokeTestService.GetAllTests())
            _tests.Add(new SmokeTestViewModel(test));

        TestListView.ItemsSource = _tests;

        // Select all by default
        foreach (var vm in _tests) vm.IsSelected = true;

        UpdateSummary();
        UpdateSelectionText();
    }

    // =======================================================================
    // BUTTON HANDLERS
    // =======================================================================

    private async void RunAll_Click(object sender, RoutedEventArgs e)
    {
        await RunTestsAsync(_tests.Select(vm => vm).ToList());
    }

    private async void RunSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _tests.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("No tests selected.\n\nUse the checkboxes or the Select buttons to choose tests.",
                "Smoke Tests", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunTestsAsync(selected);
    }

    private async Task RunTestsAsync(List<SmokeTestViewModel> testsToRun)
    {
        if (_isRunning) return;

        if (!App.Agent.IsConfigured)
        {
            MessageBox.Show(
                "Agent is not configured.\n\nGo to Settings -> Agent and set your LLM endpoint and API key first.",
                "Smoke Tests", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isRunning = true;
        RunAllBtn.IsEnabled = false;
        RunSelectedBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        LogOutput.Clear();

        // Reset only the tests we are about to run
        foreach (var vm in testsToRun)
            vm.Reset();

        _cts = new CancellationTokenSource();
        _service = new SmokeTestService(App.Agent, App.Log, App.Reports, App.Vision);

        _service.OnTestStatusChanged += OnTestStatusChanged;
        _service.OnLogMessage += OnLogMessage;

        var models = testsToRun.Select(vm => vm.Model).ToList();

        try
        {
            var suite = await Task.Run(() => _service.RunAllAsync(models, _cts.Token));

            // Allow queued BeginInvoke log messages to drain before saving
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Dispatcher.Invoke(() =>
            {
                var icon = suite.AllPassed ? "\u2705" : "\u274C";
                SummaryText.Text = $"{icon} {suite.PassedCount}/{suite.TotalCount} passed ({suite.TotalElapsedMs / 1000.0:F1}s)";
                SummaryText.Foreground = suite.AllPassed
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("ErrorBrush");

                AutoSaveLog();
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("\n\u23F9 Suite cancelled by user.");
            SummaryText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog($"\n\uD83D\uDCA5 Suite error: {ex.Message}");
            SummaryText.Text = "Error";
        }
        finally
        {
            _service.OnTestStatusChanged -= OnTestStatusChanged;
            _service.OnLogMessage -= OnLogMessage;
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

    // =======================================================================
    // SELECTION HANDLERS
    // =======================================================================

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _tests) vm.IsSelected = true;
        UpdateSelectionText();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _tests) vm.IsSelected = false;
        UpdateSelectionText();
    }

    private void FilterSimple_Click(object sender, RoutedEventArgs e) => SelectByDifficulty(TestDifficulty.Simple);
    private void FilterMedium_Click(object sender, RoutedEventArgs e) => SelectByDifficulty(TestDifficulty.Medium);
    private void FilterComplex_Click(object sender, RoutedEventArgs e) => SelectByDifficulty(TestDifficulty.Complex);

    private void SelectByDifficulty(TestDifficulty difficulty)
    {
        foreach (var vm in _tests)
            vm.IsSelected = vm.Model.Difficulty == difficulty;
        UpdateSelectionText();
    }

    // =======================================================================
    // LOG HANDLERS
    // =======================================================================

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(LogOutput.Text))
            Clipboard.SetText(LogOutput.Text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogOutput.Clear();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogOutput.Text)) return;
        SaveLogToFile();
    }

    private void SaveLogToFile()
    {
        if (string.IsNullOrWhiteSpace(LogOutput.Text)) return;

        var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        System.IO.Directory.CreateDirectory(logDir);
        var path = System.IO.Path.Combine(logDir, $"smoke_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        System.IO.File.WriteAllText(path, LogOutput.Text);
        AppendLog($"\n\uD83D\uDCC4 Log saved to {path}");
    }

    private void AutoSaveLog()
    {
        try { SaveLogToFile(); } catch { /* best-effort */ }
    }

    private void TestListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Future: show selected test''s captured logs in the log panel
    }

    // =======================================================================
    // EVENT HANDLERS (from SmokeTestService -- called from background thread)
    // =======================================================================

    private void OnTestStatusChanged(string testId, SmokeTestStatus status, SmokeTestResult? result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var vm = _tests.FirstOrDefault(t => t.Id == testId);
            if (vm != null)
            {
                vm.Status = status;
                if (result != null)
                    vm.SetResult(result);
            }
            UpdateSummary();
        });
    }

    private void OnLogMessage(string message)
    {
        Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    // =======================================================================
    // HELPERS
    // =======================================================================

    private void AppendLog(string message)
    {
        LogOutput.AppendText(message + "\n");
        LogOutput.ScrollToEnd();
    }

    private void UpdateSummary()
    {
        var passed = _tests.Count(t => t.Status == SmokeTestStatus.Passed);
        var failed = _tests.Count(t => t.Status is SmokeTestStatus.Failed or SmokeTestStatus.Error);
        var running = _tests.Count(t => t.Status == SmokeTestStatus.Running);

        if (running > 0)
            SummaryText.Text = $"Running... ({passed} passed, {failed} failed)";
        else if (passed + failed == 0)
            SummaryText.Text = $"{_tests.Count} tests ready";
        else
            SummaryText.Text = $"{passed}/{_tests.Count} passed";
    }

    private void UpdateSelectionText()
    {
        var count = _tests.Count(t => t.IsSelected);
        SelectionText.Text = $"{count} of {_tests.Count} selected";
    }
}

// =======================================================================
// VIEW MODEL
// =======================================================================

/// <summary>
/// View model wrapper for displaying a SmokeTest in the ListView with live
/// status updates, selection state, and difficulty badge colours.
/// </summary>
public class SmokeTestViewModel : INotifyPropertyChanged
{
    private SmokeTestStatus _status = SmokeTestStatus.NotStarted;
    private long _elapsedMs;
    private string _verificationSummary = "";
    private bool _isSelected = true;

    public SmokeTest Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Description => Model.Description;

    // ── Selection ────────────────────────────────────────────────────────
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    // ── Status ───────────────────────────────────────────────────────────
    public SmokeTestStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusIcon));
        }
    }

    public string StatusIcon => _status switch
    {
        SmokeTestStatus.NotStarted => "\u2B1C",
        SmokeTestStatus.Running    => "\uD83D\uDD04",
        SmokeTestStatus.Passed     => "\u2705",
        SmokeTestStatus.Failed     => "\u274C",
        SmokeTestStatus.Error      => "\uD83D\uDCA5",
        SmokeTestStatus.Skipped    => "\u23ED",
        _ => "?"
    };

    // ── Difficulty badge ─────────────────────────────────────────────────
    public string DifficultyLabel => Model.Difficulty switch
    {
        TestDifficulty.Simple  => "Simple",
        TestDifficulty.Medium  => "Medium",
        TestDifficulty.Complex => "Complex",
        _ => "?"
    };

    public Brush DifficultyBackground => Model.Difficulty switch
    {
        TestDifficulty.Simple  => new SolidColorBrush(Color.FromArgb(40, 76, 175, 80)),
        TestDifficulty.Medium  => new SolidColorBrush(Color.FromArgb(40, 255, 193, 7)),
        TestDifficulty.Complex => new SolidColorBrush(Color.FromArgb(40, 244, 67, 54)),
        _ => Brushes.Transparent
    };

    public Brush DifficultyForeground => Model.Difficulty switch
    {
        TestDifficulty.Simple  => new SolidColorBrush(Color.FromRgb(129, 199, 132)),
        TestDifficulty.Medium  => new SolidColorBrush(Color.FromRgb(255, 213, 79)),
        TestDifficulty.Complex => new SolidColorBrush(Color.FromRgb(239, 154, 154)),
        _ => Brushes.White
    };

    // ── Elapsed / Verifications ──────────────────────────────────────────
    public string ElapsedText => _elapsedMs > 0 ? $"{_elapsedMs / 1000.0:F1}s" : "\u2014";

    public string VerificationSummary
    {
        get => _verificationSummary;
        set
        {
            _verificationSummary = value;
            OnPropertyChanged(nameof(VerificationSummary));
        }
    }

    public SmokeTestViewModel(SmokeTest model)
    {
        Model = model;
        _verificationSummary = string.Join(" \u00B7 ", model.Verifications.Select(v => v.Description ?? v.Type.ToString()));
    }

    public void Reset()
    {
        Status = SmokeTestStatus.NotStarted;
        _elapsedMs = 0;
        OnPropertyChanged(nameof(ElapsedText));
        VerificationSummary = string.Join(" \u00B7 ", Model.Verifications.Select(v => v.Description ?? v.Type.ToString()));
    }

    public void SetResult(SmokeTestResult result)
    {
        _elapsedMs = result.ElapsedMs;
        OnPropertyChanged(nameof(ElapsedText));

        if (result.Verifications.Count > 0)
        {
            VerificationSummary = string.Join(" \u00B7 ", result.Verifications.Select(v =>
                $"{(v.Passed ? "\u2713" : "\u2717")} {v.Description}"));
        }
        else if (result.Error != null)
        {
            VerificationSummary = result.Error;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
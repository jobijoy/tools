using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace IdolClick.UI;

/// <summary>
/// Real-time execution dashboard that shows step-by-step progress
/// of flow and pack execution with pass/fail indicators and timing.
/// </summary>
public partial class ExecutionDashboardPanel : UserControl
{
    private readonly ObservableCollection<ExecutionStepViewModel> _steps = new();
    private int _passed;
    private int _failed;

    public ExecutionDashboardPanel()
    {
        InitializeComponent();
        StepsListView.ItemsSource = _steps;
    }

    /// <summary>
    /// Adds a step to the dashboard. Called from AgentService progress events.
    /// </summary>
    public void AddStep(string action, string? selector, string status, TimeSpan? duration, double? confidence = null)
    {
        var step = new ExecutionStepViewModel
        {
            StepNumber = (_steps.Count + 1).ToString(),
            ActionLabel = action,
            SelectorLabel = selector ?? "",
            HasSelector = !string.IsNullOrEmpty(selector),
            StatusIcon = status switch
            {
                "pass" => "✓",
                "fail" => "✗",
                "running" => "⏳",
                "skipped" => "⏭",
                _ => "•"
            },
            DurationLabel = duration.HasValue ? $"{duration.Value.TotalMilliseconds:F0}ms" : "",
            ConfidenceLabel = confidence.HasValue ? $"{confidence.Value:P0}" : ""
        };

        Dispatcher.Invoke(() =>
        {
            _steps.Add(step);
            EmptyState.Visibility = Visibility.Collapsed;

            if (status == "pass") _passed++;
            else if (status == "fail") _failed++;

            UpdateSummary();
            StepsListView.ScrollIntoView(step);
        });
    }

    /// <summary>
    /// Marks the last added step as complete with a status.
    /// </summary>
    public void UpdateLastStep(string status, TimeSpan duration, double? confidence = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (_steps.Count == 0) return;
            var last = _steps[^1];
            last.StatusIcon = status == "pass" ? "✓" : status == "fail" ? "✗" : "⏭";
            last.DurationLabel = $"{duration.TotalMilliseconds:F0}ms";
            if (confidence.HasValue) last.ConfidenceLabel = $"{confidence.Value:P0}";

            // Force UI update by replacing
            var idx = _steps.Count - 1;
            _steps[idx] = last;

            if (status == "pass") _passed++;
            else if (status == "fail") _failed++;
            UpdateSummary();
        });
    }

    public void SetStatus(string text)
    {
        Dispatcher.Invoke(() => ExecutionStatus.Text = $"  •  {text}");
    }

    public void SetElapsed(TimeSpan elapsed)
    {
        Dispatcher.Invoke(() => ElapsedText.Text = $"{elapsed.TotalSeconds:F1}s");
    }

    public void SetConfidence(double score)
    {
        Dispatcher.Invoke(() => ConfidenceText.Text = $"Confidence: {score:P0}");
    }

    private void UpdateSummary()
    {
        PassedText.Text = $"{_passed} passed";
        FailedText.Text = $"{_failed} failed";
        TotalText.Text = $"{_steps.Count} steps";
    }

    public void Reset()
    {
        _steps.Clear();
        _passed = 0;
        _failed = 0;
        UpdateSummary();
        ConfidenceText.Text = "";
        ElapsedText.Text = "";
        ExecutionStatus.Text = "  •  Idle";
        EmptyState.Visibility = Visibility.Visible;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Reset();
}

public class ExecutionStepViewModel
{
    public string StepNumber { get; set; } = "";
    public string StatusIcon { get; set; } = "•";
    public string ActionLabel { get; set; } = "";
    public string SelectorLabel { get; set; } = "";
    public bool HasSelector { get; set; }
    public string DurationLabel { get; set; } = "";
    public string ConfidenceLabel { get; set; } = "";
}

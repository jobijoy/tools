using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutomationTool.Models;
using AutomationTool.Services;

namespace AutomationTool.UI;

public partial class MainWindow : Window
{
    private System.Windows.Threading.DispatcherTimer _timer = null!;
    private int _actionCount;
    private LogLevel _logLevel = LogLevel.Info;

    public MainWindow()
    {
        InitializeComponent();
        LoadRules();
        SubscribeToLog();
        SetupStatusTimer();
        LoadSettings();

        // Register hotkey
        App.Tray.SetMainWindow(this);
    }

    private void LoadSettings()
    {
        var cfg = App.Config.GetConfig();
        EnabledCheckBox.IsChecked = cfg.Settings.AutomationEnabled;

        // Set interval combo
        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var ms) && ms == cfg.Settings.PollingIntervalMs)
            {
                IntervalCombo.SelectedItem = item;
                break;
            }
        }

        UpdateStatus();
    }

    private void LoadRules()
    {
        var cfg = App.Config.GetConfig();
        RulesListView.ItemsSource = null;
        RulesListView.ItemsSource = cfg.Rules;
        UpdateStatus();
    }

    private void SubscribeToLog()
    {
        App.Log.OnLog += OnLogEntry;

        // Load recent logs
        foreach (var entry in App.Log.GetRecent(50))
            AddLogEntry(entry);
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < _logLevel) return;
        if (entry.Category == "Action") _actionCount++;

        Dispatcher.BeginInvoke(() => AddLogEntry(entry));
    }

    private void AddLogEntry(LogEntry entry)
    {
        var icon = entry.Level switch
        {
            LogLevel.Debug => "🔍",
            LogLevel.Info => "ℹ️",
            LogLevel.Warning => "⚠️",
            LogLevel.Error => "❌",
            _ => "•"
        };

        var item = new ListBoxItem
        {
            Content = $"[{entry.Time:HH:mm:ss}] {icon} [{entry.Category}] {entry.Message}",
            Foreground = entry.Level switch
            {
                LogLevel.Error => Brushes.Red,
                LogLevel.Warning => Brushes.Orange,
                LogLevel.Debug => Brushes.Gray,
                _ => Brushes.White
            }
        };

        LogListBox.Items.Add(item);
        LogListBox.ScrollIntoView(item);

        while (LogListBox.Items.Count > 500)
            LogListBox.Items.RemoveAt(0);
    }

    private void SetupStatusTimer()
    {
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) => UpdateStatus();
        _timer.Start();
    }

    private void UpdateStatus()
    {
        var isRunning = App.Engine.IsEnabled;
        StatusIndicator.Fill = isRunning
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("ErrorBrush");
        StatusText.Text = isRunning ? "Running" : "Paused";

        var cfg = App.Config.GetConfig();
        StatsText.Text = $" | Rules: {cfg.Rules.Count(r => r.Enabled)}/{cfg.Rules.Count} | Actions: {_actionCount}";
    }

    // === Event Handlers ===

    private void EnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledCheckBox.IsChecked == true;
        App.Engine.SetEnabled(enabled);

        var cfg = App.Config.GetConfig();
        cfg.Settings.AutomationEnabled = enabled;
        App.Config.SaveConfig(cfg);

        UpdateStatus();
    }

    private void IntervalCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var ms))
        {
            var cfg = App.Config.GetConfig();
            cfg.Settings.PollingIntervalMs = ms;
            App.Config.SaveConfig(cfg);
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RuleEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Rule != null)
        {
            var cfg = App.Config.GetConfig();
            cfg.Rules.Add(editor.Rule);
            App.Config.SaveConfig(cfg);
            LoadRules();
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is Rule rule)
            EditRule(rule);
    }

    private void RulesListView_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RulesListView.SelectedItem is Rule rule)
            EditRule(rule);
    }

    private void EditRule(Rule rule)
    {
        var editor = new RuleEditorWindow(rule) { Owner = this };
        if (editor.ShowDialog() == true && editor.Rule != null)
        {
            var cfg = App.Config.GetConfig();
            var idx = cfg.Rules.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0)
            {
                cfg.Rules[idx] = editor.Rule;
                App.Config.SaveConfig(cfg);
                LoadRules();
            }
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is Rule rule)
        {
            var result = MessageBox.Show($"Delete rule '{rule.Name}'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var cfg = App.Config.GetConfig();
                cfg.Rules.RemoveAll(r => r.Id == rule.Id);
                App.Config.SaveConfig(cfg);
                LoadRules();
            }
        }
    }

    private void RuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        App.Config.SaveConfig(App.Config.GetConfig());
        UpdateStatus();
    }

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        LoadRules();
        App.Log.Info("Config", "Configuration reloaded");
    }

    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogLevelCombo.SelectedItem is ComboBoxItem item)
        {
            _logLevel = item.Content?.ToString() switch
            {
                "Debug" => LogLevel.Debug,
                "Info" => LogLevel.Info,
                "Warning" => LogLevel.Warning,
                "Error" => LogLevel.Error,
                _ => LogLevel.Info
            };
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogListBox.Items.Clear();
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(App.Log.GetLogPath()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
        LoadSettings();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("Exit Automation Tool?", "Confirm Exit",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var cfg = App.Config.GetConfig();
        if (cfg.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            App.Tray.ShowBalloon("Automation Tool", "Running in system tray. Press " + cfg.Settings.ToggleHotkey + " to toggle.");
        }
    }

    public void SetEnabled(bool enabled)
    {
        Dispatcher.Invoke(() =>
        {
            EnabledCheckBox.IsChecked = enabled;
        });
    }

    public void Toggle()
    {
        Dispatcher.Invoke(() =>
        {
            EnabledCheckBox.IsChecked = !EnabledCheckBox.IsChecked;
        });
    }
}

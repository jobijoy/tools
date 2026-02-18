using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdolClick.Models;
using IdolClick.Services;

namespace IdolClick.UI;

public partial class MainWindow : Window
{
    private System.Windows.Threading.DispatcherTimer _timer = null!;
    private int _actionCount;
    private LogLevel _logLevel = LogLevel.Debug;
    private bool _isExpanded = true;  // default true at 1060px initial width
    private AppMode _currentMode = AppMode.Classic;

    public MainWindow()
    {
        InitializeComponent();
        SetupKeyboardShortcuts();

        // Wire UserControl events
        ProfilePane.HideRequested += () =>
        {
            ProfilePaneBorder.Visibility = Visibility.Collapsed;
            ProfilePaneColumn.Width = new GridLength(0);
        };
        ProfilePane.ProfileChanged += name =>
        {
            RulesPanel.LoadRules();
            LoadSettings();
            ProfileIndicator.Text = App.Profiles.ActiveProfile;
            UpdateStatus();
        };
        RulesPanel.StatusChanged += UpdateStatus;

        // Initialise controls
        ProfilePane.LoadProfiles();
        RulesPanel.LoadRules();
        SubscribeToLog();
        SetupStatusTimer();
        SetupTimeline();
        LoadPlugins();
        LoadSettings();
        ApplyViewMode();
        ApplyMode();

        // Global profile-changed (from service level)
        App.Profiles.ProfileChanged += OnProfileChanged;
        App.Hotkey.SetMainWindow(this);
    }

    // ═══ Keyboard shortcuts ═══

    private void SetupKeyboardShortcuts()
    {
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => RulesPanel.LoadRules()), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => RulesPanel.ClearSearch()), Key.Escape, ModifierKeys.None));
    }

    // ═══ Window lifecycle ═══

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wasExpanded = _isExpanded;
        _isExpanded = ActualWidth > 800 || ActualHeight > 480;

        if (wasExpanded != _isExpanded)
            ApplyViewMode();
        else
            ResizeColumns();

        // Auto-collapse profile pane at very narrow widths
        if (ActualWidth < 600 && ProfilePaneBorder.Visibility == Visibility.Visible)
        {
            ProfilePaneBorder.Visibility = Visibility.Collapsed;
            ProfilePaneColumn.Width = new GridLength(0);
        }

        // Auto-collapse agent side log at narrow widths
        if (ActualWidth < 900)
            AgentChatPanel.CollapseLogPanel();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            _isExpanded = true;
            ApplyViewMode();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        App.Profiles.SaveCurrentProfile();
        Application.Current.Shutdown();
    }

    // ═══ View mode (compact / expanded) ═══

    private void ApplyViewMode()
    {
        IntervalCombo.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        IntervalText.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
        RulesPanel.SetExpanded(_isExpanded);

        // Right panel: show in Classic mode when window is wide enough
        if (_currentMode == AppMode.Classic)
        {
            var showRight = _isExpanded;
            RightPanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
            RightSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
            RightSplitterCol.Width = showRight ? new GridLength(5) : new GridLength(0);
            if (!showRight) RightPanelColumn.Width = new GridLength(0);
        }

        ResizeColumns();
        Title = _isExpanded ? "Idol Click v1.0.0" : "Idol Click";
    }

    private void ResizeColumns()
    {
        var profileWidth = ProfilePaneBorder.Visibility == Visibility.Visible ? ProfilePaneColumn.ActualWidth : 0;
        var rightWidth = RightPanel.Visibility == Visibility.Visible
            ? RightSplitterCol.ActualWidth + RightPanelColumn.ActualWidth
            : 0;
        var available = ActualWidth - 48 - profileWidth - rightWidth - 20;
        if (available < 150) return;
        RulesPanel.ResizeColumns(available);
    }

    // ═══ Settings ═══

    private void LoadSettings()
    {
        var cfg = App.Config.GetConfig();
        AutomationToggle.IsChecked = cfg.Settings.AutomationEnabled;
        _currentMode = cfg.Settings.Mode;
        UpdateToggleButton();

        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var ms) && ms == cfg.Settings.PollingIntervalMs)
            {
                IntervalCombo.SelectedItem = item;
                IntervalText.Text = $"  •  {ms / 1000}s";
                break;
            }
        }
        ResizeColumns();
        UpdateStatus();
    }

    private void UpdateToggleButton()
    {
        var running = AutomationToggle.IsChecked == true;
        AutomationToggle.Content = running ? "⏸" : "▶";
        AutomationToggle.ToolTip = running ? "Pause automation (Ctrl+Alt+T)" : "Start automation (Ctrl+Alt+T)";
    }

    // ═══ Status ═══

    private void SetupStatusTimer()
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (s, e) => UpdateStatus();
        _timer.Start();
    }

    private void UpdateStatus()
    {
        var isRunning = App.Engine.IsEnabled;
        StatusIndicator.Fill = isRunning ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("ErrorBrush");
        StatusText.Text = isRunning ? "Running" : "Paused";

        var cfg = App.Config.GetConfig();
        var running = cfg.Rules.Count(r => r.Enabled && r.IsRunning);
        var enabled = cfg.Rules.Count(r => r.Enabled);
        StatsText.Text = $" • {running}/{enabled} running";
    }

    // ═══ Profile events ═══

    private void OnProfileChanged()
    {
        Dispatcher.Invoke(() =>
        {
            RulesPanel.LoadRules();
            LoadSettings();
            ProfileIndicator.Text = App.Profiles.ActiveProfile;
            ProfilePane.LoadProfiles();

            var cfg = App.Config.GetConfig();
            App.Engine.SetEnabled(cfg.Settings.AutomationEnabled);
            AutomationToggle.IsChecked = cfg.Settings.AutomationEnabled;
            UpdateToggleButton();
            UpdateStatus();
        });
    }

    private void ToggleProfilePane_Click(object sender, RoutedEventArgs e)
    {
        var isVisible = ProfilePaneBorder.Visibility == Visibility.Visible;
        ProfilePaneBorder.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        ProfilePaneColumn.Width = isVisible ? new GridLength(0) : new GridLength(180);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ResizeColumns);
    }

    // ═══ Mode switching (Activity bar hub-spoke) ═══

    private void NavMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            var mode = tag switch
            {
                "Agent" => AppMode.Agent,
                "Teach" => AppMode.Teach,
                _ => AppMode.Classic
            };
            SwitchMode(mode);
        }
    }

    private void SwitchMode(AppMode mode)
    {
        if (_currentMode == mode) return;
        _currentMode = mode;

        var cfg = App.Config.GetConfig();
        cfg.Settings.Mode = mode;
        App.Config.SaveConfig(cfg);

        ApplyMode();
        App.Log.Info("Mode", $"Switched to {mode} mode");
    }

    private void ApplyMode()
    {
        // Panel visibility
        RulesPanelBorder.Visibility = _currentMode == AppMode.Classic ? Visibility.Visible : Visibility.Collapsed;
        AgentChatPanel.Visibility = _currentMode == AppMode.Agent ? Visibility.Visible : Visibility.Collapsed;
        TeachPanelBorder.Visibility = _currentMode == AppMode.Teach ? Visibility.Visible : Visibility.Collapsed;

        // Profile pane + controls: Classic only
        bool isClassic = _currentMode == AppMode.Classic;
        ProfilePaneBorder.Visibility = isClassic ? Visibility.Visible : Visibility.Collapsed;
        ProfilePaneColumn.Width = isClassic ? new GridLength(180) : new GridLength(0);
        ProfileToggleBtn.Visibility = isClassic ? Visibility.Visible : Visibility.Collapsed;
        AutomationToggle.Visibility = isClassic ? Visibility.Visible : Visibility.Collapsed;

        // Right panel (Log/Timeline/Plugins): Classic + wide enough
        bool showRight = isClassic && _isExpanded;
        RightPanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightSplitterCol.Width = showRight ? new GridLength(5) : new GridLength(0);
        if (showRight && RightPanelColumn.Width.Value == 0)
            RightPanelColumn.Width = new GridLength(280);
        if (!showRight)
            RightPanelColumn.Width = new GridLength(0);

        // Section heading — icon, title, subtitle, and accent color
        (SectionIcon.Text, SectionTitle.Text, SectionSubtitle.Text) = _currentMode switch
        {
            AppMode.Agent => ("🧠", "Reason", "AI Agent — describe tasks in plain English"),
            AppMode.Teach => ("🎓", "Teach", "Smart Sentence Builder"),
            _ => ("⚡", "Instinct", "Rule-based automation with profiles and rules")
        };
        SectionAccent.Background = _currentMode switch
        {
            AppMode.Agent => (System.Windows.Media.Brush)FindResource("AccentBrush"),
            AppMode.Teach => (System.Windows.Media.Brush)FindResource("PrimaryLightBrush"),
            _ => (System.Windows.Media.Brush)FindResource("PrimaryBrush")
        };

        // Sync nav radio buttons
        NavClassic.IsChecked = _currentMode == AppMode.Classic;
        NavAgent.IsChecked = _currentMode == AppMode.Agent;
        NavTeach.IsChecked = _currentMode == AppMode.Teach;

        if (_currentMode == AppMode.Agent)
        {
            AgentChatPanel.UpdateAgentStatus();
            AgentChatPanel.UpdateMicButtonVisibility();
        }
        if (_currentMode == AppMode.Teach)
            TeachPanel.UpdateMicButtonVisibility();

        ResizeColumns();
    }

    // ═══ Log subscription ═══

    private void SubscribeToLog()
    {
        App.Log.OnLog += OnLogEntry;
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

        var formatted = $"[{entry.Time:HH:mm:ss}] {icon} [{entry.Category}] {entry.Message}";

        // Classic bottom log
        var item = new ListBoxItem
        {
            Content = formatted,
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

        // Agent side log
        AgentChatPanel.AddLogEntry(formatted, entry.Level);
    }

    private void SetupTimeline()
    {
        TimelineListView.ItemsSource = App.Timeline.Events;
        AgentChatPanel.SetupTimeline(App.Timeline.Events);
    }

    // ═══ Automation toggle ═══

    private void AutomationToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutomationToggle.IsChecked == true;
        App.Engine.SetEnabled(enabled);

        var cfg = App.Config.GetConfig();
        cfg.Settings.AutomationEnabled = enabled;
        App.Config.SaveConfig(cfg);
        App.Profiles.SaveCurrentProfile();

        UpdateToggleButton();
        UpdateStatus();
    }

    private void IntervalCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var ms))
        {
            var cfg = App.Config.GetConfig();
            cfg.Settings.PollingIntervalMs = ms;
            App.Config.SaveConfig(cfg);

            if (IntervalText != null)
                IntervalText.Text = $"  •  {ms / 1000}s";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
        LoadSettings();
    }

    // ═══ Bottom log panel handlers ═══

    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogLevelCombo.SelectedItem is ComboBoxItem item)
        {
            _logLevel = item.Content?.ToString() switch
            {
                "All" => LogLevel.Debug,
                "Debug" => LogLevel.Debug,
                "Info" => LogLevel.Info,
                "Warning" => LogLevel.Warning,
                "Error" => LogLevel.Error,
                _ => LogLevel.Debug
            };
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogListBox.Items.Clear();

    private void CopySelectedLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogListBox.SelectedItems.Cast<ListBoxItem>().Select(i => i.Content?.ToString());
        var text = string.Join(Environment.NewLine, lines);
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void CopyAllLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogListBox.Items.Cast<ListBoxItem>().Select(i => i.Content?.ToString());
        var text = string.Join(Environment.NewLine, lines);
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void SelectAllLog_Click(object sender, RoutedEventArgs e) => LogListBox.SelectAll();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.Log.GetLogPath()) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Could not open log file: {ex.Message}"); }
    }

    // ═══ Timeline handlers ═══

    private void TimelineFilter_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ClearTimeline_Click(object sender, RoutedEventArgs e) => App.Timeline.Clear();

    // ═══ Plugin handlers ═══

    private void LoadPlugins()
    {
        PluginsListView.ItemsSource = null;
        PluginsListView.ItemsSource = App.Plugins.Plugins;
    }

    private void RefreshPlugins_Click(object sender, RoutedEventArgs e)
    {
        var pluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        App.Plugins.UnloadAll();
        App.Plugins.LoadPlugins(pluginsPath);
        LoadPlugins();
        App.Log.Info("Plugins", "Plugins reloaded");
    }

    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
    {
        var pluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        if (!Directory.Exists(pluginsPath)) Directory.CreateDirectory(pluginsPath);
        try { Process.Start(new ProcessStartInfo(pluginsPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Could not open plugins folder: {ex.Message}"); }
    }

    private void PluginEnabled_Changed(object sender, RoutedEventArgs e) { }

    // ═══ Public API (for HotkeyService, etc.) ═══

    public void SetEnabled(bool enabled)
    {
        Dispatcher.Invoke(() =>
        {
            AutomationToggle.IsChecked = enabled;
            UpdateToggleButton();
            UpdateStatus();
        });
    }

    public void Toggle()
    {
        Dispatcher.Invoke(() =>
        {
            AutomationToggle.IsChecked = !AutomationToggle.IsChecked;
            AutomationToggle_Click(AutomationToggle, new RoutedEventArgs());
        });
    }
}

public class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

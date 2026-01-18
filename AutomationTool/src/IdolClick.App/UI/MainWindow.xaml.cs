using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using IdolClick.Models;
using IdolClick.Services;

namespace IdolClick.UI;

public partial class MainWindow : Window
{
    private System.Windows.Threading.DispatcherTimer _timer = null!;
    private int _actionCount;
    private LogLevel _logLevel = LogLevel.Info;
    private List<Rule> _allRules = new();
    private string _searchText = "";

    public MainWindow()
    {
        InitializeComponent();
        SetupKeyboardShortcuts();
        LoadRules();
        SubscribeToLog();
        SetupStatusTimer();
        SetupTimeline();
        LoadPlugins();
        LoadSettings();

        // Register hotkey
        App.Tray.SetMainWindow(this);
    }

    private void SetupKeyboardShortcuts()
    {
        // F5 - Reload config
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ReloadConfig_Click(this, new RoutedEventArgs())), Key.F5, ModifierKeys.None));
        
        // Delete - Delete selected rule
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => DeleteRule_Click(this, new RoutedEventArgs())), Key.Delete, ModifierKeys.None));
        
        // Ctrl+N - New rule
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => AddRule_Click(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
        
        // Ctrl+E - Edit selected rule
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => EditRule_Click(this, new RoutedEventArgs())), Key.E, ModifierKeys.Control));
        
        // Ctrl+S - Export rules
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ExportRules_Click(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        
        // Ctrl+O - Import rules
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ImportRules_Click(this, new RoutedEventArgs())), Key.O, ModifierKeys.Control));
        
        // Escape - Clear search
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ClearSearch()), Key.Escape, ModifierKeys.None));
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
        _allRules = cfg.Rules.ToList();
        ApplyRulesFilter();
        UpdateStatus();
    }

    private void ApplyRulesFilter()
    {
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            RulesListView.ItemsSource = null;
            RulesListView.ItemsSource = _allRules;
        }
        else
        {
            var filtered = _allRules.Where(r =>
                r.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                r.TargetApp.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                r.MatchText.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                r.Action.ToString().Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            ).ToList();
            RulesListView.ItemsSource = null;
            RulesListView.ItemsSource = filtered;
        }
    }

    private void ClearSearch()
    {
        if (SearchTextBox != null)
        {
            SearchTextBox.Text = "";
            _searchText = "";
            ApplyRulesFilter();
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchTextBox.Text;
        ApplyRulesFilter();
    }

    private void LoadPlugins()
    {
        PluginsListView.ItemsSource = null;
        PluginsListView.ItemsSource = App.Plugins.Plugins;
    }

    private void SetupTimeline()
    {
        TimelineListView.ItemsSource = App.Timeline.Events;
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

    private void ExportRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = "json",
            FileName = "idol-click-rules.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var cfg = App.Config.GetConfig();
                var exportData = new { rules = cfg.Rules, exportedAt = DateTime.Now, version = "1.0.0" };
                var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
                App.Log.Info("Export", $"Exported {cfg.Rules.Count} rules to {dialog.FileName}");
                MessageBox.Show($"Exported {cfg.Rules.Count} rules successfully!", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = "json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var doc = JsonDocument.Parse(json);
                
                List<Rule>? importedRules = null;
                
                // Try to parse as export format (with "rules" property)
                if (doc.RootElement.TryGetProperty("rules", out var rulesElement))
                {
                    importedRules = JsonSerializer.Deserialize<List<Rule>>(rulesElement.GetRawText());
                }
                // Try to parse as array of rules directly
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    importedRules = JsonSerializer.Deserialize<List<Rule>>(json);
                }

                if (importedRules == null || importedRules.Count == 0)
                {
                    MessageBox.Show("No rules found in the file.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    $"Found {importedRules.Count} rules.\n\nReplace existing rules or merge with current rules?",
                    "Import Rules",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;

                var cfg = App.Config.GetConfig();
                
                if (result == MessageBoxResult.Yes)
                {
                    // Replace all rules
                    cfg.Rules = importedRules;
                }
                else
                {
                    // Merge - add rules with new IDs
                    foreach (var rule in importedRules)
                    {
                        rule.Id = Guid.NewGuid().ToString();
                        cfg.Rules.Add(rule);
                    }
                }

                App.Config.SaveConfig(cfg);
                LoadRules();
                App.Log.Info("Import", $"Imported {importedRules.Count} rules from {dialog.FileName}");
                MessageBox.Show($"Imported {importedRules.Count} rules successfully!", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
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
        var result = MessageBox.Show("Exit Idol Click?", "Confirm Exit",
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
            App.Tray.ShowBalloon("Idol Click", "Running in system tray. Press " + cfg.Settings.ToggleHotkey + " to toggle.");
        }
    }

    // === Timeline Event Handlers ===

    private void TimelineFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Timeline filtering is handled by the ObservableCollection
        // For now, just refresh the view
    }

    private void ClearTimeline_Click(object sender, RoutedEventArgs e)
    {
        App.Timeline.Clear();
    }

    // === Plugin Event Handlers ===

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
        if (!Directory.Exists(pluginsPath))
            Directory.CreateDirectory(pluginsPath);

        try
        {
            Process.Start(new ProcessStartInfo(pluginsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open plugins folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PluginEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Plugin enable/disable is handled by the binding
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

// Simple RelayCommand for keyboard shortcuts
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

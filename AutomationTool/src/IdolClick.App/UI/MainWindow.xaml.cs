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
    private bool _isExpanded = false;
    private const double CompactWidth = 420;
    private const double CompactHeight = 280;
    private const double ExpandedWidth = 900;
    private const double ExpandedHeight = 650;

    public MainWindow()
    {
        InitializeComponent();
        SetupKeyboardShortcuts();
        LoadProfiles();
        LoadRules();
        SubscribeToLog();
        SetupStatusTimer();
        SetupTimeline();
        LoadPlugins();
        LoadSettings();
        ApplyViewMode();

        // Subscribe to profile changes
        App.Profiles.ProfileChanged += OnProfileChanged;

        // Register hotkey
        App.Hotkey.SetMainWindow(this);
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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Auto-detect expanded mode based on size
        var wasExpanded = _isExpanded;
        _isExpanded = ActualWidth > 600 || ActualHeight > 450;
        
        if (wasExpanded != _isExpanded)
            ApplyViewMode();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            _isExpanded = true;
            ApplyViewMode();
        }
    }

    private void ApplyViewMode()
    {
        // Show/hide expanded elements
        var expandedVisibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        var compactVisibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;

        // Log panel
        LogPanel.Visibility = expandedVisibility;
        PanelSplitter.Visibility = expandedVisibility;
        LogPanelRow.Height = _isExpanded ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        LogPanelRow.MinHeight = _isExpanded ? 150 : 0;

        // Extended toolbar buttons
        SearchPanel.Visibility = expandedVisibility;
        RulesLabel.Visibility = compactVisibility;
        ImportBtn.Visibility = expandedVisibility;
        ExportBtn.Visibility = expandedVisibility;
        EditBtn.Visibility = expandedVisibility;
        DelBtn.Visibility = expandedVisibility;
        
        // Interval combo in footer
        IntervalCombo.Visibility = expandedVisibility;
        IntervalText.Visibility = compactVisibility;

        // Adjust column widths based on mode
        var cfg = App.Config.GetConfig();
        var showExecCount = cfg.Settings.ShowExecutionCount;
        
        if (_isExpanded)
        {
            ColStatus.Width = 24;
            ColName.Width = 130;
            ColApp.Width = 90;
            ColMatch.Width = 130;
            ColAction.Width = 70;
            ColCooldown.Width = 40;
            ColSessionExec.Width = showExecCount ? 45 : 0;
            ColTriggers.Width = 40;
            ColLastTrigger.Width = 80;
        }
        else
        {
            ColStatus.Width = 22;
            ColName.Width = 100;
            ColApp.Width = 60;
            ColMatch.Width = 80;
            ColAction.Width = 50;
            ColCooldown.Width = 28;
            ColSessionExec.Width = showExecCount ? 35 : 0;
            ColTriggers.Width = 22;
            ColLastTrigger.Width = 50;
        }

        // Update title
        Title = _isExpanded ? "Idol Click v1.0.0" : "Idol Click";
    }

    private void LoadSettings()
    {
        var cfg = App.Config.GetConfig();
        AutomationToggle.IsChecked = cfg.Settings.AutomationEnabled;
        UpdateToggleButton();

        // Set interval combo
        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var ms) && ms == cfg.Settings.PollingIntervalMs)
            {
                IntervalCombo.SelectedItem = item;
                IntervalText.Text = $" • {ms / 1000}s";
                break;
            }
        }
        
        // Show/hide execution count column based on setting
        ColSessionExec.Width = cfg.Settings.ShowExecutionCount ? 35 : 0;

        UpdateStatus();
    }

    private void UpdateToggleButton()
    {
        var isRunning = AutomationToggle.IsChecked == true;
        AutomationToggle.Content = isRunning ? "⏸" : "▶";
        AutomationToggle.ToolTip = isRunning ? "Pause automation (Ctrl+Alt+T)" : "Start automation (Ctrl+Alt+T)";
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

    // === Profile Management ===

    private void LoadProfiles()
    {
        var profiles = App.Profiles.GetProfiles();
        ProfileListBox.ItemsSource = profiles;
        ProfileListBox.SelectedItem = App.Profiles.ActiveProfile;
        ProfileIndicator.Text = App.Profiles.ActiveProfile;
    }

    private void OnProfileChanged()
    {
        Dispatcher.Invoke(() =>
        {
            LoadRules();
            LoadSettings();
            ProfileIndicator.Text = App.Profiles.ActiveProfile;
            ProfileListBox.SelectedItem = App.Profiles.ActiveProfile;
            
            // Restore automation state from the profile
            var cfg = App.Config.GetConfig();
            App.Engine.SetEnabled(cfg.Settings.AutomationEnabled);
            AutomationToggle.IsChecked = cfg.Settings.AutomationEnabled;
            UpdateToggleButton();
            UpdateStatus();
        });
    }

    private void ToggleProfilePane_Click(object sender, RoutedEventArgs e)
    {
        var isVisible = ProfilePane.Visibility == Visibility.Visible;
        ProfilePane.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        ProfilePaneColumn.Width = isVisible ? new GridLength(0) : new GridLength(180);
    }

    private void HideProfilePane_Click(object sender, RoutedEventArgs e)
    {
        ProfilePane.Visibility = Visibility.Collapsed;
        ProfilePaneColumn.Width = new GridLength(0);
    }

    private void ProfileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is string profileName)
        {
            if (profileName != App.Profiles.ActiveProfile)
            {
                App.Profiles.SwitchProfile(profileName);
            }
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("New Profile", "Enter profile name:", "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (App.Profiles.CreateProfile(name))
            {
                App.Profiles.SwitchProfile(name);
                LoadProfiles();
            }
            else
            {
                MessageBox.Show("Could not create profile. Name may already exist.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not string source) return;
        
        var name = PromptForName("Duplicate Profile", $"Enter name for copy of '{source}':", $"{source} Copy");
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (App.Profiles.DuplicateProfile(source, name))
            {
                // Switch to the new profile
                App.Profiles.SwitchProfile(name);
                LoadProfiles();
            }
            else
            {
                MessageBox.Show("Could not duplicate profile.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not string oldName) return;
        if (oldName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Cannot rename the Default profile.", "Info", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        
        var newName = PromptForName("Rename Profile", "Enter new name:", oldName);
        if (!string.IsNullOrWhiteSpace(newName) && newName != oldName)
        {
            if (App.Profiles.RenameProfile(oldName, newName))
            {
                LoadProfiles();
            }
            else
            {
                MessageBox.Show("Could not rename profile.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not string name) return;
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Cannot delete the Default profile.", "Info", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        
        var result = MessageBox.Show($"Delete profile '{name}'?\n\nThis cannot be undone.", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes)
        {
            if (App.Profiles.DeleteProfile(name))
            {
                LoadProfiles();
            }
        }
    }

    private static string? PromptForName(string title, string prompt, string defaultValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true
        };

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(20),
            Padding = new Thickness(20)
        };

        var stack = new StackPanel();
        var label = new TextBlock 
        { 
            Text = prompt, 
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), 
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12) 
        };
        var textBox = new TextBox 
        { 
            Text = defaultValue, 
            Padding = new Thickness(12, 10, 12, 10),
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            BorderThickness = new Thickness(1)
        };
        textBox.SelectAll();
        
        var buttons = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        
        var okButton = new Button 
        { 
            Content = "OK", 
            Width = 90,
            Height = 32,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        var cancelButton = new Button 
        { 
            Content = "Cancel", 
            Width = 90,
            Height = 32,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(10, 0, 0, 0),
            IsCancel = true
        };

        string? result = null;
        okButton.Click += (s, e) => { result = textBox.Text; dialog.DialogResult = true; };
        cancelButton.Click += (s, e) => dialog.DialogResult = false;

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        stack.Children.Add(label);
        stack.Children.Add(textBox);
        stack.Children.Add(buttons);
        border.Child = stack;
        dialog.Content = border;

        textBox.Focus();
        return dialog.ShowDialog() == true ? result : null;
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
        var runningCount = cfg.Rules.Count(r => r.Enabled && r.IsRunning);
        var enabledCount = cfg.Rules.Count(r => r.Enabled);
        StatsText.Text = $" • {runningCount}/{enabledCount} running";
    }

    // === Event Handlers ===

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
            
            // Update compact interval text
            if (IntervalText != null)
                IntervalText.Text = $" • {ms / 1000}s";
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
            App.Profiles.SaveCurrentProfile();
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
                App.Profiles.SaveCurrentProfile();
                LoadRules();
            }
        }
    }

    private Rule? _selectedRule;
    
    private void RulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRule = RulesListView.SelectedItem as Rule;
        UpdateDeleteButtonState();
    }
    
    private void UpdateDeleteButtonState()
    {
        if (_isExpanded && _selectedRule != null)
        {
            DelBtn.Visibility = Visibility.Visible;
        }
        else if (!_isExpanded)
        {
            DelBtn.Visibility = Visibility.Collapsed;
        }
    }
    
    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = _selectedRule ?? RulesListView.SelectedItem as Rule;
        if (rule != null)
        {
            var result = MessageBox.Show($"Delete rule '{rule.Name}'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var cfg = App.Config.GetConfig();
                cfg.Rules.RemoveAll(r => r.Id == rule.Id);
                App.Config.SaveConfig(cfg);
                App.Profiles.SaveCurrentProfile();
                _selectedRule = null;
                LoadRules();
                UpdateDeleteButtonState();
            }
        }
    }

    private void RuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Notify status color changed when enabled state changes
        if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is Rule rule)
        {
            rule.NotifyEnabledChanged();
        }
        App.Config.SaveConfig(App.Config.GetConfig());
        App.Profiles.SaveCurrentProfile();
        UpdateStatus();
    }

    private void RuleRunToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Rule rule)
        {
            rule.IsRunning = !rule.IsRunning;
            App.Log.Info("Rule", $"Rule '{rule.Name}' {(rule.IsRunning ? "resumed" : "paused")}");
            UpdateStatus();
        }
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
        // Save current profile state before exiting
        App.Profiles.SaveCurrentProfile();
        
        // Exit the application
        Application.Current.Shutdown();
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

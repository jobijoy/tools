using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using Microsoft.Win32;
using IdolClick.Models;
using IdolClick.Services;

namespace IdolClick.UI;

public partial class MainWindow : Window
{
    private System.Windows.Threading.DispatcherTimer _timer = null!;
    private int _actionCount;
    private LogLevel _logLevel = LogLevel.Info;
    private LogLevel _agentLogLevel = LogLevel.Info;
    private List<Rule> _allRules = new();
    private string _searchText = "";
    private bool _isExpanded = false;
    private AppMode _currentMode = AppMode.Classic;
    private CancellationTokenSource? _chatCts;
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
        ApplyMode(); // Set initial mode from config

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
        else
            ResizeColumns(); // Always re-distribute columns on resize
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

        // Re-distribute column widths based on current window size
        ResizeColumns();

        // Update title
        Title = _isExpanded ? "Idol Click v1.0.0" : "Idol Click";
    }

    /// <summary>
    /// Proportionally distributes GridViewColumn widths to fill available space.
    /// Called on window resize and view mode changes for DPI/resolution independence.
    /// </summary>
    private void ResizeColumns()
    {
        // Calculate available width for the rules ListView
        // Subtract profile pane if visible, margins (8*2=16), borders ~2
        var profileWidth = ProfilePane.Visibility == Visibility.Visible ? ProfilePaneColumn.ActualWidth : 0;
        var available = ActualWidth - profileWidth - 24; // 24 = margins + padding + scrollbar
        if (available < 200) return; // Too small, skip

        var cfg = App.Config.GetConfig();
        var showExecCount = cfg.Settings.ShowExecutionCount;

        // Fixed-width columns (status, checkbox, play/pause, cooldown, session, triggers, last)
        double fixedTotal = 0;

        // Status indicator: small fixed
        var statusW = _isExpanded ? 24.0 : 22.0;
        ColStatus.Width = statusW;
        fixedTotal += statusW;

        // Checkbox: 28
        fixedTotal += 28;

        // Play/Pause button: 26
        fixedTotal += 26;

        // Cooldown
        var cdW = _isExpanded ? 38.0 : 28.0;
        ColCooldown.Width = cdW;
        fixedTotal += cdW;

        // Session exec count
        var sessionW = showExecCount ? (_isExpanded ? 42.0 : 35.0) : 0;
        ColSessionExec.Width = sessionW;
        fixedTotal += sessionW;

        // Trigger count
        var trigW = _isExpanded ? 36.0 : 22.0;
        ColTriggers.Width = trigW;
        fixedTotal += trigW;

        // Last triggered
        var lastW = _isExpanded ? 72.0 : 50.0;
        ColLastTrigger.Width = lastW;
        fixedTotal += lastW;

        // Remaining space is split proportionally among Name, App, Match, Action
        var flex = Math.Max(100, available - fixedTotal);

        // Proportions: Name 28%, App 18%, Match 32%, Action 14%  (compact shifts slightly)
        if (_isExpanded)
        {
            ColName.Width = Math.Max(60, flex * 0.27);
            ColApp.Width = Math.Max(40, flex * 0.18);
            ColMatch.Width = Math.Max(60, flex * 0.34);
            ColAction.Width = Math.Max(40, flex * 0.14);
        }
        else
        {
            ColName.Width = Math.Max(50, flex * 0.28);
            ColApp.Width = Math.Max(35, flex * 0.17);
            ColMatch.Width = Math.Max(50, flex * 0.30);
            ColAction.Width = Math.Max(35, flex * 0.15);
        }
    }

    private void LoadSettings()
    {
        var cfg = App.Config.GetConfig();
        AutomationToggle.IsChecked = cfg.Settings.AutomationEnabled;
        _currentMode = cfg.Settings.Mode;
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
        ResizeColumns();

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
        // Delay column resize slightly so layout has updated ActualWidth
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ResizeColumns);
    }

    private void HideProfilePane_Click(object sender, RoutedEventArgs e)
    {
        ProfilePane.Visibility = Visibility.Collapsed;
        ProfilePaneColumn.Width = new GridLength(0);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ResizeColumns);
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
        AgentTimelineListView.ItemsSource = App.Timeline.Events;
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

        // Also feed Agent-side log panel with level filtering
        if (entry.Level >= _agentLogLevel)
        {
            var agentItem = new ListBoxItem
            {
                Content = $"[{entry.Time:HH:mm:ss}] {icon} [{entry.Category}] {entry.Message}",
                Foreground = item.Foreground
            };
            AgentLogListBox.Items.Add(agentItem);
            AgentLogListBox.ScrollIntoView(agentItem);

            while (AgentLogListBox.Items.Count > 500)
                AgentLogListBox.Items.RemoveAt(0);
        }
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

    // ═══════════════════════════════════════════════════════════════════════════════
    // AGENT SIDE LOG PANEL
    // ═══════════════════════════════════════════════════════════════════════════════

    private void AgentLogLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AgentLogLevelCombo.SelectedItem is ComboBoxItem item)
        {
            _agentLogLevel = item.Content?.ToString() switch
            {
                "Debug" => LogLevel.Debug,
                "Info" => LogLevel.Info,
                "Warning" => LogLevel.Warning,
                "Error" => LogLevel.Error,
                _ => LogLevel.Info
            };
        }
    }

    private void ClearAgentLog_Click(object sender, RoutedEventArgs e)
    {
        AgentLogListBox.Items.Clear();
    }

    private void ToggleAgentLog_Click(object sender, RoutedEventArgs e)
    {
        var isVisible = AgentLogPanelCol.Width.Value > 0;
        if (isVisible)
        {
            AgentLogPanelCol.Width = new GridLength(0);
            AgentLogPanelCol.MinWidth = 0;
            AgentLogSplitter.Visibility = Visibility.Collapsed;
            AgentLogPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            AgentLogPanelCol.Width = new GridLength(350);
            AgentLogPanelCol.MinWidth = 200;
            AgentLogSplitter.Visibility = Visibility.Visible;
            AgentLogPanel.Visibility = Visibility.Visible;
        }
    }

    private void AgentTimelineFilter_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ClearAgentTimeline_Click(object sender, RoutedEventArgs e)
    {
        AgentTimelineListView.Items.Clear();
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

    // === Mode Switching ===

    private void ClassicMode_Click(object sender, RoutedEventArgs e) => SwitchMode(AppMode.Classic);
    private void AgentMode_Click(object sender, RoutedEventArgs e) => SwitchMode(AppMode.Agent);

    private void SwitchMode(AppMode mode)
    {
        if (_currentMode == mode) return;
        _currentMode = mode;

        // Persist to config
        var cfg = App.Config.GetConfig();
        cfg.Settings.Mode = mode;
        App.Config.SaveConfig(cfg);

        ApplyMode();
        App.Log.Info("Mode", $"Switched to {mode} mode");
    }

    private void ApplyMode()
    {
        var isAgent = _currentMode == AppMode.Agent;

        // Toggle button styles
        ClassicModeBtn.Style = (Style)FindResource(isAgent ? "ModeToggleInactive" : "ModeToggleActive");
        AgentModeBtn.Style = (Style)FindResource(isAgent ? "ModeToggleActive" : "ModeToggleInactive");

        // Toggle panels
        RulesPanel.Visibility = isAgent ? Visibility.Collapsed : Visibility.Visible;
        AgentChatPanel.Visibility = isAgent ? Visibility.Visible : Visibility.Collapsed;

        // In Agent mode, hide the classic bottom log panel (agent has its own side panel)
        if (isAgent)
        {
            LogPanel.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            LogPanelRow.Height = new GridLength(0);
            LogPanelRow.MinHeight = 0;
            SplitterRow.Height = new GridLength(0);
        }
        else if (_isExpanded)
        {
            LogPanel.Visibility = Visibility.Visible;
            PanelSplitter.Visibility = Visibility.Visible;
            LogPanelRow.Height = new GridLength(1, GridUnitType.Star);
            LogPanelRow.MinHeight = 150;
        }

        // Show/hide classic-only controls
        AutomationToggle.Visibility = isAgent ? Visibility.Collapsed : Visibility.Visible;

        // Update agent status
        if (isAgent)
            UpdateAgentStatus();
    }

    private void UpdateAgentStatus()
    {
        var agent = App.Agent;
        if (agent == null) return;
        AgentStatusText.Text = $"  \u2022  {agent.StatusText}";
    }

    // === Agent Chat Handlers ===

    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendChatMessage();

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift+Enter: insert newline
                var caretIndex = ChatInputBox.CaretIndex;
                ChatInputBox.Text = ChatInputBox.Text.Insert(caretIndex, Environment.NewLine);
                ChatInputBox.CaretIndex = caretIndex + Environment.NewLine.Length;
                e.Handled = true;
            }
            else if (!string.IsNullOrWhiteSpace(ChatInputBox.Text))
            {
                // Enter alone: send
                SendChatMessage();
                e.Handled = true;
            }
        }
    }

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ChatSendBtn.IsEnabled = !string.IsNullOrWhiteSpace(ChatInputBox.Text);
    }

    private async void SendChatMessage()
    {
        var text = ChatInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Add user message to chat
        AddChatMessage(text, isUser: true);
        ChatInputBox.Text = "";
        ChatSendBtn.IsEnabled = false;
        ChatInputBox.IsEnabled = false;

        // Hide welcome panel once user sends a message
        AgentWelcomePanel.Visibility = Visibility.Collapsed;

        // Show typing indicator (we'll update this dynamically with progress)
        var typingBubble = AddChatMessage("\u2219\u2219\u2219 thinking", isUser: false);
        var typingTextBlock = typingBubble.Child as TextBlock;

        // Cancel any previous in-flight request
        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();

        // Track intermediate text messages shown during tool calls
        var intermediateMessages = new List<Border>();

        // Subscribe to progress events for live status updates
        void OnProgress(AgentProgress progress)
        {
            Dispatcher.BeginInvoke(() =>
            {
                switch (progress.Kind)
                {
                    case AgentProgressKind.IntermediateText:
                        // LLM produced text before tool calls — show it as a real message
                        if (!string.IsNullOrWhiteSpace(progress.IntermediateText))
                        {
                            var msg = AddChatMessage(progress.IntermediateText, isUser: false);
                            intermediateMessages.Add(msg);
                        }
                        break;

                    case AgentProgressKind.ToolCallStarting:
                        // Update the typing bubble with tool name
                        if (typingTextBlock != null)
                            typingTextBlock.Text = progress.Message;
                        break;

                    case AgentProgressKind.ToolCallCompleted:
                        // Brief flash of completion (will be overwritten by next tool or "Analyzing")
                        if (typingTextBlock != null)
                            typingTextBlock.Text = progress.Message;
                        break;

                    case AgentProgressKind.NewIteration:
                        if (typingTextBlock != null)
                            typingTextBlock.Text = progress.Message;
                        break;
                }

                ChatScrollViewer.ScrollToEnd();
            });
        }

        App.Agent.OnProgress += OnProgress;

        try
        {
            var response = await App.Agent.SendMessageAsync(text, _chatCts.Token);

            // Remove typing indicator
            ChatMessagesPanel.Children.Remove(typingBubble);

            // Show the final response
            AddChatMessage(response.Text, isUser: false, isError: response.IsError);

            // If response contains a test flow, show a run button
            if (response.HasFlow)
            {
                AddFlowActionBar(response.Flow!);
            }

            UpdateAgentStatus();
        }
        catch (OperationCanceledException)
        {
            ChatMessagesPanel.Children.Remove(typingBubble);
        }
        catch (Exception ex)
        {
            ChatMessagesPanel.Children.Remove(typingBubble);
            AddChatMessage($"Unexpected error: {ex.Message}", isUser: false, isError: true);
        }
        finally
        {
            App.Agent.OnProgress -= OnProgress;
            ChatInputBox.IsEnabled = true;
            ChatInputBox.Focus();
        }
    }

    private Border AddChatMessage(string text, bool isUser, bool isError = false)
    {
        var bgColor = isUser
            ? Color.FromRgb(0, 120, 212)    // PrimaryColor
            : isError
                ? Color.FromRgb(80, 30, 30) // Error tint
                : Color.FromRgb(54, 54, 54);  // CardColor

        var bubble = new Border
        {
            Background = new SolidColorBrush(bgColor),
            CornerRadius = new CornerRadius(12, 12, isUser ? 2 : 12, isUser ? 12 : 2),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(isUser ? 60 : 0, 2, isUser ? 0 : 60, 2),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 500
        };

        if (isUser)
        {
            // User messages: plain text
            bubble.Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
        }
        else
        {
            // Assistant messages: render with basic markdown + clickable paths
            bubble.Child = RenderMarkdownContent(text, isError);
        }

        ChatMessagesPanel.Children.Add(bubble);
        ChatScrollViewer.ScrollToEnd();
        return bubble;
    }

    /// <summary>
    /// Renders assistant message text with basic markdown support:
    /// - **bold** text
    /// - ## headings
    /// - Bullet points (-, •)
    /// - Clickable file paths (opens in explorer / default viewer)
    /// - Inline screenshot thumbnails
    /// </summary>
    private UIElement RenderMarkdownContent(string text, bool isError)
    {
        var panel = new StackPanel();
        var lines = text.Split('\n');
        var defaultFg = isError ? Brushes.Salmon : Brushes.White;
        var mutedFg = new SolidColorBrush(Color.FromRgb(170, 170, 170));
        var accentFg = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        var headingFg = new SolidColorBrush(Color.FromRgb(100, 200, 255));

        // Track last mentioned directory so bare filenames can be resolved
        string? lastDirPath = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            // Skip code fence markers
            if (line.TrimStart().StartsWith("```")) continue;

            // Track directory paths mentioned in the text (e.g. C:\path\to\folder\)
            var dirMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"([A-Za-z]:\\[^'\""*<>|]+\\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dirMatch.Success)
            {
                var candidate = dirMatch.Value.TrimEnd('\'', '`', ' ');
                if (System.IO.Directory.Exists(candidate))
                    lastDirPath = candidate;
            }

            // Heading (## or ###)
            if (line.TrimStart().StartsWith("##"))
            {
                var headingText = line.TrimStart('#', ' ');
                panel.Children.Add(new TextBlock
                {
                    Text = headingText,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = headingFg,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 2)
                });
                continue;
            }

            // Check for full file paths (e.g. C:\path\file.png)
            var pathMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"([A-Za-z]:\\[^'\""\s*<>|]+\.(png|jpg|jpeg|bmp|json|txt|log))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Also check for bare filenames (e.g. 'screenshot_xxx.png')
            string? filePath = null;
            if (pathMatch.Success)
            {
                filePath = pathMatch.Value.TrimEnd('\'', '`', '*');
            }
            else
            {
                var bareMatch = System.Text.RegularExpressions.Regex.Match(line,
                    @"['\x22`]?([\w\-]+\.(png|jpg|jpeg|bmp|json|txt|log))['\x22`]?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (bareMatch.Success)
                {
                    var bareName = bareMatch.Groups[1].Value;
                    // Try to resolve using last known directory
                    if (lastDirPath != null)
                    {
                        var fullPath = System.IO.Path.Combine(lastDirPath, bareName);
                        if (System.IO.File.Exists(fullPath))
                            filePath = fullPath;
                    }
                    // If not found, search in report screenshots folder
                    if (filePath == null)
                    {
                        var screenshotsDir = System.IO.Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory, "reports", "_screenshots");
                        if (System.IO.Directory.Exists(screenshotsDir))
                        {
                            var found = System.IO.Path.Combine(screenshotsDir, bareName);
                            if (System.IO.File.Exists(found))
                                filePath = found;
                        }
                    }
                }
            }

            if (filePath != null)
            {
                var isImage = filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                              filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                              filePath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);

                // Add the text line with clickable path
                var inlineTb = BuildInlineTextBlock(line, filePath, defaultFg, accentFg);
                panel.Children.Add(inlineTb);

                // Add inline thumbnail for images
                if (isImage && System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(filePath);
                        bitmap.DecodePixelWidth = 400;
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        var img = new System.Windows.Controls.Image
                        {
                            Source = bitmap,
                            MaxWidth = 400,
                            MaxHeight = 250,
                            Stretch = System.Windows.Media.Stretch.Uniform,
                            Margin = new Thickness(0, 4, 0, 4),
                            Cursor = Cursors.Hand,
                            ToolTip = "Click to open full image"
                        };
                        var capturedPath = filePath;
                        img.MouseLeftButtonUp += (s, e) =>
                        {
                            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedPath) { UseShellExecute = true }); }
                            catch { }
                        };

                        var imgBorder = new Border
                        {
                            CornerRadius = new CornerRadius(6),
                            ClipToBounds = true,
                            Child = img,
                            Margin = new Thickness(0, 2, 0, 2)
                        };
                        panel.Children.Add(imgBorder);
                    }
                    catch { /* Ignore image load errors */ }
                }
                continue;
            }

            // Regular line — parse inline bold (**text**)
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1)
            };

            // Bullet points
            bool isBullet = line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("• ");
            if (isBullet)
            {
                line = "  \u2022 " + line.TrimStart('-', '•', ' ');
            }

            // Parse **bold** segments
            var parts = System.Text.RegularExpressions.Regex.Split(line, @"(\*\*.*?\*\*)");
            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                {
                    tb.Inlines.Add(new System.Windows.Documents.Run(part[2..^2])
                    {
                        FontWeight = FontWeights.Bold,
                        Foreground = defaultFg
                    });
                }
                else
                {
                    tb.Inlines.Add(new System.Windows.Documents.Run(part)
                    {
                        Foreground = defaultFg
                    });
                }
            }

            panel.Children.Add(tb);
        }

        return panel;
    }

    /// <summary>
    /// Builds a TextBlock with a clickable file path hyperlink.
    /// </summary>
    private static TextBlock BuildInlineTextBlock(string line, string filePath, Brush defaultFg, Brush linkFg)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 1)
        };

        // Try to find the full path in the line, or fall back to just the filename
        var fileName = System.IO.Path.GetFileName(filePath);
        var idx = line.IndexOf(filePath, StringComparison.OrdinalIgnoreCase);
        var matchedText = filePath;
        if (idx < 0)
        {
            idx = line.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
            matchedText = fileName;
        }

        if (idx >= 0)
        {
            // Text before path
            if (idx > 0)
            {
                var before = line[..idx].TrimEnd('\'', '`', ' ');
                if (!string.IsNullOrWhiteSpace(before))
                    tb.Inlines.Add(new System.Windows.Documents.Run(before + " ") { Foreground = defaultFg });
            }

            // Clickable path — show filename, tooltip shows full path
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(fileName))
            {
                Foreground = linkFg,
                TextDecorations = null,
                Cursor = Cursors.Hand
            };
            link.ToolTip = filePath;
            var captured = filePath;
            link.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(captured) { UseShellExecute = true }); }
                catch { }
            };
            tb.Inlines.Add(link);

            // Text after the matched portion
            var afterIdx = idx + matchedText.Length;
            if (afterIdx < line.Length)
            {
                var after = line[afterIdx..].TrimStart('\'', '`');
                if (!string.IsNullOrWhiteSpace(after))
                    tb.Inlines.Add(new System.Windows.Documents.Run(after) { Foreground = defaultFg });
            }
        }
        else
        {
            tb.Inlines.Add(new System.Windows.Documents.Run(line) { Foreground = defaultFg });
        }

        return tb;
    }

    /// <summary>
    /// Adds a compact action bar below a test flow response, allowing the user to run or copy it.
    /// </summary>
    private void AddFlowActionBar(Models.TestFlow flow)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 60, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var runBtn = new Button
        {
            Content = $"\u25B6 Run '{flow.TestName}' ({flow.Steps.Count} steps)",
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),  // AccentColor
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        runBtn.Click += async (s, e) =>
        {
            runBtn.IsEnabled = false;
            runBtn.Content = "\u23f3 Running...";
            App.Log.Info("Agent", $"Executing flow '{flow.TestName}' ({flow.Steps.Count} steps)");

            try
            {
                var report = await Task.Run(() => App.FlowExecutor.ExecuteFlowAsync(flow,
                    onStepComplete: (step, total, result) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            var icon = result.Status == Models.StepStatus.Passed ? "\u2705" :
                                       result.Status == Models.StepStatus.Failed ? "\u274c" :
                                       result.Status == Models.StepStatus.Skipped ? "\u23ed" : "\u26a0";
                            runBtn.Content = $"{icon} Step {step}/{total}: {result.Action} ({result.TimeMs}ms)";
                        });
                    }));

                // Auto-save report to disk
                string? savedPath = null;
                try { savedPath = App.Reports?.SaveReport(report); } catch { }

                // Show report in chat
                var icon2 = report.Result == "passed" ? "\u2705" : "\u274c";
                var reportJson = System.Text.Json.JsonSerializer.Serialize(report, Models.FlowJson.Options);
                var savedMsg = savedPath != null ? $"\n📁 Report saved: {savedPath}" : "";
                AddChatMessage(
                    $"{icon2} **{report.Result.ToUpperInvariant()}** — {report.PassedCount} passed, {report.FailedCount} failed, {report.SkippedCount} skipped ({report.TotalTimeMs}ms){savedMsg}\n\n" +
                    $"```json\n{reportJson}\n```",
                    isUser: false);
            }
            catch (Exception ex)
            {
                AddChatMessage($"\u274c Flow execution error: {ex.Message}", isUser: false);
                App.Log.Error("Agent", $"Flow execution failed: {ex.Message}");
            }
            finally
            {
                runBtn.Content = $"\u25B6 Run '{flow.TestName}' ({flow.Steps.Count} steps)";
                runBtn.IsEnabled = true;
            }
        };

        var copyBtn = new Button
        {
            Content = "\ud83d\udccb Copy JSON",
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(54, 54, 54)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        copyBtn.Click += (s, e) =>
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(flow, Models.FlowJson.Options);
                Clipboard.SetText(json);
                App.Log.Info("Agent", $"Test flow '{flow.TestName}' copied to clipboard");
            }
            catch (Exception ex)
            {
                App.Log.Error("Agent", $"Failed to copy flow: {ex.Message}");
            }
        };

        bar.Children.Add(runBtn);
        bar.Children.Add(copyBtn);
        ChatMessagesPanel.Children.Add(bar);
        ChatScrollViewer.ScrollToEnd();
    }

    private void LoadFlow_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load Test Flow",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var flow = ReportService.LoadFlowFromFile(dlg.FileName);
            if (flow == null)
            {
                AddChatMessage("\u274c Could not parse flow from file. Ensure it's valid TestFlow JSON.", isUser: false);
                return;
            }

            ImportFlow(flow, $"\ud83d\udcc2 Loaded from: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            AddChatMessage($"\u274c Error loading flow: {ex.Message}", isUser: false);
            App.Log.Error("Agent", $"Load flow failed: {ex.Message}");
        }
    }

    private void PasteFlow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var clipText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                AddChatMessage("\u274c Clipboard is empty.", isUser: false);
                return;
            }

            var flow = ReportService.ParseFlowFromJson(clipText);
            if (flow == null)
            {
                AddChatMessage("\u274c Could not parse flow from clipboard. Ensure it's valid TestFlow JSON.", isUser: false);
                return;
            }

            ImportFlow(flow, "\ud83d\udccb Pasted from clipboard");
        }
        catch (Exception ex)
        {
            AddChatMessage($"\u274c Error parsing clipboard flow: {ex.Message}", isUser: false);
            App.Log.Error("Agent", $"Paste flow failed: {ex.Message}");
        }
    }

    private void ImportFlow(TestFlow flow, string sourceLabel)
    {
        AgentWelcomePanel.Visibility = Visibility.Collapsed;

        // Validate the flow
        var validator = new FlowValidatorService(App.Log);
        var result = validator.Validate(flow);

        if (!result.IsValid)
        {
            var errors = string.Join("\n", result.Errors.Select(e => $"  \u2022 {e}"));
            AddChatMessage(
                $"{sourceLabel}\n\n\u26a0 **Validation failed** ({result.Errors.Count} errors):\n{errors}",
                isUser: false);
            return;
        }

        var warnings = result.Warnings.Count > 0
            ? $"\n\u26a0 {result.Warnings.Count} warning(s)"
            : "";

        AddChatMessage(
            $"{sourceLabel}\n\n\u2705 **{flow.TestName}** — {flow.Steps.Count} steps, validated OK{warnings}",
            isUser: false);

        AddFlowActionBar(flow);
        App.Log.Info("Agent", $"Imported flow '{flow.TestName}' ({flow.Steps.Count} steps): {sourceLabel}");
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        ChatMessagesPanel.Children.Clear();
        AgentWelcomePanel.Visibility = Visibility.Visible;
        App.Agent?.ClearHistory();
        UpdateAgentStatus();
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

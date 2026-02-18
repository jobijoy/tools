using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using IdolClick.Models;

namespace IdolClick.UI.Controls;

public partial class RulesPanelControl : UserControl
{
    private List<Rule> _allRules = new();
    private string _searchText = "";
    private Rule? _selectedRule;
    private bool _isExpanded;

    /// <summary>Raised when automation status may have changed (rule toggled, etc.).</summary>
    public event Action? StatusChanged;

    public RulesPanelControl()
    {
        InitializeComponent();
    }

    // ── Public API ────────────────────────────────────────────────────

    public void LoadRules()
    {
        var cfg = App.Config.GetConfig();
        _allRules = cfg.Rules ?? new List<Rule>();
        ApplyRulesFilter();
    }

    public void ClearSearch()
    {
        _searchText = "";
        SearchTextBox.Text = "";
        ApplyRulesFilter();
    }

    public void SetExpanded(bool expanded)
    {
        _isExpanded = expanded;
        var vis = expanded ? Visibility.Visible : Visibility.Collapsed;
        var cVis = expanded ? Visibility.Collapsed : Visibility.Visible;

        SearchPanel.Visibility = vis;
        RulesLabel.Visibility = cVis;
        ImportBtn.Visibility = vis;
        ExportBtn.Visibility = vis;
        EditBtn.Visibility = vis;
        DelBtn.Visibility = vis;
    }

    public void ResizeColumns(double availableWidth)
    {
        // No-op: card layout reflows automatically
    }

    public int EnabledRuleCount => _allRules.Count(r => r.Enabled);
    public int TotalRuleCount => _allRules.Count;

    // ── Private handlers ──────────────────────────────────────────────

    private void ApplyRulesFilter()
    {
        IEnumerable<Rule> filtered = _allRules;
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var st = _searchText;
            filtered = filtered.Where(r =>
                (r.Name?.Contains(st, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.TargetApp?.Contains(st, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.MatchText?.Contains(st, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        RulesListView.ItemsSource = filtered.ToList();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchTextBox.Text;
        ApplyRulesFilter();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RuleEditorWindow(null) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true && editor.Rule != null)
        {
            var cfg = App.Config.GetConfig();
            cfg.Rules.Add(editor.Rule);
            App.Config.SaveConfig(cfg);
            LoadRules();
            App.Log.Info("Rules", $"Added rule: {editor.Rule.Name}");
            StatusChanged?.Invoke();
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e) => EditRule();

    private void RulesListView_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => EditRule();

    private void EditRule()
    {
        if (RulesListView.SelectedItem is not Rule rule) return;

        var editor = new RuleEditorWindow(rule) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true)
        {
            App.Config.SaveConfig(App.Config.GetConfig());
            LoadRules();
            App.Log.Info("Rules", $"Updated rule: {rule.Name}");
            StatusChanged?.Invoke();
        }
    }

    private void RulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRule = RulesListView.SelectedItem as Rule;
        UpdateDeleteButtonState();
    }

    private void UpdateDeleteButtonState()
    {
        if (_isExpanded)
        {
            EditBtn.Visibility = _selectedRule != null ? Visibility.Visible : Visibility.Collapsed;
            DelBtn.Visibility = _selectedRule != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is not Rule rule) return;

        var result = MessageBox.Show($"Delete rule '{rule.Name}'?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            var cfg = App.Config.GetConfig();
            cfg.Rules.Remove(rule);
            App.Config.SaveConfig(cfg);
            LoadRules();
            App.Log.Info("Rules", $"Deleted rule: {rule.Name}");
            StatusChanged?.Invoke();
        }
    }

    private void RuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is Rule rule)
            rule.NotifyEnabledChanged();
        App.Config.SaveConfig(App.Config.GetConfig());
        App.Profiles.SaveCurrentProfile();
        StatusChanged?.Invoke();
    }

    private void RuleRunToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Rule rule)
        {
            rule.IsRunning = !rule.IsRunning;
            App.Log.Info("Rule", $"Rule '{rule.Name}' {(rule.IsRunning ? "resumed" : "paused")}");
            StatusChanged?.Invoke();
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
                if (doc.RootElement.TryGetProperty("rules", out var rulesElement))
                    importedRules = JsonSerializer.Deserialize<List<Rule>>(rulesElement.GetRawText());
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    importedRules = JsonSerializer.Deserialize<List<Rule>>(json);

                if (importedRules == null || importedRules.Count == 0)
                {
                    MessageBox.Show("No rules found in the file.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    $"Found {importedRules.Count} rules.\n\nReplace existing rules or merge with current rules?",
                    "Import Rules",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Cancel) return;

                var cfg = App.Config.GetConfig();
                if (result == MessageBoxResult.Yes)
                {
                    cfg.Rules = importedRules;
                }
                else
                {
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
}

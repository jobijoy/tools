using System.Windows;
using System.Windows.Controls;
using AutomationTool.Models;

namespace AutomationTool.UI;

public partial class RuleEditorWindow : Window
{
    public Rule? Rule { get; private set; }
    private readonly Rule? _existing;

    public RuleEditorWindow(Rule? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing != null)
        {
            Title = "Edit Rule";
            LoadRule(existing);
        }
        else
        {
            Title = "New Rule";
        }
    }

    private void LoadRule(Rule r)
    {
        NameBox.Text = r.Name;
        TargetAppBox.Text = r.TargetApp;
        WindowTitleBox.Text = r.WindowTitle ?? "";
        ElementTypeCombo.Text = r.ElementType;
        MatchTextBox.Text = r.MatchText;
        UseRegexCheck.IsChecked = r.UseRegex;
        ExcludeTextBox.Text = string.Join(", ", r.ExcludeTexts);

        if (r.Region != null)
        {
            RegionXBox.Text = r.Region.X.ToString("0.##");
            RegionYBox.Text = r.Region.Y.ToString("0.##");
            RegionWidthBox.Text = r.Region.Width.ToString("0.##");
            RegionHeightBox.Text = r.Region.Height.ToString("0.##");
        }

        ActionCombo.Text = r.Action;
        KeysBox.Text = r.Keys ?? "";
        ScriptLangCombo.Text = r.ScriptLanguage;
        ScriptBox.Text = r.Script ?? "";
        NotificationBox.Text = r.NotificationMessage ?? "";

        CooldownBox.Text = r.CooldownSeconds.ToString();
        TimeWindowBox.Text = r.TimeWindow ?? "";
        RequireFocusCheck.IsChecked = r.RequireFocus;
        ConfirmCheck.IsChecked = r.ConfirmBeforeAction;
        AlertIfBox.Text = string.Join(", ", r.AlertIfContains);

        UpdateActionPanels();
    }

    private void ActionCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionPanels();
    }

    private void UpdateActionPanels()
    {
        if (SendKeysPanel == null) return; // Not loaded yet

        var action = (ActionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Click";

        SendKeysPanel.Visibility = action == "SendKeys" ? Visibility.Visible : Visibility.Collapsed;
        ScriptPanel.Visibility = action == "RunScript" ? Visibility.Visible : Visibility.Collapsed;
        NotificationPanel.Visibility = action == "ShowNotification" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Region Selection:\n\n" +
            "1. This feature will show a screen overlay\n" +
            "2. Click and drag to select a region\n" +
            "3. The coordinates will be saved\n\n" +
            "(Coming in Phase 3)",
            "Select Region",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("Please enter a rule name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ScreenRegion? region = null;
        if (double.TryParse(RegionXBox.Text, out var x) &&
            double.TryParse(RegionYBox.Text, out var y) &&
            double.TryParse(RegionWidthBox.Text, out var w) &&
            double.TryParse(RegionHeightBox.Text, out var h) &&
            (x != 0 || y != 0 || w != 1 || h != 1))
        {
            region = new ScreenRegion { X = x, Y = y, Width = w, Height = h };
        }

        Rule = new Rule
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N")[..8],
            Name = NameBox.Text.Trim(),
            Enabled = _existing?.Enabled ?? true,
            TargetApp = TargetAppBox.Text.Trim(),
            WindowTitle = string.IsNullOrWhiteSpace(WindowTitleBox.Text) ? null : WindowTitleBox.Text.Trim(),
            ElementType = (ElementTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Button",
            MatchText = MatchTextBox.Text.Trim(),
            UseRegex = UseRegexCheck.IsChecked == true,
            ExcludeTexts = ExcludeTextBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Region = region,
            Action = (ActionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Click",
            Keys = string.IsNullOrWhiteSpace(KeysBox.Text) ? null : KeysBox.Text.Trim(),
            ScriptLanguage = (ScriptLangCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "powershell",
            Script = string.IsNullOrWhiteSpace(ScriptBox.Text) ? null : ScriptBox.Text,
            NotificationMessage = string.IsNullOrWhiteSpace(NotificationBox.Text) ? null : NotificationBox.Text.Trim(),
            CooldownSeconds = int.TryParse(CooldownBox.Text, out var cd) ? cd : 2,
            TimeWindow = string.IsNullOrWhiteSpace(TimeWindowBox.Text) ? null : TimeWindowBox.Text.Trim(),
            RequireFocus = RequireFocusCheck.IsChecked == true,
            ConfirmBeforeAction = ConfirmCheck.IsChecked == true,
            AlertIfContains = AlertIfBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            LastTriggered = _existing?.LastTriggered,
            TriggerCount = _existing?.TriggerCount ?? 0
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

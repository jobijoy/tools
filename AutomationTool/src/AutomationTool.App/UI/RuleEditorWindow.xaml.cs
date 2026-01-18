using System.Windows;
using System.Windows.Controls;
using AutomationTool.Models;
using Microsoft.Win32;

namespace AutomationTool.UI;

public partial class RuleEditorWindow : Window
{
    public Rule? Rule { get; private set; }
    private readonly Rule? _existing;

    public RuleEditorWindow(Rule? existing)
    {
        InitializeComponent();
        _existing = existing;

        // Load plugins into combo
        LoadPlugins();

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

    private void LoadPlugins()
    {
        PluginCombo.ItemsSource = App.Plugins.Plugins;
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
        DryRunCheck.IsChecked = r.DryRun;

        // Plugin
        if (!string.IsNullOrEmpty(r.PluginId))
        {
            PluginCombo.SelectedValue = r.PluginId;
        }

        // Notification hooks
        if (r.Notification != null)
        {
            EnableNotificationCheck.IsChecked = true;
            NotificationTypeCombo.Text = r.Notification.Type ?? "toast";
            NotificationMessageBox.Text = r.Notification.Message ?? "";
            WebhookUrlBox.Text = r.Notification.Url ?? "";
            NotificationScriptPathBox.Text = r.Notification.ScriptPath ?? "";
            NotifyOnSuccessCheck.IsChecked = r.Notification.OnSuccess;
            NotifyOnFailureCheck.IsChecked = r.Notification.OnFailure;
            UpdateNotificationPanels();
        }

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
        PluginPanel.Visibility = action == "Plugin" ? Visibility.Visible : Visibility.Collapsed;

        // Update plugin description
        if (action == "Plugin" && PluginCombo.SelectedItem is Services.PluginInfo plugin)
        {
            PluginDescriptionText.Text = plugin.Description;
        }
    }

    private void EnableNotification_Changed(object sender, RoutedEventArgs e)
    {
        NotificationHookPanel.Visibility = EnableNotificationCheck.IsChecked == true 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }

    private void NotificationType_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateNotificationPanels();
    }

    private void UpdateNotificationPanels()
    {
        if (WebhookOptionsPanel == null) return;

        var type = (NotificationTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "toast";
        WebhookOptionsPanel.Visibility = type == "webhook" ? Visibility.Visible : Visibility.Collapsed;
        ScriptHookOptionsPanel.Visibility = type == "script" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Script files (*.ps1;*.csx)|*.ps1;*.csx|PowerShell (*.ps1)|*.ps1|C# Script (*.csx)|*.csx|All files (*.*)|*.*",
            Title = "Select Script File"
        };

        if (dialog.ShowDialog() == true)
        {
            ScriptBox.Text = dialog.FileName;
        }
    }

    private void BrowseNotificationScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Script files (*.ps1;*.csx)|*.ps1;*.csx|All files (*.*)|*.*",
            Title = "Select Notification Script"
        };

        if (dialog.ShowDialog() == true)
        {
            NotificationScriptPathBox.Text = dialog.FileName;
        }
    }

    private async void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        // Hide this window temporarily
        var wasTopmost = Topmost;
        Hide();
        await Task.Delay(200); // Let window fully hide

        try
        {
            var region = await App.RegionCapture.CaptureRegionAsync();
            if (region != null)
            {
                // Get screen dimensions for normalization
                var screen = System.Windows.Forms.Screen.PrimaryScreen;
                if (screen != null)
                {
                    var normalized = region.ToScreenNormalized(screen.Bounds.Width, screen.Bounds.Height);
                    RegionXBox.Text = normalized.X.ToString("0.####");
                    RegionYBox.Text = normalized.Y.ToString("0.####");
                    RegionWidthBox.Text = normalized.Width.ToString("0.####");
                    RegionHeightBox.Text = normalized.Height.ToString("0.####");
                }
                else
                {
                    // Fallback: use absolute pixels
                    RegionXBox.Text = region.X.ToString();
                    RegionYBox.Text = region.Y.ToString();
                    RegionWidthBox.Text = region.Width.ToString();
                    RegionHeightBox.Text = region.Height.ToString();
                }
            }
        }
        finally
        {
            Show();
            Topmost = wasTopmost;
            Activate();
        }
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
            DryRun = DryRunCheck.IsChecked == true,
            PluginId = PluginCombo.SelectedValue?.ToString(),
            Notification = BuildNotificationConfig(),
            LastTriggered = _existing?.LastTriggered,
            TriggerCount = _existing?.TriggerCount ?? 0
        };

        DialogResult = true;
        Close();
    }

    private NotificationConfig? BuildNotificationConfig()
    {
        if (EnableNotificationCheck.IsChecked != true)
            return null;

        var type = (NotificationTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "toast";

        return new NotificationConfig
        {
            Type = type,
            Message = NotificationMessageBox.Text.Trim(),
            Url = type == "webhook" ? WebhookUrlBox.Text.Trim() : null,
            ScriptPath = type == "script" ? NotificationScriptPathBox.Text.Trim() : null,
            OnSuccess = NotifyOnSuccessCheck.IsChecked == true,
            OnFailure = NotifyOnFailureCheck.IsChecked == true
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

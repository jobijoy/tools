using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AutomationTool.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var cfg = App.Config.GetConfig();
        var s = cfg.Settings;

        HotkeyBox.Text = s.ToggleHotkey;
        LogLevelCombo.Text = s.LogLevel;
        MinimizeToTrayCheck.IsChecked = s.MinimizeToTray;
        ShowPanelOnStartCheck.IsChecked = s.ShowPanelOnStart;
        MouseNudgeCheck.IsChecked = s.GlobalMouseNudge;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Config.GetConfig();
        var s = cfg.Settings;

        s.ToggleHotkey = HotkeyBox.Text.Trim();
        s.LogLevel = (LogLevelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Info";
        s.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        s.ShowPanelOnStart = ShowPanelOnStartCheck.IsChecked == true;
        s.GlobalMouseNudge = MouseNudgeCheck.IsChecked == true;

        App.Config.SaveConfig(cfg);
        App.Log.SetLevel(s.LogLevel);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start("explorer.exe", AppContext.BaseDirectory);
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsPath);
        Process.Start("explorer.exe", logsPath);
    }
}

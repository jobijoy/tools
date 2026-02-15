using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace IdolClick.UI;

public partial class SettingsWindow : Window
{
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "IdolClick";

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
        ShowPanelOnStartCheck.IsChecked = s.ShowPanelOnStart;
        MouseNudgeCheck.IsChecked = s.GlobalMouseNudge;
        ShowExecutionCountCheck.IsChecked = s.ShowExecutionCount;
        ClickRadarCheck.IsChecked = s.ClickRadar;
        StartWithWindowsCheck.IsChecked = IsStartupEnabled();
    }

    private bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    private void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Path.Combine(AppContext.BaseDirectory, "IdolClick.exe");
                key.SetValue(AppName, $"\"{exePath}\"");
                App.Log.Info("Settings", "Added to Windows startup");
            }
            else
            {
                key.DeleteValue(AppName, false);
                App.Log.Info("Settings", "Removed from Windows startup");
            }
        }
        catch (Exception ex)
        {
            App.Log.Error("Settings", $"Failed to update startup setting: {ex.Message}");
            MessageBox.Show($"Failed to update startup setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Config.GetConfig();
        var s = cfg.Settings;

        s.ToggleHotkey = HotkeyBox.Text.Trim();
        s.LogLevel = (LogLevelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Info";
        s.ShowPanelOnStart = ShowPanelOnStartCheck.IsChecked == true;
        s.GlobalMouseNudge = MouseNudgeCheck.IsChecked == true;
        s.ShowExecutionCount = ShowExecutionCountCheck.IsChecked == true;
        s.ClickRadar = ClickRadarCheck.IsChecked == true;

        App.Config.SaveConfig(cfg);
        App.Log.SetLevel(s.LogLevel);

        // Handle Windows startup separately (registry, not config)
        SetStartupEnabled(StartWithWindowsCheck.IsChecked == true);

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

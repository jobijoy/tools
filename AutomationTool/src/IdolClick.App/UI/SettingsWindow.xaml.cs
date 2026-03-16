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
        
        // Safety settings
        KillSwitchHotkeyBox.Text = s.KillSwitchHotkey;
        AllowedProcessesBox.Text = string.Join(", ", s.AllowedProcesses);
        AllowlistWarning.Visibility = s.AllowedProcesses.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        
        // Agent settings
        var ai = cfg.Ai;
        AgentEndpointBox.Text = ai.Endpoint;
        AgentModelBox.Text = ai.ModelId;
        AgentApiKeyBox.Password = ai.ApiKey;
        AgentMaxTokensBox.Text = ai.MaxTokens.ToString();
        AgentTemperatureBox.Text = ai.Temperature.ToString("F1");
        
        // Vision fallback settings
        VisionFallbackCheck.IsChecked = ai.VisionFallbackEnabled;
        VisionModelBox.Text = ai.VisionModelId;
        VisionConfidenceBox.Text = ai.VisionConfidenceThreshold.ToString("F1");
        
        // Voice input settings
        VoiceEnabledCheck.IsChecked = ai.VoiceInputEnabled;
        WhisperEndpointBox.Text = ai.WhisperEndpoint;
        WhisperApiKeyBox.Password = ai.WhisperApiKey;
        WhisperDeploymentBox.Text = ai.WhisperDeploymentId;
        VoiceLanguageBox.Text = ai.VoiceLanguage;
        UpdateVoiceConfigStatus(ai);

        var review = cfg.Review;
        ReviewEnabledCheck.IsChecked = review.Enabled;
        ReviewMicCheck.IsChecked = review.MicEnabled;
        ReviewHotkeyBox.Text = review.SaveBufferHotkey;
        ReviewDurationBox.Text = review.BufferDurationMinutes.ToString();
        ReviewFrameIntervalBox.Text = review.FrameIntervalMs.ToString();
        ReviewAudioChunkBox.Text = review.AudioChunkSeconds.ToString();
        ReviewOutputDirectoryBox.Text = review.OutputDirectory;
        UpdateReviewConfigStatus(review);

        var capture = cfg.Capture;
        RememberOrbLocationCheck.IsChecked = capture.RememberOrbLocation;
        SelectOrbPlacement(capture.OrbPlacement);
        UpdateOrbConfigStatus(capture);
    }

    private void UpdateVoiceConfigStatus(Models.AppAiSettings ai)
    {
        // Whisper-specific endpoint/key take priority; fall back to main agent settings
        var effectiveEndpoint = !string.IsNullOrWhiteSpace(ai.WhisperEndpoint) ? ai.WhisperEndpoint : ai.Endpoint;
        var effectiveKey = !string.IsNullOrWhiteSpace(ai.WhisperApiKey) ? ai.WhisperApiKey : ai.ApiKey;
        var hasEndpoint = !string.IsNullOrWhiteSpace(effectiveEndpoint);
        var hasKey = !string.IsNullOrWhiteSpace(effectiveKey);
        var hasDeployment = !string.IsNullOrWhiteSpace(ai.WhisperDeploymentId);
        var usingDedicated = !string.IsNullOrWhiteSpace(ai.WhisperEndpoint);

        if (!ai.VoiceInputEnabled)
        {
            VoiceConfigStatus.Text = "ℹ Voice input is disabled. Enable the checkbox above to show the 🎤 mic button.";
            VoiceConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }
        else if (!hasEndpoint || !hasKey)
        {
            VoiceConfigStatus.Text = "⚠ Voice requires an endpoint and API key. Set either the Whisper-specific fields below, or the main Agent endpoint/key above.";
            VoiceConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        }
        else if (!hasDeployment)
        {
            VoiceConfigStatus.Text = "⚠ Enter a Whisper deployment ID (e.g., \"whisper\" or \"whisper-1\").";
            VoiceConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        }
        else
        {
            var source = usingDedicated ? "dedicated Whisper endpoint" : "main Agent endpoint";
            VoiceConfigStatus.Text = $"✓ Voice input is configured and ready (using {source}). The 🎤 mic button will appear in Reason and Teach modes.";
            VoiceConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
    }

    private void UpdateReviewConfigStatus(Models.ReviewBufferSettings review)
    {
        if (!review.Enabled)
        {
            ReviewConfigStatus.Text = "Review buffer is disabled. Enable it to keep the last few minutes ready for instant save.";
            ReviewConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            return;
        }

        if (review.FrameIntervalMs < 250)
        {
            ReviewConfigStatus.Text = "Frame interval is too low. Use 250 ms or higher to avoid excessive CPU and disk usage.";
            ReviewConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
            return;
        }

        ReviewConfigStatus.Text = $"Review buffer ready: {review.BufferDurationMinutes} minute(s) at {review.FrameIntervalMs} ms intervals. Save hotkey: {review.SaveBufferHotkey}.";
        ReviewConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    private void UpdateOrbConfigStatus(Models.CaptureWorkspaceSettings capture)
    {
        var placement = string.IsNullOrWhiteSpace(capture.OrbPlacement) ? "BottomRight" : capture.OrbPlacement;
        OrbConfigStatus.Text = placement == "Custom"
            ? capture.RememberOrbLocation && capture.OrbLeft.HasValue && capture.OrbTop.HasValue
                ? $"Orb uses the saved custom location at X={capture.OrbLeft.Value:F0}, Y={capture.OrbTop.Value:F0}."
                : "Orb is set to Custom. Drag the orb handle once to store its location."
            : $"Orb opens in the {placement} preset and stays always on top.";
        OrbConfigStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    private void SelectOrbPlacement(string placement)
    {
        foreach (var item in OrbPlacementCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), placement, StringComparison.OrdinalIgnoreCase))
            {
                OrbPlacementCombo.SelectedItem = item;
                return;
            }
        }

        OrbPlacementCombo.SelectedIndex = 0;
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

        // Safety settings
        s.KillSwitchHotkey = KillSwitchHotkeyBox.Text.Trim();
        s.AllowedProcesses = AllowedProcessesBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Agent settings
        var ai = cfg.Ai;
        ai.Endpoint = AgentEndpointBox.Text.Trim();
        ai.ModelId = AgentModelBox.Text.Trim();
        ai.ApiKey = AgentApiKeyBox.Password;
        if (int.TryParse(AgentMaxTokensBox.Text.Trim(), out var maxTokens) && maxTokens > 0)
            ai.MaxTokens = maxTokens;
        if (double.TryParse(AgentTemperatureBox.Text.Trim(), out var temp) && temp >= 0 && temp <= 2)
            ai.Temperature = temp;
        
        // Vision fallback settings
        ai.VisionFallbackEnabled = VisionFallbackCheck.IsChecked == true;
        ai.VisionModelId = VisionModelBox.Text.Trim();
        if (double.TryParse(VisionConfidenceBox.Text.Trim(), out var threshold) && threshold >= 0 && threshold <= 1)
            ai.VisionConfidenceThreshold = threshold;
        
        // Voice input settings
        ai.VoiceInputEnabled = VoiceEnabledCheck.IsChecked == true;
        ai.WhisperEndpoint = WhisperEndpointBox.Text.Trim();
        ai.WhisperApiKey = WhisperApiKeyBox.Password;
        var whisperDeploy = WhisperDeploymentBox.Text.Trim();
        ai.WhisperDeploymentId = string.IsNullOrWhiteSpace(whisperDeploy) ? "whisper" : whisperDeploy;
        ai.VoiceLanguage = VoiceLanguageBox.Text.Trim();

        var review = cfg.Review;
        review.Enabled = ReviewEnabledCheck.IsChecked == true;
        review.MicEnabled = ReviewMicCheck.IsChecked == true;
        review.SaveBufferHotkey = string.IsNullOrWhiteSpace(ReviewHotkeyBox.Text) ? "Ctrl+Alt+R" : ReviewHotkeyBox.Text.Trim();
        if (int.TryParse(ReviewDurationBox.Text.Trim(), out var duration) && duration > 0)
            review.BufferDurationMinutes = duration;
        if (int.TryParse(ReviewFrameIntervalBox.Text.Trim(), out var frameInterval) && frameInterval >= 250)
            review.FrameIntervalMs = frameInterval;
        if (int.TryParse(ReviewAudioChunkBox.Text.Trim(), out var audioChunk) && audioChunk > 0)
            review.AudioChunkSeconds = audioChunk;
        review.OutputDirectory = ReviewOutputDirectoryBox.Text.Trim();

        var capture = cfg.Capture;
        capture.RememberOrbLocation = RememberOrbLocationCheck.IsChecked == true;
        capture.OrbPlacement = (OrbPlacementCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "BottomRight";
        if (!capture.RememberOrbLocation)
        {
            capture.OrbLeft = null;
            capture.OrbTop = null;
            if (capture.OrbPlacement == "Custom")
                capture.OrbPlacement = "BottomRight";
        }

        App.Config.SaveConfig(cfg);
        App.Log.SetLevel(s.LogLevel);

        // Reconfigure agent service if settings changed
        App.Agent?.Reconfigure();
        
        // Reconfigure vision service if settings changed
        App.Vision?.Reconfigure();

        App.ReviewBuffer?.Reconfigure();

        // Re-register hotkeys so capture/review shortcuts pick up config changes immediately.
        App.Hotkey?.ReloadHotkeys();

        if (Owner is MainWindow mainWindow)
            mainWindow.RefreshSnapOrbPlacement();

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

    private void OpenReviewFolder_Click(object sender, RoutedEventArgs e)
    {
        var reviewPath = App.ReviewBuffer?.ReviewBundlesDirectory ?? Path.Combine(AppContext.BaseDirectory, "reports", "_review-buffers");
        Directory.CreateDirectory(reviewPath);
        Process.Start("explorer.exe", reviewPath);
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using IdolClick.Models;
using IdolClick.Services;
using IdolClick.UI;

namespace IdolClick.UI.Controls;

public partial class CapturePanelControl : UserControl
{
    private enum CaptureViewLevel
    {
        Library,
        Profile,
        Target,
        Configure
    }

    private CaptureProfile? _selectedProfile;
    private CapturePreviewWindow? _previewWindow;
    private CaptureTargetFolderItem? _selectedTargetItem;
    private CaptureTargetRecentArtifactItem? _selectedTargetArtifact;
    private CaptureGalleryItem? _selectedProjectCapture;
    private CaptureViewLevel _viewLevel = CaptureViewLevel.Library;
    private List<CaptureEventResult> _recentCaptureEvents = new();
    private bool _isSyncingArtifactSelection;
    private bool _isSyncingProfileSelection;
    private bool _isPreviewDockCollapsed;
    private double _previewDockWidth = 400;
    private PromptPackHistoryService? _promptHistory;

    public CapturePanelControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _promptHistory ??= new PromptPackHistoryService(App.Config);
        RefreshWindows();
        LoadProfiles();
        RefreshReviewStatus();
        LoadRecentReviews();
        LoadPromptPackHistory();
        App.ReviewBuffer.BundleSaved += OnReviewBundleSaved;
        ApplyPreviewDockState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.ReviewBuffer.BundleSaved -= OnReviewBundleSaved;
    }

    private void CapturePanelControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputContext(e.OriginalSource as DependencyObject))
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            CaptureSelected_Click(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            NewScreenRegionProfile_Click(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            e.Handled = true;
            NewWindowProfile_Click(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            e.Handled = true;
            SaveReviewBuffer_Click(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.F2 && _selectedProfile != null)
        {
            e.Handled = true;
            ShowConfigureView();
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_viewLevel == CaptureViewLevel.Library && ProfilesListBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                OpenSelectedProfile();
                return;
            }

            if (_viewLevel == CaptureViewLevel.Profile && TargetsListBox.IsKeyboardFocusWithin && TargetsListBox.SelectedItem is CaptureTargetFolderItem targetItem)
            {
                e.Handled = true;
                ShowTargetDetailView(targetItem);
                return;
            }

            if (_viewLevel == CaptureViewLevel.Target && (TargetComparisonStripListBox.IsKeyboardFocusWithin || TargetRecentArtifactsListBox.IsKeyboardFocusWithin))
            {
                e.Handled = true;
                OpenTargetArtifactFile_Click(this, new RoutedEventArgs());
                return;
            }
        }

        if (e.Key == Key.Delete)
        {
            if (_viewLevel == CaptureViewLevel.Library && ProfilesListBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                DeleteProfile_Click(this, new RoutedEventArgs());
                return;
            }

            if (_viewLevel == CaptureViewLevel.Profile && TargetsListBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                RemoveTarget_Click(this, new RoutedEventArgs());
                return;
            }
        }

        if (e.Key is Key.Escape or Key.Back)
        {
            e.Handled = true;
            NavigateUpOneLevel();
        }
    }

    private void NavigateUpOneLevel()
    {
        switch (_viewLevel)
        {
            case CaptureViewLevel.Configure:
                ShowProfileView();
                break;
            case CaptureViewLevel.Target:
                ShowProfileView();
                break;
            case CaptureViewLevel.Profile:
                ShowLibraryView();
                break;
        }
    }

    private static bool IsTextInputContext(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is TextBox or PasswordBox or ComboBox)
                return true;

            source = source switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                _ => null
            };
        }

        return false;
    }

    public void LoadProfiles()
    {
        RefreshRecentEvents();

        var profileItems = App.SnapCapture.GetProfiles()
            .Select(profile => CaptureProfileLibraryItem.FromProfile(profile, _recentCaptureEvents))
            .ToList();

        BindProfileExplorers(profileItems);

        var configured = App.SnapCapture.GetSelectedProfile();
        if (configured != null)
            _selectedProfile = configured;

        var restoredSelection = false;
        if (_selectedProfile != null)
        {
            var selected = profileItems.FirstOrDefault(item => item.Profile.Id == _selectedProfile.Id);
            if (selected != null)
            {
                SyncProfileExplorerSelection(selected, null);
                restoredSelection = true;
            }
        }

        if (!restoredSelection && ProfilesListBox.Items.Count > 0)
            ProfilesListBox.SelectedIndex = 0;
        else if (ProfilesListBox.Items.Count == 0)
            BindProfile(null);

        BindRecentCaptureLists();
        RefreshReviewStatus();
        LoadRecentReviews();
        ShowLibraryView();
    }

    public async Task TriggerSelectedCaptureAsync()
    {
        var profile = App.SnapCapture.GetSelectedProfile() ?? _selectedProfile;
        if (profile == null)
            return;

        await CaptureProfileAsync(profile);
    }

    private void RefreshWindows()
    {
        WindowsComboBox.ItemsSource = App.SnapCapture.ListWindows();
        if (WindowsComboBox.Items.Count > 0)
            WindowsComboBox.SelectedIndex = 0;
    }

    private void RefreshRecentEvents()
    {
        _recentCaptureEvents = App.SnapCapture.ListRecentCaptureEvents(24).ToList();
    }

    private void BindRecentCaptureLists()
    {
        var profiles = App.SnapCapture.GetProfiles();
        var profileIds = profiles
            .Select(profile => profile.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var relevantEvents = _selectedProfile != null
            ? _recentCaptureEvents.Where(result => string.Equals(result.ProfileId, _selectedProfile.Id, StringComparison.OrdinalIgnoreCase)).ToList()
            : profileIds.Count == 0
                ? []
                : _recentCaptureEvents.Where(result => profileIds.Contains(result.ProfileId)).ToList();

        var items = relevantEvents
            .Take(8)
            .Select(CaptureGalleryItem.FromResult)
            .ToList();

        PreviewDockCapturesListBox.ItemsSource = items;

        _selectedProjectCapture = ResolveCaptureSelection(items, _selectedProjectCapture);
        PreviewDockCapturesListBox.SelectedItem = _selectedProjectCapture;

        UpdatePreviewDock(_selectedProjectCapture);
    }

    private async void NewScreenRegionProfile_Click(object sender, RoutedEventArgs e)
    {
        var region = await App.RegionCapture.CaptureRegionAsync();
        if (region == null)
            return;

        var profile = new CaptureProfile
        {
            Name = CreateProfileName("Screen region"),
            Targets =
            [
                App.SnapCapture.CreateScreenRegionTarget("Screen region", region)
            ]
        };

        App.SnapCapture.AddProfile(profile);
        LoadProfiles();
        SelectProfile(profile.Id);
    }

    private void NewWindowProfile_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsComboBox.SelectedItem is not CaptureWindowCandidate window)
            return;

        var profile = new CaptureProfile
        {
            Name = CreateProfileName(window.ProcessName),
            Targets =
            [
                App.SnapCapture.CreateWindowTarget(window)
            ]
        };

        App.SnapCapture.AddProfile(profile);
        LoadProfiles();
        SelectProfile(profile.Id);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null)
            return;

        var preview = App.SnapCapture.PreviewDeleteProfileAndCaptures(_selectedProfile.Id);
        var result = MessageBox.Show(
            $"Delete capture profile '{_selectedProfile.Name}'?\n\n"
            + $"Profile-linked cleanup will affect {preview.AffectedEvents} capture event(s) and {preview.AffectedArtifacts} artifact file(s).\n"
            + "Saved files are moved into the capture recycle area instead of being permanently deleted.\n\n"
            + "Yes: delete the profile and recycle its saved captures\n"
            + "No: delete only the profile\n"
            + "Cancel: keep everything",
            "Delete profile",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
            return;

        if (result == MessageBoxResult.Yes)
        {
            var cleanup = App.SnapCapture.DeleteProfileAndCaptures(_selectedProfile.Id);
            CaptureStatusText.Text = cleanup.RemovedEvents == 0
                ? "Profile deleted. No saved captures were found."
                : $"Profile deleted and recycled {cleanup.RemovedEvents} capture event(s) and {cleanup.RemovedArtifacts} artifact(s).";
        }
        else
        {
            App.SnapCapture.DeleteProfile(_selectedProfile.Id);
            CaptureStatusText.Text = "Profile deleted. Saved captures remain on disk.";
        }

        _selectedProfile = null;
        _selectedTargetItem = null;
        _selectedTargetArtifact = null;
        _selectedProjectCapture = null;
        LoadProfiles();
    }

    private void CleanOrphanedCaptures_Click(object sender, RoutedEventArgs e)
    {
        var preview = App.SnapCapture.PreviewOrphanedCapturesCleanup();
        if (preview.AffectedEvents == 0)
        {
            CaptureStatusText.Text = "No orphaned captures were found.";
            return;
        }

        var confirm = MessageBox.Show(
            $"Clean orphaned captures?\n\n"
            + $"This will recycle {preview.AffectedEvents} orphaned capture event(s) and {preview.AffectedArtifacts} artifact file(s).\n"
            + "Orphaned items are capture records whose profile no longer exists.\n\n"
            + "Continue?",
            "Clean orphaned captures",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var cleanup = App.SnapCapture.CleanupOrphanedCaptures();
        _selectedProjectCapture = null;
        CaptureStatusText.Text = $"Recycled {cleanup.RemovedEvents} orphaned capture event(s) and {cleanup.RemovedArtifacts} artifact(s).";
        LoadProfiles();
    }

    private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingProfileSelection)
            return;

        var selectedItem = ProfilesListBox.SelectedItem as CaptureProfileLibraryItem;
        SyncProfileExplorerSelection(selectedItem, ProfilesListBox);
        BindProfile(selectedItem?.Profile);
    }

    private void ProfilesListBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedProfile();
    }

    private void BindProfileExplorers(List<CaptureProfileLibraryItem> items)
    {
        ProfilesListBox.ItemsSource = null;
        ProfilesListBox.ItemsSource = items;
    }

    private void SyncProfileExplorerSelection(CaptureProfileLibraryItem? selectedItem, Selector? source)
    {
        _isSyncingProfileSelection = true;
        try
        {
            ProfilesListBox.SelectedItem = selectedItem;
        }
        finally
        {
            _isSyncingProfileSelection = false;
        }
    }

    private void BindProfile(CaptureProfile? profile)
    {
        _selectedProfile = profile;
        App.SnapCapture.SetSelectedProfile(profile?.Id);
        var hasProfile = profile != null;

        SelectedProfileTitle.Text = hasProfile ? profile!.Name : "Choose or create a profile";
        ProfileNameTextBox.Text = hasProfile ? profile!.Name : "";
        FilePrefixTextBox.Text = hasProfile ? profile!.FilePrefix : "snap";
        OutputDirectoryTextBox.Text = hasProfile ? profile!.OutputDirectory : "";

        if (hasProfile)
        {
            var targetItems = profile!.Targets
                .Select(target => CaptureTargetFolderItem.FromTarget(target, _recentCaptureEvents))
                .ToList();
            TargetsListBox.ItemsSource = targetItems;
            _selectedTargetItem = _selectedTargetItem != null
                ? targetItems.FirstOrDefault(item => item.Target.Id == _selectedTargetItem.Target.Id)
                : null;
            TargetsListBox.SelectedItem = _selectedTargetItem;
        }
        else
        {
            TargetsListBox.ItemsSource = null;
            _selectedTargetItem = null;
            _selectedTargetArtifact = null;
        }

        ProfileViewSummaryText.Text = hasProfile
            ? $"{profile!.Targets.Count} target subfolder(s) ready inside this profile. Open one to inspect its latest captures or jump into configure when you need to change structure."
            : "Open a profile to see its capture targets and recent output.";
        LibrarySummaryText.Text = hasProfile
            ? $"{profile!.Name} is selected. It currently contains {profile.Targets.Count} target(s). Double-click or use Open selected to step inside."
            : "Choose a profile to see its summary.";

        var selectedLibraryItem = hasProfile
            ? (ProfilesListBox.ItemsSource as IEnumerable<CaptureProfileLibraryItem>)?.FirstOrDefault(item => item.Profile.Id == profile!.Id)
            : null;
        SyncProfileExplorerSelection(selectedLibraryItem, null);

        BindProjectWorkbench(profile);
        UpdateTargetDetail();
    }

    private void BindProjectWorkbench(CaptureProfile? profile)
    {
        var hasProfile = profile != null;
        var outputDirectory = hasProfile ? ResolveProfileOutputDirectory(profile!) : string.Empty;
        var captureCount = hasProfile
            ? _recentCaptureEvents.Count(result => string.Equals(result.ProfileId, profile!.Id, StringComparison.OrdinalIgnoreCase))
            : 0;

        LibrarySelectedProjectTitleText.Text = hasProfile ? profile!.Name : "Select a capture project";
        LibraryProjectManifestText.Text = hasProfile
            ? $"{profile!.Targets.Count} asset(s) • {captureCount} capture event(s) • {Path.GetFileName(outputDirectory)} output root"
            : "Projects collect assets, captures, and reusable output in one docked workspace.";
        LibraryProjectOutputText.Text = hasProfile ? outputDirectory : "Output path will appear here.";

        SelectedProfileTitle.Text = hasProfile ? $"{profile!.Name}.captureproj" : "Choose or create a project";
        ProfileViewSummaryText.Text = hasProfile
            ? $"Project assets stay on the left, project activity stays docked on the right, and the preview pane can be resized like an editor workbench."
            : "Open a project to see its assets and recent output.";
        ProjectManifestText.Text = hasProfile
            ? $"{profile!.Targets.Count} asset(s) • {captureCount} capture event(s)"
            : "No project selected.";
        ProjectOutputPathText.Text = hasProfile ? outputDirectory : "No output directory yet.";

        var resourceItems = BuildProjectResources(profile, captureCount, outputDirectory);
        LibraryProjectResourcesItemsControl.ItemsSource = resourceItems;
        ProjectResourcesItemsControl.ItemsSource = resourceItems.ToList();
    }

    private List<CaptureProjectResourceItem> BuildProjectResources(CaptureProfile? profile, int captureCount, string outputDirectory)
    {
        if (profile == null)
        {
            return
            [
                new CaptureProjectResourceItem("Assets", "Targets and source regions live here.", "0", CaptureImageLoader.DefaultAccentBrush),
                new CaptureProjectResourceItem("Captures", "Rendered snapshots, batches, and annotations.", "0", CaptureImageLoader.DefaultAccentBrush),
                new CaptureProjectResourceItem("Output", "Project export root.", "--", CaptureImageLoader.DefaultAccentBrush)
            ];
        }

        var accent = CaptureImageLoader.CreateAccentPalette(_recentCaptureEvents.FirstOrDefault(result => string.Equals(result.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))?.PreviewPath);
        return
        [
            new CaptureProjectResourceItem("Assets", $"{profile.Targets.Count} reusable source target(s).", profile.Targets.Count.ToString(), accent.AccentBrush),
            new CaptureProjectResourceItem("Captures", $"{captureCount} capture event(s) currently indexed.", captureCount.ToString(), accent.AccentBrush),
            new CaptureProjectResourceItem("Output", string.IsNullOrWhiteSpace(outputDirectory) ? "App-managed output root." : outputDirectory, Path.GetFileName(outputDirectory), accent.AccentBrush)
        ];
    }

    private string ResolveProfileOutputDirectory(CaptureProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.OutputDirectory))
            return profile.OutputDirectory;

        var configured = App.Config.GetConfig().Capture.DefaultOutputDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(App.Reports.ReportsDirectory, "_captures", SanitizeFileName(profile.Name));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private async void AddScreenRegionTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null)
            return;

        var region = await App.RegionCapture.CaptureRegionAsync();
        if (region == null)
            return;

        _selectedProfile.Targets.Add(App.SnapCapture.CreateScreenRegionTarget($"Screen region {_selectedProfile.Targets.Count + 1}", region));
        BindProfile(_selectedProfile);
        ShowProfileView();
    }

    private void AddWindowTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null || WindowsComboBox.SelectedItem is not CaptureWindowCandidate window)
            return;

        _selectedProfile.Targets.Add(App.SnapCapture.CreateWindowTarget(window));
        BindProfile(_selectedProfile);
        ShowProfileView();
    }

    private async void AddWindowRegionTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null || WindowsComboBox.SelectedItem is not CaptureWindowCandidate window)
            return;

        var region = await App.RegionCapture.CaptureRegionForWindowAsync(new IntPtr(window.Handle));
        if (region == null)
            return;

        _selectedProfile.Targets.Add(App.SnapCapture.CreateWindowRegionTarget(window, region));
        BindProfile(_selectedProfile);
        ShowProfileView();
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        CaptureStatusText.Text = $"{WindowsComboBox.Items.Count} windows ready";
    }

    private void CapturePromptPackBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = RunPromptCapturePackAsync(smokeMode: true);
        }
    }

    private async void RunPromptPack_Click(object sender, RoutedEventArgs e)
    {
        await RunPromptCapturePackAsync(smokeMode: true);
    }

    private async void RunPromptPackFull_Click(object sender, RoutedEventArgs e)
    {
        await RunPromptCapturePackAsync(smokeMode: false);
    }

    private async Task RunPromptCapturePackAsync(bool smokeMode)
    {
        var prompt = CapturePromptPackBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            CapturePromptPackStatusText.Text = "Enter a capture instruction first.";
            return;
        }

        SetPromptPackUiState(false);
        CaptureStatusText.Text = "Preparing prompt-driven pack run...";
        CapturePromptPackStatusText.Text = smokeMode
            ? "Resolving a canonical capture pack for a smoke run..."
            : "Resolving a canonical capture pack for a full observation run...";

        try
        {
            await EnsurePromptPackServicesAsync();

            var orchestrator = new PromptCapturePackOrchestratorService(
                App.Config,
                App.Log,
                App.Reports,
                App.SnapCapture,
                App.CaptureAnnotations,
                App.FlowExecutor);

            var result = await orchestrator.RunAsync(prompt, autoConfirm: true, smokeMode, CancellationToken.None);
            if (!result.Executed)
            {
                var message = string.IsNullOrWhiteSpace(result.Error) ? result.Reason : result.Error;
                CapturePromptPackStatusText.Text = message;
                CaptureStatusText.Text = "Prompt pack was not executed.";
                return;
            }

            _promptHistory?.RecordRun(prompt, result, smokeMode);
            LoadPromptPackHistory();
            LoadProfiles();
            if (!string.IsNullOrWhiteSpace(result.PackId))
                SelectProfile(result.PackId);

            var runKind = smokeMode ? "smoke" : "full";
            CapturePromptPackStatusText.Text = result.Succeeded
                ? $"{result.PackName} {runKind} run completed."
                : $"{result.PackName} {runKind} run failed: {result.Error}";
            CaptureStatusText.Text = result.Succeeded
                ? $"Prompt pack ready: {Path.GetFileName(Path.GetDirectoryName(result.ReportPath) ?? result.ReportPath)}"
                : "Prompt pack failed.";

            if (result.Succeeded)
                CapturePromptPackBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            CapturePromptPackStatusText.Text = $"Prompt pack error: {ex.Message}";
            CaptureStatusText.Text = "Prompt pack failed.";
        }
        finally
        {
            SetPromptPackUiState(true);
        }
    }

    private static async Task EnsurePromptPackServicesAsync()
    {
        if (App.FlowExecutor != null)
            return;

        if (Application.Current is IdolClick.App app)
            await app.EnsureAgentServicesAsync();
    }

    private void SetPromptPackUiState(bool enabled)
    {
        CapturePromptPackBox.IsEnabled = enabled;
        RunPromptPackButton.IsEnabled = enabled;
        RunPromptPackFullButton.IsEnabled = enabled;
    }

    private void LoadPromptPackHistory()
    {
        var items = _promptHistory?.GetRecentEntries() ?? [];
        CapturePromptHistoryItemsControl.ItemsSource = items;
        CapturePromptHistoryItemsControl.Visibility = CapturePromptHistoryItemsControl.Items.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void CapturePromptHistoryRun_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PromptPackHistoryEntry entry)
            return;

        CapturePromptPackBox.Text = entry.Prompt;
        await RunPromptCapturePackAsync(entry.SmokeMode);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedProfile();
    }

    private async void CaptureSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null)
            return;

        await CaptureProfileAsync(_selectedProfile);
    }

    private async void SaveReviewBuffer_Click(object sender, RoutedEventArgs e)
    {
        CaptureStatusText.Text = "Saving review...";
        var metadataPath = await App.ReviewBuffer.SaveBufferAsync();
        CaptureStatusText.Text = metadataPath != null ? "Review saved" : "Review buffer disabled";
        RefreshReviewStatus();
        LoadRecentReviews();
    }

    private void OpenReviewFolder_Click(object sender, RoutedEventArgs e)
    {
        var reviewPath = App.ReviewBuffer.ReviewBundlesDirectory;
        Directory.CreateDirectory(reviewPath);
        Process.Start("explorer.exe", reviewPath);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow mainWindow)
            return;

        var settings = new SettingsWindow { Owner = mainWindow };
        settings.ShowDialog();
        RefreshReviewStatus();
        LoadRecentReviews();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null)
        {
            CaptureStatusText.Text = "Select a profile to export.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export capture profile",
            FileName = SanitizeFileName(_selectedProfile.Name) + ".capture-profile",
            DefaultExt = ".json",
            Filter = "Capture profile (*.capture-profile.json)|*.capture-profile.json|JSON file (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            App.SnapCapture.ExportProfile(_selectedProfile, dialog.FileName);
            CaptureStatusText.Text = $"Exported '{_selectedProfile.Name}' to {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import capture profile",
            DefaultExt = ".json",
            Filter = "Capture profile (*.capture-profile.json)|*.capture-profile.json|JSON file (*.json)|*.json",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var (imported, wasReplaced) = App.SnapCapture.ImportProfile(dialog.FileName);
            LoadProfiles();
            SelectProfile(imported.Id);
            CaptureStatusText.Text = wasReplaced
                ? $"Updated existing profile '{imported.Name}' from file."
                : $"Imported new profile '{imported.Name}'.";
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    private void SaveSelectedProfile()
    {
        if (_selectedProfile == null)
            return;

        _selectedProfile.Name = string.IsNullOrWhiteSpace(ProfileNameTextBox.Text)
            ? "New Capture"
            : ProfileNameTextBox.Text.Trim();
        _selectedProfile.FilePrefix = string.IsNullOrWhiteSpace(FilePrefixTextBox.Text)
            ? "snap"
            : FilePrefixTextBox.Text.Trim();
        _selectedProfile.OutputDirectory = OutputDirectoryTextBox.Text.Trim();

        App.SnapCapture.SaveProfile(_selectedProfile);
        CaptureStatusText.Text = "Profile saved";
        LoadProfiles();
        SelectProfile(_selectedProfile.Id);
        ShowProfileView();
    }

    private async Task CaptureProfileAsync(CaptureProfile profile)
    {
        SaveSelectedProfile();
        CaptureStatusText.Text = "Capturing...";

        var result = await App.SnapCapture.CaptureProfileAsync(profile, NoteTextBox.Text.Trim());
        CaptureStatusText.Text = result.Failures.Count == 0
            ? $"Saved {result.Artifacts.Count} artifact(s)"
            : $"Saved {result.Artifacts.Count} with {result.Failures.Count} warnings";

        RefreshRecentEvents();
        BindProfile(_selectedProfile);
        BindRecentCaptureLists();

        if (_viewLevel == CaptureViewLevel.Target && _selectedTargetItem != null)
            ShowTargetDetailView(_selectedTargetItem);
    }

    private void SelectProfile(string profileId)
    {
        var item = (ProfilesListBox.ItemsSource as IEnumerable<CaptureProfileLibraryItem>)
            ?.FirstOrDefault(profile => profile.Profile.Id == profileId);
        if (item != null)
            ProfilesListBox.SelectedItem = item;
    }

    private string CreateProfileName(string prefix)
    {
        var existing = App.SnapCapture.GetProfiles().Count + 1;
        return $"{prefix} {existing}";
    }

    private void FocusOrb_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.FocusSnapOrb();
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null || TargetsListBox.SelectedItem is not CaptureTargetFolderItem targetItem)
            return;

        _selectedProfile.Targets.RemoveAll(target => target.Id == targetItem.Target.Id);
        if (_selectedTargetItem?.Target.Id == targetItem.Target.Id)
        {
            _selectedTargetItem = null;
            _selectedTargetArtifact = null;
        }
        BindProfile(_selectedProfile);
    }

    private void TargetsListBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TargetsListBox.SelectedItem is CaptureTargetFolderItem targetItem)
            ShowTargetDetailView(targetItem);
    }

    private void PreviewSelectedCapture_Click(object sender, RoutedEventArgs e)
    {
        OpenRecentPreview();
    }

    private void PreviewDockCapturesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProjectCapture = PreviewDockCapturesListBox.SelectedItem as CaptureGalleryItem;
        UpdatePreviewDock(_selectedProjectCapture);
    }

    private void RecentReviewBundlesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentReviewBundlesListBox.SelectedItem is not ReviewBundleListItem item)
            return;

        CaptureStatusText.Text = $"Review ready: {item.BundleId}";
    }

    private void OpenRecentPreview()
    {
        if (PreviewDockCapturesListBox.SelectedItem is not CaptureGalleryItem item)
            return;

        _previewWindow?.Close();
        _previewWindow = new CapturePreviewWindow(item.Result)
        {
            Owner = Window.GetWindow(this)
        };
        _previewWindow.Show();
    }

    private void OpenSelectedProfile()
    {
        if (_selectedProfile == null)
            return;

        _selectedTargetItem = null;
        _selectedTargetArtifact = null;
        ShowProfileView();
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedProfile();
    }

    private static CaptureGalleryItem? ResolveCaptureSelection(List<CaptureGalleryItem> items, CaptureGalleryItem? current)
    {
        if (items.Count == 0)
            return null;

        if (current != null)
        {
            var match = items.FirstOrDefault(item => string.Equals(item.Result.EventId, current.Result.EventId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return items[0];
    }

    private void UpdatePreviewDock(CaptureGalleryItem? item)
    {
        if (item?.PreviewImage == null)
        {
            PreviewDockImage.Source = null;
            PreviewDockEmptyText.Visibility = Visibility.Visible;
            PreviewDockTitleText.Text = _selectedProfile?.Name ?? "No project selected";
            PreviewDockMetaText.Text = "Select a capture project to inspect its preview dock.";
            return;
        }

        PreviewDockImage.Source = item.PreviewImage;
        PreviewDockEmptyText.Visibility = Visibility.Collapsed;
        PreviewDockTitleText.Text = item.Result.ProfileName;
        PreviewDockMetaText.Text = $"{item.TimeLabel} • {item.Result.Artifacts.Count} artifact(s)";
    }

    private void TogglePreviewDock_Click(object sender, RoutedEventArgs e)
    {
        _isPreviewDockCollapsed = !_isPreviewDockCollapsed;
        ApplyPreviewDockState();
    }

    private void ApplyPreviewDockState()
    {
        if (PreviewDockColumn == null || PreviewDockSplitterColumn == null)
            return;

        PreviewDockColumn.Width = _isPreviewDockCollapsed ? new GridLength(0) : new GridLength(_previewDockWidth);
        PreviewDockSplitterColumn.Width = _isPreviewDockCollapsed ? new GridLength(0) : new GridLength(8);
        PreviewDockBorder.Visibility = _isPreviewDockCollapsed ? Visibility.Collapsed : Visibility.Visible;
        PreviewDockToggleButton.Content = _isPreviewDockCollapsed ? "Open preview dock" : "Collapse preview";
    }

    private void PreviewDockBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isPreviewDockCollapsed && e.NewSize.Width > 240)
            _previewDockWidth = e.NewSize.Width;
    }

    private void ShowLibraryView()
    {
        _viewLevel = CaptureViewLevel.Library;
        UpdateViewState();
    }

    private void ShowProfileView()
    {
        if (_selectedProfile == null)
        {
            ShowLibraryView();
            return;
        }

        _viewLevel = CaptureViewLevel.Profile;
        UpdateViewState();
    }

    private void ShowTargetDetailView(CaptureTargetFolderItem? targetItem = null)
    {
        if (targetItem != null)
            _selectedTargetItem = targetItem;

        if (_selectedTargetItem == null)
        {
            ShowProfileView();
            return;
        }

        _viewLevel = CaptureViewLevel.Target;
        UpdateTargetDetail();
        UpdateViewState();
    }

    private void ShowConfigureView()
    {
        if (_selectedProfile == null)
        {
            ShowLibraryView();
            return;
        }

        _viewLevel = CaptureViewLevel.Configure;
        UpdateViewState();
    }

    private void UpdateTargetDetail()
    {
        if (_selectedTargetItem == null)
        {
            TargetDetailSummaryText.Text = "Open a subfolder from the profile view to inspect its latest captures.";
            TargetRecentArtifactsListBox.ItemsSource = null;
            TargetComparisonStripListBox.ItemsSource = null;
            TargetComparisonStripListBox.SelectedItem = null;
            TargetPreviewMetaText.Text = "No target selected";
            ShowTargetPreview(null);
            return;
        }

        TargetDetailSummaryText.Text = _selectedTargetItem.PathHint;
        TargetPreviewMetaText.Text = $"{_selectedTargetItem.CaptureCountLabel} • {_selectedTargetItem.LastCaptureLabel}";

        var recentTargetArtifacts = _recentCaptureEvents
            .SelectMany(result => result.Artifacts
                .Where(artifact => artifact.TargetId == _selectedTargetItem.Target.Id)
                .Select(artifact => CaptureTargetRecentArtifactItem.FromArtifact(result, artifact)))
            .Take(12)
            .ToList();

        TargetRecentArtifactsListBox.ItemsSource = recentTargetArtifacts;
        TargetComparisonStripListBox.ItemsSource = recentTargetArtifacts;

        if (recentTargetArtifacts.Count == 0)
        {
            _selectedTargetArtifact = null;
            TargetComparisonStripListBox.SelectedItem = null;
            ShowTargetPreview(null);
            return;
        }

        var nextSelection = _selectedTargetArtifact != null
            ? recentTargetArtifacts.FirstOrDefault(item => string.Equals(item.Path, _selectedTargetArtifact.Path, StringComparison.OrdinalIgnoreCase))
            : recentTargetArtifacts[0];
        SetSelectedTargetArtifact(nextSelection ?? recentTargetArtifacts[0], null);
    }

    private void ShowTargetPreview(CaptureTargetRecentArtifactItem? item)
    {
        if (item?.PreviewImage == null)
        {
            TargetPreviewImage.Source = null;
            TargetPreviewEmptyText.Visibility = Visibility.Visible;
            TargetPreviewMetaText.Text = "No target preview available yet.";
            return;
        }

        TargetPreviewImage.Source = item.PreviewImage;
        TargetPreviewEmptyText.Visibility = Visibility.Collapsed;
        TargetPreviewMetaText.Text = $"{item.TimeLabel} • {item.EventSummary}";
    }

    private void TargetRecentArtifactsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingArtifactSelection)
            return;

        SetSelectedTargetArtifact(TargetRecentArtifactsListBox.SelectedItem as CaptureTargetRecentArtifactItem, TargetRecentArtifactsListBox);
    }

    private void TargetComparisonStripListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingArtifactSelection)
            return;

        SetSelectedTargetArtifact(TargetComparisonStripListBox.SelectedItem as CaptureTargetRecentArtifactItem, TargetComparisonStripListBox);
    }

    private void SetSelectedTargetArtifact(CaptureTargetRecentArtifactItem? item, Selector? source)
    {
        _selectedTargetArtifact = item;
        _isSyncingArtifactSelection = true;
        try
        {
            if (!ReferenceEquals(source, TargetRecentArtifactsListBox))
                TargetRecentArtifactsListBox.SelectedItem = item;
            if (!ReferenceEquals(source, TargetComparisonStripListBox))
                TargetComparisonStripListBox.SelectedItem = item;
        }
        finally
        {
            _isSyncingArtifactSelection = false;
        }

        ShowTargetPreview(item);
    }

    private void OpenTargetArtifactFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTargetArtifact == null || !File.Exists(_selectedTargetArtifact.Path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _selectedTargetArtifact.Path,
            UseShellExecute = true
        });
    }

    private void OpenTargetArtifactFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTargetArtifact == null || !Directory.Exists(_selectedTargetArtifact.OutputDirectory))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _selectedTargetArtifact.OutputDirectory,
            UseShellExecute = true
        });
    }

    private void BackToLibrary_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryView();
    }

    private void OpenConfigureView_Click(object sender, RoutedEventArgs e)
    {
        ShowConfigureView();
    }

    private void BackToProfile_Click(object sender, RoutedEventArgs e)
    {
        ShowProfileView();
    }

    private void BackToProfileFromTarget_Click(object sender, RoutedEventArgs e)
    {
        ShowProfileView();
    }

    private void ConfigureTargetFromDetail_Click(object sender, RoutedEventArgs e)
    {
        ShowConfigureView();
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        switch (_viewLevel)
        {
            case CaptureViewLevel.Profile:
                ShowLibraryView();
                break;
            case CaptureViewLevel.Target:
            case CaptureViewLevel.Configure:
                ShowProfileView();
                break;
        }
    }

    private void UpdateViewState()
    {
        // Canvas panes
        LibraryCanvasPane.Visibility  = _viewLevel == CaptureViewLevel.Library   ? Visibility.Visible : Visibility.Collapsed;
        ProfileCanvasPane.Visibility  = _viewLevel == CaptureViewLevel.Profile   ? Visibility.Visible : Visibility.Collapsed;
        TargetCanvasPane.Visibility   = _viewLevel == CaptureViewLevel.Target    ? Visibility.Visible : Visibility.Collapsed;
        ConfigureCanvasPane.Visibility = _viewLevel == CaptureViewLevel.Configure ? Visibility.Visible : Visibility.Collapsed;

        // Action bars
        LibraryActionBar.Visibility  = _viewLevel == CaptureViewLevel.Library   ? Visibility.Visible : Visibility.Collapsed;
        ProfileActionBar.Visibility  = _viewLevel == CaptureViewLevel.Profile   ? Visibility.Visible : Visibility.Collapsed;
        TargetActionBar.Visibility   = _viewLevel == CaptureViewLevel.Target    ? Visibility.Visible : Visibility.Collapsed;
        ConfigureActionBar.Visibility = _viewLevel == CaptureViewLevel.Configure ? Visibility.Visible : Visibility.Collapsed;

        // Breadcrumbs
        var showProfileCrumb = _viewLevel != CaptureViewLevel.Library;
        var showLeafCrumb    = _viewLevel == CaptureViewLevel.Target || _viewLevel == CaptureViewLevel.Configure;
        BreadcrumbProfileArrow.Visibility = showProfileCrumb ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbProfileBorder.Visibility = showProfileCrumb ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbProfileText.Text = _selectedProfile?.Name ?? "Profile";
        BreadcrumbLeafArrow.Visibility = showLeafCrumb ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbLeafBorder.Visibility = showLeafCrumb ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbLeafText.Text = _viewLevel == CaptureViewLevel.Target
            ? (_selectedTargetItem?.Name ?? "Target")
            : "Configure";

        // Nav title & back button
        NavBackButton.Visibility = _viewLevel != CaptureViewLevel.Library ? Visibility.Visible : Visibility.Collapsed;
        switch (_viewLevel)
        {
            case CaptureViewLevel.Library:
                NavKindText.Text  = "Profile library";
                NavTitleText.Text = "Capture workspace";
                NavMetaText.Text  = "";
                break;
            case CaptureViewLevel.Profile:
                NavKindText.Text  = "Project";
                NavTitleText.Text = _selectedProfile?.Name ?? "Choose or create a profile";
                NavMetaText.Text  = "";
                break;
            case CaptureViewLevel.Target:
                NavKindText.Text  = _selectedTargetItem?.KindLabel ?? "Target folder";
                NavTitleText.Text = _selectedTargetItem?.Name ?? "Select a target";
                NavMetaText.Text  = _selectedTargetItem != null
                    ? $"{_selectedTargetItem.CaptureCountLabel} • {_selectedTargetItem.LastCaptureLabel}"
                    : "No target selected";
                break;
            case CaptureViewLevel.Configure:
                NavKindText.Text  = "Configure profile";
                NavTitleText.Text = _selectedProfile?.Name ?? "Choose or create a profile";
                NavMetaText.Text  = "";
                break;
        }

        // Animate the incoming canvas pane
        UIElement activePane = _viewLevel switch
        {
            CaptureViewLevel.Profile   => ProfileCanvasPane,
            CaptureViewLevel.Target    => TargetCanvasPane,
            CaptureViewLevel.Configure => ConfigureCanvasPane,
            _                          => LibraryCanvasPane
        };
        EnsureAnimatedView(activePane);
        AnimateView(activePane);
    }

    private void RefreshReviewStatus()
    {
        var review = App.Config.GetConfig().Review;
        if (!review.Enabled)
        {
            ReviewStatusText.Text = "Disabled. Enable it in Settings to keep the last few minutes ready for one-click save.";
            ReviewHealthText.Text = "Health: disabled";
            return;
        }

        var latest = App.ReviewBuffer.GetLatestBundleSummary();
        ReviewStatusText.Text = latest == null
            ? $"Running with {review.BufferDurationMinutes} minute(s) at {review.FrameIntervalMs} ms. Hotkey: {review.SaveBufferHotkey}."
            : $"Running. Last saved {latest.SavedAtUtc.ToLocalTime():HH:mm:ss}: {latest.FrameCount} frame(s), {latest.AudioChunkCount} audio chunk(s). Hotkey: {review.SaveBufferHotkey}.";

        ReviewHealthText.Text = review.MicEnabled
            ? "Health: buffer enabled, microphone commentary on"
            : "Health: buffer enabled, microphone commentary off";
    }

    private void LoadRecentReviews()
    {
        RecentReviewBundlesListBox.ItemsSource = App.ReviewBuffer.ListSavedBundles(3)
            .Select(ReviewBundleListItem.FromSummary)
            .ToList();
    }

    private void OnReviewBundleSaved(ReviewBufferBundleSummary summary)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshReviewStatus();
            LoadRecentReviews();
            CaptureStatusText.Text = $"Review saved: {summary.BundleId}";
        });
    }

    private static void EnsureAnimatedView(UIElement view)
    {
        if (view.RenderTransform is not TranslateTransform)
            view.RenderTransform = new TranslateTransform();

        var transform = (TranslateTransform)view.RenderTransform;
        transform.X = 18;
        view.Opacity = 0;
    }

    private static void AnimateView(UIElement view)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing };
        view.BeginAnimation(OpacityProperty, opacityAnimation);

        if (view.RenderTransform is TranslateTransform transform)
        {
            var slideAnimation = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = easing };
            transform.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
        }
    }
}

internal sealed class CaptureProfileLibraryItem
{
    public required CaptureProfile Profile { get; init; }
    public BitmapImage? PreviewImage { get; init; }
    public Brush AccentBrush { get; init; } = CaptureImageLoader.DefaultAccentBrush;
    public Brush AccentTintBrush { get; init; } = CaptureImageLoader.DefaultAccentTintBrush;
    public string Name => Profile.Name;
    public string TargetCountLabel => $"{Profile.Targets.Count} target(s)";
    public string FolderBadge => Profile.Targets.Count == 0 ? "NEW" : $"{Profile.Targets.Count}";
    public string Glyph => Profile.Targets.FirstOrDefault()?.Kind switch
    {
        CaptureTargetKind.Window => "WIN",
        CaptureTargetKind.WindowRegion => "REG",
        CaptureTargetKind.ScreenRegion => "SCR",
        _ => "NEW"
    };
    public string Subtitle => Profile.Targets.FirstOrDefault()?.Kind switch
    {
        CaptureTargetKind.Window => "Window folder",
        CaptureTargetKind.WindowRegion => "Window region folder",
        CaptureTargetKind.ScreenRegion => "Screen region folder",
        _ => "Empty folder"
    };
    public string LastCaptureLabel { get; init; } = "No captures yet";

    public static CaptureProfileLibraryItem FromProfile(CaptureProfile profile, IEnumerable<CaptureEventResult> recentEvents)
    {
        var latestEvent = recentEvents.FirstOrDefault(result => string.Equals(result.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
        var accent = CaptureImageLoader.CreateAccentPalette(latestEvent?.PreviewPath);
        return new CaptureProfileLibraryItem
        {
            Profile = profile,
            PreviewImage = CaptureImageLoader.LoadPreviewImage(latestEvent?.PreviewPath, 220),
            AccentBrush = accent.AccentBrush,
            AccentTintBrush = accent.TintBrush,
            LastCaptureLabel = latestEvent != null ? $"Last capture {latestEvent.TimeLabel}" : "No captures yet"
        };
    }
}

internal sealed class CaptureTargetFolderItem
{
    public required CaptureTargetDefinition Target { get; init; }
    public BitmapImage? PreviewImage { get; init; }
    public Brush AccentBrush { get; init; } = CaptureImageLoader.DefaultAccentBrush;
    public Brush AccentTintBrush { get; init; } = CaptureImageLoader.DefaultAccentTintBrush;
    public string Name => Target.Name;
    public string KindLabel => Target.Kind switch
    {
        CaptureTargetKind.Window => "Window target",
        CaptureTargetKind.WindowRegion => "Window region",
        CaptureTargetKind.ScreenRegion => "Screen region",
        _ => Target.Kind.ToString()
    };
    public string Glyph => Target.Kind switch
    {
        CaptureTargetKind.Window => "WIN",
        CaptureTargetKind.WindowRegion => "REG",
        CaptureTargetKind.ScreenRegion => "SCR",
        _ => "TGT"
    };
    public string PathHint => Target.Kind switch
    {
        CaptureTargetKind.Window => string.IsNullOrWhiteSpace(Target.WindowTitle) ? "Top-level window" : Target.WindowTitle,
        CaptureTargetKind.WindowRegion => string.IsNullOrWhiteSpace(Target.WindowTitle) ? "Window sub-region" : $"Inside {Target.WindowTitle}",
        CaptureTargetKind.ScreenRegion => "Desktop selection",
        _ => string.Empty
    };
    public string CaptureCountLabel { get; init; } = "0 captures";
    public string LastCaptureLabel { get; init; } = "No captures yet";

    public static CaptureTargetFolderItem FromTarget(CaptureTargetDefinition target, IEnumerable<CaptureEventResult> recentEvents)
    {
        var targetEvents = recentEvents
            .Where(result => result.Artifacts.Any(artifact => string.Equals(artifact.TargetId, target.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var latestArtifact = targetEvents
            .SelectMany(result => result.Artifacts.Where(artifact => string.Equals(artifact.TargetId, target.Id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        var accent = CaptureImageLoader.CreateAccentPalette(latestArtifact?.Path);

        return new CaptureTargetFolderItem
        {
            Target = target,
            PreviewImage = CaptureImageLoader.LoadPreviewImage(latestArtifact?.Path, 180),
            AccentBrush = accent.AccentBrush,
            AccentTintBrush = accent.TintBrush,
            CaptureCountLabel = targetEvents.Count == 1 ? "1 capture" : $"{targetEvents.Count} captures",
            LastCaptureLabel = targetEvents.Count > 0 ? $"Last capture {targetEvents[0].TimeLabel}" : "No captures yet"
        };
    }
}

internal sealed class CaptureTargetRecentArtifactItem
{
    public required CaptureEventResult EventResult { get; init; }
    public required CaptureArtifact Artifact { get; init; }
    public BitmapImage? PreviewImage { get; init; }
    public string Path => Artifact.Path;
    public string OutputDirectory => EventResult.OutputDirectory;
    public string TimeLabel => EventResult.TimeLabel;
    public string DeltaLabel => EventResult.CapturedAt.ToString("MMM dd • HH:mm:ss");
    public string EventSummary => EventResult.Failures.Count == 0
        ? $"{EventResult.Artifacts.Count} artifact(s)"
        : $"{EventResult.Artifacts.Count} artifact(s), {EventResult.Failures.Count} warning(s)";

    public static CaptureTargetRecentArtifactItem FromArtifact(CaptureEventResult eventResult, CaptureArtifact artifact) => new()
    {
        EventResult = eventResult,
        Artifact = artifact,
        PreviewImage = CaptureImageLoader.LoadPreviewImage(artifact.Path, 280)
    };
}

internal sealed class ReviewBundleListItem
{
    public required ReviewBufferBundleSummary Summary { get; init; }
    public string BundleId => Summary.BundleId;
    public string PrimaryLabel => Summary.BundleId.Replace("review_", "Review ");
    public string SecondaryLabel => Summary.SavedAtUtc.ToLocalTime().ToString("MMM dd, HH:mm:ss");
    public string DetailLabel => $"{Summary.FrameCount} frame(s) • {Summary.AudioChunkCount} audio chunk(s)";

    public static ReviewBundleListItem FromSummary(ReviewBufferBundleSummary summary)
        => new() { Summary = summary };
}

internal sealed class CaptureProjectResourceItem
{
    public CaptureProjectResourceItem(string title, string description, string countLabel, Brush accentBrush)
    {
        Title = title;
        Description = description;
        CountLabel = countLabel;
        AccentBrush = accentBrush;
    }

    public string Title { get; }
    public string Description { get; }
    public string CountLabel { get; }
    public Brush AccentBrush { get; }
}

internal static class CaptureImageLoader
{
    public static readonly Brush DefaultAccentBrush = CreateBrush(Color.FromRgb(16, 185, 129));
    public static readonly Brush DefaultAccentTintBrush = CreateBrush(Color.FromArgb(88, 16, 185, 129));

    public static BitmapImage? LoadPreviewImage(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static CaptureAccentPalette CreateAccentPalette(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new CaptureAccentPalette(DefaultAccentBrush, DefaultAccentTintBrush);

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var source = decoder.Frames[0];
            var scaleX = Math.Min(1d, 20d / source.PixelWidth);
            var scaleY = Math.Min(1d, 20d / source.PixelHeight);
            BitmapSource sampled = source;
            if (scaleX < 1d || scaleY < 1d)
                sampled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));

            var formatted = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
            var stride = formatted.PixelWidth * 4;
            var pixels = new byte[stride * formatted.PixelHeight];
            formatted.CopyPixels(pixels, stride, 0);

            double weightedRed = 0;
            double weightedGreen = 0;
            double weightedBlue = 0;
            double totalWeight = 0;

            for (var index = 0; index < pixels.Length; index += 4)
            {
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var alpha = pixels[index + 3] / 255d;
                if (alpha < 0.15)
                    continue;

                var max = Math.Max(red, Math.Max(green, blue));
                var min = Math.Min(red, Math.Min(green, blue));
                var saturation = max == 0 ? 0 : (max - min) / (double)max;
                var weight = alpha * (0.35 + saturation);

                weightedRed += red * weight;
                weightedGreen += green * weight;
                weightedBlue += blue * weight;
                totalWeight += weight;
            }

            if (totalWeight <= 0.001)
                return new CaptureAccentPalette(DefaultAccentBrush, DefaultAccentTintBrush);

            var accentColor = Color.FromRgb(
                ClampColor((weightedRed / totalWeight) * 0.84 + 26),
                ClampColor((weightedGreen / totalWeight) * 0.84 + 26),
                ClampColor((weightedBlue / totalWeight) * 0.84 + 26));

            var tintColor = Color.FromArgb(88, accentColor.R, accentColor.G, accentColor.B);
            return new CaptureAccentPalette(CreateBrush(accentColor), CreateBrush(tintColor));
        }
        catch
        {
            return new CaptureAccentPalette(DefaultAccentBrush, DefaultAccentTintBrush);
        }
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static byte ClampColor(double value)
        => (byte)Math.Max(28, Math.Min(230, Math.Round(value)));
}

internal sealed record CaptureAccentPalette(Brush AccentBrush, Brush TintBrush);

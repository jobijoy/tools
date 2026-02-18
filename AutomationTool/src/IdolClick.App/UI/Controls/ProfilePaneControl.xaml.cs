using System.Windows;
using System.Windows.Controls;

namespace IdolClick.UI.Controls;

public partial class ProfilePaneControl : UserControl
{
    /// <summary>Raised when the user wants to hide the pane.</summary>
    public event Action? HideRequested;

    /// <summary>Raised after a profile switch so the host can reload rules/settings.</summary>
    public event Action<string>? ProfileChanged;

    public string ActiveProfileName => App.Profiles.ActiveProfile;

    public ProfilePaneControl()
    {
        InitializeComponent();
    }

    public void LoadProfiles()
    {
        ProfileListBox.Items.Clear();
        foreach (var name in App.Profiles.GetProfiles())
            ProfileListBox.Items.Add(name);

        // Select current
        var current = App.Profiles.ActiveProfile;
        for (int i = 0; i < ProfileListBox.Items.Count; i++)
        {
            if ((string)ProfileListBox.Items[i] == current)
            {
                ProfileListBox.SelectedIndex = i;
                break;
            }
        }
    }

    private void ProfileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is string name && name != App.Profiles.ActiveProfile)
        {
            App.Profiles.SwitchProfile(name);
            ProfileChanged?.Invoke(name);
        }
    }

    private void HidePane_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("New Profile", "Enter a name for the new profile:");
        if (string.IsNullOrWhiteSpace(name)) return;

        App.Profiles.CreateProfile(name);
        App.Profiles.SwitchProfile(name);
        LoadProfiles();
        ProfileChanged?.Invoke(name);
        App.Log.Info("Profile", $"Created and switched to profile '{name}'");
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not string source) return;

        var name = PromptForName("Duplicate Profile", $"Enter a name for the copy of '{source}':");
        if (string.IsNullOrWhiteSpace(name)) return;

        App.Profiles.DuplicateProfile(source, name);
        App.Profiles.SwitchProfile(name);
        LoadProfiles();
        ProfileChanged?.Invoke(name);
        App.Log.Info("Profile", $"Duplicated '{source}' → '{name}'");
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not string oldName) return;

        var newName = PromptForName("Rename Profile", $"Enter new name for '{oldName}':", oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

        App.Profiles.RenameProfile(oldName, newName);
        LoadProfiles();
        ProfileChanged?.Invoke(newName);
        App.Log.Info("Profile", $"Renamed '{oldName}' → '{newName}'");
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not string name) return;
        if (App.Profiles.GetProfiles().Count <= 1)
        {
            MessageBox.Show("Cannot delete the last profile.", "Delete Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"Delete profile '{name}'?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        App.Profiles.DeleteProfile(name);
        LoadProfiles();
        ProfileChanged?.Invoke(App.Profiles.ActiveProfile);
        App.Log.Info("Profile", $"Deleted profile '{name}'");
    }

    internal static string? PromptForName(string title, string message, string defaultValue = "")
    {
        // Simple input dialog using a WPF window
        var dialog = new Window
        {
            Title = title,
            Width = 350,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush"),
            Owner = Application.Current.MainWindow
        };

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var textBox = new TextBox { Text = defaultValue, Padding = new Thickness(6, 4, 6, 4) };
        stack.Children.Add(textBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var okBtn = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", Width = 70, IsCancel = true };

        string? result = null;
        okBtn.Click += (s, e) => { result = textBox.Text; dialog.Close(); };
        cancelBtn.Click += (s, e) => dialog.Close();

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);

        dialog.Content = stack;
        dialog.ShowDialog();
        return result;
    }
}

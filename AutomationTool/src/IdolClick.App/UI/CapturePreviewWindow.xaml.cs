using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdolClick.Services;

namespace IdolClick.UI;

public partial class CapturePreviewWindow : Window
{
    private readonly ObservableCollection<CapturePreviewArtifact> _artifacts = [];
    private readonly ObservableCollection<CaptureAnnotationEntry> _notes = [];
    private readonly CaptureEventResult _eventResult;
    private readonly MediaPlayer _mediaPlayer = new();
    private bool _autoPlayedNote;

    public CapturePreviewWindow(CaptureEventResult eventResult)
    {
        _eventResult = eventResult ?? throw new ArgumentNullException(nameof(eventResult));
        InitializeComponent();
        ArtifactsListBox.ItemsSource = _artifacts;
        NotesListBox.ItemsSource = _notes;
        LoadArtifacts();
        LoadNotes();
    }

    private void LoadArtifacts()
    {
        HeaderTitleText.Text = _eventResult.ProfileName;
        HeaderMetaText.Text = $"{_eventResult.CapturedAt:dddd, MMM dd yyyy • HH:mm:ss.fff}";
        FooterText.Text = _eventResult.Failures.Count == 0
            ? $"{_eventResult.Artifacts.Count} artifact(s) in this capture event"
            : $"{_eventResult.Artifacts.Count} artifact(s), {_eventResult.Failures.Count} warning(s)";

        _artifacts.Clear();
        foreach (var artifact in _eventResult.Artifacts)
            _artifacts.Add(CapturePreviewArtifact.FromArtifact(artifact));

        if (_artifacts.Count > 0)
            ArtifactsListBox.SelectedIndex = 0;
        else
            ShowPreview(null);

        UpdateArtifactNavigationState();
    }

    private void LoadNotes()
    {
        _notes.Clear();
        foreach (var note in App.CaptureAnnotations.ListAnnotationsForCaptureEvent(_eventResult.EventId, 8))
            _notes.Add(note);

        NotesHelpText.Text = _notes.Count == 0
            ? "No related voice notes were recorded for this capture event."
            : $"{_notes.Count} related voice note(s) available for playback.";

        if (_notes.Count > 0)
        {
            NotesListBox.SelectedIndex = 0;
            if (!_autoPlayedNote)
            {
                _autoPlayedNote = true;
                PlaySelectedNote();
            }
        }
    }

    private void ArtifactsListBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        ShowPreview(ArtifactsListBox.SelectedItem as CapturePreviewArtifact);
    }

    private void ShowPreview(CapturePreviewArtifact? artifact)
    {
        if (artifact?.PreviewImage == null)
        {
            PreviewImage.Source = null;
            EmptyPreviewText.Visibility = Visibility.Visible;
            HeaderSelectionText.Text = "";
            return;
        }

        PreviewImage.Source = artifact.PreviewImage;
        EmptyPreviewText.Visibility = Visibility.Collapsed;
        HeaderSelectionText.Text = $"Viewing {artifact.TargetName} • {artifact.Kind} • {ArtifactsListBox.SelectedIndex + 1}/{_artifacts.Count}";
        UpdateArtifactNavigationState();
    }

    private void UpdateArtifactNavigationState()
    {
        var hasArtifacts = _artifacts.Count > 0;
        PreviousArtifactButton.IsEnabled = hasArtifacts && ArtifactsListBox.SelectedIndex > 0;
        NextArtifactButton.IsEnabled = hasArtifacts && ArtifactsListBox.SelectedIndex < _artifacts.Count - 1;
    }

    private void PreviousArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (ArtifactsListBox.SelectedIndex > 0)
            ArtifactsListBox.SelectedIndex -= 1;
    }

    private void NextArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (ArtifactsListBox.SelectedIndex < _artifacts.Count - 1)
            ArtifactsListBox.SelectedIndex += 1;
    }

    private void NotesListBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (NotesListBox.SelectedItem is CaptureAnnotationEntry note)
            FooterText.Text = string.IsNullOrWhiteSpace(note.Text) ? FooterText.Text : $"Voice note: {note.Text}";
    }

    private void PlayNote_Click(object sender, RoutedEventArgs e)
    {
        PlaySelectedNote();
    }

    private void StopNote_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
    }

    private void PlaySelectedNote()
    {
        if (NotesListBox.SelectedItem is not CaptureAnnotationEntry note || string.IsNullOrWhiteSpace(note.AudioPath) || !File.Exists(note.AudioPath))
            return;

        _mediaPlayer.Stop();
        _mediaPlayer.Open(new Uri(note.AudioPath, UriKind.Absolute));
        _mediaPlayer.Play();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (ArtifactsListBox.SelectedItem is not CapturePreviewArtifact artifact || !File.Exists(artifact.Path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = artifact.Path,
            UseShellExecute = true
        });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_eventResult.OutputDirectory))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _eventResult.OutputDirectory,
            UseShellExecute = true
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            PreviousArtifact_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            NextArtifact_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            PlaySelectedNote();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _mediaPlayer.Stop();
        _mediaPlayer.Close();
        base.OnClosed(e);
    }
}

public class CapturePreviewArtifact
{
    public string TargetName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public BitmapImage? PreviewImage { get; init; }

    public static CapturePreviewArtifact FromArtifact(CaptureArtifact artifact)
    {
        BitmapImage? image = null;
        if (!string.IsNullOrWhiteSpace(artifact.Path) && File.Exists(artifact.Path))
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(artifact.Path, UriKind.Absolute);
            image.DecodePixelWidth = 280;
            image.EndInit();
            image.Freeze();
        }

        return new CapturePreviewArtifact
        {
            TargetName = artifact.TargetName,
            Kind = artifact.Kind.ToString(),
            Path = artifact.Path,
            PreviewImage = image
        };
    }
}
using System.Text.Json;
using System.Collections.Concurrent;

namespace IdolClick.Services;

public sealed class CaptureAnnotationService : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly SnapCaptureService _snapCapture;
    private readonly VoiceInputService? _voice;
    private readonly string _annotationsRoot;
    private readonly string _journalPath;
    private readonly object _journalLock;
    private readonly object _recentLock = new();
    private readonly List<CaptureAnnotationEntry> _recentAnnotations = [];
    private DateTime? _recordingStartedAtUtc;
    private bool _isTranscribing;

    public CaptureAnnotationService(
        ConfigService config,
        LogService log,
        ReportService reports,
        SnapCaptureService snapCapture,
        VoiceInputService? voice)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _snapCapture = snapCapture ?? throw new ArgumentNullException(nameof(snapCapture));
        _voice = voice;
        _annotationsRoot = Path.Combine(reports.ReportsDirectory, "_captures", "_annotations");
        _journalPath = Path.Combine(_annotationsRoot, "voice_annotation_journal.jsonl");
        _journalLock = JournalLocks.GetOrAdd(_journalPath, static _ => new object());
        LoadRecentAnnotations();
    }

    public event Action<CaptureAnnotationEntry>? AnnotationAdded;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsAvailable => _voice?.IsConfigured == true;
    public bool IsRecording => _voice?.IsRecording == true && _recordingStartedAtUtc != null;
    public bool IsTranscribing => _isTranscribing;
    public string JournalPath => _journalPath;

    public IReadOnlyList<CaptureAnnotationEntry> ListRecentAnnotations(int maxCount = 20)
    {
        lock (_recentLock)
        {
            return _recentAnnotations
                .Take(Math.Max(1, maxCount))
                .Select(CloneEntry)
                .ToList();
        }
    }

    public IReadOnlyList<CaptureAnnotationEntry> ListAnnotationsForCaptureEvent(string captureEventId, int maxCount = 10)
    {
        if (string.IsNullOrWhiteSpace(captureEventId))
            return [];

        lock (_recentLock)
        {
            return _recentAnnotations
                .Where(item => item.RelatedCaptureEventIds.Any(id => string.Equals(id, captureEventId, StringComparison.OrdinalIgnoreCase)))
                .Take(Math.Max(1, maxCount))
                .Select(CloneEntry)
                .ToList();
        }
    }

    public IReadOnlyList<CaptureTimelineItem> ListMergedTimeline(int maxCount = 30)
    {
        var captures = _snapCapture.ListRecentCaptureEvents(maxCount)
            .Select(capture => new CaptureTimelineItem
            {
                ItemType = "capture",
                Id = capture.EventId,
                OccurredAt = capture.CapturedAt,
                Title = capture.ProfileName,
                Summary = $"{capture.Artifacts.Count} artifact(s), {capture.Failures.Count} warning(s)",
                RelatedCaptureEventIds = [capture.EventId],
                PreviewPath = capture.PreviewPath
            });

        var annotations = ListRecentAnnotations(maxCount)
            .Select(annotation => new CaptureTimelineItem
            {
                ItemType = "annotation",
                Id = annotation.AnnotationId,
                OccurredAt = annotation.CreatedAt,
                Title = string.IsNullOrWhiteSpace(annotation.ProfileName) ? "Voice note" : annotation.ProfileName,
                Summary = annotation.Text,
                RelatedCaptureEventIds = annotation.RelatedCaptureEventIds.ToList()
            });

        return captures
            .Concat(annotations)
            .OrderByDescending(item => item.OccurredAt)
            .Take(Math.Max(1, maxCount))
            .ToList();
    }

    public void StartPushToTalk()
    {
        if (!IsAvailable || IsRecording || _isTranscribing || _voice == null)
            return;

        _voice.OnError -= OnVoiceError;
        _voice.OnError += OnVoiceError;
        _voice.OnSilenceDetected -= OnVoiceSilenceDetected;
        _voice.OnSilenceDetected += OnVoiceSilenceDetected;

        _recordingStartedAtUtc = DateTime.UtcNow;
        _voice.StartRecording();
        StatusChanged?.Invoke("Listening...");
        _log.Info("CaptureAnnotation", "Voice annotation recording started");
    }

    public async Task StopPushToTalkAsync()
    {
        if (!IsRecording || _voice == null)
            return;

        _isTranscribing = true;
        StatusChanged?.Invoke("Transcribing note...");
        try
        {
            var startedAt = _recordingStartedAtUtc ?? DateTime.UtcNow;
            var endedAt = DateTime.UtcNow;
            var result = await _voice.StopRecordingAndTranscribeDetailedAsync();
            if (result == null || string.IsNullOrWhiteSpace(result.Text))
                return;

            var entry = RecordAnnotation(result.Text, startedAt, endedAt, result.AudioBytes);
            StatusChanged?.Invoke($"Saved note: {Truncate(entry.Text, 72)}");
        }
        finally
        {
            ResetVoiceState();
        }
    }

    public void Cancel()
    {
        if (_voice == null)
            return;

        _voice.CancelRecording();
        ResetVoiceState();
        StatusChanged?.Invoke("Annotation cancelled");
    }

    internal CaptureAnnotationEntry RecordAnnotation(string text, DateTime startedAtUtc, DateTime endedAtUtc, byte[]? audioBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Directory.CreateDirectory(_annotationsRoot);

        var recentCaptures = _snapCapture.ListRecentCaptureEvents(12)
            .Where(capture => capture.CapturedAt >= startedAtUtc.AddSeconds(-30) && capture.CapturedAt <= endedAtUtc.AddSeconds(10))
            .Select(capture => capture.EventId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectedProfile = _snapCapture.GetSelectedProfile();
        var entry = new CaptureAnnotationEntry
        {
            AnnotationId = Guid.NewGuid().ToString("N")[..12],
            SessionId = DateTime.UtcNow.ToString("yyyyMMdd"),
            CreatedAt = DateTime.UtcNow,
            StartedAt = startedAtUtc,
            EndedAt = endedAtUtc,
            DurationMs = Math.Max(0, (long)(endedAtUtc - startedAtUtc).TotalMilliseconds),
            Text = text.Trim(),
            ProfileId = selectedProfile?.Id ?? string.Empty,
            ProfileName = selectedProfile?.Name ?? string.Empty,
            RelatedCaptureEventIds = recentCaptures
        };

        if (audioBytes != null && audioBytes.Length > 0)
            entry.AudioPath = PersistAnnotationAudio(entry.AnnotationId, entry.SessionId, audioBytes);

        var json = JsonSerializer.Serialize(entry, AnnotationJsonOptions);
        lock (_journalLock)
        {
            using var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(json);
        }

        lock (_recentLock)
        {
            _recentAnnotations.Insert(0, CloneEntry(entry));
            var max = Math.Max(10, _config.GetConfig().Capture.MaxRecentEventsInMemory);
            if (_recentAnnotations.Count > max)
                _recentAnnotations.RemoveRange(max, _recentAnnotations.Count - max);
        }

        _log.Info("CaptureAnnotation", $"annotationId={entry.AnnotationId} durationMs={entry.DurationMs} relatedCaptures={entry.RelatedCaptureEventIds.Count}");
        AnnotationAdded?.Invoke(entry);
        return entry;
    }

    private void OnVoiceError(string message)
    {
        _log.Warn("CaptureAnnotation", message);
        ErrorOccurred?.Invoke(message);
        StatusChanged?.Invoke(message);
        ResetVoiceState();
    }

    private void OnVoiceSilenceDetected()
    {
        if (IsRecording)
            StatusChanged?.Invoke("Silence detected - release to save");
    }

    private void ResetVoiceState()
    {
        _isTranscribing = false;
        _recordingStartedAtUtc = null;
        if (_voice == null)
            return;

        _voice.OnError -= OnVoiceError;
        _voice.OnSilenceDetected -= OnVoiceSilenceDetected;
    }

    private string PersistAnnotationAudio(string annotationId, string sessionId, byte[] audioBytes)
    {
        var audioDirectory = Path.Combine(_annotationsRoot, sessionId, "audio");
        Directory.CreateDirectory(audioDirectory);
        var audioPath = Path.Combine(audioDirectory, $"annotation_{annotationId}.wav");
        File.WriteAllBytes(audioPath, audioBytes);
        return audioPath;
    }

    private void LoadRecentAnnotations()
    {
        if (!File.Exists(_journalPath))
            return;

        try
        {
            var entries = File.ReadLines(_journalPath)
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<CaptureAnnotationEntry>(line, AnnotationJsonOptions); }
                    catch { return null; }
                })
                .Where(entry => entry != null)
                .Cast<CaptureAnnotationEntry>()
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(Math.Max(10, _config.GetConfig().Capture.MaxRecentEventsInMemory))
                .ToList();

            lock (_recentLock)
            {
                _recentAnnotations.Clear();
                _recentAnnotations.AddRange(entries.Select(CloneEntry));
            }
        }
        catch
        {
        }
    }

    private static CaptureAnnotationEntry CloneEntry(CaptureAnnotationEntry entry) => new()
    {
        AnnotationId = entry.AnnotationId,
        SessionId = entry.SessionId,
        CreatedAt = entry.CreatedAt,
        StartedAt = entry.StartedAt,
        EndedAt = entry.EndedAt,
        DurationMs = entry.DurationMs,
        Text = entry.Text,
        ProfileId = entry.ProfileId,
        ProfileName = entry.ProfileName,
        AudioPath = entry.AudioPath,
        RelatedCaptureEventIds = entry.RelatedCaptureEventIds.ToList()
    };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";

    private static readonly JsonSerializerOptions AnnotationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConcurrentDictionary<string, object> JournalLocks = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        ResetVoiceState();
    }
}

public class CaptureAnnotationEntry
{
    public string AnnotationId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public long DurationMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string AudioPath { get; set; } = string.Empty;
    public List<string> RelatedCaptureEventIds { get; set; } = [];
    public string TimeLabel => CreatedAt.ToString("HH:mm:ss");
}

public class CaptureTimelineItem
{
    public string ItemType { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PreviewPath { get; set; } = string.Empty;
    public List<string> RelatedCaptureEventIds { get; set; } = [];
}
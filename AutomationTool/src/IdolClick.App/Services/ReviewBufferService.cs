using System.Text.Json;
using NAudio.Wave;

namespace IdolClick.Services;

public sealed class ReviewBufferService : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ReportService _reports;
    private readonly Func<string, string, string?> _captureScreenshot;
    private readonly object _bufferLock = new();
    private readonly List<ReviewBufferedArtifact> _screenFrames = [];
    private readonly List<ReviewBufferedArtifact> _audioChunks = [];
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private Timer? _frameTimer;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _currentAudioChunkPath;
    private DateTime _currentAudioChunkStartedUtc;
    private int _isCapturingFrame;

    public ReviewBufferService(
        ConfigService config,
        LogService log,
        ReportService reports,
        Func<string, string, string?>? captureScreenshot = null)
    {
        _config = config;
        _log = log;
        _reports = reports;
        _captureScreenshot = captureScreenshot ?? ((outputDir, fileName) => _reports.CaptureScreenshot(outputDir, fileName));
    }

    public bool IsRunning { get; private set; }
    public event Action<ReviewBufferBundleSummary>? BundleSaved;

    public string ReviewBundlesDirectory
    {
        get
        {
            var configured = _config.GetConfig().Review.OutputDirectory;
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(_reports.ReportsDirectory, "_review-buffers")
                : configured;
        }
    }

    internal IReadOnlyList<ReviewBufferedArtifact> SnapshotFrames()
    {
        lock (_bufferLock)
            return _screenFrames.Select(CloneArtifact).ToList();
    }

    public void Start()
    {
        var settings = _config.GetConfig().Review;
        if (!settings.Enabled || IsRunning)
            return;

        Directory.CreateDirectory(GetLiveBufferDirectory());
        var interval = Math.Max(250, settings.FrameIntervalMs);
        _frameTimer = new Timer(CaptureFrameCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(interval));
        if (settings.MicEnabled)
            StartMicrophoneCapture(settings);

        IsRunning = true;
        _log.Info("ReviewBuffer", $"Started rolling buffer ({settings.BufferDurationMinutes} min @ {interval} ms)");
    }

    public void Reconfigure()
    {
        DisposeRuntimeState();
        Start();
    }

    public async Task<string?> SaveBufferAsync(CancellationToken cancellationToken = default)
    {
        var settings = _config.GetConfig().Review;
        if (!settings.Enabled)
            return null;

        var cutoffUtc = DateTime.UtcNow.AddMinutes(-Math.Max(1, settings.BufferDurationMinutes));
        List<ReviewBufferedArtifact> frames;
        List<ReviewBufferedArtifact> audio;
        lock (_bufferLock)
        {
            frames = _screenFrames.Where(item => item.CapturedAtUtc >= cutoffUtc).Select(CloneArtifact).ToList();
            audio = _audioChunks.Where(item => item.CapturedAtUtc >= cutoffUtc).Select(CloneArtifact).ToList();
        }

        var bundleDir = ResolveBundleDirectory();
        Directory.CreateDirectory(bundleDir);
        var framesDir = Path.Combine(bundleDir, "frames");
        Directory.CreateDirectory(framesDir);
        var audioDir = Path.Combine(bundleDir, "audio");
        Directory.CreateDirectory(audioDir);

        foreach (var frame in frames)
            await CopyArtifactAsync(frame, framesDir, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in audio)
            await CopyArtifactAsync(chunk, audioDir, cancellationToken).ConfigureAwait(false);

        var metadata = new ReviewBufferBundle
        {
            BundleId = Path.GetFileName(bundleDir),
            SavedAtUtc = DateTime.UtcNow,
            BufferDurationMinutes = Math.Max(1, settings.BufferDurationMinutes),
            FrameIntervalMs = Math.Max(250, settings.FrameIntervalMs),
            MicEnabled = settings.MicEnabled,
            FrameCount = frames.Count,
            AudioChunkCount = audio.Count,
            FrameFiles = Directory.EnumerateFiles(framesDir).Select(Path.GetFileName).Where(name => name != null).Cast<string>().OrderBy(name => name).ToList(),
            AudioFiles = Directory.EnumerateFiles(audioDir).Select(Path.GetFileName).Where(name => name != null).Cast<string>().OrderBy(name => name).ToList()
        };

        var metadataPath = Path.Combine(bundleDir, "review-buffer.json");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions), cancellationToken).ConfigureAwait(false);
        App.Timeline?.RecordSystem($"Review buffer saved: {metadata.BundleId} ({metadata.FrameCount} frame(s), {metadata.AudioChunkCount} audio chunk(s))");
        _log.Info("ReviewBuffer", $"Saved bundle {metadata.BundleId} with {metadata.FrameCount} frame(s)");
        BundleSaved?.Invoke(new ReviewBufferBundleSummary
        {
            BundleId = metadata.BundleId,
            SavedAtUtc = metadata.SavedAtUtc,
            FrameCount = metadata.FrameCount,
            AudioChunkCount = metadata.AudioChunkCount,
            MicEnabled = metadata.MicEnabled,
            MetadataPath = metadataPath,
            OutputDirectory = bundleDir
        });
        return metadataPath;
    }

    public IReadOnlyList<ReviewBufferBundleSummary> ListSavedBundles(int maxCount = 20)
    {
        var root = ReviewBundlesDirectory;
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateDirectories(root, "review_*")
            .Select(path =>
            {
                var metadataPath = Path.Combine(path, "review-buffer.json");
                if (!File.Exists(metadataPath))
                    return null;

                try
                {
                    var bundle = JsonSerializer.Deserialize<ReviewBufferBundle>(File.ReadAllText(metadataPath), _jsonOptions);
                    if (bundle == null)
                        return null;

                    return new ReviewBufferBundleSummary
                    {
                        BundleId = bundle.BundleId,
                        SavedAtUtc = bundle.SavedAtUtc,
                        FrameCount = bundle.FrameCount,
                        AudioChunkCount = bundle.AudioChunkCount,
                        MicEnabled = bundle.MicEnabled,
                        MetadataPath = metadataPath,
                        OutputDirectory = path
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(item => item != null)
            .Cast<ReviewBufferBundleSummary>()
            .OrderByDescending(item => item.SavedAtUtc)
            .Take(Math.Max(1, maxCount))
            .ToList();
    }

    public ReviewBufferBundle? GetSavedBundle(string bundleId)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
            return null;

        var metadataPath = Path.Combine(ReviewBundlesDirectory, bundleId, "review-buffer.json");
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ReviewBufferBundle>(File.ReadAllText(metadataPath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public ReviewBufferBundleSummary? GetLatestBundleSummary()
        => ListSavedBundles(1).FirstOrDefault();

    private void CaptureFrameCallback(object? state)
    {
        if (Interlocked.Exchange(ref _isCapturingFrame, 1) != 0)
            return;

        try
        {
            var fileName = $"frame_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
            var path = _captureScreenshot(GetLiveFramesDirectory(), fileName);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            lock (_bufferLock)
                _screenFrames.Add(new ReviewBufferedArtifact(path, DateTime.UtcNow));

            PruneExpiredArtifacts();
        }
        catch (Exception ex)
        {
            _log.Warn("ReviewBuffer", $"Frame capture failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isCapturingFrame, 0);
        }
    }

    private void StartMicrophoneCapture(Models.ReviewBufferSettings settings)
    {
        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnWaveDataAvailable;
            _waveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    _log.Warn("ReviewBuffer", $"Microphone recording stopped with error: {e.Exception.Message}");
            };
            RollAudioChunk(settings);
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            _log.Warn("ReviewBuffer", $"Could not start microphone capture: {ex.Message}");
            StopMicrophoneCapture();
        }
    }

    private void OnWaveDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            _waveWriter?.Flush();

            var settings = _config.GetConfig().Review;
            if (settings.AudioChunkSeconds > 0 && DateTime.UtcNow - _currentAudioChunkStartedUtc >= TimeSpan.FromSeconds(settings.AudioChunkSeconds))
                RollAudioChunk(settings);
        }
        catch (Exception ex)
        {
            _log.Warn("ReviewBuffer", $"Audio chunk write failed: {ex.Message}");
        }
    }

    private void RollAudioChunk(Models.ReviewBufferSettings settings)
    {
        FinalizeCurrentAudioChunk();
        Directory.CreateDirectory(GetLiveAudioDirectory());
        _currentAudioChunkStartedUtc = DateTime.UtcNow;
        _currentAudioChunkPath = Path.Combine(GetLiveAudioDirectory(), $"audio_{_currentAudioChunkStartedUtc:yyyyMMdd_HHmmss_fff}.wav");
        _waveWriter = new WaveFileWriter(_currentAudioChunkPath, _waveIn?.WaveFormat ?? new WaveFormat(16000, 16, 1));
    }

    private void FinalizeCurrentAudioChunk()
    {
        if (_waveWriter == null || string.IsNullOrWhiteSpace(_currentAudioChunkPath))
            return;

        _waveWriter.Dispose();
        _waveWriter = null;

        if (File.Exists(_currentAudioChunkPath))
        {
            lock (_bufferLock)
                _audioChunks.Add(new ReviewBufferedArtifact(_currentAudioChunkPath, _currentAudioChunkStartedUtc));
        }
        _currentAudioChunkPath = null;
        PruneExpiredArtifacts();
    }

    private void PruneExpiredArtifacts()
    {
        var cutoffUtc = DateTime.UtcNow.AddMinutes(-Math.Max(1, _config.GetConfig().Review.BufferDurationMinutes));
        lock (_bufferLock)
        {
            PruneList(_screenFrames, cutoffUtc);
            PruneList(_audioChunks, cutoffUtc);
        }
    }

    private static void PruneList(List<ReviewBufferedArtifact> artifacts, DateTime cutoffUtc)
    {
        for (int index = artifacts.Count - 1; index >= 0; index--)
        {
            if (artifacts[index].CapturedAtUtc >= cutoffUtc)
                continue;

            try
            {
                if (File.Exists(artifacts[index].Path))
                    File.Delete(artifacts[index].Path);
            }
            catch
            {
            }
            artifacts.RemoveAt(index);
        }
    }

    private static ReviewBufferedArtifact CloneArtifact(ReviewBufferedArtifact artifact)
        => new(artifact.Path, artifact.CapturedAtUtc);

    private async Task CopyArtifactAsync(ReviewBufferedArtifact artifact, string outputDir, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(outputDir, Path.GetFileName(artifact.Path));
        await using var source = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveBundleDirectory()
    {
        var configured = _config.GetConfig().Review.OutputDirectory;
        var baseDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_reports.ReportsDirectory, "_review-buffers")
            : configured;
        return Path.Combine(baseDir, $"review_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}");
    }

    private string GetLiveBufferDirectory() => Path.Combine(_reports.ReportsDirectory, "_review-buffer", "live");
    private string GetLiveFramesDirectory() => Path.Combine(GetLiveBufferDirectory(), "frames");
    private string GetLiveAudioDirectory() => Path.Combine(GetLiveBufferDirectory(), "audio");

    private void StopMicrophoneCapture()
    {
        try { _waveIn?.StopRecording(); } catch { }
        _waveIn?.Dispose();
        _waveIn = null;
        FinalizeCurrentAudioChunk();
    }

    public void Dispose()
    {
        DisposeRuntimeState();
    }

    private void DisposeRuntimeState()
    {
        _frameTimer?.Dispose();
        _frameTimer = null;
        StopMicrophoneCapture();
        IsRunning = false;
    }
}

internal sealed record ReviewBufferedArtifact(string Path, DateTime CapturedAtUtc);

public sealed class ReviewBufferBundle
{
    public string BundleId { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
    public int BufferDurationMinutes { get; set; }
    public int FrameIntervalMs { get; set; }
    public bool MicEnabled { get; set; }
    public int FrameCount { get; set; }
    public int AudioChunkCount { get; set; }
    public List<string> FrameFiles { get; set; } = [];
    public List<string> AudioFiles { get; set; } = [];
}

public sealed class ReviewBufferBundleSummary
{
    public string BundleId { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
    public int FrameCount { get; set; }
    public int AudioChunkCount { get; set; }
    public bool MicEnabled { get; set; }
    public string MetadataPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
}
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// SNAP CAPTURE SERVICE — reusable capture profiles for windows and regions.
//
// Purpose:
//   • Provide a shared execution path for the capture workspace, global hotkey,
//     and future floating trigger surfaces.
//   • Resolve window targets from saved hints and capture one or many targets
//     into a grouped snap event with sidecar metadata.
//   • Persist profile changes back into AppConfig via ConfigService.
// ═══════════════════════════════════════════════════════════════════════════════════

public class SnapCaptureService : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ReportService _reports;
    private readonly string _captureRoot;
    private readonly string _recycleRoot;
    private readonly string _journalPath;
    private readonly object _journalLock = new();
    private readonly object _recentLock = new();
    private readonly object _windowCacheLock = new();
    private readonly List<CaptureEventResult> _recentEvents = [];
    private readonly Channel<CapturePersistenceWorkItem> _persistenceChannel;
    private readonly CancellationTokenSource _persistenceCts = new();
    private readonly Task _persistenceWorker;
    private IReadOnlyList<CaptureWindowCandidate> _windowCache = [];
    private DateTime _windowCacheExpiresUtc = DateTime.MinValue;
    private int _maintenanceCounter;

    public SnapCaptureService(ConfigService config, LogService log, ReportService reports)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _captureRoot = Path.Combine(_reports.ReportsDirectory, "_captures");
        _recycleRoot = Path.Combine(_captureRoot, "_recycle");
        _journalPath = Path.Combine(_captureRoot, "capture_journal.jsonl");
        _persistenceChannel = Channel.CreateUnbounded<CapturePersistenceWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        LoadRecentEventsFromDisk();
        _persistenceWorker = Task.Run(ProcessPersistenceQueueAsync);
    }

    public event Action<CaptureEventResult>? CaptureCompleted;

    public string CaptureJournalPath => _journalPath;

    public IReadOnlyList<CaptureProfile> GetProfiles() => _config.GetConfig().Capture.Profiles;

    public CaptureProfile? GetSelectedProfile()
    {
        var selectedId = _config.GetConfig().Capture.SelectedProfileId;
        if (string.IsNullOrWhiteSpace(selectedId))
            return GetProfiles().FirstOrDefault();

        return GetProfiles().FirstOrDefault(p => p.Id == selectedId) ?? GetProfiles().FirstOrDefault();
    }

    public void SetSelectedProfile(string? profileId)
    {
        var cfg = _config.GetConfig();
        cfg.Capture.SelectedProfileId = profileId ?? "";
        _config.SaveConfig(cfg);
    }

    public IReadOnlyList<CaptureWindowCandidate> ListWindows()
    {
        lock (_windowCacheLock)
        {
            if (DateTime.UtcNow < _windowCacheExpiresUtc && _windowCache.Count > 0)
                return _windowCache;
        }

        var stopwatch = Stopwatch.StartNew();
        var windows = new List<CaptureWindowCandidate>();
        var root = AutomationElement.RootElement;
        var children = root.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);

        for (int i = 0; i < children.Count; i++)
        {
            try
            {
                var window = children[i];
                var title = window.Current.Name ?? "";
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var rect = window.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                    continue;

                var handle = window.Current.NativeWindowHandle;
                var processId = window.Current.ProcessId;
                string processName = "";
                try
                {
                    using var process = Process.GetProcessById(processId);
                    processName = process.ProcessName;
                }
                catch
                {
                    processName = "unknown";
                }

                windows.Add(new CaptureWindowCandidate
                {
                    Handle = handle,
                    ProcessId = processId,
                    ProcessName = processName,
                    WindowTitle = title,
                    Left = (int)rect.Left,
                    Top = (int)rect.Top,
                    Width = (int)rect.Width,
                    Height = (int)rect.Height
                });
            }
            catch
            {
                // Skip individual enumeration failures and continue.
            }
        }

        var ordered = windows
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_windowCacheLock)
        {
            _windowCache = ordered;
            _windowCacheExpiresUtc = DateTime.UtcNow.AddMilliseconds(750);
        }

        if (stopwatch.ElapsedMilliseconds >= 20)
            _log.Debug("CapturePerf", $"window-enumeration-ms={stopwatch.ElapsedMilliseconds} count={ordered.Count}");

        return ordered;
    }

    public CaptureProfile AddProfile(CaptureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var cfg = _config.GetConfig();
        cfg.Capture.Profiles.Add(profile);
        if (string.IsNullOrWhiteSpace(cfg.Capture.SelectedProfileId))
            cfg.Capture.SelectedProfileId = profile.Id;
        _config.SaveConfig(cfg);
        _log.Info("Capture", $"Added capture profile: {profile.Name}");
        return profile;
    }

    public void SaveProfile(CaptureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var cfg = _config.GetConfig();
        var existing = cfg.Capture.Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (existing == null)
        {
            cfg.Capture.Profiles.Add(profile);
        }
        else
        {
            var index = cfg.Capture.Profiles.IndexOf(existing);
            cfg.Capture.Profiles[index] = profile;
        }

        _config.SaveConfig(cfg);
        _log.Info("Capture", $"Saved capture profile: {profile.Name}");
    }

    private static readonly JsonSerializerOptions _profileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string ExportProfile(CaptureProfile profile, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var json = JsonSerializer.Serialize(profile, _profileJsonOptions);
        File.WriteAllText(destinationPath, json, System.Text.Encoding.UTF8);
        _log.Info("Capture", $"Exported capture profile '{profile.Name}' to {destinationPath}");
        return destinationPath;
    }

    public (CaptureProfile profile, bool wasReplaced) ImportProfile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var json = File.ReadAllText(sourcePath, System.Text.Encoding.UTF8);
        var imported = JsonSerializer.Deserialize<CaptureProfile>(json, _profileJsonOptions)
            ?? throw new InvalidOperationException("File does not contain a valid capture profile.");

        // Always give the imported copy a fresh ID to avoid collisions
        var cfg = _config.GetConfig();
        var existing = cfg.Capture.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, imported.Name, StringComparison.OrdinalIgnoreCase));

        bool wasReplaced = false;
        if (existing != null)
        {
            imported.Id = existing.Id;
            var idx = cfg.Capture.Profiles.IndexOf(existing);
            cfg.Capture.Profiles[idx] = imported;
            wasReplaced = true;
        }
        else
        {
            imported.Id = Guid.NewGuid().ToString("N")[..8];
            cfg.Capture.Profiles.Add(imported);
            if (string.IsNullOrWhiteSpace(cfg.Capture.SelectedProfileId))
                cfg.Capture.SelectedProfileId = imported.Id;
        }

        _config.SaveConfig(cfg);
        _log.Info("Capture", $"Imported capture profile '{imported.Name}' (replaced={wasReplaced}) from {sourcePath}");
        return (imported, wasReplaced);
    }

    public void DeleteProfile(string profileId)
    {
        var cfg = _config.GetConfig();
        var removed = cfg.Capture.Profiles.RemoveAll(p => p.Id == profileId);
        if (removed > 0)
        {
            if (string.Equals(cfg.Capture.SelectedProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                cfg.Capture.SelectedProfileId = cfg.Capture.Profiles.FirstOrDefault()?.Id ?? "";
            _config.SaveConfig(cfg);
            _log.Info("Capture", $"Deleted capture profile: {profileId}");
        }
    }

    public CaptureCleanupPreview PreviewDeleteProfileAndCaptures(string profileId)
    {
        var cfg = _config.GetConfig();
        var affectedProfiles = cfg.Capture.Profiles.Count(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        var affectsSelectedProfile = string.Equals(cfg.Capture.SelectedProfileId, profileId, StringComparison.OrdinalIgnoreCase);
        return BuildCleanupPreview(
            entry => string.Equals(entry.ProfileId, profileId, StringComparison.OrdinalIgnoreCase),
            affectedProfiles,
            affectsSelectedProfile);
    }

    public CaptureCleanupPreview PreviewOrphanedCapturesCleanup()
    {
        var validProfileIds = _config.GetConfig().Capture.Profiles
            .Select(profile => profile.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return BuildCleanupPreview(
            entry => string.IsNullOrWhiteSpace(entry.ProfileId) || !validProfileIds.Contains(entry.ProfileId),
            affectedProfiles: 0,
            affectsSelectedProfile: false);
    }

    public CaptureCleanupResult DeleteProfileAndCaptures(string profileId)
    {
        var preview = PreviewDeleteProfileAndCaptures(profileId);
        var cfg = _config.GetConfig();
        var removedProfiles = cfg.Capture.Profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        var removedSelectedProfile = string.Equals(cfg.Capture.SelectedProfileId, profileId, StringComparison.OrdinalIgnoreCase);
        if (removedSelectedProfile)
            cfg.Capture.SelectedProfileId = cfg.Capture.Profiles.FirstOrDefault()?.Id ?? "";
        if (removedProfiles > 0)
            _config.SaveConfig(cfg);

        var cleanup = RemoveCaptureEntries(
            entry => string.Equals(entry.ProfileId, profileId, StringComparison.OrdinalIgnoreCase),
            preview,
            operationName: $"profile_{SanitizeFileName(profileId)}");
        cleanup.RemovedProfiles = removedProfiles;
        cleanup.RemovedSelectedProfile = removedSelectedProfile;

        if (removedProfiles > 0)
        {
            _log.Info("Capture", $"Deleted capture profile with captures: {profileId} events={cleanup.RemovedEvents} artifacts={cleanup.RemovedArtifacts}");
        }

        return cleanup;
    }

    public CaptureCleanupResult CleanupOrphanedCaptures()
    {
        var preview = PreviewOrphanedCapturesCleanup();
        var validProfileIds = _config.GetConfig().Capture.Profiles
            .Select(profile => profile.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cleanup = RemoveCaptureEntries(
            entry => string.IsNullOrWhiteSpace(entry.ProfileId) || !validProfileIds.Contains(entry.ProfileId),
            preview,
            operationName: "orphaned");
        if (cleanup.RemovedEvents > 0)
            _log.Info("Capture", $"Cleaned orphaned captures: events={cleanup.RemovedEvents} artifacts={cleanup.RemovedArtifacts}");

        return cleanup;
    }

    public async Task<CaptureEventResult?> CaptureSelectedProfileAsync(string? note = null)
    {
        var profile = GetSelectedProfile();
        if (profile == null)
            return null;

        return await CaptureProfileAsync(profile, note);
    }

    public CaptureTargetDefinition CreateScreenRegionTarget(string name, CapturedRegion region)
    {
        var virtualLeft = (int)SystemParameters.VirtualScreenLeft;
        var virtualTop = (int)SystemParameters.VirtualScreenTop;
        var virtualWidth = (int)SystemParameters.VirtualScreenWidth;
        var virtualHeight = (int)SystemParameters.VirtualScreenHeight;

        return new CaptureTargetDefinition
        {
            Name = name,
            Kind = CaptureTargetKind.ScreenRegion,
            Region = region.ToScreenNormalized(virtualWidth, virtualHeight, virtualLeft, virtualTop)
        };
    }

    public CaptureTargetDefinition CreateWindowTarget(CaptureWindowCandidate window)
    {
        return new CaptureTargetDefinition
        {
            Name = string.IsNullOrWhiteSpace(window.WindowTitle) ? window.ProcessName : window.WindowTitle,
            Kind = CaptureTargetKind.Window,
            ProcessName = window.ProcessName,
            WindowTitle = window.WindowTitle,
            WindowHandleHint = window.Handle
        };
    }

    public CaptureTargetDefinition CreateWindowRegionTarget(CaptureWindowCandidate window, CapturedRegion regionRelativeToWindow)
    {
        return new CaptureTargetDefinition
        {
            Name = $"{window.WindowTitle} region",
            Kind = CaptureTargetKind.WindowRegion,
            ProcessName = window.ProcessName,
            WindowTitle = window.WindowTitle,
            WindowHandleHint = window.Handle,
            Region = regionRelativeToWindow.ToNormalized(0, 0, Math.Max(window.Width, 1), Math.Max(window.Height, 1))
        };
    }

    public async Task<CaptureEventResult> CaptureProfileAsync(CaptureProfile profile, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return await Task.Run(() => CaptureProfileCore(profile, note));
    }

    public IReadOnlyList<CaptureEventResult> ListRecentCaptureEvents(int maxCount = 24)
    {
        lock (_recentLock)
        {
            return _recentEvents
                .Take(Math.Max(1, maxCount))
                .Select(CloneEvent)
                .ToList();
        }
    }

    public CapturePerformanceSnapshot GetPerformanceSnapshot(int sampleSize = 50)
    {
        List<CapturePerformanceMetrics> metrics;
        lock (_recentLock)
        {
            metrics = _recentEvents
                .Select(item => item.Metrics)
                .Where(item => item != null)
                .Cast<CapturePerformanceMetrics>()
                .Take(Math.Max(1, sampleSize))
                .ToList();
        }

        if (metrics.Count == 0)
        {
            return new CapturePerformanceSnapshot
            {
                SampleCount = 0,
                WindowCacheTtlMs = 750
            };
        }

        static long Percentile(List<long> values, double percentile)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            var index = (int)Math.Ceiling((values.Count * percentile)) - 1;
            return values[Math.Clamp(index, 0, values.Count - 1)];
        }

        var total = metrics.Select(m => m.TotalCaptureMs).Where(v => v > 0).ToList();
        var screenshot = metrics.Select(m => m.ScreenshotCaptureMs).Where(v => v > 0).ToList();
        var queue = metrics.Select(m => m.PersistenceQueueMs).Where(v => v >= 0).ToList();
        var write = metrics.Select(m => m.PersistenceWriteMs).Where(v => v >= 0).ToList();

        return new CapturePerformanceSnapshot
        {
            SampleCount = metrics.Count,
            WindowCacheTtlMs = 750,
            AvgTotalCaptureMs = total.Count == 0 ? 0 : total.Average(),
            P50TotalCaptureMs = Percentile(total, 0.50),
            P95TotalCaptureMs = Percentile(total, 0.95),
            AvgScreenshotCaptureMs = screenshot.Count == 0 ? 0 : screenshot.Average(),
            AvgPersistenceQueueMs = queue.Count == 0 ? 0 : queue.Average(),
            AvgPersistenceWriteMs = write.Count == 0 ? 0 : write.Average()
        };
    }

    public CaptureEventResult? GetCaptureEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return null;

        lock (_recentLock)
        {
            var cached = _recentEvents.FirstOrDefault(r => string.Equals(r.EventId, eventId, StringComparison.OrdinalIgnoreCase));
            if (cached != null)
                return CloneEvent(cached);
        }

        if (!File.Exists(_journalPath))
            return null;

        foreach (var line in File.ReadLines(_journalPath).Reverse())
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CaptureJournalEntry>(line, JournalJsonOptions);
                if (entry == null || !string.Equals(entry.EventId, eventId, StringComparison.OrdinalIgnoreCase))
                    continue;

                return LoadEventFromMetadata(entry.MetadataPath);
            }
            catch
            {
            }
        }

        return null;
    }

    public JsonElement? GetCaptureAnalysis(string eventId)
    {
        var captureEvent = GetCaptureEvent(eventId);
        if (captureEvent == null || string.IsNullOrWhiteSpace(captureEvent.AnalysisPath) || !File.Exists(captureEvent.AnalysisPath))
            return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(captureEvent.AnalysisPath));
        return doc.RootElement.Clone();
    }

    private CaptureEventResult CaptureProfileCore(CaptureProfile profile, string? note)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var now = DateTime.Now;
        var eventId = now.ToString("yyyyMMdd_HHmmss_fff");
        var outputDir = ResolveOutputDirectory(profile);
        Directory.CreateDirectory(outputDir);

        var artifacts = new List<CaptureArtifact>();
        var failures = new List<string>();
        var metrics = new CapturePerformanceMetrics();
        var screenshotStopwatch = Stopwatch.StartNew();

        for (int i = 0; i < profile.Targets.Count; i++)
        {
            var target = profile.Targets[i];
            var safeTargetName = SanitizeFileName(target.Name);
            var fileName = $"{profile.FilePrefix}_{eventId}_{i + 1:D2}_{safeTargetName}.png";

            try
            {
                string? path = target.Kind switch
                {
                    CaptureTargetKind.ScreenRegion => CaptureScreenRegion(target, outputDir, fileName),
                    CaptureTargetKind.Window => CaptureWindow(target, outputDir, fileName),
                    CaptureTargetKind.WindowRegion => CaptureWindowRegion(target, outputDir, fileName),
                    _ => null
                };

                if (path == null)
                {
                    failures.Add($"Target '{target.Name}' did not produce an image.");
                    continue;
                }

                artifacts.Add(new CaptureArtifact
                {
                    TargetId = target.Id,
                    TargetName = target.Name,
                    Kind = target.Kind,
                    Path = path
                });
            }
            catch (Exception ex)
            {
                failures.Add($"Target '{target.Name}' failed: {ex.Message}");
            }
        }
        screenshotStopwatch.Stop();
        metrics.ScreenshotCaptureMs = screenshotStopwatch.ElapsedMilliseconds;

        var metadataPath = Path.Combine(outputDir, $"{profile.FilePrefix}_{eventId}.json");
        var analysisPath = Path.Combine(outputDir, $"{profile.FilePrefix}_{eventId}.analysis.json");
        var result = new CaptureEventResult
        {
            EventId = eventId,
            CapturedAt = now,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Note = note ?? "",
            OutputDirectory = outputDir,
            MetadataPath = metadataPath,
            AnalysisPath = analysisPath,
            Metrics = metrics,
            Artifacts = artifacts,
            Failures = failures
        };

        var queueStopwatch = Stopwatch.StartNew();
        EnqueuePersistence(result, metadataPath);
        queueStopwatch.Stop();
        metrics.PersistenceQueueMs = queueStopwatch.ElapsedMilliseconds;
        foreach (var failure in failures)
            _log.Warn("Capture", failure);

        totalStopwatch.Stop();
        metrics.TotalCaptureMs = totalStopwatch.ElapsedMilliseconds;
        RefreshRecentEvent(result);

        CaptureCompleted?.Invoke(result);

        return result;
    }

    private void EnqueuePersistence(CaptureEventResult result, string metadataPath)
    {
        result.MetadataPath = metadataPath;
        if (string.IsNullOrWhiteSpace(result.AnalysisPath))
            result.AnalysisPath = Path.ChangeExtension(metadataPath, ".analysis.json");

        AppendRecentEvent(result);

        if (!_persistenceChannel.Writer.TryWrite(new CapturePersistenceWorkItem(CloneEvent(result), metadataPath)))
            RecordCaptureEvent(result, metadataPath);
    }

    internal void RecordCaptureEvent(CaptureEventResult result, string metadataPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        result.Metrics ??= new CapturePerformanceMetrics();

        Directory.CreateDirectory(_captureRoot);

        result.MetadataPath = metadataPath;
        if (string.IsNullOrWhiteSpace(result.AnalysisPath))
            result.AnalysisPath = Path.ChangeExtension(metadataPath, ".analysis.json");

        var persistenceStopwatch = Stopwatch.StartNew();
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(result, PersistedJsonOptions));
        if (!File.Exists(result.AnalysisPath))
            WriteAnalysisSummary(result);

        var journalEntry = new CaptureJournalEntry
        {
            EventId = result.EventId,
            CapturedAt = result.CapturedAt,
            ProfileId = result.ProfileId,
            ProfileName = result.ProfileName,
            Note = result.Note,
            OutputDirectory = result.OutputDirectory,
            MetadataPath = metadataPath,
            AnalysisPath = result.AnalysisPath,
            ArtifactCount = result.Artifacts.Count,
            FailureCount = result.Failures.Count,
            ArtifactPaths = result.Artifacts.Select(a => a.Path).ToList(),
            Failures = result.Failures.ToList()
        };

        var json = JsonSerializer.Serialize(journalEntry, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        lock (_journalLock)
        {
            File.AppendAllText(_journalPath, json + Environment.NewLine);
        }
        AppendRecentEvent(result);
        ScheduleRetentionMaintenance();

        persistenceStopwatch.Stop();
        result.Metrics.PersistenceWriteMs = persistenceStopwatch.ElapsedMilliseconds;

        _log.Info("CaptureEvent",
            $"eventId={result.EventId} profile={result.ProfileName} artifacts={result.Artifacts.Count} failures={result.Failures.Count} manifest={metadataPath}");
    }

    private async Task ProcessPersistenceQueueAsync()
    {
        try
        {
            await foreach (var workItem in _persistenceChannel.Reader.ReadAllAsync(_persistenceCts.Token))
            {
                try
                {
                    RecordCaptureEvent(workItem.Result, workItem.MetadataPath);
                }
                catch (Exception ex)
                {
                    _log.Error("CapturePersistence", $"Failed to persist capture event {workItem.Result.EventId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void LoadRecentEventsFromDisk()
    {
        if (!File.Exists(_journalPath))
            return;

        try
        {
            var journalEntries = File.ReadLines(_journalPath)
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<CaptureJournalEntry>(line, JournalJsonOptions); }
                    catch { return null; }
                })
                .Where(entry => entry != null)
                .Cast<CaptureJournalEntry>()
                .OrderByDescending(entry => entry.CapturedAt)
                .Take(GetMaxRecentEvents())
                .ToList();

            lock (_recentLock)
            {
                _recentEvents.Clear();
                foreach (var entry in journalEntries)
                {
                    var captureEvent = LoadEventFromMetadata(entry.MetadataPath);
                    if (captureEvent != null)
                        _recentEvents.Add(captureEvent);
                }
            }
        }
        catch
        {
            // Ignore startup cache hydration failures.
        }
    }

    private CaptureEventResult? LoadEventFromMetadata(string metadataPath)
    {
        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
            return null;

        try
        {
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<CaptureEventResult>(json, JournalJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void AppendRecentEvent(CaptureEventResult result)
    {
        lock (_recentLock)
        {
            _recentEvents.RemoveAll(entry => string.Equals(entry.EventId, result.EventId, StringComparison.OrdinalIgnoreCase));
            _recentEvents.Insert(0, CloneEvent(result));

            var maxRecent = GetMaxRecentEvents();
            if (_recentEvents.Count > maxRecent)
                _recentEvents.RemoveRange(maxRecent, _recentEvents.Count - maxRecent);
        }
    }

    private void RefreshRecentEvent(CaptureEventResult result)
    {
        lock (_recentLock)
        {
            var index = _recentEvents.FindIndex(entry => string.Equals(entry.EventId, result.EventId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _recentEvents[index] = CloneEvent(result);
        }
    }

    private int GetMaxRecentEvents()
        => Math.Max(10, _config.GetConfig().Capture.MaxRecentEventsInMemory);

    private void WriteAnalysisSummary(CaptureEventResult result)
    {
        if (string.IsNullOrWhiteSpace(result.AnalysisPath))
            return;

        var analysis = new CaptureAnalysisSummary
        {
            EventId = result.EventId,
            ProfileName = result.ProfileName,
            CapturedAt = result.CapturedAt,
            Severity = result.Failures.Count == 0 ? "normal" : "warning",
            Summary = result.Failures.Count == 0
                ? $"Captured {result.Artifacts.Count} artifact(s) successfully."
                : $"Captured {result.Artifacts.Count} artifact(s) with {result.Failures.Count} warning(s).",
            ArtifactCount = result.Artifacts.Count,
            FailureCount = result.Failures.Count,
            Note = result.Note,
            Metrics = result.Metrics,
            ArtifactKinds = result.Artifacts.Select(a => a.Kind.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ArtifactPaths = result.Artifacts.Select(a => a.Path).ToList(),
            Failures = result.Failures.ToList()
        };

        File.WriteAllText(result.AnalysisPath, JsonSerializer.Serialize(analysis, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));
    }

    private void ScheduleRetentionMaintenance()
    {
        if (!_config.GetConfig().Capture.RetentionEnabled)
            return;

        if (Interlocked.Increment(ref _maintenanceCounter) % 10 != 0)
            return;

        _ = Task.Run(() =>
        {
            try { ApplyRetentionPolicy(); }
            catch (Exception ex) { _log.Warn("CaptureRetention", $"Retention maintenance failed: {ex.Message}"); }
        });
    }

    private void ApplyRetentionPolicy()
    {
        var stopwatch = Stopwatch.StartNew();
        if (!File.Exists(_journalPath))
            return;

        List<CaptureJournalEntry> entries;
        lock (_journalLock)
        {
            entries = File.ReadLines(_journalPath)
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<CaptureJournalEntry>(line, JournalJsonOptions); }
                    catch { return null; }
                })
                .Where(entry => entry != null)
                .Cast<CaptureJournalEntry>()
                .OrderByDescending(entry => entry.CapturedAt)
                .ToList();

            var retainCount = Math.Max(10, Math.Min(_config.GetConfig().Capture.MaxSavedEvents, _config.GetConfig().Capture.MaxJournalEntries));
            if (entries.Count <= retainCount)
                return;

            var keep = entries.Take(retainCount).ToList();
            var prune = entries.Skip(retainCount).ToList();
            var recycleDirectory = CreateRecycleOperationDirectory("retention");

            foreach (var entry in prune)
            {
                RecycleCaptureEntryFiles(entry, recycleDirectory);
            }

            var lines = keep.Select(entry => JsonSerializer.Serialize(entry, JournalJsonOptions));
            File.WriteAllLines(_journalPath, lines);

            lock (_recentLock)
            {
                _recentEvents.RemoveAll(existing => prune.Any(pruned => string.Equals(pruned.EventId, existing.EventId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        stopwatch.Stop();
        _log.Debug("CaptureRetention", $"retention-ms={stopwatch.ElapsedMilliseconds}");
    }

    private CaptureCleanupPreview BuildCleanupPreview(Func<CaptureJournalEntry, bool> predicate, int affectedProfiles, bool affectsSelectedProfile)
    {
        var entries = ReadJournalEntries();
        var affectedEntries = entries.Where(predicate).ToList();
        return new CaptureCleanupPreview
        {
            AffectedProfiles = affectedProfiles,
            AffectedEvents = affectedEntries.Count,
            AffectedArtifacts = affectedEntries.Sum(entry => entry.ArtifactPaths.Count),
            AffectsSelectedProfile = affectsSelectedProfile
        };
    }

    private List<CaptureJournalEntry> ReadJournalEntries()
    {
        if (!File.Exists(_journalPath))
            return [];

        lock (_journalLock)
        {
            return File.ReadLines(_journalPath)
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<CaptureJournalEntry>(line, JournalJsonOptions); }
                    catch { return null; }
                })
                .Where(entry => entry != null)
                .Cast<CaptureJournalEntry>()
                .OrderByDescending(entry => entry.CapturedAt)
                .ToList();
        }
    }

    private CaptureCleanupResult RemoveCaptureEntries(Func<CaptureJournalEntry, bool> predicate, CaptureCleanupPreview preview, string operationName)
    {
        if (!File.Exists(_journalPath))
        {
            return new CaptureCleanupResult
            {
                Preview = preview
            };
        }

        lock (_journalLock)
        {
            var entries = ReadJournalEntries();

            var prune = entries.Where(predicate).ToList();
            if (prune.Count == 0)
            {
                return new CaptureCleanupResult
                {
                    Preview = preview
                };
            }

            var keep = entries.Where(entry => !predicate(entry)).ToList();
            var recycleDirectory = CreateRecycleOperationDirectory(operationName);
            foreach (var entry in prune)
                RecycleCaptureEntryFiles(entry, recycleDirectory);

            var lines = keep.Select(entry => JsonSerializer.Serialize(entry, JournalJsonOptions));
            File.WriteAllLines(_journalPath, lines);

            var prunedEventIds = prune.Select(entry => entry.EventId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_recentLock)
            {
                _recentEvents.RemoveAll(existing => prunedEventIds.Contains(existing.EventId));
            }

            return new CaptureCleanupResult
            {
                Preview = preview,
                RemovedEvents = prune.Count,
                RemovedArtifacts = prune.Sum(entry => entry.ArtifactPaths.Count),
                RecycleDirectory = recycleDirectory
            };
        }
    }

    private string CreateRecycleOperationDirectory(string operationName)
    {
        Directory.CreateDirectory(_recycleRoot);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var folderName = $"{SanitizeFileName(operationName)}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{suffix}";
        var recycleDirectory = Path.Combine(_recycleRoot, folderName);
        Directory.CreateDirectory(recycleDirectory);
        return recycleDirectory;
    }

    private void RecycleCaptureEntryFiles(CaptureJournalEntry entry, string recycleDirectory)
    {
        var eventDirectory = Path.Combine(recycleDirectory, SanitizeFileName(entry.EventId));
        Directory.CreateDirectory(eventDirectory);

        foreach (var path in entry.ArtifactPaths)
        {
            if (File.Exists(path))
                MoveFileToRecycle(path, Path.Combine(eventDirectory, "artifacts"));
        }

        if (!string.IsNullOrWhiteSpace(entry.MetadataPath) && File.Exists(entry.MetadataPath))
            MoveFileToRecycle(entry.MetadataPath, Path.Combine(eventDirectory, "metadata"));
        if (!string.IsNullOrWhiteSpace(entry.AnalysisPath) && File.Exists(entry.AnalysisPath))
            MoveFileToRecycle(entry.AnalysisPath, Path.Combine(eventDirectory, "analysis"));

        if (!string.IsNullOrWhiteSpace(entry.OutputDirectory))
            PruneEmptyDirectories(entry.OutputDirectory);
    }

    private static void MoveFileToRecycle(string sourcePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var fileName = Path.GetFileName(sourcePath);
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        if (File.Exists(destinationPath))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            destinationPath = Path.Combine(destinationDirectory, $"{stem}_{Guid.NewGuid().ToString("N")[..6]}{extension}");
        }

        File.Move(sourcePath, destinationPath);
    }

    private void PruneEmptyDirectories(string directoryPath)
    {
        var root = Path.GetFullPath(_captureRoot);
        var current = directoryPath;

        while (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            var currentFullPath = Path.GetFullPath(current);
            if (string.Equals(currentFullPath, root, StringComparison.OrdinalIgnoreCase))
                break;
            if (string.Equals(currentFullPath, Path.GetFullPath(_recycleRoot), StringComparison.OrdinalIgnoreCase))
                break;
            if (Directory.EnumerateFileSystemEntries(current).Any())
                break;

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions PersistedJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static CaptureEventResult CloneEvent(CaptureEventResult value) => new()
    {
        EventId = value.EventId,
        CapturedAt = value.CapturedAt,
        ProfileId = value.ProfileId,
        ProfileName = value.ProfileName,
        Note = value.Note,
        OutputDirectory = value.OutputDirectory,
        MetadataPath = value.MetadataPath,
        AnalysisPath = value.AnalysisPath,
        Metrics = value.Metrics == null ? null : new CapturePerformanceMetrics
        {
            TotalCaptureMs = value.Metrics.TotalCaptureMs,
            ScreenshotCaptureMs = value.Metrics.ScreenshotCaptureMs,
            PersistenceQueueMs = value.Metrics.PersistenceQueueMs,
            PersistenceWriteMs = value.Metrics.PersistenceWriteMs,
            RetentionMaintenanceMs = value.Metrics.RetentionMaintenanceMs
        },
        Artifacts = value.Artifacts.Select(a => new CaptureArtifact
        {
            TargetId = a.TargetId,
            TargetName = a.TargetName,
            Kind = a.Kind,
            Path = a.Path
        }).ToList(),
        Failures = value.Failures.ToList()
    };

    public void Dispose()
    {
        try
        {
            _persistenceChannel.Writer.TryComplete();
            _persistenceWorker.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        finally
        {
            _persistenceCts.Cancel();
            _persistenceCts.Dispose();
        }
    }

    private string CaptureScreenRegion(CaptureTargetDefinition target, string outputDir, string fileName)
    {
        if (target.Region == null)
            throw new InvalidOperationException("Screen region target is missing its region.");

        var region = DenormalizeScreenRegion(target.Region);
        return _reports.CaptureRegionScreenshot(region, outputDir, fileName)
            ?? throw new InvalidOperationException("Region capture failed.");
    }

    private string CaptureWindow(CaptureTargetDefinition target, string outputDir, string fileName)
    {
        var window = ResolveWindow(target)
            ?? throw new InvalidOperationException("Window could not be resolved.");

        return _reports.CaptureWindowScreenshot(new IntPtr(window.Handle), outputDir, fileName)
            ?? throw new InvalidOperationException("Window capture failed.");
    }

    private string CaptureWindowRegion(CaptureTargetDefinition target, string outputDir, string fileName)
    {
        if (target.Region == null)
            throw new InvalidOperationException("Window-region target is missing its region.");

        var window = ResolveWindow(target)
            ?? throw new InvalidOperationException("Window could not be resolved.");

        var region = new CapturedRegion
        {
            X = (int)Math.Round(target.Region.X * window.Width),
            Y = (int)Math.Round(target.Region.Y * window.Height),
            Width = (int)Math.Round(target.Region.Width * window.Width),
            Height = (int)Math.Round(target.Region.Height * window.Height)
        };

        var absoluteRegion = new CapturedRegion
        {
            X = window.Left + region.X,
            Y = window.Top + region.Y,
            Width = region.Width,
            Height = region.Height
        };

        return _reports.CaptureRegionScreenshot(absoluteRegion, outputDir, fileName)
            ?? throw new InvalidOperationException("Window-region capture failed.");
    }

    private CaptureWindowCandidate? ResolveWindow(CaptureTargetDefinition target)
    {
        var windows = ListWindows();

        if (target.WindowHandleHint != 0)
        {
            var byHandle = windows.FirstOrDefault(w => w.Handle == target.WindowHandleHint);
            if (byHandle != null)
                return byHandle;
        }

        var byProcessAndTitle = windows.FirstOrDefault(w =>
            Matches(w.ProcessName, target.ProcessName) && Matches(w.WindowTitle, target.WindowTitle));
        if (byProcessAndTitle != null)
            return byProcessAndTitle;

        return windows.FirstOrDefault(w =>
            Matches(w.ProcessName, target.ProcessName) || Matches(w.WindowTitle, target.WindowTitle));
    }

    private static bool Matches(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return false;
        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveOutputDirectory(CaptureProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.OutputDirectory))
            return profile.OutputDirectory;

        var configured = _config.GetConfig().Capture.DefaultOutputDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(_reports.ReportsDirectory, "_captures", SanitizeFileName(profile.Name));
    }

    private static CapturedRegion DenormalizeScreenRegion(ScreenRegion region)
    {
        var left = (int)SystemParameters.VirtualScreenLeft;
        var top = (int)SystemParameters.VirtualScreenTop;
        var width = (int)SystemParameters.VirtualScreenWidth;
        var height = (int)SystemParameters.VirtualScreenHeight;

        return new CapturedRegion
        {
            X = left + (int)Math.Round(region.X * width),
            Y = top + (int)Math.Round(region.Y * height),
            Width = (int)Math.Round(region.Width * width),
            Height = (int)Math.Round(region.Height * height)
        };
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

public class CaptureWindowCandidate
{
    public long Handle { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string DisplayName => $"{ProcessName} — {WindowTitle}";
}

public class CaptureArtifact
{
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";
    public CaptureTargetKind Kind { get; set; }
    public string Path { get; set; } = "";
}

public class CaptureEventResult
{
    public string EventId { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Note { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string MetadataPath { get; set; } = "";
    public string AnalysisPath { get; set; } = "";
    public CapturePerformanceMetrics? Metrics { get; set; }
    public List<CaptureArtifact> Artifacts { get; set; } = [];
    public List<string> Failures { get; set; } = [];

    public string PreviewPath => Artifacts.FirstOrDefault()?.Path ?? "";
    public string TimeLabel => CapturedAt.ToString("HH:mm:ss.fff");
}

internal class CaptureJournalEntry
{
    public string EventId { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Note { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string MetadataPath { get; set; } = "";
    public string AnalysisPath { get; set; } = "";
    public int ArtifactCount { get; set; }
    public int FailureCount { get; set; }
    public List<string> ArtifactPaths { get; set; } = [];
    public List<string> Failures { get; set; } = [];
}

internal class CaptureAnalysisSummary
{
    public string EventId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string Severity { get; set; } = "normal";
    public string Summary { get; set; } = "";
    public int ArtifactCount { get; set; }
    public int FailureCount { get; set; }
    public string Note { get; set; } = "";
    public CapturePerformanceMetrics? Metrics { get; set; }
    public List<string> ArtifactKinds { get; set; } = [];
    public List<string> ArtifactPaths { get; set; } = [];
    public List<string> Failures { get; set; } = [];
}

public class CapturePerformanceMetrics
{
    public long TotalCaptureMs { get; set; }
    public long ScreenshotCaptureMs { get; set; }
    public long PersistenceQueueMs { get; set; }
    public long PersistenceWriteMs { get; set; }
    public long RetentionMaintenanceMs { get; set; }
}

public class CapturePerformanceSnapshot
{
    public int SampleCount { get; set; }
    public double AvgTotalCaptureMs { get; set; }
    public long P50TotalCaptureMs { get; set; }
    public long P95TotalCaptureMs { get; set; }
    public double AvgScreenshotCaptureMs { get; set; }
    public double AvgPersistenceQueueMs { get; set; }
    public double AvgPersistenceWriteMs { get; set; }
    public int WindowCacheTtlMs { get; set; }
}

public class CaptureCleanupResult
{
    public CaptureCleanupPreview Preview { get; set; } = new();
    public int RemovedProfiles { get; set; }
    public int RemovedEvents { get; set; }
    public int RemovedArtifacts { get; set; }
    public bool RemovedSelectedProfile { get; set; }
    public string RecycleDirectory { get; set; } = "";
}

public class CaptureCleanupPreview
{
    public int AffectedProfiles { get; set; }
    public int AffectedEvents { get; set; }
    public int AffectedArtifacts { get; set; }
    public bool AffectsSelectedProfile { get; set; }
}

internal sealed record CapturePersistenceWorkItem(CaptureEventResult Result, string MetadataPath);
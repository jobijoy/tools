using System.Text.Json;
using IdolClick.Models;
using System.Windows.Automation;

namespace IdolClick.Services;

internal sealed class CaptureProfilePackRunnerService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ReportService _reports;
    private readonly SnapCaptureService _snapCapture;
    private readonly CaptureAnnotationService _annotations;
    private readonly SelectorParser _selectorParser;
    private readonly StepExecutor _flowExecutor;
    private StreamWriter? _fileWriter;

    public CaptureProfilePackRunnerService(
        ConfigService config,
        LogService log,
        ReportService reports,
        SnapCaptureService snapCapture,
        CaptureAnnotationService annotations,
        SelectorParser selectorParser,
        StepExecutor flowExecutor)
    {
        _config = config;
        _log = log;
        _reports = reports;
        _snapCapture = snapCapture;
        _annotations = annotations;
        _selectorParser = selectorParser;
        _flowExecutor = flowExecutor;
    }

    public event Action<string>? OnLogMessage;

    public void SetLogFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        _fileWriter = new StreamWriter(path, append: false)
        {
            AutoFlush = true
        };
    }

    public void CloseLogFile()
    {
        _fileWriter?.Dispose();
        _fileWriter = null;
    }

    public async Task<CapturePackRunResult> RunAsync(
        string packPath,
        bool smokeMode,
        IReadOnlyDictionary<string, string>? inputs,
        CancellationToken ct)
    {
        var fullPackPath = Path.GetFullPath(packPath);
        var pack = CaptureProfilePack.LoadFromFile(fullPackPath, inputs, out var resolvedInputs);
        var runId = $"{pack.PackId}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var outputDirectory = Path.Combine(_reports.ReportsDirectory, "_capture-pack-runs", runId);
        Directory.CreateDirectory(outputDirectory);
        var result = new CapturePackRunResult
        {
            PackId = pack.PackId,
            PackName = pack.Name,
            SmokeMode = smokeMode,
            OutputDirectory = outputDirectory,
            ObservationPlan = pack.ObservationPlan,
            ResolvedInputs = resolvedInputs
        };

        EmitLog($"[pack] Running {(smokeMode ? "smoke" : "full")} test for {pack.Name}");
        var originalSelectedProfileId = _config.GetConfig().Capture.SelectedProfileId;

        try
        {
            UpsertProfile(pack.CaptureProfile);
            _snapCapture.SetSelectedProfile(pack.CaptureProfile.Id);
            EmitLog($"[capture] Selected profile {pack.CaptureProfile.Name}");

            if (!string.IsNullOrWhiteSpace(pack.BootstrapFlowPath))
            {
                var flowPath = ResolveRelativePath(fullPackPath, pack.BootstrapFlowPath);
                var flowJson = CaptureProfilePack.ApplyInputTokens(File.ReadAllText(flowPath), resolvedInputs);
                var flow = JsonSerializer.Deserialize<TestFlow>(flowJson, FlowJson.Options)
                    ?? throw new InvalidOperationException($"Failed to load bootstrap flow: {flowPath}");
                EmitLog($"[flow] Executing bootstrap flow {Path.GetFileName(flowPath)}");
                var flowReport = await _flowExecutor.ExecuteFlowAsync(flow, cancellationToken: ct).ConfigureAwait(false);
                result.BootstrapFlowPath = flowPath;
                result.BootstrapReportPath = Path.Combine(outputDirectory, "bootstrap-report.json");
                await File.WriteAllTextAsync(result.BootstrapReportPath, JsonSerializer.Serialize(flowReport, FlowJson.Options), ct).ConfigureAwait(false);
                if (!string.Equals(flowReport.Result, "passed", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(flowReport.Result, "passed_with_warnings", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Bootstrap flow failed: {flowReport.Result}");
                }
            }

            var captureCount = DetermineCaptureCount(pack.ObservationPlan, smokeMode);
            var intervalSeconds = DetermineIntervalSeconds(pack.ObservationPlan, smokeMode);
            var audioSeconds = DetermineAudioSeconds(pack.ObservationPlan, smokeMode);
            var bootstrapWindow = ResolvePrimaryWindow(pack);
            var bootstrapStatus = await CaptureStatusSnapshotsAsync(pack, bootstrapWindow, ct).ConfigureAwait(false);
            result.BootstrapStatus = bootstrapStatus;
            ValidateBootstrapStatus(pack, bootstrapStatus);
            var previousStatusSignature = BuildStatusSignature(bootstrapStatus);

            if ((string.Equals(pack.ObservationPlan.TriggerMode, "status-change", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pack.ObservationPlan.TriggerMode, "hybrid", StringComparison.OrdinalIgnoreCase))
                && pack.GetEffectiveStatusProbes().Count > 0
                && bootstrapStatus.All(item => !item.Resolved))
            {
                throw new InvalidOperationException("No status selectors could be resolved after bootstrap, so status-change monitoring cannot start.");
            }

            for (var index = 0; index < captureCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                var statusWindow = bootstrapWindow;
                List<CapturePackStatusSnapshot> statusSnapshots = [];

                if (index == 0)
                {
                    statusSnapshots = bootstrapStatus;
                }
                else
                {
                    switch (pack.ObservationPlan.TriggerMode.ToLowerInvariant())
                    {
                        case "status-change":
                        case "hybrid":
                            var waitResult = await WaitForStatusChangeAsync(pack, previousStatusSignature, intervalSeconds, ct).ConfigureAwait(false);
                            statusWindow = waitResult.Window;
                            statusSnapshots = waitResult.Snapshots;
                            if (!waitResult.Changed)
                                EmitLog($"[status] No selector change detected within {intervalSeconds}s; continuing capture on timeout");
                            break;
                        default:
                            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct).ConfigureAwait(false);
                            statusWindow = ResolvePrimaryWindow(pack);
                            statusSnapshots = await CaptureStatusSnapshotsAsync(pack, statusWindow, ct).ConfigureAwait(false);
                            break;
                    }
                }

                var capture = await _snapCapture.CaptureSelectedProfileAsync($"Pack run {pack.PackId} capture {index + 1}").ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Capture service did not return a result.");
                EmitLog($"[snap] Capture {index + 1}/{captureCount}: {capture.EventId}");

                string annotationId = string.Empty;
                string annotationAudioPath = string.Empty;
                if (audioSeconds > 0)
                {
                    var audioBytes = CaptureHarnessService.CreateSyntheticVoiceWave(index + 1, durationMs: audioSeconds * 1000);
                    var now = DateTime.UtcNow;
                    var annotation = _annotations.RecordAnnotation(
                        $"Synthetic audio note for {pack.Name} capture {index + 1}",
                        now.AddSeconds(-audioSeconds),
                        now,
                        audioBytes);
                    annotationId = annotation.AnnotationId;
                    annotationAudioPath = annotation.AudioPath;
                    EmitLog($"[voice] Annotation {annotationId}");
                }

                result.Captures.Add(new CapturePackCaptureResult
                {
                    Index = index + 1,
                    CaptureEventId = capture.EventId,
                    PreviewPath = capture.PreviewPath,
                    MetadataPath = capture.MetadataPath,
                    AnnotationId = annotationId,
                    AnnotationAudioPath = annotationAudioPath,
                    StatusSnapshots = statusSnapshots
                });

                previousStatusSignature = BuildStatusSignature(statusSnapshots);
            }

            result.Succeeded = true;
            result.ReportPath = Path.Combine(outputDirectory, "capture-pack-run.json");
            await File.WriteAllTextAsync(result.ReportPath, JsonSerializer.Serialize(result, CaptureProfilePackJson.Options), ct).ConfigureAwait(false);
            EmitLog($"[pack] Completed successfully: {result.ReportPath}");
            return result;
        }
        catch (Exception ex)
        {
            result.Succeeded = false;
            result.Error = ex.Message;
            result.ReportPath = Path.Combine(outputDirectory, "capture-pack-run.json");
            await File.WriteAllTextAsync(result.ReportPath, JsonSerializer.Serialize(result, CaptureProfilePackJson.Options), CancellationToken.None).ConfigureAwait(false);
            EmitLog($"[pack] FAILED: {ex.Message}");
            return result;
        }
        finally
        {
            _snapCapture.SetSelectedProfile(originalSelectedProfileId);
        }
    }

    private void UpsertProfile(CaptureProfile profile)
    {
        var cfg = _config.GetConfig();
        var existing = cfg.Capture.Profiles.FirstOrDefault(item => string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            cfg.Capture.Profiles.Add(profile);
        }
        else
        {
            existing.Name = profile.Name;
            existing.Enabled = profile.Enabled;
            existing.FilePrefix = profile.FilePrefix;
            existing.OutputDirectory = profile.OutputDirectory;
            existing.Targets = profile.Targets;
        }
        _config.SaveConfig(cfg);
    }

    private static string ResolveRelativePath(string packPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        var packDirectory = Path.GetDirectoryName(packPath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(packDirectory, relativePath));
    }

    private static int DetermineCaptureCount(CaptureObservationPlan plan, bool smokeMode)
    {
        if (!smokeMode)
        {
            var interval = Math.Max(1, plan.IntervalSeconds);
            var duration = Math.Max(interval, plan.DurationSeconds);
            return Math.Max(1, duration / interval);
        }

        return 2;
    }

    private static int DetermineIntervalSeconds(CaptureObservationPlan plan, bool smokeMode)
    {
        if (!smokeMode)
            return Math.Max(1, plan.IntervalSeconds);
        return Math.Max(2, Math.Min(5, plan.IntervalSeconds == 0 ? 5 : plan.IntervalSeconds));
    }

    private static int DetermineAudioSeconds(CaptureObservationPlan plan, bool smokeMode)
    {
        if (plan.AudioClipSeconds <= 0)
            return 0;
        return smokeMode ? Math.Min(2, plan.AudioClipSeconds) : plan.AudioClipSeconds;
    }

    private void EmitLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _fileWriter?.WriteLine(line);
        OnLogMessage?.Invoke(line);
    }

    private AutomationElement? ResolvePrimaryWindow(CaptureProfilePack pack)
    {
        var target = pack.CaptureProfile.Targets.FirstOrDefault(item =>
            item.Kind == CaptureTargetKind.Window || item.Kind == CaptureTargetKind.WindowRegion);
        if (target == null)
            return null;

        return _selectorParser.FindWindow(target.ProcessName, target.WindowTitle);
    }

    private async Task<List<CapturePackStatusSnapshot>> CaptureStatusSnapshotsAsync(
        CaptureProfilePack pack,
        AutomationElement? window,
        CancellationToken ct)
    {
        var snapshots = new List<CapturePackStatusSnapshot>();
        var probes = pack.GetEffectiveStatusProbes();
        if (probes.Count == 0)
            return snapshots;

        foreach (var probe in probes)
            snapshots.Add(await EvaluateStatusProbeAsync(window, probe, ct).ConfigureAwait(false));

        return snapshots;
    }

    private async Task<CapturePackStatusWaitResult> WaitForStatusChangeAsync(
        CaptureProfilePack pack,
        string previousSignature,
        int waitSeconds,
        CancellationToken ct)
    {
        var timeout = DateTime.UtcNow.AddSeconds(Math.Max(1, waitSeconds));
        AutomationElement? lastWindow = null;
        List<CapturePackStatusSnapshot> lastSnapshots = [];

        while (DateTime.UtcNow < timeout)
        {
            ct.ThrowIfCancellationRequested();
            lastWindow = ResolvePrimaryWindow(pack);
            lastSnapshots = await CaptureStatusSnapshotsAsync(pack, lastWindow, ct).ConfigureAwait(false);
            var signature = BuildStatusSignature(lastSnapshots);
            if (!string.IsNullOrWhiteSpace(signature) && !string.Equals(signature, previousSignature, StringComparison.Ordinal))
                return new CapturePackStatusWaitResult(lastWindow, lastSnapshots, true);

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        return new CapturePackStatusWaitResult(lastWindow, lastSnapshots, false);
    }

    private async Task<CapturePackStatusSnapshot> EvaluateStatusProbeAsync(
        AutomationElement? window,
        CaptureStatusProbe probe,
        CancellationToken ct)
    {
        if (window == null)
        {
            return new CapturePackStatusSnapshot
            {
                ProbeName = probe.Name,
                ProbeKind = probe.Kind.ToString(),
                Selector = probe.Selector,
                ExpectedContains = probe.Contains,
                ExpectedEquals = probe.EqualsValue,
                Required = probe.Required,
                UseForChangeDetection = probe.UseForChangeDetection,
                Error = "Window not available"
            };
        }

        try
        {
            AutomationElement? element = null;
            ElementSnapshot? snapshot = null;
            var actualValue = string.Empty;

            if (probe.Kind == CaptureStatusProbeKind.WindowTitle)
            {
                var title = window.Current.Name ?? string.Empty;
                element = window;
                snapshot = new ElementSnapshot
                {
                    ControlType = window.Current.ControlType.ProgrammaticName,
                    Name = title,
                    AutomationId = window.Current.AutomationId,
                    IsEnabled = window.Current.IsEnabled
                };
                actualValue = title;
            }
            else
            {
                var match = await _selectorParser.ResolveAsync(window, probe.Selector, 1200, ct: ct).ConfigureAwait(false);
                if (match != null)
                {
                    element = match.Element;
                    snapshot = match.Snapshot;
                    actualValue = probe.Kind == CaptureStatusProbeKind.SelectorExists
                        ? (snapshot.Name ?? snapshot.AutomationId ?? "resolved")
                        : ReadElementValue(element, snapshot);
                }
            }

            if (element == null || snapshot == null)
            {
                return new CapturePackStatusSnapshot
                {
                    ProbeName = probe.Name,
                    ProbeKind = probe.Kind.ToString(),
                    Selector = probe.Selector,
                    ExpectedContains = probe.Contains,
                    ExpectedEquals = probe.EqualsValue,
                    Required = probe.Required,
                    UseForChangeDetection = probe.UseForChangeDetection,
                    Error = "Selector not resolved"
                };
            }

            var matchedExpectation = MatchesProbeExpectation(actualValue, probe);

            return new CapturePackStatusSnapshot
            {
                ProbeName = probe.Name,
                ProbeKind = probe.Kind.ToString(),
                Selector = probe.Selector,
                ExpectedContains = probe.Contains,
                ExpectedEquals = probe.EqualsValue,
                Required = probe.Required,
                UseForChangeDetection = probe.UseForChangeDetection,
                Resolved = true,
                Value = actualValue,
                Name = snapshot.Name ?? string.Empty,
                AutomationId = snapshot.AutomationId ?? string.Empty,
                ControlType = snapshot.ControlType,
                MatchedExpectation = matchedExpectation,
                TimestampUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new CapturePackStatusSnapshot
            {
                ProbeName = probe.Name,
                ProbeKind = probe.Kind.ToString(),
                Selector = probe.Selector,
                ExpectedContains = probe.Contains,
                ExpectedEquals = probe.EqualsValue,
                Required = probe.Required,
                UseForChangeDetection = probe.UseForChangeDetection,
                Error = ex.Message,
                TimestampUtc = DateTime.UtcNow
            };
        }
    }

    private static bool MatchesProbeExpectation(string value, CaptureStatusProbe probe)
    {
        if (!string.IsNullOrWhiteSpace(probe.EqualsValue))
            return string.Equals(value ?? string.Empty, probe.EqualsValue, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(probe.Contains))
            return (value ?? string.Empty).Contains(probe.Contains, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static void ValidateBootstrapStatus(CaptureProfilePack pack, IReadOnlyList<CapturePackStatusSnapshot> snapshots)
    {
        var failedRequired = snapshots.Where(item => item.Required && (!item.Resolved || !item.MatchedExpectation)).ToList();
        if (failedRequired.Count == 0)
            return;

        var details = string.Join("; ", failedRequired.Select(item =>
            string.IsNullOrWhiteSpace(item.Error)
                ? $"{item.ProbeNameOrSelector()} expectation not met"
                : $"{item.ProbeNameOrSelector()} {item.Error}"));
        throw new InvalidOperationException($"Bootstrap status validation failed: {details}");
    }

    private static string ReadElementValue(AutomationElement element, ElementSnapshot snapshot)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)
                && valuePattern is ValuePattern value)
                return value.Current.Value ?? snapshot.Name ?? string.Empty;

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)
                && textPattern is TextPattern text)
                return text.DocumentRange.GetText(-1).Trim();
        }
        catch
        {
        }

        return snapshot.Name ?? string.Empty;
    }

    private static string BuildStatusSignature(IReadOnlyList<CapturePackStatusSnapshot> snapshots)
    {
        return string.Join("||", snapshots
            .Where(item => item.Resolved && item.UseForChangeDetection)
            .Select(item => $"{item.ProbeNameOrSelector()}={item.Value}"));
    }
}

public sealed class CapturePackRunResult
{
    public string PackId { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    public bool SmokeMode { get; set; }
    public bool Succeeded { get; set; }
    public string Error { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public string BootstrapFlowPath { get; set; } = string.Empty;
    public string BootstrapReportPath { get; set; } = string.Empty;
    public Dictionary<string, string> ResolvedInputs { get; set; } = [];
    public CaptureObservationPlan ObservationPlan { get; set; } = new();
    public List<CapturePackStatusSnapshot> BootstrapStatus { get; set; } = [];
    public List<CapturePackCaptureResult> Captures { get; set; } = [];
}

public sealed class CapturePackCaptureResult
{
    public int Index { get; set; }
    public string CaptureEventId { get; set; } = string.Empty;
    public string PreviewPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public string AnnotationId { get; set; } = string.Empty;
    public string AnnotationAudioPath { get; set; } = string.Empty;
    public List<CapturePackStatusSnapshot> StatusSnapshots { get; set; } = [];
}

public sealed class CapturePackStatusSnapshot
{
    public string ProbeName { get; set; } = string.Empty;
    public string ProbeKind { get; set; } = string.Empty;
    public string Selector { get; set; } = string.Empty;
    public string ExpectedContains { get; set; } = string.Empty;
    public string ExpectedEquals { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool UseForChangeDetection { get; set; } = true;
    public bool Resolved { get; set; }
    public bool MatchedExpectation { get; set; } = true;
    public string Value { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public string ProbeNameOrSelector() => !string.IsNullOrWhiteSpace(ProbeName) ? ProbeName : Selector;
}

internal sealed record CapturePackStatusWaitResult(
    AutomationElement? Window,
    List<CapturePackStatusSnapshot> Snapshots,
    bool Changed);
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

internal sealed class CaptureHarnessService
{
    private const string HarnessProfileId = "capture-harness-calculator";
    private const string HarnessProfileName = "Calculator Harness";
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ReportService _reports;
    private readonly SnapCaptureService _snapCapture;
    private readonly CaptureAnnotationService _annotations;
    private readonly ReviewBufferService _reviewBuffer;
    private readonly IFlowActionExecutor _flowActionExecutor;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private StreamWriter? _fileWriter;

    public CaptureHarnessService(
        ConfigService config,
        LogService log,
        ReportService reports,
        SnapCaptureService snapCapture,
        CaptureAnnotationService annotations,
        ReviewBufferService reviewBuffer,
        IFlowActionExecutor flowActionExecutor)
    {
        _config = config;
        _log = log;
        _reports = reports;
        _snapCapture = snapCapture;
        _annotations = annotations;
        _reviewBuffer = reviewBuffer;
        _flowActionExecutor = flowActionExecutor;
    }

    public event Action<string>? OnLogMessage;

    public void SetLogFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        _fileWriter = new StreamWriter(path, append: false, encoding: Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public void CloseLogFile()
    {
        _fileWriter?.Dispose();
        _fileWriter = null;
    }

    public async Task<CaptureHarnessRunResult> RunCalculatorHarnessAsync(CancellationToken ct)
    {
        var harnessId = $"calculator_capture_{DateTime.Now:yyyyMMdd_HHmmss}";
        var outputDirectory = Path.Combine(_reports.ReportsDirectory, "_harness", harnessId);
        var flowDirectory = Path.Combine(outputDirectory, "flows");
        var voiceDirectory = Path.Combine(outputDirectory, "sample-voice");
        var orbSnapDirectory = Path.Combine(outputDirectory, "orb-snaps");
        var reviewOutputDirectory = Path.Combine(outputDirectory, "review-buffers");
        Directory.CreateDirectory(flowDirectory);
        Directory.CreateDirectory(voiceDirectory);
        Directory.CreateDirectory(orbSnapDirectory);
        Directory.CreateDirectory(reviewOutputDirectory);

        EmitLog($"[harness] Starting calculator capture harness: {harnessId}");

        var runResult = new CaptureHarnessRunResult
        {
            HarnessId = harnessId,
            OutputDirectory = outputDirectory
        };

        var originalConfig = _config.GetConfig();
        var originalSelectedProfileId = originalConfig.Capture.SelectedProfileId;
        var originalReviewSettings = CloneReviewSettings(originalConfig.Review);
        Process? calculatorProcess = null;

        try
        {
            ConfigureReviewBufferForHarness(reviewOutputDirectory);
            _reviewBuffer.Reconfigure();
            EmitLog("[review] Rolling review buffer enabled for harness run");

            var launchStartedAtUtc = DateTime.UtcNow;
            calculatorProcess = Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
            if (calculatorProcess == null)
                throw new InvalidOperationException("Could not start calc.exe");

            EmitLog($"[calc] Started Calculator (pid={calculatorProcess.Id})");
            var calculatorWindow = await WaitForWindowByTitleAsync("Calculator", 15000, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Calculator window did not appear in time.");
            var candidate = CreateWindowCandidateFromElement(calculatorWindow, calculatorProcess.Id, launchStartedAtUtc)
                ?? throw new InvalidOperationException("Calculator capture target could not be resolved.");

            var profile = EnsureHarnessProfile(candidate, orbSnapDirectory);
            runResult.ProfileId = profile.Id;
            runResult.ProfileName = profile.Name;
            EmitLog($"[capture] Profile ready: {profile.Name} -> {profile.OutputDirectory}");

            var checkpoints = CreateDefaultCheckpoints();
            for (var index = 0; index < checkpoints.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var checkpoint = checkpoints[index];
                EmitLog($"[step] {checkpoint.Id}: {checkpoint.Description}");
                FocusWindow(calculatorWindow);
                await Task.Delay(250, ct).ConfigureAwait(false);

                var actionResults = await ExecuteCheckpointAsync(checkpoint, calculatorWindow, ct).ConfigureAwait(false);
                var flowReportPath = Path.Combine(flowDirectory, $"{checkpoint.Id}.json");
                await File.WriteAllTextAsync(flowReportPath, JsonSerializer.Serialize(actionResults, _jsonOptions), ct).ConfigureAwait(false);

                var capture = await _snapCapture.CaptureSelectedProfileAsync($"Harness checkpoint {checkpoint.Id}: {checkpoint.ExpectedDisplay}").ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Capture failed for checkpoint {checkpoint.Id}.");
                EmitLog($"[snap] Captured {capture.EventId} with {capture.Artifacts.Count} artifact(s)");

                var sampleVoiceBytes = CreateSyntheticVoiceWave(index + 1);
                var sampleVoiceSourcePath = Path.Combine(voiceDirectory, $"{checkpoint.Id}.wav");
                await File.WriteAllBytesAsync(sampleVoiceSourcePath, sampleVoiceBytes, ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var annotation = _annotations.RecordAnnotation(
                    checkpoint.VoiceNote,
                    now.AddMilliseconds(-900),
                    now,
                    sampleVoiceBytes);
                EmitLog($"[voice] Annotation {annotation.AnnotationId}: {checkpoint.VoiceNote}");

                runResult.Checkpoints.Add(new CaptureHarnessCheckpointResult
                {
                    Id = checkpoint.Id,
                    Description = checkpoint.Description,
                    ExpectedDisplay = checkpoint.ExpectedDisplay,
                    FlowReportPath = flowReportPath,
                    CaptureEventId = capture.EventId,
                    CapturePreviewPath = capture.PreviewPath,
                    AnnotationId = annotation.AnnotationId,
                    AnnotationAudioPath = annotation.AudioPath,
                    SampleVoiceSourcePath = sampleVoiceSourcePath
                });
            }

            runResult.ReviewBundleMetadataPath = await _reviewBuffer.SaveBufferAsync(ct).ConfigureAwait(false) ?? string.Empty;
            EmitLog($"[review] Saved review bundle: {runResult.ReviewBundleMetadataPath}");

            runResult.Succeeded = true;
            runResult.ReportPath = Path.Combine(outputDirectory, "harness-report.json");
            await File.WriteAllTextAsync(runResult.ReportPath, JsonSerializer.Serialize(runResult, _jsonOptions), ct).ConfigureAwait(false);
            EmitLog($"[harness] Completed successfully: {runResult.ReportPath}");
            return runResult;
        }
        catch (Exception ex)
        {
            runResult.Succeeded = false;
            runResult.Error = ex.Message;
            runResult.ReportPath = Path.Combine(outputDirectory, "harness-report.json");
            await File.WriteAllTextAsync(runResult.ReportPath, JsonSerializer.Serialize(runResult, _jsonOptions), CancellationToken.None).ConfigureAwait(false);
            EmitLog($"[harness] FAILED: {ex.Message}");
            _log.Error("CaptureHarness", ex.Message);
            return runResult;
        }
        finally
        {
            RestoreReviewSettings(originalReviewSettings);
            var cfg = _config.GetConfig();
            cfg.Capture.SelectedProfileId = originalSelectedProfileId;
            _config.SaveConfig(cfg);
            _reviewBuffer.Reconfigure();

            CleanupProcess(calculatorProcess);
            CloseLogFile();
        }
    }

    internal static IReadOnlyList<CalculatorHarnessCheckpoint> CreateDefaultCheckpoints() =>
    [
        new(
            "checkpoint-01",
            "Compute 2 + 5 = 7",
            "7",
            "Checkpoint one. Two plus five equals seven.",
            ["num2Button", "plusButton", "num5Button", "equalButton"]),
        new(
            "checkpoint-02",
            "Compute 9 / 3 = 3",
            "3",
            "Checkpoint two. Nine divided by three equals three.",
            ["num9Button", "divideButton", "num3Button", "equalButton"]),
        new(
            "checkpoint-03",
            "Compute 7 x 8 = 56",
            "56",
            "Checkpoint three. Seven times eight equals fifty-six.",
            ["num7Button", "multiplyButton", "num8Button", "equalButton"])
    ];

    internal static byte[] CreateSyntheticVoiceWave(int variant, int durationMs = 1100, int sampleRate = 16000)
    {
        var totalSamples = Math.Max(1, durationMs * sampleRate / 1000);
        var pcm = new short[totalSamples];
        var frequency = 330 + (variant * 55);

        for (var index = 0; index < totalSamples; index++)
        {
            var envelope = 1d - (index / (double)totalSamples);
            var sample = Math.Sin(2 * Math.PI * frequency * index / sampleRate) * envelope;
            pcm[index] = (short)(sample * short.MaxValue * 0.35);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var dataLength = pcm.Length * sizeof(short);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (var sample in pcm)
            writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    private void ConfigureReviewBufferForHarness(string reviewOutputDirectory)
    {
        var cfg = _config.GetConfig();
        cfg.Review.Enabled = true;
        cfg.Review.MicEnabled = false;
        cfg.Review.FrameIntervalMs = 1000;
        cfg.Review.BufferDurationMinutes = 1;
        cfg.Review.OutputDirectory = reviewOutputDirectory;
        _config.SaveConfig(cfg);
    }

    private void RestoreReviewSettings(ReviewBufferSettings originalReviewSettings)
    {
        var cfg = _config.GetConfig();
        cfg.Review.Enabled = originalReviewSettings.Enabled;
        cfg.Review.MicEnabled = originalReviewSettings.MicEnabled;
        cfg.Review.FrameIntervalMs = originalReviewSettings.FrameIntervalMs;
        cfg.Review.BufferDurationMinutes = originalReviewSettings.BufferDurationMinutes;
        cfg.Review.OutputDirectory = originalReviewSettings.OutputDirectory;
        cfg.Review.AudioChunkSeconds = originalReviewSettings.AudioChunkSeconds;
        cfg.Review.SaveBufferHotkey = originalReviewSettings.SaveBufferHotkey;
        _config.SaveConfig(cfg);
    }

    private CaptureProfile EnsureHarnessProfile(CaptureWindowCandidate candidate, string outputDirectory)
    {
        var profile = _snapCapture.GetProfiles().FirstOrDefault(item => string.Equals(item.Id, HarnessProfileId, StringComparison.OrdinalIgnoreCase));
        var target = _snapCapture.CreateWindowTarget(candidate);

        if (profile == null)
        {
            profile = new CaptureProfile
            {
                Id = HarnessProfileId,
                Name = HarnessProfileName,
                FilePrefix = "calc_harness",
                OutputDirectory = outputDirectory,
                Targets = [target]
            };
            _snapCapture.AddProfile(profile);
        }
        else
        {
            profile.Name = HarnessProfileName;
            profile.FilePrefix = "calc_harness";
            profile.OutputDirectory = outputDirectory;
            profile.Targets.Clear();
            profile.Targets.Add(target);
            _snapCapture.SaveProfile(profile);
        }

        _snapCapture.SetSelectedProfile(profile.Id);
        return profile;
    }

    private async Task<List<CaptureHarnessActionResult>> ExecuteCheckpointAsync(CalculatorHarnessCheckpoint checkpoint, AutomationElement window, CancellationToken ct)
    {
        var results = new List<CaptureHarnessActionResult>();

        var focusStep = new TestStep { Order = 1, Action = StepAction.FocusWindow, Description = "Focus Calculator" };
        var focusResult = await _flowActionExecutor.ExecuteAsync(focusStep, null, window).ConfigureAwait(false);
        AppendActionResult(results, focusStep.Description ?? "Focus Calculator", focusResult);
        EnsureSuccess(focusResult, checkpoint, "Could not focus Calculator window.");

        var clearStep = new TestStep { Order = 2, Action = StepAction.SendKeys, Keys = "Escape", Description = "Clear display" };
        var clearResult = await _flowActionExecutor.ExecuteAsync(clearStep, null, window).ConfigureAwait(false);
        AppendActionResult(results, clearStep.Description ?? "Clear display", clearResult);
        EnsureSuccess(clearResult, checkpoint, "Could not clear Calculator display.");

        for (var index = 0; index < checkpoint.ButtonAutomationIds.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var automationId = checkpoint.ButtonAutomationIds[index];
            var element = FindByAutomationId(window, automationId)
                ?? throw new InvalidOperationException($"Calculator button '{automationId}' was not found.");

            var step = new TestStep
            {
                Order = 3 + index,
                Action = StepAction.Click,
                Selector = $"Button#{automationId}",
                Description = $"Click {automationId}"
            };
            var result = await _flowActionExecutor.ExecuteAsync(step, element, window).ConfigureAwait(false);
            AppendActionResult(results, step.Description ?? automationId, result);
            EnsureSuccess(result, checkpoint, $"Could not click Calculator button '{automationId}'.");
            await Task.Delay(180, ct).ConfigureAwait(false);
        }

        var display = FindByAutomationId(window, "CalculatorResults")
            ?? throw new InvalidOperationException("Calculator display was not found.");
        var assertStep = new TestStep
        {
            Order = 99,
            Action = StepAction.AssertText,
            Selector = "Text#CalculatorResults",
            Contains = checkpoint.ExpectedDisplay,
            Description = $"Assert result contains {checkpoint.ExpectedDisplay}"
        };
        var assertResult = await _flowActionExecutor.ExecuteAsync(assertStep, display, window).ConfigureAwait(false);
        AppendActionResult(results, assertStep.Description ?? "Assert result", assertResult);
        EnsureSuccess(assertResult, checkpoint, $"Calculator display did not contain '{checkpoint.ExpectedDisplay}'.");

        EmitLog($"[calc] {checkpoint.Id} verified result {checkpoint.ExpectedDisplay}");
        return results;
    }

    private static void EnsureSuccess(ActionResult result, CalculatorHarnessCheckpoint checkpoint, string fallbackMessage)
    {
        if (!result.Success)
            throw new InvalidOperationException($"{checkpoint.Id}: {result.Error ?? fallbackMessage}");
    }

    private static void AppendActionResult(List<CaptureHarnessActionResult> results, string label, ActionResult result)
    {
        results.Add(new CaptureHarnessActionResult
        {
            Label = label,
            Success = result.Success,
            Diagnostics = result.Diagnostics ?? string.Empty,
            Error = result.Error ?? string.Empty,
            Found = result.Found ?? string.Empty,
            Expected = result.Expected ?? string.Empty
        });
    }

    private static async Task<AutomationElement?> WaitForWindowByTitleAsync(string titleSubstring, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var root = AutomationElement.RootElement;
            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (var index = 0; index < windows.Count; index++)
            {
                try
                {
                    var window = windows[index];
                    var title = window.Current.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(title)
                        && title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                        return window;
                }
                catch (ElementNotAvailableException)
                {
                }
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static CaptureWindowCandidate? CreateWindowCandidateFromElement(AutomationElement window, int launcherProcessId, DateTime launchStartedAtUtc)
    {
        try
        {
            var rect = window.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                return null;

            var processId = window.Current.ProcessId;
            var processName = ResolveProcessName(processId);
            var startedAt = GetProcessStartTimeUtc(processId);
            if (processId != launcherProcessId && startedAt != null && startedAt.Value < launchStartedAtUtc.AddSeconds(-5))
            {
                // Still allow it if the window title clearly matches Calculator.
                var title = window.Current.Name ?? string.Empty;
                if (!title.Contains("Calculator", StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            return new CaptureWindowCandidate
            {
                Handle = window.Current.NativeWindowHandle,
                ProcessId = processId,
                ProcessName = processName,
                WindowTitle = window.Current.Name ?? string.Empty,
                Left = (int)rect.Left,
                Top = (int)rect.Top,
                Width = (int)rect.Width,
                Height = (int)rect.Height
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static DateTime? GetProcessStartTimeUtc(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElement? FindByAutomationId(AutomationElement parent, string automationId)
    {
        try
        {
            return parent.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }
        catch
        {
            return null;
        }
    }

    private static void FocusWindow(AutomationElement window)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            Win32.SetForegroundWindow(hwnd);
        }
        catch
        {
        }
    }

    private static void CleanupProcess(Process? process)
    {
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch
        {
        }
    }

    private static ReviewBufferSettings CloneReviewSettings(ReviewBufferSettings review) => new()
    {
        Enabled = review.Enabled,
        MicEnabled = review.MicEnabled,
        SaveBufferHotkey = review.SaveBufferHotkey,
        BufferDurationMinutes = review.BufferDurationMinutes,
        FrameIntervalMs = review.FrameIntervalMs,
        AudioChunkSeconds = review.AudioChunkSeconds,
        OutputDirectory = review.OutputDirectory
    };

    private void EmitLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _fileWriter?.WriteLine(line);
        OnLogMessage?.Invoke(line);
    }
}

internal sealed record CalculatorHarnessCheckpoint(
    string Id,
    string Description,
    string ExpectedDisplay,
    string VoiceNote,
    IReadOnlyList<string> ButtonAutomationIds);

public sealed class CaptureHarnessRunResult
{
    public string HarnessId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string Error { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string ReviewBundleMetadataPath { get; set; } = string.Empty;
    public List<CaptureHarnessCheckpointResult> Checkpoints { get; set; } = [];
}

public sealed class CaptureHarnessCheckpointResult
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ExpectedDisplay { get; set; } = string.Empty;
    public string FlowReportPath { get; set; } = string.Empty;
    public string CaptureEventId { get; set; } = string.Empty;
    public string CapturePreviewPath { get; set; } = string.Empty;
    public string AnnotationId { get; set; } = string.Empty;
    public string AnnotationAudioPath { get; set; } = string.Empty;
    public string SampleVoiceSourcePath { get; set; } = string.Empty;
}

public sealed class CaptureHarnessActionResult
{
    public string Label { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Diagnostics { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Found { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
}
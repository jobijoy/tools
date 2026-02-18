using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// SMOKE TEST SERVICE — Automated end-to-end agent integration tests.
//
// Sends natural-language prompts to the LLM agent, waits for the full tool-calling
// loop to complete, then runs deterministic verification checks against desktop state.
//
// Usage (GUI):
//   var svc = new SmokeTestService(App.Agent, App.Log, App.Reports, App.Vision);
//   svc.OnTestStatusChanged += (id, status) => UpdateUI(id, status);
//   svc.OnLogMessage += msg => AppendLog(msg);
//   var suite = await svc.RunAllAsync(SmokeTestService.GetBuiltInTests(), ct);
//   if (suite.AllPassed) { /* 🎉 */ }
//
// Usage (file-based):
//   var tests = SmokeTestFile.LoadFromFile("my-tests.json");
//   var suite = await svc.RunAllAsync(tests, ct);
//
// Thread safety: RunAllAsync runs sequentially (one test at a time).
// Each test clears agent history to prevent cross-contamination.
// Multi-step tests send each prompt sequentially, with optional screenshots between steps.
// Verification checks use UIA from the calling thread (must be STA or threadpool).
// ═══════════════════════════════════════════════════════════════════════════════════

public class SmokeTestService
{
    private readonly IAgentService _agent;
    private readonly LogService _log;
    private readonly ReportService? _reports;
    private readonly VisionService? _vision;
    private StreamWriter? _fileWriter;

    /// <summary>Fired when a test's status changes (id, new status, result — null while running).</summary>
    public event Action<string, SmokeTestStatus, SmokeTestResult?>? OnTestStatusChanged;

    /// <summary>Fired for each log line during test execution.</summary>
    public event Action<string>? OnLogMessage;

    /// <summary>
    /// Creates a new SmokeTestService.
    /// Pass ReportService and VisionService to enable screenshot capture and
    /// ScreenshotContainsText verification. Both are optional for backward compat.
    /// </summary>
    public SmokeTestService(IAgentService agent, LogService log,
        ReportService? reports = null, VisionService? vision = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reports = reports;
        _vision = vision;
    }

    /// <summary>
    /// Sets an output file path for incremental (real-time) log writing.
    /// Each EmitLog call flushes immediately so external tools can tail the file.
    /// </summary>
    public void SetLogFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _fileWriter = new StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    /// <summary>Closes the incremental log file writer.</summary>
    public void CloseLogFile()
    {
        _fileWriter?.Dispose();
        _fileWriter = null;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // BUILT-IN TEST CASES
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns ALL smoke tests — basic (5) + advanced (10).
    /// </summary>
    public static List<SmokeTest> GetAllTests()
    {
        var all = new List<SmokeTest>();
        all.AddRange(GetBuiltInTests());
        all.AddRange(GetAdvancedTests());
        return all;
    }

    /// <summary>
    /// Returns the 5 built-in smoke test cases covering core automation scenarios.
    /// </summary>
    public static List<SmokeTest> GetBuiltInTests() =>
    [
        // ── ST-01: Basic app launch ──────────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-01",
            Name = "Launch Calculator",
            Description = "Agent launches Windows Calculator via a flow",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open the Windows Calculator application.",
            TimeoutSeconds = 90,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "CalculatorApp", Description = "Calculator process is running" },
                new() { Type = SmokeVerificationType.WindowTitleContains, Target = "Calculator", Description = "Calculator window is visible" }
            ],
            CleanupProcesses = ["CalculatorApp"]
        },

        // ── ST-02: Keyboard input + read result ─────────────────────────────
        new SmokeTest
        {
            Id = "ST-02",
            Name = "Calculator Arithmetic",
            Description = "Agent opens Calculator, computes 40+32, reads '72' from the display",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open Calculator, compute 40 + 32, and tell me the exact result number. Keep Calculator open after.",
            TimeoutSeconds = 180,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ResponseContains, Target = "72", Description = "Agent response contains '72'" }
            ],
            CleanupProcesses = ["CalculatorApp"]
        },

        // ── ST-03: Text editor typing ───────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-03",
            Name = "Notepad Text Entry",
            Description = "Agent opens Notepad and types specific text",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open Notepad and type this exact text: IdolClick Smoke Test OK",
            TimeoutSeconds = 120,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "notepad", Description = "Notepad process is running" },
                new() { Type = SmokeVerificationType.WindowTitleContains, Target = "Notepad", Description = "Notepad window is visible" }
            ],
            CleanupProcesses = ["notepad"]
        },

        // ── ST-04: Browser navigation ───────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-04",
            Name = "Edge Browser Navigation",
            Description = "Agent opens Edge and navigates to example.com",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open Microsoft Edge browser and navigate to https://example.com — then tell me the exact page title shown in the browser tab.",
            TimeoutSeconds = 120,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "msedge", Description = "Edge process is running" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "Example Domain", Description = "Agent reports 'Example Domain' page title" }
            ],
            // Don't kill the browser — user may have other tabs open
            CleanupProcesses = []
        },

        // ── ST-05: Screenshot capture ───────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-05",
            Name = "Desktop Screenshot",
            Description = "Agent takes a desktop screenshot and confirms the file path",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Take a screenshot of the full desktop right now.",
            TimeoutSeconds = 60,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ScreenshotCreated, Target = "", Description = "Screenshot file was created" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "screenshot", Description = "Agent mentions screenshot in response" }
            ],
            CleanupProcesses = []
        }
    ];

    // ═══════════════════════════════════════════════════════════════════════════════
    // ADVANCED TEST CASES (10 tests: 3 simple, 5 medium, 2 complex)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns 10 advanced smoke tests with graduated difficulty:
    /// 3 Simple, 5 Medium, 2 Complex.
    /// </summary>
    public static List<SmokeTest> GetAdvancedTests() =>
    [
        // ──────────────────────────── SIMPLE (3) ────────────────────────────

        // ── ST-06: Open Settings ─────────────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-06",
            Name = "Open Windows Settings",
            Description = "Agent opens the Windows Settings app",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open the Windows Settings application.",
            TimeoutSeconds = 90,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "SystemSettings", Description = "Settings process is running" },
                new() { Type = SmokeVerificationType.WindowTitleContains, Target = "Settings", Description = "Settings window is visible" }
            ],
            CleanupProcesses = ["SystemSettings"]
        },

        // ── ST-07: Open File Explorer ────────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-07",
            Name = "Open File Explorer",
            Description = "Agent opens File Explorer to the Documents folder",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open File Explorer and navigate to the Documents folder. Tell me the folder name you navigated to.",
            TimeoutSeconds = 90,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "explorer", Description = "Explorer process is running" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "Documents", Description = "Agent confirms Documents folder" }
            ],
            CleanupProcesses = []
        },

        // ── ST-08: Launch Paint ──────────────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-08",
            Name = "Launch Paint",
            Description = "Agent opens Microsoft Paint application",
            Difficulty = TestDifficulty.Simple,
            AgentPrompt = "Open Microsoft Paint application. Keep it open.",
            TimeoutSeconds = 90,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "mspaint", Description = "Paint process is running" },
                new() { Type = SmokeVerificationType.WindowTitleContains, Target = "Paint", Description = "Paint window is visible" }
            ],
            CleanupProcesses = ["mspaint"]
        },

        // ──────────────────────────── MEDIUM (5) ────────────────────────────

        // ── ST-09: Calculator Chain ──────────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-09",
            Name = "Calculator Chain Math",
            Description = "Agent computes (15 × 8) − 30 = 90 in Calculator",
            Difficulty = TestDifficulty.Medium,
            AgentPrompt = "Open Calculator. First compute 15 times 8, then subtract 30 from the result. Tell me the final number. Keep Calculator open.",
            TimeoutSeconds = 180,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ResponseContains, Target = "90", Description = "Agent response contains '90'" },
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "CalculatorApp", Description = "Calculator still running" }
            ],
            CleanupProcesses = ["CalculatorApp"]
        },

        // ── ST-10: Notepad Type + Read Back ──────────────────────────────────
        new SmokeTest
        {
            Id = "ST-10",
            Name = "Notepad Write & Verify",
            Description = "Agent types multi-line text in Notepad and reads it back",
            Difficulty = TestDifficulty.Medium,
            AgentPrompt = "Open Notepad. Type the following two lines:\nLine 1: Hello World\nLine 2: Smoke Test Complete\nThen read back the content and tell me what the second line says. Keep Notepad open.",
            TimeoutSeconds = 150,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "notepad", Description = "Notepad process is running" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "Smoke Test Complete", Description = "Agent read back the second line" }
            ],
            CleanupProcesses = ["notepad"]
        },

        // ── ST-11: Edge Read Page Content ────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-11",
            Name = "Edge Read Web Content",
            Description = "Agent navigates to example.com and reads the paragraph text",
            Difficulty = TestDifficulty.Medium,
            AgentPrompt = "Open Edge, navigate to https://example.com. Read the text shown on the page. " +
                           "Answer this question: According to the page, what kind of examples is this domain used for? " +
                           "Your answer MUST include the exact phrase 'illustrative examples' copied from the page.",
            TimeoutSeconds = 150,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "msedge", Description = "Edge process is running" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "illustrative", Description = "Agent quoted 'illustrative' from page" }
            ],
            CleanupProcesses = []
        },

        // ── ST-12: Notepad Word Count ────────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-12",
            Name = "Notepad Paragraph Entry",
            Description = "Agent types a longer paragraph in Notepad and confirms it",
            Difficulty = TestDifficulty.Medium,
            AgentPrompt = "Open Notepad and type the following paragraph exactly:\nThe quick brown fox jumps over the lazy dog. This sentence is a classic pangram used for testing.\nThen inspect the text you typed and tell me how many sentences you count in the Notepad window. Keep Notepad open.",
            TimeoutSeconds = 150,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "notepad", Description = "Notepad is running" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "2", Description = "Agent reports 2 sentences" }
            ],
            CleanupProcesses = ["notepad"]
        },

        // ── ST-13: Calculator + Report ───────────────────────────────────────
        new SmokeTest
        {
            Id = "ST-13",
            Name = "Calculator Division Check",
            Description = "Agent computes 256 ÷ 16 and explains the result",
            Difficulty = TestDifficulty.Medium,
            AgentPrompt = "Open Calculator, compute 256 divided by 16, read the result from the display, and tell me the answer. Keep Calculator open.",
            TimeoutSeconds = 180,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ResponseContains, Target = "16", Description = "Agent response contains '16'" },
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "CalculatorApp", Description = "Calculator still running" }
            ],
            CleanupProcesses = ["CalculatorApp"]
        },

        // ──────────────────────────── COMPLEX (2) ───────────────────────────

        // ── ST-14: Multi-App Calc → Notepad Pipeline ─────────────────────────
        new SmokeTest
        {
            Id = "ST-14",
            Name = "Calc → Notepad Pipeline",
            Description = "Agent computes in Calculator, then types the result in Notepad",
            Difficulty = TestDifficulty.Complex,
            AgentPrompt = "Step 1: Open Calculator and compute 125 + 375. Read the result.\n" +
                           "Step 2: Open Notepad and type 'Calculation Result: ' followed by the exact number you got from Calculator.\n" +
                           "Tell me the final text you typed in Notepad. Keep both apps open.",
            TimeoutSeconds = 240,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ResponseContains, Target = "500", Description = "Agent reports '500'" },
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "notepad", Description = "Notepad is running" },
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "CalculatorApp", Description = "Calculator is running" }
            ],
            CleanupProcesses = ["CalculatorApp", "notepad"]
        },

        // ── ST-15: Notepad → Chrome Multi-Site Pipeline ───────────────────────
        new SmokeTest
        {
            Id = "ST-15",
            Name = "Notepad → Chrome Multi-Site",
            Description = "Agent writes URLs to Notepad, saves & closes, re-opens to read them, then opens each in Chrome",
            Difficulty = TestDifficulty.Complex,
            AgentPrompt = "Follow these steps exactly:\n" +
                           "Step 1: Open Notepad and type these three URLs, one per line:\nhttps://example.com\nhttps://httpbin.org\nhttps://jsonplaceholder.typicode.com\n" +
                           "Step 2: Save the file to the Desktop as 'smoke-urls.txt' using Ctrl+S or File > Save As, then close Notepad.\n" +
                           "Step 3: Open Notepad again and open the file 'smoke-urls.txt' from the Desktop. Read the URLs from the file.\n" +
                           "Step 4: Open Google Chrome and navigate to each of the three URLs you read, one at a time. You can use new tabs.\n" +
                           "Tell me which three sites you opened in Chrome and confirm they loaded.",
            TimeoutSeconds = 360,
            Verifications =
            [
                new() { Type = SmokeVerificationType.ProcessRunning, Target = "chrome", Description = "Chrome is running" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "example.com", Description = "Agent confirms example.com" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "httpbin.org", Description = "Agent confirms httpbin.org" },
                new() { Type = SmokeVerificationType.ResponseContains, Target = "jsonplaceholder", Description = "Agent confirms jsonplaceholder" }
            ],
            CleanupProcesses = ["notepad"]
        }
    ];

    // ═══════════════════════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs all provided smoke tests sequentially and returns the suite result.
    /// </summary>
    public async Task<SmokeTestSuiteResult> RunAllAsync(
        IReadOnlyList<SmokeTest> tests,
        CancellationToken ct = default)
    {
        var suite = new SmokeTestSuiteResult { StartedAt = DateTime.UtcNow };

        EmitLog($"═══ Smoke Test Suite: {tests.Count} test(s) ═══");
        EmitLog($"Agent status: {_agent.StatusText}");
        EmitLog("");

        if (!_agent.IsConfigured)
        {
            EmitLog("ERROR: Agent is not configured. Set your LLM endpoint and API key in Settings first.");
            foreach (var t in tests)
            {
                var errResult = new SmokeTestResult
                {
                    TestId = t.Id,
                    TestName = t.Name,
                    Status = SmokeTestStatus.Error,
                    Error = "Agent not configured"
                };
                suite.Results.Add(errResult);
                OnTestStatusChanged?.Invoke(t.Id, SmokeTestStatus.Error, errResult);
            }
            suite.FinishedAt = DateTime.UtcNow;
            return suite;
        }

        for (int i = 0; i < tests.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                // Mark remaining as skipped
                for (int j = i; j < tests.Count; j++)
                {
                    var skipResult = new SmokeTestResult
                    {
                        TestId = tests[j].Id,
                        TestName = tests[j].Name,
                        Status = SmokeTestStatus.Skipped,
                        Error = "Suite cancelled"
                    };
                    suite.Results.Add(skipResult);
                    OnTestStatusChanged?.Invoke(tests[j].Id, SmokeTestStatus.Skipped, skipResult);
                }
                break;
            }

            var result = await RunSingleAsync(tests[i], ct).ConfigureAwait(false);
            suite.Results.Add(result);
        }

        suite.FinishedAt = DateTime.UtcNow;

        EmitLog("");
        EmitLog($"═══ Suite Complete: {suite.PassedCount}/{suite.TotalCount} passed " +
                $"({suite.TotalElapsedMs / 1000.0:F1}s) ═══");

        return suite;
    }

    /// <summary>
    /// Runs a single smoke test: prompt(s) → agent → verify → cleanup.
    /// Supports multi-step sequential prompts with intermediate screenshots.
    /// Emits full diagnostic output including agent response text, tool calls,
    /// and captured log entries for real-time analysis by external watchers.
    /// </summary>
    private async Task<SmokeTestResult> RunSingleAsync(SmokeTest test, CancellationToken ct)
    {
        var result = new SmokeTestResult
        {
            TestId = test.Id,
            TestName = test.Name,
            Status = SmokeTestStatus.Running
        };

        var steps = test.GetEffectiveSteps();
        OnTestStatusChanged?.Invoke(test.Id, SmokeTestStatus.Running, null);
        EmitLog($"── [{test.Id}] {test.Name} ({steps.Count} step(s)) ──────────────────────────");
        if (steps.Count == 1)
            EmitLog($"   Prompt: \"{steps[0].Prompt}\"");
        else
            for (int si = 0; si < steps.Count; si++)
                EmitLog($"   Step {si + 1}: \"{Truncate(steps[si].Prompt, 100)}\"");

        // ── Capture logs during this test ────────────────────────────────────
        var capturedLogs = new List<string>();
        void LogCapture(LogEntry entry)
        {
            var line = $"[{entry.Time:HH:mm:ss.fff}] {entry.Level,-7} [{entry.Category,-15}] {entry.Message}";
            capturedLogs.Add(line);
        }
        _log.OnLog += LogCapture;

        // ── Subscribe to agent progress for real-time tool/LLM visibility ────
        void ProgressCapture(AgentProgress p)
        {
            switch (p.Kind)
            {
                case AgentProgressKind.NewIteration:
                    EmitLog($"   [iter] Round {p.Iteration} starting");
                    break;
                case AgentProgressKind.ToolCallStarting:
                    EmitLog($"   [tool] ⚙ {p.ToolName ?? "?"} — {Truncate(p.Message, 120)}");
                    break;
                case AgentProgressKind.ToolCallCompleted:
                    EmitLog($"   [tool] ✓ {p.ToolName ?? "?"} done — {Truncate(p.Message, 120)}");
                    break;
                case AgentProgressKind.IntermediateText:
                    if (!string.IsNullOrWhiteSpace(p.IntermediateText))
                        EmitLog($"   [llm]  {Truncate(p.IntermediateText, 200)}");
                    break;
            }
        }
        _agent.OnProgress += ProgressCapture;

        // ── Pre-test cleanup ─────────────────────────────────────────────────
        KillProcesses(test.CleanupProcesses, "pre-test");

        // ── Screenshot output directory for this test ────────────────────────
        var screenshotDir = Path.Combine(
            AppContext.BaseDirectory, "reports", "_smoke_screenshots", test.Id);
        Directory.CreateDirectory(screenshotDir);

        var sw = Stopwatch.StartNew();

        try
        {
            // Clear agent history for a clean test
            _agent.ClearHistory();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(test.TimeoutSeconds));

            // ── Execute steps sequentially ───────────────────────────────────
            var allResponses = new List<string>();
            bool stepFailed = false;

            for (int stepIdx = 0; stepIdx < steps.Count; stepIdx++)
            {
                var step = steps[stepIdx];
                var stepNum = stepIdx + 1;

                if (step.ClearHistory)
                {
                    _agent.ClearHistory();
                    EmitLog($"   [step {stepNum}] Cleared agent history");
                }

                EmitLog($"   [step {stepNum}/{steps.Count}] Sending: \"{Truncate(step.Prompt, 150)}\"");
                var response = await _agent.SendMessageAsync(step.Prompt, cts.Token).ConfigureAwait(false);
                allResponses.Add(response.Text);

                EmitLog($"   [step {stepNum}] Agent responded ({response.Text.Length} chars)");
                EmitLog($"   --- Agent Response (Step {stepNum}) ---");
                EmitLog($"   {response.Text}");
                EmitLog($"   --- End Response ---");

                if (response.HasFlow)
                    EmitLog($"   [flow] Detected test flow: '{response.Flow!.TestName}' with {response.Flow.Steps?.Count ?? 0} step(s)");

                if (response.IsError)
                {
                    result.Status = SmokeTestStatus.Error;
                    result.Error = $"Agent error at step {stepNum}: {response.Text}";
                    EmitLog($"   ❌ AGENT ERROR at step {stepNum}: {response.Text}");
                    stepFailed = true;
                    break;
                }

                // ── Capture screenshot after step if requested ────────────────
                if (step.ScreenshotAfter && _reports != null)
                {
                    await Task.Delay(500, cts.Token).ConfigureAwait(false); // brief settle
                    var label = !string.IsNullOrWhiteSpace(step.ScreenshotLabel)
                        ? step.ScreenshotLabel
                        : $"step{stepNum}";
                    var ssPath = _reports.CaptureScreenshot(screenshotDir, $"{label}.png");
                    if (ssPath != null)
                        EmitLog($"   [screenshot] Captured: {ssPath}");
                    else
                        EmitLog($"   [screenshot] Capture failed for step {stepNum}");
                }

                // ── Run intermediate verifications for this step ──────────────
                if (step.Verifications.Count > 0)
                {
                    EmitLog($"   [step {stepNum}] Running {step.Verifications.Count} intermediate verification(s)...");
                    foreach (var sv in step.Verifications)
                    {
                        var svr = await RunVerificationAsync(sv, response.Text, screenshotDir).ConfigureAwait(false);
                        result.Verifications.Add(svr);
                        var icon = svr.Passed ? "✅" : "❌";
                        EmitLog($"   {icon} {svr.Description}: {svr.Detail}");
                        if (!svr.Passed)
                        {
                            stepFailed = true;
                            EmitLog($"   ⚠ Intermediate verification failed at step {stepNum} — continuing...");
                        }
                    }
                }

                // ── Delay between steps ──────────────────────────────────────
                if (stepIdx < steps.Count - 1 && step.DelayAfterMs > 0)
                {
                    EmitLog($"   [delay] Waiting {step.DelayAfterMs}ms before next step...");
                    await Task.Delay(step.DelayAfterMs, cts.Token).ConfigureAwait(false);
                }
            }

            // Combine all step responses for final verification
            result.AgentResponse = string.Join("\n---\n", allResponses);

            sw.Stop();
            EmitLog($"   All {steps.Count} step(s) completed in {sw.ElapsedMilliseconds / 1000.0:F1}s");

            if (!stepFailed)
            {
                // Brief pause for UI state to settle after automation
                await Task.Delay(800, ct).ConfigureAwait(false);

                // ── Run final verifications ──────────────────────────────────
                EmitLog($"   Running {test.Verifications.Count} final verification(s)...");
                foreach (var v in test.Verifications)
                {
                    var vr = await RunVerificationAsync(v, result.AgentResponse, screenshotDir).ConfigureAwait(false);
                    result.Verifications.Add(vr);

                    var icon = vr.Passed ? "✅" : "❌";
                    EmitLog($"   {icon} {vr.Description}: {vr.Detail}");
                }

                result.Status = result.Verifications.All(v => v.Passed)
                    ? SmokeTestStatus.Passed
                    : SmokeTestStatus.Failed;
            }
            else if (result.Status == SmokeTestStatus.Running)
            {
                result.Status = SmokeTestStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = SmokeTestStatus.Error;
            result.Error = $"Timed out after {test.TimeoutSeconds}s";
            EmitLog($"   ⏱ TIMEOUT after {test.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            result.Status = SmokeTestStatus.Error;
            result.Error = ex.Message;
            EmitLog($"   ❌ EXCEPTION: {ex.Message}");
        }
        finally
        {
            if (sw.IsRunning) sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.LogEntries = capturedLogs;
            _log.OnLog -= LogCapture;
            _agent.OnProgress -= ProgressCapture;

            // ── Post-test cleanup ────────────────────────────────────────────
            KillProcesses(test.CleanupProcesses, "post-test");

            var statusIcon = result.Status switch
            {
                SmokeTestStatus.Passed => "✅ PASSED",
                SmokeTestStatus.Failed => "❌ FAILED",
                SmokeTestStatus.Error => "💥 ERROR",
                _ => result.Status.ToString()
            };
            EmitLog($"   Result: {statusIcon} ({result.ElapsedMs / 1000.0:F1}s)");

            // ── Dump captured internal logs for failed/errored tests ─────────
            if (result.Status is SmokeTestStatus.Failed or SmokeTestStatus.Error && capturedLogs.Count > 0)
            {
                EmitLog($"   --- Captured Internal Logs ({capturedLogs.Count} entries) ---");
                foreach (var line in capturedLogs)
                    EmitLog($"   {line}");
                EmitLog($"   --- End Captured Logs ---");
            }

            EmitLog("");

            OnTestStatusChanged?.Invoke(test.Id, result.Status, result);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // VERIFICATIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs a single verification, including async vision-based checks.
    /// </summary>
    private async Task<VerificationResult> RunVerificationAsync(
        SmokeVerification v, string agentResponse, string screenshotDir)
    {
        var result = new VerificationResult { Description = v.Description ?? v.Type.ToString() };

        try
        {
            switch (v.Type)
            {
                case SmokeVerificationType.ProcessRunning:
                {
                    var procs = Process.GetProcessesByName(v.Target);
                    result.Passed = procs.Length > 0;
                    result.Detail = result.Passed
                        ? $"{v.Target} running ({procs.Length} instance(s))"
                        : $"{v.Target} not found";
                    foreach (var p in procs) p.Dispose();
                    break;
                }

                case SmokeVerificationType.WindowTitleContains:
                {
                    var title = FindWindowByTitleSubstring(v.Target);
                    result.Passed = title != null;
                    result.Detail = result.Passed
                        ? $"Found: '{title}'"
                        : $"No window with '{v.Target}' in title";
                    break;
                }

                case SmokeVerificationType.ResponseContains:
                {
                    result.Passed = agentResponse.Contains(v.Target, StringComparison.OrdinalIgnoreCase);
                    result.Detail = result.Passed
                        ? $"Response contains '{v.Target}'"
                        : $"'{v.Target}' not found in response ({agentResponse.Length} chars)";
                    break;
                }

                case SmokeVerificationType.ScreenshotCreated:
                {
                    var ssDir = Path.Combine(AppContext.BaseDirectory, "reports", "_screenshots");
                    if (Directory.Exists(ssDir))
                    {
                        var recent = Directory.GetFiles(ssDir, "*.png")
                            .Select(f => new FileInfo(f))
                            .Where(f => f.CreationTime > DateTime.Now.AddMinutes(-2))
                            .OrderByDescending(f => f.CreationTime)
                            .FirstOrDefault();

                        result.Passed = recent != null;
                        result.Detail = result.Passed
                            ? $"Found: {recent!.Name} ({recent.Length / 1024}KB)"
                            : "No recent screenshot file";
                    }
                    else
                    {
                        result.Passed = false;
                        result.Detail = "_screenshots/ directory does not exist";
                    }
                    break;
                }

                case SmokeVerificationType.ScreenshotContainsText:
                {
                    result = await RunScreenshotContainsTextAsync(v, screenshotDir).ConfigureAwait(false);
                    break;
                }

                default:
                    result.Passed = false;
                    result.Detail = $"Unknown verification type: {v.Type}";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Detail = $"Verification error: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Takes a fresh screenshot, sends it to Vision LLM to read screen content,
    /// and checks whether the target text appears in the visible UI.
    /// </summary>
    private async Task<VerificationResult> RunScreenshotContainsTextAsync(
        SmokeVerification v, string screenshotDir)
    {
        var result = new VerificationResult { Description = v.Description ?? $"Screen shows '{v.Target}'" };

        if (_reports == null || _vision == null || !_vision.IsEnabled)
        {
            result.Passed = false;
            result.Detail = "ScreenshotContainsText requires ReportService and VisionService (vision must be enabled)";
            return result;
        }

        try
        {
            // 1. Capture a fresh screenshot
            var ssPath = _reports.CaptureScreenshot(screenshotDir, $"verify_{DateTime.Now:HHmmss_fff}.png");
            if (ssPath == null)
            {
                result.Passed = false;
                result.Detail = "Screenshot capture failed";
                return result;
            }

            EmitLog($"   [vision] Captured screenshot for OCR: {Path.GetFileName(ssPath)}");

            // 2. Send to Vision LLM with a "read text" prompt
            var visionResult = await _vision.LocateElementAsync(
                $"Read all visible text on the screen. Does the screen contain the text '{v.Target}'? " +
                $"Answer with YES or NO, then briefly describe what you see.",
                windowBounds: null,
                screenshotDir: screenshotDir
            ).ConfigureAwait(false);

            var raw = visionResult.RawResponse ?? visionResult.Description ?? "";
            EmitLog($"   [vision] Response: {Truncate(raw, 200)}");

            // 3. Check if vision confirms the text is present
            // Look for YES in the response, or for the target text itself
            var hasYes = raw.Contains("YES", StringComparison.OrdinalIgnoreCase);
            var hasTarget = raw.Contains(v.Target, StringComparison.OrdinalIgnoreCase);
            result.Passed = hasYes || hasTarget;
            result.Detail = result.Passed
                ? $"Vision confirms '{v.Target}' visible on screen"
                : $"Vision did not confirm '{v.Target}' on screen. Response: {Truncate(raw, 150)}";
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Detail = $"Vision verification error: {ex.Message}";
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    private static string? FindWindowByTitleSubstring(string titlePart)
    {
        try
        {
            var root = AutomationElement.RootElement;
            var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < children.Count; i++)
            {
                try
                {
                    var title = children[i].Current.Name;
                    if (!string.IsNullOrWhiteSpace(title) &&
                        title.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                        return title;
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch { }
        return null;
    }

    private void KillProcesses(List<string> processNames, string phase)
    {
        foreach (var name in processNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    EmitLog($"   [{phase}] Killing {procs.Length} {name} process(es)");
                    foreach (var p in procs)
                    {
                        try { p.Kill(); } catch { }
                        p.Dispose();
                    }
                    // Brief pause for process to fully exit
                    Thread.Sleep(300);
                }
            }
            catch { }
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void EmitLog(string message)
    {
        OnLogMessage?.Invoke(message);
        _fileWriter?.WriteLine(message);
        if (!string.IsNullOrWhiteSpace(message))
            _log.Info("SmokeTest", message.TrimStart());
    }
}

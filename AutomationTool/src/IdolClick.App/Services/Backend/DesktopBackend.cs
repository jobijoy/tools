using System.Diagnostics;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services.Backend;

// ═══════════════════════════════════════════════════════════════════════════════════
// DESKTOP BACKEND — Windows UI Automation implementation of IAutomationBackend.
//
// Wraps the existing 3-layer architecture:
//   SelectorParser → FlowActionExecutor → AssertionEvaluator
//
// Sprint 7 additions:
//   • Actionability checks (exists, visible, stable, enabled, receives-events)
//   • Backend call log (auto-wait reasons, retry events, resolution metadata)
//   • Typed selector support (DesktopUia kind)
//   • InspectTarget for agent tool discovery
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Windows Desktop automation backend using UI Automation + Win32.
/// Supports optional vision-based fallback when UIA selector resolution fails.
/// </summary>
public class DesktopBackend : IAutomationBackend
{
    private readonly LogService _log;
    private readonly IFlowActionExecutor _actionExecutor;
    private readonly IAssertionEvaluator _assertionEvaluator;
    private readonly SelectorParser _selectorParser;
    private VisionService? _visionService;
    private TimingSettings _timing = new();

    public DesktopBackend(
        LogService log,
        IFlowActionExecutor actionExecutor,
        IAssertionEvaluator assertionEvaluator,
        SelectorParser selectorParser,
        VisionService? visionService = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _actionExecutor = actionExecutor ?? throw new ArgumentNullException(nameof(actionExecutor));
        _assertionEvaluator = assertionEvaluator ?? throw new ArgumentNullException(nameof(assertionEvaluator));
        _selectorParser = selectorParser ?? throw new ArgumentNullException(nameof(selectorParser));
        _visionService = visionService;
    }

    /// <summary>Injects configurable timing settings.</summary>
    public void SetTiming(TimingSettings timing) => _timing = timing ?? new TimingSettings();

    /// <summary>
    /// Sets or replaces the vision service for fallback element location.
    /// </summary>
    public void SetVisionService(VisionService? visionService)
    {
        _visionService = visionService;
        _log.Info("DesktopBackend", visionService != null
            ? "Vision fallback connected"
            : "Vision fallback disconnected");
    }

    public string Name => "desktop-uia";
    public string Version => "1.0.0";

    public BackendCapabilities Capabilities { get; } = new()
    {
        SupportedActions = new HashSet<StepAction>(Enum.GetValues<StepAction>()),
        SupportedAssertions = new HashSet<AssertionType>(Enum.GetValues<AssertionType>()),
        SupportedSelectorKinds = new HashSet<SelectorKind> { SelectorKind.DesktopUia },
        SupportsTracing = false,
        SupportsScreenshots = true,
        SupportsActionabilityChecks = true
    };

    public Task InitializeAsync(BackendInitOptions options, CancellationToken ct = default)
    {
        _log.Info("DesktopBackend", "Initialized (Windows UI Automation)");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // STEP EXECUTION — Full lifecycle with actionability checks + call log
    // ═══════════════════════════════════════════════════════════════════════════════

    public async Task<StepResult> ExecuteStepAsync(TestStep step, BackendExecutionContext ctx, CancellationToken ct = default)
    {
        var result = new StepResult
        {
            Step = step.Order,
            Action = step.Action,
            Selector = step.Selector,
            Description = step.Description ?? $"{step.Action} {step.Selector}".Trim(),
            StartTime = DateTime.UtcNow,
            BackendName = Name
        };

        var callLog = new List<BackendCallLogEntry>();
        var stepSw = Stopwatch.StartNew();

        void Log(string msg, string level = "info")
        {
            callLog.Add(new BackendCallLogEntry
            {
                TimestampMs = stepSw.ElapsedMilliseconds,
                Message = msg,
                Level = level
            });
        }

        try
        {
            // ── Resolve window context ───────────────────────────────────────
            var targetApp = step.App ?? ctx.Flow.TargetApp;
            var windowTitle = step.WindowTitle;
            AutomationElement? window = null;

            // Inherit window context from previous steps when this step doesn't specify any.
            // Common pattern: step 1 = focus_window(app="X"), step 2 = send_keys() with no target.
            if (string.IsNullOrWhiteSpace(targetApp) && string.IsNullOrWhiteSpace(windowTitle))
            {
                if (ctx.State.TryGetValue("LastResolvedApp", out var savedApp))
                    targetApp = (string)savedApp;
                if (ctx.State.TryGetValue("LastResolvedTitle", out var savedTitle))
                    windowTitle = (string)savedTitle;
            }

            if (RequiresWindow(step))
            {
                Log($"Finding window (app='{targetApp}', title='{windowTitle}')");

                // Smart window wait: poll for the window to appear (handles post-launch timing)
                var windowTimeout = Math.Max(step.TimeoutMs, 5000);
                var windowSw = Stopwatch.StartNew();
                while (windowSw.ElapsedMilliseconds < windowTimeout)
                {
                    window = _selectorParser.FindWindow(targetApp, windowTitle);
                    if (window != null) break;

                    Log($"Window not found yet, retrying... ({windowSw.ElapsedMilliseconds}ms)");
                    await Task.Delay(_timing.WindowFindPollIntervalMs, ct).ConfigureAwait(false);
                }

                if (window == null)
                {
                    Log($"Window not found after {windowTimeout}ms", "error");
                    return Fail(result, stepSw, callLog,
                        $"Target window not found after {windowTimeout}ms (app='{targetApp}', title='{windowTitle}'). Use ListWindows to verify.");
                }

                Log($"Window found: '{window.Current.Name}' ({windowSw.ElapsedMilliseconds}ms)");

                // Remember resolved window context so subsequent steps can inherit it
                if (!string.IsNullOrWhiteSpace(targetApp))
                    ctx.State["LastResolvedApp"] = targetApp;
                if (!string.IsNullOrWhiteSpace(window.Current.Name))
                    ctx.State["LastResolvedTitle"] = window.Current.Name;

                // ── Target Lock enforcement ──────────────────────────────────
                if (ctx.Flow.TargetLock)
                {
                    var hwnd = new IntPtr(window.Current.NativeWindowHandle);
                    int pid = window.Current.ProcessId;
                    string wTitle = window.Current.Name ?? "";

                    if (!ctx.State.ContainsKey("TargetLock.HWND"))
                    {
                        // First step — capture lock state (PID + HWND + title)
                        ctx.State["TargetLock.HWND"] = hwnd.ToInt64();
                        ctx.State["TargetLock.PID"] = pid;
                        ctx.State["TargetLock.Title"] = wTitle;
                        Log($"TargetLock captured: HWND={hwnd}, PID={pid}, title='{wTitle}'");
                    }
                    else
                    {
                        // Subsequent steps — verify against locked state
                        var lockedHwnd = (long)ctx.State["TargetLock.HWND"];
                        var lockedPid = (int)ctx.State["TargetLock.PID"];
                        string? violation = null;

                        if (hwnd.ToInt64() != lockedHwnd)
                            violation = $"HWND changed (expected 0x{lockedHwnd:X}, got 0x{hwnd.ToInt64():X})";
                        else if (pid != lockedPid)
                            violation = $"Process ID changed (expected {lockedPid}, got {pid})";

                        if (violation != null)
                        {
                            Log($"TargetLock violation: {violation}", "error");
                            _log.Audit("TargetLock", $"Violation in flow '{ctx.Flow.TestName}' step {step.Order}: {violation}");
                            return Fail(result, stepSw, callLog,
                                $"TargetLock violation — {violation}. Execution paused for safety.");
                        }

                        Log("TargetLock verified ✓");
                    }
                }
            }

            // ── Resolve element via selector ─────────────────────────────────
            SelectorMatch? match = null;
            int retryCount = 0;
            var selectorStr = ResolveSelector(step);
            bool resolvedByVision = false;
            bool exactMatch = step.TypedSelector?.ExactMatch ?? false;

            if (!string.IsNullOrWhiteSpace(selectorStr) && window != null)
            {
                Log($"Resolving selector: '{selectorStr}'" + (exactMatch ? " [exact]" : ""));

                if (step.Action == StepAction.AssertNotExists)
                {
                    match = _selectorParser.ResolveOnce(window, selectorStr, exactMatch);
                    Log(match != null ? "Element found (assert_not_exists will fail)" : "Element not found (correct for assert_not_exists)");
                }
                else
                {
                    match = await _selectorParser.ResolveAsync(window, selectorStr, step.TimeoutMs, exactMatch, ct).ConfigureAwait(false);
                    retryCount = match?.RetryCount ?? 0;
                    if (match != null)
                        Log($"Element resolved after {retryCount} retries");
                    else
                        Log($"Element not found after {step.TimeoutMs}ms ({retryCount} retries)", "warn");
                }
            }

            // ── Vision fallback (when UIA selector fails) ───────────────────
            // When UIA trees lack sufficient info, vision is the fallback resolution path.
            // Eligible actions: Click, Type, Hover, AssertExists, AssertText, Scroll.

            // Diagnostic: dump vision eligibility for debugging
            try
            {
                var diagPath = Path.Combine(AppContext.BaseDirectory, "_spec_diag", "_vision_trace.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
                File.AppendAllText(diagPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] Step {step.Order}: action={step.Action} " +
                    $"match={(match != null ? "found" : "null")} " +
                    $"vision={(_visionService != null ? $"IsEnabled={_visionService.IsEnabled}" : "null")} " +
                    $"desc='{(step.Description?.Length > 50 ? step.Description[..50] + "..." : step.Description)}' " +
                    $"selector='{selectorStr}'\n");
            }
            catch { /* diagnostic only */ }

            if (match == null && _visionService is { IsEnabled: true }
                && !string.IsNullOrWhiteSpace(step.Description)
                && step.Action is StepAction.Click or StepAction.Type or StepAction.Hover
                    or StepAction.AssertExists or StepAction.AssertText or StepAction.Scroll
                && step.Action != StepAction.AssertNotExists)
            {
                Log("UIA selector failed — attempting vision fallback");

                // Get window bounds for region capture
                Models.ElementBounds? windowRegion = null;
                if (window != null)
                {
                    try
                    {
                        var rect = window.Current.BoundingRectangle;
                        if (!rect.IsEmpty)
                        {
                            windowRegion = new Models.ElementBounds
                            {
                                X = (int)rect.X,
                                Y = (int)rect.Y,
                                Width = (int)rect.Width,
                                Height = (int)rect.Height
                            };
                        }
                    }
                    catch (ElementNotAvailableException) { }
                }

                var visionDesc = step.Description ?? selectorStr ?? "target element";
                var visionResult = await _visionService.LocateElementAsync(visionDesc, windowRegion, ct: ct).ConfigureAwait(false);

                // Diagnostic: dump vision result
                try
                {
                    var diagPath = Path.Combine(AppContext.BaseDirectory, "_spec_diag", "_vision_trace.txt");
                    File.AppendAllText(diagPath,
                        $"  → Vision result: Found={visionResult.Found} " +
                        $"Confidence={visionResult.Confidence:F2} " +
                        $"Center=({visionResult.CenterX},{visionResult.CenterY}) " +
                        $"Error='{visionResult.Error}' " +
                        $"Screenshot='{visionResult.ScreenshotPath}'\n");
                }
                catch { /* diagnostic only */ }

                if (visionResult.Found && visionResult.Bounds != null)
                {
                    Log($"Vision located element at ({visionResult.CenterX},{visionResult.CenterY}) confidence={visionResult.Confidence:F2}");
                    resolvedByVision = true;
                    result.SelectorResolvedTo = $"[Vision] {visionResult.Description} confidence={visionResult.Confidence:F2}";
                    result.ClickPoint = new Models.ElementBounds
                    {
                        X = visionResult.CenterX,
                        Y = visionResult.CenterY,
                        Width = 1,
                        Height = 1
                    };

                    if (visionResult.ScreenshotPath != null)
                        result.Screenshot = visionResult.ScreenshotPath;
                }
                else
                {
                    Log($"Vision fallback also failed: {visionResult.Error}", "warn");
                }
            }

            result.RetryCount = retryCount;
            result.Element = match?.Snapshot;
            result.SelectorResolvedTo ??= match != null
                ? $"{match.Snapshot.ControlType} '{match.Snapshot.Name}' (id={match.Snapshot.AutomationId})"
                : null;

            // ── Vision-resolved action (bypass normal action path) ─────────
            if (resolvedByVision && result.ClickPoint != null
                && step.Action is StepAction.Click or StepAction.Type or StepAction.Hover
                    or StepAction.AssertExists or StepAction.AssertText or StepAction.Scroll)
            {
                if (step.Action == StepAction.AssertExists)
                {
                    // Vision found the element — assertion passes
                    Log($"Vision-resolved assert_exists: element found at ({result.ClickPoint.X},{result.ClickPoint.Y})");
                }
                else if (step.Action == StepAction.Click)
                {
                    Log($"Executing vision-based click at ({result.ClickPoint.X},{result.ClickPoint.Y})");
                    Win32.Click(result.ClickPoint.X, result.ClickPoint.Y);
                    Log("Vision click executed");
                }
                else if (step.Action == StepAction.Type)
                {
                    // Click to focus, then type
                    Log($"Executing vision-based click-to-focus at ({result.ClickPoint.X},{result.ClickPoint.Y})");
                    Win32.Click(result.ClickPoint.X, result.ClickPoint.Y);
                    await Task.Delay(_timing.PostClickFocusDelayMs, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(step.Text))
                    {
                        foreach (var ch in step.Text)
                        {
                            Win32.SendChar(ch);
                            await Task.Delay(_timing.TypeCharDelayMs, ct).ConfigureAwait(false);
                        }
                        Log($"Vision-typed '{step.Text}'");
                    }
                }
                else if (step.Action == StepAction.Hover)
                {
                    Log($"Executing vision-based hover at ({result.ClickPoint.X},{result.ClickPoint.Y})");
                    Win32.MoveCursor(result.ClickPoint.X, result.ClickPoint.Y);
                    Log("Vision hover executed");
                }
                else if (step.Action == StepAction.AssertText)
                {
                    // Vision found a matching element — treat as text verification pass.
                    // The vision description should include the expected text content.
                    Log($"Vision-resolved assert_text: element with expected content found at ({result.ClickPoint.X},{result.ClickPoint.Y})");
                    result.Expected = step.Contains ?? step.Description;
                    result.Found = $"[Vision-verified] {step.Description}";
                }
                else if (step.Action == StepAction.Scroll)
                {
                    // Vision found the target area — scroll at that position
                    Log($"Vision-resolved scroll at ({result.ClickPoint.X},{result.ClickPoint.Y})");
                    Win32.Click(result.ClickPoint.X, result.ClickPoint.Y);
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    var amount = step.ScrollAmount > 0 ? step.ScrollAmount : 3;
                    var direction = step.Direction?.ToLowerInvariant() == "up" ? 1 : -1;
                    for (int s = 0; s < amount; s++)
                    {
                        Win32.MouseWheel(direction * 120);
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                    Log($"Vision scroll executed ({amount} units {step.Direction})");
                }

                // Still evaluate post-step assertions if present
                if (step.Assertions.Count > 0 && !ct.IsCancellationRequested)
                {
                    Log($"Evaluating {step.Assertions.Count} post-step assertion(s)");
                    bool allPassed = true;
                    foreach (var assertion in step.Assertions)
                    {
                        var assertResult = await _assertionEvaluator.EvaluateAsync(assertion, window, _selectorParser, ct).ConfigureAwait(false);
                        result.AssertionResults.Add(assertResult);

                        if (!assertResult.Passed)
                        {
                            allPassed = false;
                            Log($"Assertion failed: {assertResult.Error}", "warn");
                        }
                        else
                        {
                            Log($"Assertion passed: {assertion.Type} '{assertion.Selector}'");
                        }
                    }

                    if (!allPassed)
                    {
                        var errors = string.Join("; ", result.AssertionResults.Where(a => !a.Passed).Select(a => a.Error));
                        return Fail(result, stepSw, callLog, $"Post-step assertion(s) failed: {errors}");
                    }
                }

                Log("Step passed (vision fallback)");
                stepSw.Stop();
                result.EndTime = DateTime.UtcNow;
                result.TimeMs = stepSw.ElapsedMilliseconds;
                result.Status = StepStatus.Warning;
                result.WarningCode = "VisionFallbackUsed";
                result.BackendCallLog = callLog;
                result.Diagnostics = "Resolved by vision fallback — non-deterministic resolution, review recommended";
                _log.Audit("Vision", $"Vision fallback used in flow step {step.Order}: {step.Description}");
                return result;
            }

            // ── Actionability checks ─────────────────────────────────────────
            if (match != null)
            {
                var requiredChecks = ActionabilityContracts.GetRequiredChecks(step.Action);
                var checkResult = await EvaluateActionability(match.Element, requiredChecks, step.TimeoutMs, callLog, stepSw).ConfigureAwait(false);

                if (!checkResult.Passed)
                {
                    return Fail(result, stepSw, callLog,
                        $"Actionability check failed: {checkResult.FailReason}");
                }

                // Record click point for positional actions
                if (step.Action == StepAction.Click && match.Snapshot.Bounds != null)
                {
                    var b = match.Snapshot.Bounds;
                    result.ClickPoint = new ElementBounds
                    {
                        X = b.X + b.Width / 2,
                        Y = b.Y + b.Height / 2,
                        Width = 1,
                        Height = 1
                    };
                }
            }

            // ── Execute action ───────────────────────────────────────────────
            Log($"Executing action: {step.Action}");
            var actionResult = await _actionExecutor.ExecuteAsync(step, match?.Element, window).ConfigureAwait(false);

            result.Expected = actionResult.Expected;
            result.Found = actionResult.Found;
            result.Diagnostics = actionResult.Diagnostics;

            if (actionResult.ScreenshotPath != null)
                result.Screenshot = actionResult.ScreenshotPath;

            if (!actionResult.Success)
            {
                Log($"Action failed: {actionResult.Error}", "error");
                return Fail(result, stepSw, callLog, actionResult.Error);
            }

            Log("Action succeeded");

            // ── Post-step assertions ─────────────────────────────────────────
            if (step.Assertions.Count > 0 && !ct.IsCancellationRequested)
            {
                Log($"Evaluating {step.Assertions.Count} post-step assertion(s)");
                bool allPassed = true;
                foreach (var assertion in step.Assertions)
                {
                    var assertResult = await _assertionEvaluator.EvaluateAsync(assertion, window, _selectorParser, ct).ConfigureAwait(false);
                    result.AssertionResults.Add(assertResult);

                    if (!assertResult.Passed)
                    {
                        allPassed = false;
                        Log($"Assertion failed: {assertResult.Error}", "warn");
                    }
                    else
                    {
                        Log($"Assertion passed: {assertion.Type} '{assertion.Selector}'");
                    }
                }

                if (!allPassed)
                {
                    var errors = string.Join("; ", result.AssertionResults.Where(a => !a.Passed).Select(a => a.Error));
                    return Fail(result, stepSw, callLog, $"Post-step assertion(s) failed: {errors}");
                }
            }

            // ── Success ──────────────────────────────────────────────────────
            Log("Step passed");
            stepSw.Stop();
            result.EndTime = DateTime.UtcNow;
            result.TimeMs = stepSw.ElapsedMilliseconds;
            result.Status = StepStatus.Passed;
            result.BackendCallLog = callLog;
            return result;
        }
        catch (OperationCanceledException)
        {
            Log("Step cancelled", "warn");
            stepSw.Stop();
            result.EndTime = DateTime.UtcNow;
            result.TimeMs = stepSw.ElapsedMilliseconds;
            result.Status = StepStatus.Skipped;
            result.Diagnostics = "Step cancelled";
            result.BackendCallLog = callLog;
            return result;
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.GetType().Name}: {ex.Message}", "error");
            stepSw.Stop();
            result.EndTime = DateTime.UtcNow;
            result.TimeMs = stepSw.ElapsedMilliseconds;
            result.Status = StepStatus.Error;
            result.Error = $"Unexpected error: {ex.GetType().Name}: {ex.Message}";
            result.BackendCallLog = callLog;
            _log.Error("DesktopBackend", $"Step {step.Order}: {result.Error}");
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ACTIONABILITY CHECKS — Pre-action validation
    // ═══════════════════════════════════════════════════════════════════════════════

    private record ActionabilityResult(bool Passed, string? FailReason = null);

    private async Task<ActionabilityResult> EvaluateActionability(
        AutomationElement element,
        IReadOnlySet<ActionabilityCheck> requiredChecks,
        int timeoutMs,
        List<BackendCallLogEntry> callLog,
        Stopwatch sw)
    {
        if (requiredChecks.Count == 0)
            return new ActionabilityResult(true);

        void Log(string msg, string level = "info") =>
            callLog.Add(new BackendCallLogEntry { TimestampMs = sw.ElapsedMilliseconds, Message = msg, Level = level });

        try
        {
            var current = element.Current;

            // ── Exists: already resolved, so passes ──────────────────────────
            // (included for completeness)

            // ── Visible: non-zero bounding box ───────────────────────────────
            if (requiredChecks.Contains(ActionabilityCheck.Visible))
            {
                var rect = current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    Log("Actionability: element not visible (empty bounding box)", "warn");
                    return new ActionabilityResult(false, "Element is not visible (bounding box is empty or zero-sized)");
                }
                Log("Actionability: visible ✓");
            }

            // ── Enabled: not grayed/disabled ─────────────────────────────────
            if (requiredChecks.Contains(ActionabilityCheck.Enabled))
            {
                if (!current.IsEnabled)
                {
                    Log("Actionability: element is disabled", "warn");
                    return new ActionabilityResult(false, "Element is disabled (IsEnabled=false)");
                }
                Log("Actionability: enabled ✓");
            }

            // ── Stable: bounding box unchanged across two reads ──────────────
            if (requiredChecks.Contains(ActionabilityCheck.Stable))
            {
                Log("Actionability: checking stability (2-frame bounding box comparison)");
                var rect1 = current.BoundingRectangle;
                await Task.Delay(_timing.StabilityFrameDelayMs).ConfigureAwait(false); // Wait one frame plus safety margin

                try
                {
                    var rect2 = element.Current.BoundingRectangle;
                    if (rect1 != rect2)
                    {
                        // One more retry with longer wait
                        await Task.Delay(_timing.StabilityRetryDelayMs).ConfigureAwait(false);
                        var rect3 = element.Current.BoundingRectangle;
                        if (rect2 != rect3)
                        {
                            Log("Actionability: element is animating (bounds changing)", "warn");
                            return new ActionabilityResult(false,
                                $"Element is not stable (bounds moving: [{rect1.X},{rect1.Y}] → [{rect3.X},{rect3.Y}])");
                        }
                    }
                }
                catch (ElementNotAvailableException)
                {
                    return new ActionabilityResult(false, "Element became unavailable during stability check");
                }
                Log("Actionability: stable ✓");
            }

            // ── ReceivesEvents: not off-screen ──────────────────────────────
            if (requiredChecks.Contains(ActionabilityCheck.ReceivesEvents))
            {
                if (current.IsOffscreen)
                {
                    Log("Actionability: element is off-screen", "warn");
                    return new ActionabilityResult(false, "Element is off-screen and cannot receive events");
                }
                Log("Actionability: receives events ✓");
            }

            // ── Editable: for text input, check for ValuePattern ─────────────
            if (requiredChecks.Contains(ActionabilityCheck.Editable))
            {
                bool editable = element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern val
                    ? !val.Current.IsReadOnly
                    : !current.IsOffscreen && current.IsEnabled; // Fallback: assume editable if enabled

                if (!editable)
                {
                    Log("Actionability: element is read-only", "warn");
                    return new ActionabilityResult(false, "Element is read-only and cannot accept text input");
                }
                Log("Actionability: editable ✓");
            }

            return new ActionabilityResult(true);
        }
        catch (ElementNotAvailableException)
        {
            Log("Actionability: element became unavailable during checks", "error");
            return new ActionabilityResult(false, "Element became unavailable during actionability checks");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // INSPECTION — For agent tool discovery
    // ═══════════════════════════════════════════════════════════════════════════════

    public Task<IReadOnlyList<InspectableTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        var targets = new List<InspectableTarget>();
        try
        {
            var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < children.Count; i++)
            {
                try
                {
                    var w = children[i];
                    var current = w.Current;
                    if (string.IsNullOrWhiteSpace(current.Name)) continue;

                    var rect = current.BoundingRectangle;
                    targets.Add(new InspectableTarget
                    {
                        Id = current.AutomationId ?? $"hwnd_{current.NativeWindowHandle}",
                        Title = current.Name,
                        TargetType = "window",
                        Source = current.ProcessId > 0 ? GetProcessName(current.ProcessId) : null,
                        Bounds = rect.IsEmpty ? null : new ElementBounds
                        {
                            X = (int)rect.X, Y = (int)rect.Y,
                            Width = (int)rect.Width, Height = (int)rect.Height
                        }
                    });
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("DesktopBackend", $"ListTargets error: {ex.Message}");
        }

        return Task.FromResult<IReadOnlyList<InspectableTarget>>(targets);
    }

    public Task<InspectionResult> InspectTargetAsync(InspectTargetRequest request, CancellationToken ct = default)
    {
        var result = new InspectionResult { TargetId = request.TargetId };

        try
        {
            // Find the window by handle or AutomationId
            var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            AutomationElement? target = null;

            for (int i = 0; i < children.Count; i++)
            {
                try
                {
                    var w = children[i];
                    var current = w.Current;
                    if ((current.AutomationId ?? $"hwnd_{current.NativeWindowHandle}") == request.TargetId)
                    {
                        target = w;
                        break;
                    }
                }
                catch (ElementNotAvailableException) { }
            }

            if (target == null)
            {
                result.Nodes.Add(new InspectionNode { Type = "Error", Name = "Target not found" });
                return Task.FromResult(result);
            }

            // Build tree
            BuildInspectionTree(target, result.Nodes, 0, request.MaxDepth, ref result);
        }
        catch (Exception ex)
        {
            result.Nodes.Add(new InspectionNode { Type = "Error", Name = ex.Message });
        }

        return Task.FromResult(result);
    }

    private void BuildInspectionTree(AutomationElement element, List<InspectionNode> nodes, int depth, int maxDepth, ref InspectionResult result)
    {
        if (depth >= maxDepth)
        {
            result.Truncated = true;
            return;
        }

        try
        {
            var elems = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < elems.Count && i < 50; i++) // Cap at 50 children per level
            {
                try
                {
                    var e = elems[i];
                    var current = e.Current;
                    var controlType = current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "Unknown";
                    var rect = current.BoundingRectangle;

                    var node = new InspectionNode
                    {
                        Type = controlType,
                        Name = current.Name,
                        Id = current.AutomationId,
                        IsInteractive = current.IsEnabled && !current.IsOffscreen,
                        SuggestedSelector = BuildSuggestedSelector(controlType, current.Name, current.AutomationId),
                        Bounds = rect.IsEmpty ? null : new ElementBounds
                        {
                            X = (int)rect.X, Y = (int)rect.Y,
                            Width = (int)rect.Width, Height = (int)rect.Height
                        }
                    };

                    result.TotalCount++;
                    nodes.Add(node);

                    BuildInspectionTree(e, node.Children, depth + 1, maxDepth, ref result);
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (ElementNotAvailableException) { }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ARTIFACTS — Screenshots only for Desktop (no tracing support)
    // ═══════════════════════════════════════════════════════════════════════════════

    public Task<BackendArtifact?> StartArtifactCaptureAsync(ArtifactOptions options, CancellationToken ct = default)
    {
        // Desktop UIA does not support structured tracing
        return Task.FromResult<BackendArtifact?>(null);
    }

    public Task<BackendArtifact?> StopArtifactCaptureAsync(CancellationToken ct = default)
    {
        return Task.FromResult<BackendArtifact?>(null);
    }

    public ValueTask DisposeAsync()
    {
        _log.Debug("DesktopBackend", "Disposed");
        return ValueTask.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the effective selector string from a step, honoring TypedSelector if present.
    /// </summary>
    private static string? ResolveSelector(TestStep step)
    {
        if (step.TypedSelector != null && step.TypedSelector.Kind == SelectorKind.DesktopUia)
            return step.TypedSelector.Value;

        return step.Selector;
    }

    /// <summary>
    /// Determines if a step action requires a window context.
    /// </summary>
    private static bool RequiresWindow(TestStep step)
    {
        return step.Action switch
        {
            StepAction.Launch => false,
            StepAction.Wait when string.IsNullOrWhiteSpace(step.Selector) => false,
            StepAction.Screenshot => false,
            StepAction.Navigate => false,
            _ => true
        };
    }

    private static string? BuildSuggestedSelector(string controlType, string? name, string? automationId)
    {
        if (!string.IsNullOrWhiteSpace(automationId))
            return $"{controlType}#{automationId}";
        if (!string.IsNullOrWhiteSpace(name))
            return $"{controlType}#{name}";
        return null;
    }

    private static string? GetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return null; }
    }

    private static StepResult Fail(StepResult result, Stopwatch sw, List<BackendCallLogEntry> callLog, string? error)
    {
        sw.Stop();
        result.EndTime = DateTime.UtcNow;
        result.TimeMs = sw.ElapsedMilliseconds;
        result.Status = StepStatus.Failed;
        result.Error = error;
        result.BackendCallLog = callLog;
        return result;
    }
}

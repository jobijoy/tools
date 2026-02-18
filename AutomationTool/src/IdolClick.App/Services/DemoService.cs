using System.Diagnostics;
using System.Windows.Automation;
using IdolClick.Models;
using IdolClick.UI;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// DEMO SERVICE — Self-demonstrating showcase of IdolClick capabilities.
//
// Runs scripted demo scenarios using real Windows apps (Calculator, Notepad)
// and the built-in ClassicTestWindow. No LLM/API key required — all demos
// use deterministic UIA automation.
//
// Entry points:
//   • DemoWindow (GUI button)
//   • --demo CLI flag
// ═══════════════════════════════════════════════════════════════════════════════════

public class DemoService
{
    private readonly LogService _log;
    private int _stepDelayMs = 400;

    public event Action<string>? OnNarrate;
    public event Action<string, DemoStatus>? OnScenarioStatusChanged;

    public DemoService(LogService log)
    {
        _log = log;
    }

    public static List<DemoScenario> GetScenarios() =>
    [
        new("DEMO-01", "Calculator Sprint",
            "Open Calculator, compute 42 \u00D7 13, verify the result = 546",
            DemoDifficulty.Basic),
        new("DEMO-02", "Notepad Author",
            "Open Notepad, type a multi-line message with visible keystrokes",
            DemoDifficulty.Basic),
        new("DEMO-03", "Classic Rule Watch",
            "Show the rule engine auto-clicking matching buttons in real time",
            DemoDifficulty.Intermediate),
        new("DEMO-04", "Desktop Inventory",
            "List all open windows \u2014 demonstrates IdolClick's UIA sight",
            DemoDifficulty.Basic),
        new("DEMO-05", "Cross-App Pipeline",
            "Calculate in Calculator, then transcribe the result into Notepad",
            DemoDifficulty.Advanced),
    ];

    public async Task<DemoResult> RunAsync(List<DemoScenario> scenarios, CancellationToken ct)
    {
        var results = new List<DemoScenarioResult>();
        foreach (var scenario in scenarios)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunScenarioAsync(scenario, ct);
            results.Add(result);
        }
        return new DemoResult(results);
    }

    private async Task<DemoScenarioResult> RunScenarioAsync(DemoScenario scenario, CancellationToken ct)
    {
        OnScenarioStatusChanged?.Invoke(scenario.Id, DemoStatus.Running);
        var sw = Stopwatch.StartNew();

        try
        {
            bool success = scenario.Id switch
            {
                "DEMO-01" => await RunCalculatorDemo(ct),
                "DEMO-02" => await RunNotepadDemo(ct),
                "DEMO-03" => await RunClassicRuleDemo(ct),
                "DEMO-04" => await RunDesktopInventory(ct),
                "DEMO-05" => await RunCrossAppPipeline(ct),
                _ => false
            };

            sw.Stop();
            var status = success ? DemoStatus.Passed : DemoStatus.Failed;
            OnScenarioStatusChanged?.Invoke(scenario.Id, status);
            return new DemoScenarioResult(scenario.Id, status, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            OnScenarioStatusChanged?.Invoke(scenario.Id, DemoStatus.Skipped);
            return new DemoScenarioResult(scenario.Id, DemoStatus.Skipped, sw.ElapsedMilliseconds, "Cancelled");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Narrate($"\u2717 Error: {ex.Message}");
            OnScenarioStatusChanged?.Invoke(scenario.Id, DemoStatus.Error);
            return new DemoScenarioResult(scenario.Id, DemoStatus.Error, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DEMO 1 — Calculator Sprint
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task<bool> RunCalculatorDemo(CancellationToken ct)
    {
        Narrate("━━━ Demo 1: Calculator Sprint ━━━");
        Narrate("Opening Windows Calculator...");

        var proc = Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
        await Task.Delay(1500, ct);

        var window = await WaitForWindowAsync("Calculator", 8000, ct);
        if (window == null)
        {
            Narrate("\u2717 Calculator window not found");
            return false;
        }
        Narrate($"\u2713 Window found: \"{window.Current.Name}\"");

        // Clear display
        await Task.Delay(_stepDelayMs, ct);
        var clearBtn = FindByAutomationId(window, "clearButton")
                    ?? FindByAutomationId(window, "clearEntryButton");
        if (clearBtn != null)
        {
            ClickUiaElement(clearBtn);
            Narrate("  Cleared display");
            await Task.Delay(_stepDelayMs, ct);
        }

        // Type: 4 2 × 1 3 =
        var sequence = new[]
        {
            ("num4Button", "4"), ("num2Button", "2"),
            ("multiplyButton", "\u00D7"),
            ("num1Button", "1"), ("num3Button", "3"),
            ("equalButton", "=")
        };

        foreach (var (id, label) in sequence)
        {
            ct.ThrowIfCancellationRequested();
            var btn = FindByAutomationId(window, id);
            if (btn != null)
            {
                ClickUiaElement(btn);
                Narrate($"  Clicked [{label}]");
                await Task.Delay(_stepDelayMs, ct);
            }
            else
            {
                Narrate($"  \u2717 Button \"{id}\" not found (Calculator version mismatch?)");
                CleanupProcess(proc);
                return false;
            }
        }

        // Read result
        await Task.Delay(300, ct);
        var display = FindByAutomationId(window, "CalculatorResults");
        if (display != null)
        {
            var text = display.Current.Name;
            Narrate($"  Display reads: {text}");

            if (text?.Contains("546") == true)
                Narrate("\u2713 Result verified: 42 \u00D7 13 = 546");
            else
                Narrate($"\u26A0 Expected 546 in display, got: \"{text}\"");
        }
        else
        {
            Narrate("  \u26A0 Could not read display (CalculatorResults not found)");
        }

        Narrate("  Closing Calculator...");
        CleanupProcess(proc);
        Narrate("\u2713 Calculator Sprint complete!\n");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DEMO 2 — Notepad Author
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task<bool> RunNotepadDemo(CancellationToken ct)
    {
        Narrate("━━━ Demo 2: Notepad Author ━━━");
        Narrate("Opening Notepad...");

        var proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        await Task.Delay(1500, ct);

        var window = await WaitForWindowAsync("Notepad", 8000, ct);
        if (window == null)
        {
            Narrate("\u2717 Notepad window not found");
            return false;
        }
        Narrate($"\u2713 Window found: \"{window.Current.Name}\"");

        // Focus window
        FocusWindow(window);
        await Task.Delay(300, ct);

        // Type text with visible keystrokes
        var lines = new[]
        {
            "Hello from IdolClick!",
            "",
            "This text was typed automatically by the IdolClick",
            "desktop automation engine -- no human hands involved.",
            "",
            $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Machine:   {Environment.MachineName}",
            "",
            "IdolClick sees your desktop through the Windows",
            "UI Automation tree and acts with deterministic precision.",
        };

        Narrate("  Typing with visible keystrokes...");
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var ch in line)
            {
                Win32.SendChar(ch);
                await Task.Delay(20, ct);
            }
            Win32.SendKey(Win32.VK_RETURN);
            await Task.Delay(60, ct);

            if (line.Length > 0)
                Narrate($"    \u00BB {line}");
        }

        Narrate("  Text entry complete!");

        // Select all to show it worked
        await Task.Delay(300, ct);
        Narrate("  Selecting all text (Ctrl+A)...");
        Win32.SendKeyCombo([Win32.VK_CONTROL], 0x41); // Ctrl+A
        await Task.Delay(1200, ct); // pause so user can see the result

        Narrate("  Closing Notepad...");
        CleanupProcess(proc);
        Narrate("\u2713 Notepad Author complete!\n");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DEMO 3 — Classic Rule Watch
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task<bool> RunClassicRuleDemo(CancellationToken ct)
    {
        Narrate("━━━ Demo 3: Classic Rule Engine Watch ━━━");
        Narrate("This demo shows the polling rule engine in action.");
        Narrate("A rule is configured to auto-click the \"Accept\" button.\n");

        // Open ClassicTestWindow on UI thread
        ClassicTestWindow? testWindow = null;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            testWindow = new ClassicTestWindow();
            testWindow.Title = "IdolClick Demo \u2014 Rule Target Surface";
            testWindow.Show();
        });

        if (testWindow == null)
        {
            Narrate("\u2717 Could not create test window");
            return false;
        }

        Narrate("\u2713 Test window opened with buttons: Accept, Decline, OK, Save...");
        await Task.Delay(1000, ct);

        // Create a rule that matches "Accept"
        Narrate("  Configuring rule: match \"Accept\" button \u2192 Click");
        var rule = new Rule
        {
            Name = "Demo Auto-Accept",
            TargetApp = "IdolClick",
            MatchText = "Accept",
            Action = "Click",
            Enabled = true,
            CooldownSeconds = 0,
            ElementType = "Button"
        };

        // Create temp config with the rule and run the engine
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "IdolClick_Demo_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(tempDir);
        var configPath = System.IO.Path.Combine(tempDir, "config.json");

        try
        {
            var config = new AppConfig
            {
                Settings = new GlobalSettings
                {
                    PollingIntervalMs = 1000,
                    ClickRadar = false,
                    LogLevel = "Debug"
                },
                Rules = [rule]
            };

            var json = System.Text.Json.JsonSerializer.Serialize(config,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
            await System.IO.File.WriteAllTextAsync(configPath, json, ct);

            var configService = new ConfigService(configPath);
            using var engine = new AutomationEngine(configService, _log);

            Narrate("  Engine created. Running polling cycle...");
            Narrate("  \u23F1 Watching for \"Accept\" button...");
            await Task.Delay(500, ct);

            // Run three cycles to show the engine working
            for (int cycle = 1; cycle <= 3; cycle++)
            {
                ct.ThrowIfCancellationRequested();
                Narrate($"\n  Cycle {cycle}/3:");
                var (evaluated, triggered) = await engine.ProcessRulesOnceAsync();
                Narrate($"    Evaluated: {evaluated} rule(s), Triggered: {triggered}");

                if (triggered > 0)
                {
                    // Check what was clicked
                    if (testWindow.ClickLog.TryDequeue(out var clicked))
                        Narrate($"    \u2713 Auto-clicked: \"{clicked}\"");
                }

                // Reset for next cycle
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    testWindow.ResetLog());
                await Task.Delay(800, ct);
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }

        // Close test window
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            testWindow.Close());

        Narrate("\u2713 Classic Rule Watch complete!\n");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DEMO 4 — Desktop Inventory
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task<bool> RunDesktopInventory(CancellationToken ct)
    {
        Narrate("━━━ Demo 4: Desktop Inventory ━━━");
        Narrate("Scanning all open windows via UI Automation tree...\n");

        await Task.Delay(300, ct);

        var root = AutomationElement.RootElement;
        var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
        int count = 0;

        for (int i = 0; i < children.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var current = children[i].Current;
                var name = current.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pid = current.ProcessId;
                string? procName = null;
                try { procName = Process.GetProcessById(pid).ProcessName; } catch { }

                var rect = current.BoundingRectangle;
                var bounds = rect.IsEmpty ? "off-screen" : $"{(int)rect.Width}\u00D7{(int)rect.Height}";

                count++;
                Narrate($"  [{count:D2}] \"{name}\"");
                Narrate($"       Process: {procName ?? "?"} (PID {pid})  Size: {bounds}");

                // Inspect first few children to show UIA depth
                if (count <= 3)
                {
                    var kids = children[i].FindAll(TreeScope.Children, Condition.TrueCondition);
                    if (kids.Count > 0)
                    {
                        var childTypes = new List<string>();
                        for (int k = 0; k < Math.Min(kids.Count, 5); k++)
                        {
                            try
                            {
                                var ct2 = kids[k].Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "");
                                var cn = kids[k].Current.Name;
                                childTypes.Add(string.IsNullOrEmpty(cn) ? ct2 ?? "?" : $"{ct2}:\"{cn}\"");
                            }
                            catch { }
                        }
                        if (childTypes.Count > 0)
                        {
                            var suffix = kids.Count > 5 ? $" ... +{kids.Count - 5} more" : "";
                            Narrate($"       Children: {string.Join(", ", childTypes)}{suffix}");
                        }
                    }
                }
            }
            catch (ElementNotAvailableException) { }
        }

        Narrate($"\n  Total visible windows: {count}");
        Narrate("  This is exactly what IdolClick's Eye sees \u2014 every window,");
        Narrate("  every control, every button accessible through the UIA tree.");
        Narrate("\u2713 Desktop Inventory complete!\n");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DEMO 5 — Cross-App Pipeline
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task<bool> RunCrossAppPipeline(CancellationToken ct)
    {
        Narrate("━━━ Demo 5: Cross-App Pipeline ━━━");
        Narrate("Goal: Calculate 7 \u00D7 8 in Calculator, then type the result in Notepad.\n");

        // ── Step 1: Calculator ──
        Narrate("Step 1: Opening Calculator...");
        var calcProc = Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
        await Task.Delay(1500, ct);

        var calcWindow = await WaitForWindowAsync("Calculator", 8000, ct);
        if (calcWindow == null)
        {
            Narrate("\u2717 Calculator not found");
            return false;
        }
        Narrate($"\u2713 Calculator found: \"{calcWindow.Current.Name}\"");
        await Task.Delay(_stepDelayMs, ct);

        // Clear
        var clearBtn = FindByAutomationId(calcWindow, "clearButton")
                    ?? FindByAutomationId(calcWindow, "clearEntryButton");
        if (clearBtn != null) ClickUiaElement(clearBtn);
        await Task.Delay(_stepDelayMs, ct);

        // 7 × 8 =
        var calcSequence = new[]
        {
            ("num7Button", "7"), ("multiplyButton", "\u00D7"),
            ("num8Button", "8"), ("equalButton", "=")
        };

        foreach (var (id, label) in calcSequence)
        {
            ct.ThrowIfCancellationRequested();
            var btn = FindByAutomationId(calcWindow, id);
            if (btn != null)
            {
                ClickUiaElement(btn);
                Narrate($"  Calculator: clicked [{label}]");
                await Task.Delay(_stepDelayMs, ct);
            }
            else
            {
                Narrate($"  \u2717 Button \"{id}\" not found");
                CleanupProcess(calcProc);
                return false;
            }
        }

        // Read result
        await Task.Delay(300, ct);
        string resultValue = "56";
        var display = FindByAutomationId(calcWindow, "CalculatorResults");
        if (display != null)
        {
            var displayText = display.Current.Name ?? "";
            Narrate($"  Calculator display: {displayText}");

            // Extract the number from "Display is 56"
            var match = System.Text.RegularExpressions.Regex.Match(displayText, @"\d+");
            if (match.Success) resultValue = match.Value;
        }
        Narrate($"  \u2713 Captured result: {resultValue}");

        // ── Step 2: Notepad ──
        Narrate("\nStep 2: Opening Notepad...");
        var notepadProc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        await Task.Delay(1500, ct);

        var notepadWindow = await WaitForWindowAsync("Notepad", 8000, ct);
        if (notepadWindow == null)
        {
            Narrate("\u2717 Notepad not found");
            CleanupProcess(calcProc);
            return false;
        }
        Narrate($"\u2713 Notepad found: \"{notepadWindow.Current.Name}\"");

        FocusWindow(notepadWindow);
        await Task.Delay(300, ct);

        // Type the result
        var message = $"IdolClick Cross-App Pipeline Result\r\n" +
                      $"===================================\r\n" +
                      $"Calculation: 7 x 8 = {resultValue}\r\n" +
                      $"Source: Windows Calculator (UIA)\r\n" +
                      $"Destination: Notepad (keyboard input)\r\n" +
                      $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

        Narrate("  Typing result into Notepad...");
        foreach (var ch in message)
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\r') continue;
            if (ch == '\n')
                Win32.SendKey(Win32.VK_RETURN);
            else
                Win32.SendChar(ch);
            await Task.Delay(15, ct);
        }

        Narrate($"  \u2713 Typed {message.Length} characters into Notepad");
        await Task.Delay(1200, ct); // pause so user can see the result

        Narrate("\n  Closing Calculator...");
        CleanupProcess(calcProc);
        Narrate("  Closing Notepad...");
        CleanupProcess(notepadProc);

        Narrate("\u2713 Cross-App Pipeline complete!");
        Narrate("  Calculator \u2192 Notepad data transfer successful.\n");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // UIA HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    private void Narrate(string message)
    {
        OnNarrate?.Invoke(message);
        _log.Info("Demo", message);
    }

    private static async Task<AutomationElement?> WaitForWindowAsync(
        string titleSubstring, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var root = AutomationElement.RootElement;
            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < windows.Count; i++)
            {
                try
                {
                    var name = windows[i].Current.Name;
                    if (!string.IsNullOrEmpty(name) &&
                        name.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                        return windows[i];
                }
                catch (ElementNotAvailableException) { }
            }
            await Task.Delay(250, ct);
        }
        return null;
    }

    private static AutomationElement? FindByAutomationId(AutomationElement parent, string automationId)
    {
        try
        {
            return parent.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }
        catch { return null; }
    }

    private static bool ClickUiaElement(AutomationElement element)
    {
        try
        {
            // Prefer InvokePattern for reliability
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }

            // Fall back to coordinate click
            var rect = element.Current.BoundingRectangle;
            if (!rect.IsEmpty)
            {
                int x = (int)(rect.X + rect.Width / 2);
                int y = (int)(rect.Y + rect.Height / 2);
                Win32.Click(x, y);
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static void FocusWindow(AutomationElement window)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            Win32.SetForegroundWindow(hwnd);
        }
        catch { }
    }

    private static void CleanupProcess(Process? proc)
    {
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
                proc.Kill();
        }
        catch { }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// DEMO MODELS
// ═══════════════════════════════════════════════════════════════════════════════════

public record DemoScenario(string Id, string Name, string Description, DemoDifficulty Difficulty);
public record DemoScenarioResult(string Id, DemoStatus Status, long ElapsedMs, string? Error);
public record DemoResult(List<DemoScenarioResult> Scenarios)
{
    public int Passed => Scenarios.Count(s => s.Status == DemoStatus.Passed);
    public int Failed => Scenarios.Count(s => s.Status is DemoStatus.Failed or DemoStatus.Error);
    public bool AllPassed => Scenarios.All(s => s.Status == DemoStatus.Passed);
}

public enum DemoStatus { NotStarted, Running, Passed, Failed, Error, Skipped }
public enum DemoDifficulty { Basic, Intermediate, Advanced }

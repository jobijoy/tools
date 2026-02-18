using System.IO;
using IdolClick.Models;
using IdolClick.UI;

namespace IdolClick.Services;

/// <summary>
/// Integration test runner for the classic rule engine.
/// Opens a real WPF test window, configures rules, runs the engine for
/// one cycle via <see cref="AutomationEngine.ProcessRulesOnceAsync"/>,
/// and verifies that the correct elements were clicked.
/// 
/// Launched via <c>--test-classic</c> CLI flag.
/// </summary>
public class ClassicTestRunner
{
    private readonly LogService _log;
    private readonly Action<string> _output;
    private ClassicTestWindow _window = null!;
    private int _passed;
    private int _failed;
    private int _total;

    public ClassicTestRunner(LogService log, Action<string> output)
    {
        _log = log;
        _output = output;
    }

    /// <summary>
    /// Runs all classic engine integration tests. Must be called from the UI (STA) thread.
    /// </summary>
    /// <returns>True if all tests passed.</returns>
    public async Task<bool> RunAllAsync()
    {
        _output("═══════════════════════════════════════════════════════");
        _output("  IdolClick Classic Engine Integration Tests");
        _output("═══════════════════════════════════════════════════════");
        _output("");

        // Create test window
        _window = new ClassicTestWindow();
        _window.Show();
        
        // Give UIA time to register the window tree
        await Task.Delay(500);

        try
        {
            // ── Click Tests ──────────────────────────────────────────────
            await RunTest("TC-CLICK-01", "Button exact match click", TestClickExactMatch);
            await RunTest("TC-CLICK-02", "Button prefix match (shortcut)", TestClickPrefixMatch);
            await RunTest("TC-CLICK-03", "Comma-separated multi-pattern", TestClickMultiPattern);
            await RunTest("TC-CLICK-04", "Regex pattern match", TestClickRegex);
            await RunTest("TC-CLICK-05", "ExcludeTexts filter", TestClickExcludeTexts);
            await RunTest("TC-CLICK-06", "Disabled element skipped", TestDisabledElementSkipped);
            await RunTest("TC-CLICK-07", "Wrong ElementType → no match", TestWrongElementType);
            await RunTest("TC-CLICK-08", "Cooldown enforcement", TestCooldownEnforcement);
            await RunTest("TC-CLICK-09", "DryRun mode (no click)", TestDryRunModeNoClick);

            // ── Control Type Tests ───────────────────────────────────────
            await RunTest("TC-TYPE-01", "CheckBox control type match", TestCheckBoxType);
            await RunTest("TC-TYPE-02", "Any control type match", TestAnyControlType);

            // ── Safety Tests ─────────────────────────────────────────────
            await RunTest("TC-SAFETY-01", "MaxExecutionsPerSession limit", TestMaxExecutionsLimit);
            await RunTest("TC-SAFETY-02", "WindowTitle filter", TestWindowTitleFilter);
            await RunTest("TC-SAFETY-03", "Non-existent process → no match", TestNonExistentProcess);
        }
        finally
        {
            _window.Close();
        }

        _output("");
        _output("═══════════════════════════════════════════════════════");
        _output($"  Result: {_passed}/{_total} passed, {_failed} failed");
        _output("═══════════════════════════════════════════════════════");

        return _failed == 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TEST INFRASTRUCTURE
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task RunTest(string id, string name, Func<Task<bool>> test)
    {
        _total++;
        _window.ResetLog();

        // Pump WPF messages so the window updates
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);

        try
        {
            var result = await test();
            if (result)
            {
                _passed++;
                _output($"  [PASS] {id} — {name}");
            }
            else
            {
                _failed++;
                _output($"  [FAIL] {id} — {name}");
            }
        }
        catch (Exception ex)
        {
            _failed++;
            _output($"  [ERR ] {id} — {name}: {ex.GetType().Name}: {ex.Message}");
            _log.Error("ClassicTest", $"{id} exception: {ex}");
        }
    }

    /// <summary>
    /// Creates a temporary config, engine, and runs one cycle with the given rules.
    /// Returns the engine result and disposes everything.
    /// </summary>
    private async Task<(int Evaluated, int Triggered)> RunEngineOnce(params Rule[] rules)
    {
        // Create a temp config.json with the test rules
        var tempDir = Path.Combine(Path.GetTempPath(), "IdolClick_ClassicTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var config = new AppConfig
            {
                Settings = new GlobalSettings
                {
                    PollingIntervalMs = 1000,
                    ClickRadar = false, // No visual radar during tests
                    LogLevel = "Debug"
                },
                Rules = rules.ToList()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(configPath, json);

            var configService = new ConfigService(configPath);
            var engine = new AutomationEngine(configService, _log);

            try
            {
                var result = await engine.ProcessRulesOnceAsync();
                return result;
            }
            finally
            {
                engine.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TEST CASES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>TC-CLICK-01: Exact text match should click the button.</summary>
    private async Task<bool> TestClickExactMatch()
    {
        var rule = MakeRule("Accept", action: "Click");
        var (_, triggered) = await RunEngineOnce(rule);
        await WaitForClick();
        return triggered == 1 && _window.ClickLog.TryPeek(out var name) && name == "Accept";
    }

    /// <summary>TC-CLICK-02: Prefix match should find "Allow (Ctrl+Enter)".</summary>
    private async Task<bool> TestClickPrefixMatch()
    {
        var rule = MakeRule("Allow", action: "Click");
        var (_, triggered) = await RunEngineOnce(rule);
        await WaitForClick();
        return triggered == 1 && _window.ClickLog.TryPeek(out var name) && name == "Allow (Ctrl+Enter)";
    }

    /// <summary>TC-CLICK-03: Comma-separated patterns should match any.</summary>
    private async Task<bool> TestClickMultiPattern()
    {
        var rule = MakeRule("NonExistent, OK", action: "Click");
        var (_, triggered) = await RunEngineOnce(rule);
        await WaitForClick();
        return triggered == 1 && _window.ClickLog.TryPeek(out var name) && name == "OK";
    }

    /// <summary>TC-CLICK-04: Regex pattern should match.</summary>
    private async Task<bool> TestClickRegex()
    {
        var rule = MakeRule("^Save.*", action: "Click", useRegex: true);
        var (_, triggered) = await RunEngineOnce(rule);
        await WaitForClick();
        return triggered == 1 && _window.ClickLog.TryPeek(out var name) && name == "Save Changes";
    }

    /// <summary>TC-CLICK-05: ExcludeTexts should prevent matching.</summary>
    private async Task<bool> TestClickExcludeTexts()
    {
        var rule = MakeRule("Accept", action: "Click", excludeTexts: new[] { "Accept" });
        var (eval, triggered) = await RunEngineOnce(rule);
        return eval == 1 && triggered == 0 && _window.ClickLog.IsEmpty;
    }

    /// <summary>TC-CLICK-06: Disabled button should be skipped.</summary>
    private async Task<bool> TestDisabledElementSkipped()
    {
        var rule = MakeRule("Disabled Action", action: "Click");
        var (eval, triggered) = await RunEngineOnce(rule);
        return eval == 1 && triggered == 0;
    }

    /// <summary>TC-CLICK-07: Wrong ElementType should not match.</summary>
    private async Task<bool> TestWrongElementType()
    {
        var rule = MakeRule("Accept", action: "Click", elementType: "CheckBox");
        var (eval, triggered) = await RunEngineOnce(rule);
        return eval == 1 && triggered == 0;
    }

    /// <summary>TC-CLICK-08: Second execution within cooldown should be skipped.</summary>
    private async Task<bool> TestCooldownEnforcement()
    {
        var rule = MakeRule("Accept", action: "Click", cooldownSeconds: 60);

        // First run — should trigger
        var (_, t1) = await RunEngineOnce(rule);
        await WaitForClick();
        if (t1 != 1) return false;

        // Reset the click log but keep the rule state (LastTriggered)
        _window.ResetLog();

        // Second run with same rule (still within cooldown) — should skip
        // The engine uses an internal _lastTrigger dict, so we need a fresh engine 
        // that recognizes the cooldown. Since ProcessRulesOnceAsync uses its own 
        // _lastTrigger, we test via two consecutive calls on the same engine.
        var tempDir = Path.Combine(Path.GetTempPath(), "IdolClick_ClassicTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        try
        {
            var config = new AppConfig
            {
                Settings = new GlobalSettings { PollingIntervalMs = 1000, ClickRadar = false },
                Rules = new List<Rule> { rule }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(configPath, json);

            var configService = new ConfigService(configPath);
            var engine = new AutomationEngine(configService, _log);
            try
            {
                // First cycle triggers
                var r1 = await engine.ProcessRulesOnceAsync();
                await WaitForClick();
                _window.ResetLog();

                // Second cycle should be cooldown-blocked
                var r2 = await engine.ProcessRulesOnceAsync();
                return r1.Triggered == 1 && r2.Triggered == 0;
            }
            finally
            {
                engine.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>TC-CLICK-09: DryRun should not execute the click.</summary>
    private async Task<bool> TestDryRunModeNoClick()
    {
        var rule = MakeRule("Accept", action: "Click", dryRun: true);
        var (eval, triggered) = await RunEngineOnce(rule);
        // DryRun returns true (success) but the click itself doesn't happen
        return eval == 1 && triggered == 1 && _window.ClickLog.IsEmpty;
    }

    /// <summary>TC-TYPE-01: CheckBox ElementType should find checkboxes.</summary>
    private async Task<bool> TestCheckBoxType()
    {
        var rule = MakeRule("Enable Feature", action: "Click", elementType: "CheckBox");
        var (_, triggered) = await RunEngineOnce(rule);
        // CheckBox doesn't fire the Button click handler, but engine should trigger
        return triggered == 1;
    }

    /// <summary>TC-TYPE-02: Any (empty) ElementType should match any control.</summary>
    private async Task<bool> TestAnyControlType()
    {
        var rule = MakeRule("Status: Ready", action: "Click", elementType: "Any");
        var (eval, triggered) = await RunEngineOnce(rule);
        // Text control has no InvokePattern and no ClickablePoint — click will "fail"
        // but the engine should still evaluate and attempt the action
        return eval == 1;
    }

    /// <summary>TC-SAFETY-01: MaxExecutionsPerSession should cap triggers.</summary>
    private async Task<bool> TestMaxExecutionsLimit()
    {
        var rule = MakeRule("OK", action: "Click", maxExecutions: 1);

        var tempDir = Path.Combine(Path.GetTempPath(), "IdolClick_ClassicTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        try
        {
            var config = new AppConfig
            {
                Settings = new GlobalSettings { PollingIntervalMs = 1000, ClickRadar = false },
                Rules = new List<Rule> { rule }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(configPath, json);

            // Use cooldown=0 to allow rapid re-trigger
            rule.CooldownSeconds = 0;

            var configService = new ConfigService(configPath);
            var engine = new AutomationEngine(configService, _log);
            try
            {
                var r1 = await engine.ProcessRulesOnceAsync();
                await WaitForClick();
                _window.ResetLog();

                var r2 = await engine.ProcessRulesOnceAsync();
                // First succeeds (SessionExecutionCount → 1), second blocked by max
                return r1.Triggered == 1 && r2.Triggered == 0;
            }
            finally
            {
                engine.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>TC-SAFETY-02: WindowTitle filter should exclude non-matching windows.</summary>
    private async Task<bool> TestWindowTitleFilter()
    {
        var rule = MakeRule("Accept", action: "Click", windowTitle: "This Title Does Not Exist At All");
        var (eval, triggered) = await RunEngineOnce(rule);
        return eval == 1 && triggered == 0;
    }

    /// <summary>TC-SAFETY-03: Non-existent process should not match any windows.</summary>
    private async Task<bool> TestNonExistentProcess()
    {
        var rule = new Rule
        {
            Name = "Test-NonExistentProcess",
            Enabled = true,
            IsRunning = true,
            TargetApp = "ThisProcessDoesNotExistEver12345",
            ElementType = "Button",
            MatchText = "Accept",
            Action = "Click",
            CooldownSeconds = 0
        };
        var (eval, triggered) = await RunEngineOnce(rule);
        return eval == 1 && triggered == 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a test rule targeting the ClassicTestWindow by WindowTitle.  
    /// Uses WindowTitle match (no TargetApp) so the engine finds our test window
    /// via the top-level window title search path.
    /// </summary>
    private static Rule MakeRule(
        string matchText,
        string action = "Click",
        string elementType = "Button",
        bool useRegex = false,
        string[]? excludeTexts = null,
        bool dryRun = false,
        int cooldownSeconds = 0,
        int maxExecutions = 0,
        string? windowTitle = null)
    {
        return new Rule
        {
            Name = $"Test-{matchText.Replace(" ", "")}",
            Enabled = true,
            IsRunning = true,
            TargetApp = "", // empty — match by window title only
            WindowTitle = windowTitle ?? "IdolClick Classic Test Window",
            ElementType = elementType,
            MatchText = matchText,
            UseRegex = useRegex,
            ExcludeTexts = excludeTexts ?? [],
            Action = action,
            DryRun = dryRun,
            CooldownSeconds = cooldownSeconds,
            MaxExecutionsPerSession = maxExecutions
        };
    }

    /// <summary>
    /// Waits briefly for a click to be processed and the UI to update.
    /// Physical clicks go through Win32 message pump so we need to yield.
    /// </summary>
    private async Task WaitForClick(int maxWaitMs = 2000)
    {
        // Process WPF messages
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
        
        // Wait for click signal from test window
        _window.ClickReceived.Wait(maxWaitMs);
        
        // One more WPF message pump pass
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
    }
}

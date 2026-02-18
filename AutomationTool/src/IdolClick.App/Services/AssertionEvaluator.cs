using System.Diagnostics;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// ASSERTION EVALUATOR — Evaluates post-step assertions against live UI state.
//
// Layer 2 of the 3-layer execution architecture:
//   FlowActionExecutor → AssertionEvaluator → StepExecutor
//
// Each assertion type maps to a concrete evaluation. No free-form strings.
// Supports retry with timeout for assertions that depend on async UI updates.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Evaluates assertions against live UI state.
/// </summary>
public interface IAssertionEvaluator
{
    /// <summary>
    /// Evaluates a single assertion with retry/timeout (async — uses non-blocking waits).
    /// </summary>
    Task<AssertionResult> EvaluateAsync(Assertion assertion, AutomationElement? window, SelectorParser selectorParser, CancellationToken ct = default);
}

/// <summary>
/// Evaluates post-step assertions against live Windows UI Automation state.
/// </summary>
public class AssertionEvaluator : IAssertionEvaluator
{
    private readonly LogService _log;

    public AssertionEvaluator(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<AssertionResult> EvaluateAsync(Assertion assertion, AutomationElement? window, SelectorParser selectorParser, CancellationToken ct = default)
    {
        try
        {
            return assertion.Type switch
            {
                AssertionType.Exists => await EvaluateExistsAsync(assertion, window, selectorParser, ct).ConfigureAwait(false),
                AssertionType.NotExists => await EvaluateNotExistsAsync(assertion, window, selectorParser).ConfigureAwait(false),
                AssertionType.TextContains => await EvaluateTextContainsAsync(assertion, window, selectorParser, ct).ConfigureAwait(false),
                AssertionType.TextEquals => await EvaluateTextEqualsAsync(assertion, window, selectorParser, ct).ConfigureAwait(false),
                AssertionType.WindowTitle => EvaluateWindowTitle(assertion, window),
                AssertionType.ProcessRunning => EvaluateProcessRunning(assertion),
                _ => new AssertionResult
                {
                    Type = assertion.Type,
                    Passed = false,
                    Error = $"Unknown assertion type: {assertion.Type}"
                }
            };
        }
        catch (Exception ex)
        {
            _log.Error("Assertion", $"Evaluation error for {assertion.Type}: {ex.Message}");
            return new AssertionResult
            {
                Type = assertion.Type,
                Passed = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Description = assertion.Description
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ASSERTION IMPLEMENTATIONS — ASYNC (non-blocking waits)
    // ═══════════════════════════════════════════════════════════════════════════════

    private async Task<AssertionResult> EvaluateExistsAsync(Assertion assertion, AutomationElement? window, SelectorParser parser, CancellationToken ct)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Fail(assertion, "exists", "no window/selector");

        var match = await parser.ResolveAsync(window, assertion.Selector, assertion.TimeoutMs, ct: ct).ConfigureAwait(false);

        return match != null
            ? Pass(assertion, "exists", "found")
            : Fail(assertion, "exists", "not found");
    }

    private Task<AssertionResult> EvaluateNotExistsAsync(Assertion assertion, AutomationElement? window, SelectorParser parser)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Task.FromResult(Pass(assertion, "not exists", "no window/selector"));

        var match = parser.ResolveOnce(window, assertion.Selector);

        return Task.FromResult(match == null
            ? Pass(assertion, "not exists", "confirmed absent")
            : Fail(assertion, "not exists", "still present"));
    }

    private async Task<AssertionResult> EvaluateTextContainsAsync(Assertion assertion, AutomationElement? window, SelectorParser parser, CancellationToken ct)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Fail(assertion, assertion.Expected ?? "", "no window/selector");

        var match = await parser.ResolveAsync(window, assertion.Selector, assertion.TimeoutMs, ct: ct).ConfigureAwait(false);
        if (match == null)
            return Fail(assertion, assertion.Expected ?? "", $"element '{assertion.Selector}' not found");

        var actual = SelectorParser.GetElementText(match.Element);
        var expected = assertion.Expected ?? "";

        bool contains = actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
        return contains
            ? Pass(assertion, expected, actual)
            : Fail(assertion, expected, actual, $"Text '{actual}' does not contain '{expected}'");
    }

    private async Task<AssertionResult> EvaluateTextEqualsAsync(Assertion assertion, AutomationElement? window, SelectorParser parser, CancellationToken ct)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Fail(assertion, assertion.Expected ?? "", "no window/selector");

        var match = await parser.ResolveAsync(window, assertion.Selector, assertion.TimeoutMs, ct: ct).ConfigureAwait(false);
        if (match == null)
            return Fail(assertion, assertion.Expected ?? "", $"element '{assertion.Selector}' not found");

        var actual = SelectorParser.GetElementText(match.Element);
        var expected = assertion.Expected ?? "";

        bool equals = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        return equals
            ? Pass(assertion, expected, actual)
            : Fail(assertion, expected, actual, $"Text '{actual}' does not equal '{expected}'");
    }

    private AssertionResult EvaluateWindowTitle(Assertion assertion, AutomationElement? window)
    {
        if (window == null)
            return Fail(assertion, assertion.Expected ?? "", "no window context");

        string actual;
        try { actual = window.Current.Name ?? ""; }
        catch { actual = ""; }

        var expected = assertion.Expected ?? "";
        bool match = assertion.Exact
            ? actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            : actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

        return match
            ? Pass(assertion, expected, actual)
            : Fail(assertion, expected, actual, $"Window title '{actual}' does not match '{expected}'");
    }

    private AssertionResult EvaluateProcessRunning(Assertion assertion)
    {
        var processName = assertion.Expected ?? "";
        if (string.IsNullOrWhiteSpace(processName))
            return Fail(assertion, "", "no process name specified");

        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch { procs = []; }

        bool running = procs.Length > 0;

        // Dispose handles
        foreach (var p in procs)
            p.Dispose();

        return running
            ? Pass(assertion, processName, $"{procs.Length} instance(s)")
            : Fail(assertion, processName, "not running");
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    private static AssertionResult Pass(Assertion assertion, string expected, string actual)
    {
        return new AssertionResult
        {
            Type = assertion.Type,
            Passed = true,
            Expected = expected,
            Actual = actual,
            Description = assertion.Description
        };
    }

    private static AssertionResult Fail(Assertion assertion, string expected, string actual, string? error = null)
    {
        return new AssertionResult
        {
            Type = assertion.Type,
            Passed = false,
            Expected = expected,
            Actual = actual,
            Error = error ?? $"{assertion.Type} assertion failed: expected '{expected}', got '{actual}'",
            Description = assertion.Description
        };
    }
}

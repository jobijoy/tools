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
    /// Evaluates a single assertion with retry/timeout.
    /// </summary>
    /// <param name="assertion">The assertion to evaluate.</param>
    /// <param name="window">The window context for element lookups.</param>
    /// <param name="selectorParser">Selector parser for element resolution.</param>
    /// <returns>Structured assertion result.</returns>
    AssertionResult Evaluate(Assertion assertion, AutomationElement? window, SelectorParser selectorParser);
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

    public AssertionResult Evaluate(Assertion assertion, AutomationElement? window, SelectorParser selectorParser)
    {
        try
        {
            return assertion.Type switch
            {
                AssertionType.Exists => EvaluateExists(assertion, window, selectorParser),
                AssertionType.NotExists => EvaluateNotExists(assertion, window, selectorParser),
                AssertionType.TextContains => EvaluateTextContains(assertion, window, selectorParser),
                AssertionType.TextEquals => EvaluateTextEquals(assertion, window, selectorParser),
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
    // ASSERTION IMPLEMENTATIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    private AssertionResult EvaluateExists(Assertion assertion, AutomationElement? window, SelectorParser parser)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Fail(assertion, "exists", "no window/selector");

        var match = RetryResolve(parser, window, assertion.Selector, assertion.TimeoutMs);

        return match != null
            ? Pass(assertion, "exists", "found")
            : Fail(assertion, "exists", "not found");
    }

    private AssertionResult EvaluateNotExists(Assertion assertion, AutomationElement? window, SelectorParser parser)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Pass(assertion, "not exists", "no window/selector");

        // For "not exists", we do a single check (no retry — we expect it to be gone)
        var match = parser.ResolveOnce(window, assertion.Selector);

        return match == null
            ? Pass(assertion, "not exists", "confirmed absent")
            : Fail(assertion, "not exists", "still present");
    }

    private AssertionResult EvaluateTextContains(Assertion assertion, AutomationElement? window, SelectorParser parser)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Fail(assertion, assertion.Expected ?? "", "no window/selector");

        var match = RetryResolve(parser, window, assertion.Selector, assertion.TimeoutMs);
        if (match == null)
            return Fail(assertion, assertion.Expected ?? "", $"element '{assertion.Selector}' not found");

        var actual = SelectorParser.GetElementText(match.Element);
        var expected = assertion.Expected ?? "";

        bool contains = actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
        return contains
            ? Pass(assertion, expected, actual)
            : Fail(assertion, expected, actual, $"Text '{actual}' does not contain '{expected}'");
    }

    private AssertionResult EvaluateTextEquals(Assertion assertion, AutomationElement? window, SelectorParser parser)
    {
        if (window == null || string.IsNullOrWhiteSpace(assertion.Selector))
            return Fail(assertion, assertion.Expected ?? "", "no window/selector");

        var match = RetryResolve(parser, window, assertion.Selector, assertion.TimeoutMs);
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

    private static SelectorMatch? RetryResolve(SelectorParser parser, AutomationElement window, string selector, int timeoutMs)
    {
        return parser.Resolve(window, selector, timeoutMs, out _);
    }

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

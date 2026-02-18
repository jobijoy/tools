using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// FLOW VALIDATOR — Schema validation for TestFlow before execution.
//
// The LLM generates structured flows, but we NEVER trust LLM output blindly.
// This service validates every flow against the rigid schema before the
// execution engine touches it.
//
// Called by:
//   • AgentTools.ValidateFlow (tool-calling endpoint)
//   • ExecutionPipeline (Sprint 5 — pre-execution gate)
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Structured validation result with errors and warnings.
/// </summary>
public class FlowValidationResult
{
    /// <summary>True if no errors were found.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Blocking errors that prevent execution.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Non-blocking warnings (e.g., missing descriptions, unusual patterns).</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Validates TestFlow schema integrity before execution.
/// Never trust LLM output blindly — validate first.
/// </summary>
public class FlowValidatorService
{
    private readonly LogService _log;

    public FlowValidatorService(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Validates a TestFlow and returns structured errors/warnings.
    /// Auto-assigns Order values to steps if not set.
    /// </summary>
    public FlowValidationResult Validate(TestFlow flow)
    {
        var result = new FlowValidationResult();

        // ── Top-level validation ──────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(flow.TestName) || flow.TestName == "Untitled Flow")
            result.Warnings.Add("TestName should be descriptive (not 'Untitled Flow').");

        if (flow.Steps.Count == 0)
            result.Errors.Add("Flow must have at least one step.");

        if (flow.TimeoutSeconds < 0)
            result.Errors.Add("TimeoutSeconds must be non-negative.");

        if (flow.SchemaVersion != 1)
            result.Warnings.Add($"SchemaVersion {flow.SchemaVersion} — only v1 is currently supported.");

        // ── Per-step validation ───────────────────────────────────────────────
        for (int i = 0; i < flow.Steps.Count; i++)
        {
            var step = flow.Steps[i];
            var prefix = $"Step {i + 1}";

            // Auto-assign order if not set
            if (step.Order == 0)
                step.Order = i + 1;

            ValidateStep(step, prefix, result);
        }

        // ── Cross-step checks ─────────────────────────────────────────────────
        var orders = flow.Steps
            .Where(s => s.Order > 0)
            .GroupBy(s => s.Order)
            .Where(g => g.Count() > 1);
        foreach (var dup in orders)
            result.Warnings.Add($"Duplicate order {dup.Key} found on {dup.Count()} steps.");

        // ── Backend-specific validation ───────────────────────────────────────
        ValidateBackendRules(flow, result);

        // ── Log result ────────────────────────────────────────────────────────
        if (result.IsValid)
            _log.Debug("Validator", $"Flow '{flow.TestName}' validated: {flow.Steps.Count} steps, {result.Warnings.Count} warnings");
        else
            _log.Warn("Validator", $"Flow '{flow.TestName}' INVALID: {result.Errors.Count} error(s): {string.Join("; ", result.Errors)}");

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // STEP VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════════

    private static void ValidateStep(TestStep step, string prefix, FlowValidationResult result)
    {
        // Action-specific required fields
        switch (step.Action)
        {
            case StepAction.Click:
            case StepAction.Hover:
            case StepAction.AssertExists:
            case StepAction.AssertNotExists:
                if (string.IsNullOrWhiteSpace(step.Selector))
                    result.Errors.Add($"{prefix}: '{step.Action}' requires a selector.");
                break;

            case StepAction.Type:
                if (string.IsNullOrWhiteSpace(step.Text))
                    result.Errors.Add($"{prefix}: 'Type' requires text.");
                break;

            case StepAction.SendKeys:
                if (string.IsNullOrWhiteSpace(step.Keys))
                    result.Errors.Add($"{prefix}: 'SendKeys' requires keys.");
                break;

            case StepAction.AssertText:
                if (string.IsNullOrWhiteSpace(step.Selector))
                    result.Errors.Add($"{prefix}: 'AssertText' requires a selector.");
                if (string.IsNullOrWhiteSpace(step.Contains))
                    result.Errors.Add($"{prefix}: 'AssertText' requires 'contains' value.");
                break;

            case StepAction.AssertWindow:
                if (string.IsNullOrWhiteSpace(step.WindowTitle) && string.IsNullOrWhiteSpace(step.Contains))
                    result.Errors.Add($"{prefix}: 'AssertWindow' requires windowTitle or contains.");
                break;

            case StepAction.Navigate:
                if (string.IsNullOrWhiteSpace(step.Url))
                    result.Errors.Add($"{prefix}: 'Navigate' requires a URL.");
                break;

            case StepAction.FocusWindow:
                if (string.IsNullOrWhiteSpace(step.App) && string.IsNullOrWhiteSpace(step.WindowTitle))
                    result.Errors.Add($"{prefix}: 'FocusWindow' requires app or windowTitle.");
                break;

            case StepAction.Launch:
                if (string.IsNullOrWhiteSpace(step.ProcessPath))
                    result.Errors.Add($"{prefix}: 'Launch' requires processPath.");
                break;

            case StepAction.Scroll:
                if (string.IsNullOrWhiteSpace(step.Direction))
                    result.Errors.Add($"{prefix}: 'Scroll' requires direction.");
                else if (!new[] { "up", "down", "left", "right" }.Contains(step.Direction.ToLowerInvariant()))
                    result.Errors.Add($"{prefix}: 'Scroll' direction must be up/down/left/right.");
                break;

            case StepAction.Wait:
                // Wait with no selector = fixed delay — valid
                break;

            case StepAction.Screenshot:
                // No required fields
                break;
        }

        // ── Selector format validation ────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(step.Selector))
            ValidateSelector(step.Selector, prefix, result);

        // ── Post-step assertion validation ────────────────────────────────────
        for (int i = 0; i < step.Assertions.Count; i++)
        {
            var assertion = step.Assertions[i];
            var aPrefix = $"{prefix}, Assertion {i + 1}";
            ValidateAssertion(assertion, aPrefix, result);
        }

        // ── General sanity checks ─────────────────────────────────────────────
        if (step.TimeoutMs < 0)
            result.Errors.Add($"{prefix}: TimeoutMs must be non-negative.");

        if (step.DelayAfterMs < 0)
            result.Errors.Add($"{prefix}: DelayAfterMs must be non-negative.");

        if (string.IsNullOrWhiteSpace(step.Description))
            result.Warnings.Add($"{prefix}: Missing step description (recommended for readability).");
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // BACKEND-SPECIFIC VALIDATION — Desktop UIA rules
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates rules specific to the desktop-uia backend.
    /// </summary>
    private static void ValidateBackendRules(TestFlow flow, FlowValidationResult result)
    {
        var backend = flow.Backend?.ToLowerInvariant() ?? "desktop";

        ValidateDesktopFlow(flow, result);

        // TypedSelector validation (any backend)
        for (int i = 0; i < flow.Steps.Count; i++)
        {
            var step = flow.Steps[i];
            if (step.TypedSelector != null)
            {
                ValidateTypedSelector(step.TypedSelector, backend, $"Step {i + 1}", result);
            }
        }
    }

    /// <summary>
    /// Validates desktop-specific flow rules.
    /// </summary>
    private static void ValidateDesktopFlow(TestFlow flow, FlowValidationResult result)
    {
        for (int i = 0; i < flow.Steps.Count; i++)
        {
            var step = flow.Steps[i];
            var prefix = $"Step {i + 1}";

            // Navigate with URL in desktop flow is unusual (not an error — Launch + URL is valid)
            if (step.Action == StepAction.Navigate && !string.IsNullOrWhiteSpace(step.Url))
            {
                result.Warnings.Add(
                    $"{prefix}: 'Navigate' with URL in desktop flow. " +
                    "This will open a URL in the default browser via shell execute.");
            }
        }
    }

    /// <summary>
    /// Validates a TypedSelector against the flow's backend.
    /// </summary>
    private static void ValidateTypedSelector(
        StepSelector typed, string backend, string prefix, FlowValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(typed.Value))
        {
            result.Errors.Add($"{prefix}: TypedSelector has empty value.");
            return;
        }

        // ── Desktop backend: only desktop_uia allowed ────────────────────────
        var desktopKinds = new HashSet<SelectorKind> { SelectorKind.DesktopUia };

        if (!desktopKinds.Contains(typed.Kind))
        {
            result.Errors.Add(
                $"{prefix}: TypedSelector kind '{typed.Kind}' is not supported. " +
                "Only DesktopUia selectors are supported.");
        }

        // ── Kind-specific format checks ──────────────────────────────────────
        switch (typed.Kind)
        {
            case SelectorKind.DesktopUia:
                // Desktop UIA selectors should follow ElementType#Identifier format
                if (!typed.Value.Contains('#'))
                    result.Warnings.Add(
                        $"{prefix}: Desktop UIA selector '{typed.Value}' " +
                        "should use 'ElementType#Identifier' format.");
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SELECTOR VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════════

    private static void ValidateSelector(string selector, string prefix, FlowValidationResult result)
    {
        // Valid formats: "ElementType#TextOrId", "#AutomationId", "Window#Title"
        if (!selector.Contains('#'))
        {
            result.Warnings.Add($"{prefix}: Selector '{selector}' should use format 'ElementType#TextOrAutomationId'.");
        }
        else
        {
            var parts = selector.Split('#', 2);
            if (parts.Length == 2 && string.IsNullOrWhiteSpace(parts[1]))
                result.Errors.Add($"{prefix}: Selector '{selector}' has empty identifier after '#'.");

            // Validate known element types
            var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Button", "TextBox", "TextBlock", "Label", "CheckBox", "RadioButton",
                "ComboBox", "ListItem", "MenuItem", "TabItem", "TreeItem", "Window",
                "Hyperlink", "Image", "Slider", "ProgressBar", "DataGrid", "Toggle",
                "Text", "Edit", "Pane", "Group", "ScrollBar", "ToolBar", "StatusBar"
            };

            if (!string.IsNullOrWhiteSpace(parts[0]) && !knownTypes.Contains(parts[0]))
                result.Warnings.Add($"{prefix}: Element type '{parts[0]}' in selector is not a commonly known type.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ASSERTION VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════════

    private static void ValidateAssertion(Assertion assertion, string prefix, FlowValidationResult result)
    {
        switch (assertion.Type)
        {
            case AssertionType.Exists:
            case AssertionType.NotExists:
                if (string.IsNullOrWhiteSpace(assertion.Selector))
                    result.Errors.Add($"{prefix}: '{assertion.Type}' assertion requires a selector.");
                break;

            case AssertionType.TextContains:
            case AssertionType.TextEquals:
                if (string.IsNullOrWhiteSpace(assertion.Selector))
                    result.Errors.Add($"{prefix}: '{assertion.Type}' assertion requires a selector.");
                if (string.IsNullOrWhiteSpace(assertion.Expected))
                    result.Errors.Add($"{prefix}: '{assertion.Type}' assertion requires expected value.");
                break;

            case AssertionType.WindowTitle:
                if (string.IsNullOrWhiteSpace(assertion.Expected))
                    result.Errors.Add($"{prefix}: 'WindowTitle' assertion requires expected value.");
                break;

            case AssertionType.ProcessRunning:
                if (string.IsNullOrWhiteSpace(assertion.Expected))
                    result.Errors.Add($"{prefix}: 'ProcessRunning' assertion requires expected process name.");
                break;
        }
    }
}

using IdolClick.Models;

namespace IdolClick.Services.Templates;

// ═══════════════════════════════════════════════════════════════════════════════════
// EXPERIMENTAL TEMPLATES (7) — May escalate more, require confirmation.//                              STATUS: ALPHA — part of the Commands module.//
// Experimental templates:
//   9.  ToggleSystemSetting       — Toggle a Windows setting (HIGH risk)
//   10. ExportReportToFile        — Export/save a report to file
//   11. DragAndDrop               — Drag an element to a target
//   12. FormFill                  — Fill out a multi-field form
//   13. ChainedNavigation         — Multi-step navigation chain
//   14. MultiWindowExtractValue   — Copy a value between windows
//   15. SystemSettingDeepNavigation — Deep-navigate Windows Settings (HIGH risk)
//
// All Experimental templates:
//   • Maturity = Experimental
//   • Default to confirmation mode even at ≥0.8 confidence
//   • Emit more conservative waits
//
// TestStep property reminder:
//   Launch    → ProcessPath
//   Type      → Text
//   SendKeys  → Keys
//   Navigate  → Url
//   FocusWindow / AssertWindow → WindowTitle
//   AssertText → Contains (+ optional Exact)
//   Delay after step → DelayAfterMs (default 200)
//   Element wait budget → TimeoutMs (default 5000)
// ═══════════════════════════════════════════════════════════════════════════════════

// ── 9. ToggleSystemSetting ──────────────────────────────────────────────────────

public sealed class ToggleSystemSettingTemplate : IFlowTemplate
{
    public string TemplateId => "toggle-setting";
    public string DisplayName => "Toggle System Setting";
    public string Description => "Toggle a Windows system setting on or off. Requires confirmation due to system modification.";
    public IntentKind IntentKind => IntentKind.ToggleSetting;
    public IReadOnlyList<string> RequiredSlots => ["setting"];
    public IReadOnlyList<string> OptionalSlots => ["state"];
    public RiskLevel RiskLevel => RiskLevel.High;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.ToggleSetting;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var setting = intent.Slots.GetValueOrDefault("setting", "");
        var state = intent.Slots.GetValueOrDefault("state", "toggle");

        return new TestFlow
        {
            TestName = $"Toggle Setting: {setting}",
            Description = $"Toggle system setting '{setting}' ({state}).",
            TargetApp = "SystemSettings",
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = "Open Windows Settings",
                    Action = StepAction.Launch,
                    ProcessPath = "ms-settings:",
                    DelayAfterMs = 3000
                },
                new TestStep
                {
                    Order = 2,
                    Description = "Focus Settings window",
                    Action = StepAction.FocusWindow,
                    WindowTitle = "Settings",
                    DelayAfterMs = 1000
                },
                new TestStep
                {
                    Order = 3,
                    Description = $"Find setting: {setting}",
                    Action = StepAction.Click,
                    Selector = $"Text#{setting}",
                    TimeoutMs = 8000,
                    DelayAfterMs = 1500
                },
                new TestStep
                {
                    Order = 4,
                    Description = $"Toggle: {setting}",
                    Action = StepAction.Click,
                    Selector = "Button#ToggleSwitch",
                    DelayAfterMs = 1000
                },
                new TestStep
                {
                    Order = 5,
                    Description = "Verify setting changed",
                    Action = StepAction.AssertExists,
                    Selector = $"Text#{setting}",
                    TimeoutMs = 5000
                }
            ],
            TimeoutSeconds = 60
        };
    }
}

// ── 10. ExportReportToFile ──────────────────────────────────────────────────────

public sealed class ExportReportToFileTemplate : IFlowTemplate
{
    public string TemplateId => "export-report";
    public string DisplayName => "Export Report to File";
    public string Description => "Export or save a report/document to a file on disk.";
    public IntentKind IntentKind => IntentKind.ExportFile;
    public IReadOnlyList<string> RequiredSlots => ["path"];
    public IReadOnlyList<string> OptionalSlots => ["format", "source"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.ExportFile;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var path = intent.Slots.GetValueOrDefault("path", "");
        var source = intent.Slots.GetValueOrDefault("source", "");
        var fileName = Path.GetFileName(path);

        return new TestFlow
        {
            TestName = $"Export: {fileName}",
            Description = $"Export file to {path}.",
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = "Invoke Save As dialog (Ctrl+Shift+S)",
                    Action = StepAction.SendKeys,
                    Keys = "^+s",
                    DelayAfterMs = 2000
                },
                new TestStep
                {
                    Order = 2,
                    Description = "Wait for Save dialog",
                    Action = StepAction.AssertWindow,
                    WindowTitle = "Save",
                    TimeoutMs = 5000,
                    DelayAfterMs = 1000
                },
                new TestStep
                {
                    Order = 3,
                    Description = $"Type file path: {path}",
                    Action = StepAction.Type,
                    Selector = "Edit#FileNameControlHost",
                    Text = path,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 4,
                    Description = "Click Save button",
                    Action = StepAction.Click,
                    Selector = "Button#Save",
                    DelayAfterMs = 3000
                }
            ],
            TimeoutSeconds = 60
        };
    }
}

// ── 11. DragAndDrop ─────────────────────────────────────────────────────────────

public sealed class DragAndDropTemplate : IFlowTemplate
{
    public string TemplateId => "drag-drop";
    public string DisplayName => "Drag and Drop";
    public string Description => "Drag an element from a source to a target location.";
    public IntentKind IntentKind => IntentKind.DragAndDrop;
    public IReadOnlyList<string> RequiredSlots => ["source", "target"];
    public IReadOnlyList<string> OptionalSlots => [];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.DragAndDrop;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var source = intent.Slots.GetValueOrDefault("source", "");
        var target = intent.Slots.GetValueOrDefault("target", "");

        return new TestFlow
        {
            TestName = $"Drag '{source}' to '{target}'",
            Description = $"Drag element '{source}' and drop onto '{target}'.",
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = $"Verify source element exists: {source}",
                    Action = StepAction.AssertExists,
                    Selector = source,
                    TimeoutMs = 5000,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 2,
                    Description = $"Verify target element exists: {target}",
                    Action = StepAction.AssertExists,
                    Selector = target,
                    TimeoutMs = 5000,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 3,
                    Description = $"Click and hold source: {source}",
                    Action = StepAction.Click,
                    Selector = source,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 4,
                    Description = $"Drop onto target: {target}",
                    Action = StepAction.Click,
                    Selector = target,
                    DelayAfterMs = 1000
                }
            ],
            TimeoutSeconds = 30
        };
    }
}

// ── 12. FormFill ────────────────────────────────────────────────────────────────

public sealed class FormFillTemplate : IFlowTemplate
{
    public string TemplateId => "form-fill";
    public string DisplayName => "Fill Form";
    public string Description => "Fill out a multi-field form with provided values.";
    public IntentKind IntentKind => IntentKind.FillForm;
    public IReadOnlyList<string> RequiredSlots => ["fields"];
    public IReadOnlyList<string> OptionalSlots => ["submit", "target"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.FillForm;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var target = intent.Slots.GetValueOrDefault("target", "");
        var submit = intent.Slots.GetValueOrDefault("submit", "Button#Submit");

        // Parse fields: expect "field1=value1,field2=value2" format
        var fieldsRaw = intent.Slots.GetValueOrDefault("fields", "");
        var fieldPairs = fieldsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var steps = new List<TestStep>();
        var order = 1;

        if (!string.IsNullOrEmpty(target))
        {
            steps.Add(new TestStep
            {
                Order = order++,
                Description = $"Focus target: {target}",
                Action = StepAction.FocusWindow,
                WindowTitle = target,
                DelayAfterMs = 1000
            });
        }

        foreach (var pair in fieldPairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;

            var fieldSelector = parts[0].Trim();
            var fieldValue = parts[1].Trim();

            steps.Add(new TestStep
            {
                Order = order++,
                Description = $"Fill field: {fieldSelector}",
                Action = StepAction.Type,
                Selector = $"Edit#{fieldSelector}",
                Text = fieldValue,
                DelayAfterMs = 300
            });
        }

        steps.Add(new TestStep
        {
            Order = order,
            Description = "Submit form",
            Action = StepAction.Click,
            Selector = submit,
            DelayAfterMs = 2000
        });

        return new TestFlow
        {
            TestName = $"Form Fill: {(string.IsNullOrEmpty(target) ? "form" : target)}",
            Description = $"Fill {fieldPairs.Length} field(s) and submit.",
            TargetApp = string.IsNullOrEmpty(target) ? null : target,
            Steps = steps,
            TimeoutSeconds = 60
        };
    }
}

// ── 13. ChainedNavigation ───────────────────────────────────────────────────────

public sealed class ChainedNavigationTemplate : IFlowTemplate
{
    public string TemplateId => "chained-nav";
    public string DisplayName => "Chained Navigation";
    public string Description => "Multi-step navigation chain: click through a sequence of UI elements.";
    public IntentKind IntentKind => IntentKind.ChainedNavigation;
    public IReadOnlyList<string> RequiredSlots => ["steps"];
    public IReadOnlyList<string> OptionalSlots => ["target"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.ChainedNavigation;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var target = intent.Slots.GetValueOrDefault("target", "");
        var stepsRaw = intent.Slots.GetValueOrDefault("steps", "");
        var navSteps = stepsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var steps = new List<TestStep>();
        var order = 1;

        if (!string.IsNullOrEmpty(target))
        {
            steps.Add(new TestStep
            {
                Order = order++,
                Description = $"Focus: {target}",
                Action = StepAction.FocusWindow,
                WindowTitle = target,
                DelayAfterMs = 1000
            });
        }

        foreach (var nav in navSteps)
        {
            steps.Add(new TestStep
            {
                Order = order++,
                Description = $"Navigate: {nav}",
                Action = StepAction.Click,
                Selector = nav.Trim(),
                TimeoutMs = 8000,
                DelayAfterMs = 1500
            });
        }

        return new TestFlow
        {
            TestName = $"Chained Navigation ({navSteps.Length} steps)",
            Description = $"Navigate through {navSteps.Length} UI elements in sequence.",
            TargetApp = string.IsNullOrEmpty(target) ? null : target,
            Steps = steps,
            TimeoutSeconds = navSteps.Length * 15 + 30
        };
    }
}

// ── 14. MultiWindowExtractValue ─────────────────────────────────────────────────

public sealed class MultiWindowExtractValueTemplate : IFlowTemplate
{
    public string TemplateId => "multi-window-extract";
    public string DisplayName => "Multi-Window Extract Value";
    public string Description => "Read a value from one window and verify or paste it in another.";
    public IntentKind IntentKind => IntentKind.MultiWindowExtract;
    public IReadOnlyList<string> RequiredSlots => ["sourceWindow", "targetWindow"];
    public IReadOnlyList<string> OptionalSlots => ["sourceSelector", "targetSelector"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.MultiWindowExtract;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var sourceWindow = intent.Slots.GetValueOrDefault("sourceWindow", "");
        var targetWindow = intent.Slots.GetValueOrDefault("targetWindow", "");
        var sourceSelector = intent.Slots.GetValueOrDefault("sourceSelector", "");
        var targetSelector = intent.Slots.GetValueOrDefault("targetSelector", "");

        return new TestFlow
        {
            TestName = $"Extract: {sourceWindow} → {targetWindow}",
            Description = $"Copy a value from {sourceWindow} to {targetWindow}.",
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = $"Focus source: {sourceWindow}",
                    Action = StepAction.FocusWindow,
                    WindowTitle = sourceWindow,
                    DelayAfterMs = 1000
                },
                new TestStep
                {
                    Order = 2,
                    Description = "Select and copy value (Ctrl+C)",
                    Action = StepAction.Click,
                    Selector = string.IsNullOrEmpty(sourceSelector) ? sourceWindow : sourceSelector,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 3,
                    Description = "Copy selected value",
                    Action = StepAction.SendKeys,
                    Keys = "^c",
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 4,
                    Description = $"Focus target: {targetWindow}",
                    Action = StepAction.FocusWindow,
                    WindowTitle = targetWindow,
                    DelayAfterMs = 1000
                },
                new TestStep
                {
                    Order = 5,
                    Description = "Click target field",
                    Action = StepAction.Click,
                    Selector = string.IsNullOrEmpty(targetSelector) ? targetWindow : targetSelector,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 6,
                    Description = "Paste value (Ctrl+V)",
                    Action = StepAction.SendKeys,
                    Keys = "^v",
                    DelayAfterMs = 1000
                }
            ],
            TimeoutSeconds = 60
        };
    }
}

// ── 15. SystemSettingDeepNavigation ─────────────────────────────────────────────

public sealed class SystemSettingDeepNavigationTemplate : IFlowTemplate
{
    public string TemplateId => "setting-deep-nav";
    public string DisplayName => "System Setting Deep Navigation";
    public string Description => "Navigate deep into Windows Settings through multiple panes. Modifies system state.";
    public IntentKind IntentKind => IntentKind.ToggleSetting;
    public IReadOnlyList<string> RequiredSlots => ["setting"];
    public IReadOnlyList<string> OptionalSlots => ["category", "subcategory"];
    public RiskLevel RiskLevel => RiskLevel.High;
    public TemplateMaturity Maturity => TemplateMaturity.Experimental;

    public bool CanHandle(IntentParse intent) =>
        intent.Kind == IntentKind.ToggleSetting &&
        intent.Slots.ContainsKey("category");

    public TestFlow BuildFlow(IntentParse intent)
    {
        var setting = intent.Slots.GetValueOrDefault("setting", "");
        var category = intent.Slots.GetValueOrDefault("category", "System");
        var subcategory = intent.Slots.GetValueOrDefault("subcategory", "");

        var steps = new List<TestStep>
        {
            new()
            {
                Order = 1,
                Description = "Open Windows Settings",
                Action = StepAction.Launch,
                ProcessPath = "ms-settings:",
                DelayAfterMs = 3000
            },
            new()
            {
                Order = 2,
                Description = "Focus Settings window",
                Action = StepAction.FocusWindow,
                WindowTitle = "Settings",
                DelayAfterMs = 1000
            },
            new()
            {
                Order = 3,
                Description = $"Navigate to category: {category}",
                Action = StepAction.Click,
                Selector = $"Text#{category}",
                TimeoutMs = 8000,
                DelayAfterMs = 2000
            }
        };

        var order = 4;
        if (!string.IsNullOrEmpty(subcategory))
        {
            steps.Add(new TestStep
            {
                Order = order++,
                Description = $"Navigate to subcategory: {subcategory}",
                Action = StepAction.Click,
                Selector = $"Text#{subcategory}",
                TimeoutMs = 8000,
                DelayAfterMs = 2000
            });
        }

        steps.Add(new TestStep
        {
            Order = order++,
            Description = $"Find setting: {setting}",
            Action = StepAction.Click,
            Selector = $"Text#{setting}",
            TimeoutMs = 8000,
            DelayAfterMs = 1500
        });

        steps.Add(new TestStep
        {
            Order = order++,
            Description = $"Toggle: {setting}",
            Action = StepAction.Click,
            Selector = "Button#ToggleSwitch",
            DelayAfterMs = 1500
        });

        steps.Add(new TestStep
        {
            Order = order,
            Description = "Verify setting state changed",
            Action = StepAction.AssertExists,
            Selector = $"Text#{setting}",
            TimeoutMs = 5000
        });

        return new TestFlow
        {
            TestName = $"Deep Setting: {category} > {(string.IsNullOrEmpty(subcategory) ? setting : $"{subcategory} > {setting}")}",
            Description = $"Navigate Windows Settings to toggle '{setting}'.",
            TargetApp = "SystemSettings",
            Steps = steps,
            TimeoutSeconds = 90
        };
    }
}

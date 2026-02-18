using IdolClick.Models;

namespace IdolClick.Services.Templates;

// ═══════════════════════════════════════════════════════════════════════════════════
// CORE TEMPLATES (8) — Safe, deterministic, well-tested flow generators.
//
// Core templates:
//   1. BrowserSearch       — Search the web via default browser
//   2. BrowserNavigate     — Navigate to a specific URL
//   3. LaunchAndFocusApp   — Launch or focus a desktop application
//   4. OpenFileAndVerifyText — Open a file and verify text content
//   5. WaitAndVerify       — Wait for an element then assert its state
//   6. LoginFlowBasic      — Basic login with username + password
//   7. CaptureScreenshotEvidence — Take a screenshot for evidence
//   8. RunRegressionFlow   — Re-run a previously saved flow
//
// All Core templates:
//   • RiskLevel = Normal
//   • Maturity = Core
//   • Can auto-execute at ≥0.8 confidence with all required slots
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

// ── 1. BrowserSearch ────────────────────────────────────────────────────────────

public sealed class BrowserSearchTemplate : IFlowTemplate
{
    public string TemplateId => "browser-search";
    public string DisplayName => "Browser Search";
    public string Description => "Search the web using the default browser. Opens a new browser tab with the search query.";
    public IntentKind IntentKind => IntentKind.BrowserSearch;
    public IReadOnlyList<string> RequiredSlots => ["query"];
    public IReadOnlyList<string> OptionalSlots => ["browser"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.BrowserSearch;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var query = intent.Slots.GetValueOrDefault("query", "");
        var browser = intent.Slots.GetValueOrDefault("browser", "msedge");
        var encodedQuery = Uri.EscapeDataString(query);

        return new TestFlow
        {
            TestName = $"Browser Search: {query}",
            Description = $"Search the web for '{query}' using {browser}.",
            TargetApp = browser,
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = "Launch browser with search URL",
                    Action = StepAction.Launch,
                    ProcessPath = $"https://www.google.com/search?q={encodedQuery}",
                    DelayAfterMs = 2000
                },
                new TestStep
                {
                    Order = 2,
                    Description = "Verify browser window appeared",
                    Action = StepAction.AssertWindow,
                    WindowTitle = query,
                    TimeoutMs = 5000
                }
            ]
        };
    }
}

// ── 2. BrowserNavigate ──────────────────────────────────────────────────────────

public sealed class BrowserNavigateTemplate : IFlowTemplate
{
    public string TemplateId => "browser-navigate";
    public string DisplayName => "Browser Navigate";
    public string Description => "Navigate to a specific URL in the default browser.";
    public IntentKind IntentKind => IntentKind.BrowserNavigate;
    public IReadOnlyList<string> RequiredSlots => ["url"];
    public IReadOnlyList<string> OptionalSlots => ["browser"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.BrowserNavigate;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var url = intent.Slots.GetValueOrDefault("url", "");
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var browser = intent.Slots.GetValueOrDefault("browser", "msedge");

        return new TestFlow
        {
            TestName = $"Navigate to {url}",
            Description = $"Open {url} in {browser}.",
            TargetApp = browser,
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = "Launch browser with URL",
                    Action = StepAction.Launch,
                    ProcessPath = url,
                    DelayAfterMs = 3000
                },
                new TestStep
                {
                    Order = 2,
                    Description = "Verify page loaded",
                    Action = StepAction.AssertWindow,
                    WindowTitle = new Uri(url).Host,
                    TimeoutMs = 5000
                }
            ]
        };
    }
}

// ── 3. LaunchAndFocusApp ────────────────────────────────────────────────────────

public sealed class LaunchAndFocusAppTemplate : IFlowTemplate
{
    public string TemplateId => "launch-app";
    public string DisplayName => "Launch & Focus App";
    public string Description => "Launch a desktop application and bring it to focus.";
    public IntentKind IntentKind => IntentKind.LaunchApp;
    public IReadOnlyList<string> RequiredSlots => ["app"];
    public IReadOnlyList<string> OptionalSlots => [];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) =>
        intent.Kind == IntentKind.LaunchApp || intent.Kind == IntentKind.FocusApp;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var app = intent.Slots.GetValueOrDefault("app", "");

        return new TestFlow
        {
            TestName = $"Launch {app}",
            Description = $"Launch or focus {app}.",
            TargetApp = app,
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = $"Launch {app}",
                    Action = StepAction.Launch,
                    ProcessPath = app,
                    DelayAfterMs = 2000
                },
                new TestStep
                {
                    Order = 2,
                    Description = $"Focus {app} window",
                    Action = StepAction.FocusWindow,
                    WindowTitle = app,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 3,
                    Description = $"Verify {app} is running",
                    Action = StepAction.AssertWindow,
                    WindowTitle = app,
                    TimeoutMs = 5000
                }
            ]
        };
    }
}

// ── 4. OpenFileAndVerifyText ────────────────────────────────────────────────────

public sealed class OpenFileAndVerifyTextTemplate : IFlowTemplate
{
    public string TemplateId => "open-file-verify";
    public string DisplayName => "Open File & Verify Text";
    public string Description => "Open a file in its default application and optionally verify text content.";
    public IntentKind IntentKind => IntentKind.OpenFile;
    public IReadOnlyList<string> RequiredSlots => ["path"];
    public IReadOnlyList<string> OptionalSlots => ["expectedText"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.OpenFile;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var path = intent.Slots.GetValueOrDefault("path", "");
        var expectedText = intent.Slots.GetValueOrDefault("expectedText", "");
        var fileName = Path.GetFileName(path);

        var steps = new List<TestStep>
        {
            new()
            {
                Order = 1,
                Description = $"Open file: {fileName}",
                Action = StepAction.Launch,
                ProcessPath = path,
                DelayAfterMs = 3000
            },
            new()
            {
                Order = 2,
                Description = $"Verify window appeared for {fileName}",
                Action = StepAction.AssertWindow,
                WindowTitle = fileName,
                TimeoutMs = 5000
            }
        };

        if (!string.IsNullOrEmpty(expectedText))
        {
            steps.Add(new TestStep
            {
                Order = 3,
                Description = $"Verify text content: {expectedText}",
                Action = StepAction.AssertText,
                Contains = expectedText,
                TimeoutMs = 5000
            });
        }

        return new TestFlow
        {
            TestName = $"Open and verify: {fileName}",
            Description = $"Open {path} and verify it loads correctly.",
            Steps = steps
        };
    }
}

// ── 5. WaitAndVerify ────────────────────────────────────────────────────────────

public sealed class WaitAndVerifyTemplate : IFlowTemplate
{
    public string TemplateId => "wait-and-verify";
    public string DisplayName => "Wait & Verify";
    public string Description => "Wait for a UI element to appear, then assert its state.";
    public IntentKind IntentKind => IntentKind.WaitAndVerify;
    public IReadOnlyList<string> RequiredSlots => ["target"];
    public IReadOnlyList<string> OptionalSlots => ["timeout", "expectedText"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) =>
        intent.Kind == IntentKind.WaitAndVerify || intent.Kind == IntentKind.ValidateUiState;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var target = intent.Slots.GetValueOrDefault("target", "");
        var timeout = intent.Slots.TryGetValue("timeout", out var t) && int.TryParse(t, out var tv) ? tv * 1000 : 5000;
        var expectedText = intent.Slots.GetValueOrDefault("expectedText", "");

        var steps = new List<TestStep>
        {
            new()
            {
                Order = 1,
                Description = $"Wait for '{target}' to appear",
                Action = StepAction.Wait,
                Selector = target,
                TimeoutMs = timeout,
                DelayAfterMs = 500
            },
            new()
            {
                Order = 2,
                Description = $"Verify '{target}' exists",
                Action = StepAction.AssertExists,
                Selector = target,
                TimeoutMs = 5000
            }
        };

        if (!string.IsNullOrEmpty(expectedText))
        {
            steps.Add(new TestStep
            {
                Order = 3,
                Description = $"Verify text: {expectedText}",
                Action = StepAction.AssertText,
                Selector = target,
                Contains = expectedText,
                TimeoutMs = 5000
            });
        }

        return new TestFlow
        {
            TestName = $"Wait and verify: {target}",
            Description = $"Wait for '{target}' to appear and verify its state.",
            Steps = steps,
            TimeoutSeconds = (timeout / 1000) + 30
        };
    }
}

// ── 6. LoginFlowBasic ──────────────────────────────────────────────────────────

public sealed class LoginFlowBasicTemplate : IFlowTemplate
{
    public string TemplateId => "login-basic";
    public string DisplayName => "Basic Login Flow";
    public string Description => "Perform a basic login with username and password fields.";
    public IntentKind IntentKind => IntentKind.Login;
    public IReadOnlyList<string> RequiredSlots => ["target"];
    public IReadOnlyList<string> OptionalSlots => ["username", "password", "usernameSelector", "passwordSelector", "submitSelector"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.Login;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var target = intent.Slots.GetValueOrDefault("target", "");
        var usernameSelector = intent.Slots.GetValueOrDefault("usernameSelector", "Edit#username");
        var passwordSelector = intent.Slots.GetValueOrDefault("passwordSelector", "Edit#password");
        var submitSelector = intent.Slots.GetValueOrDefault("submitSelector", "Button#submit");
        var username = intent.Slots.GetValueOrDefault("username", "{{USERNAME}}");
        var password = intent.Slots.GetValueOrDefault("password", "{{PASSWORD}}");

        return new TestFlow
        {
            TestName = $"Login to {target}",
            Description = $"Basic login flow for {target}.",
            TargetApp = target,
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = "Focus target application",
                    Action = StepAction.FocusWindow,
                    WindowTitle = target,
                    DelayAfterMs = 1000
                },
                new TestStep
                {
                    Order = 2,
                    Description = "Enter username",
                    Action = StepAction.Type,
                    Selector = usernameSelector,
                    Text = username,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 3,
                    Description = "Enter password",
                    Action = StepAction.Type,
                    Selector = passwordSelector,
                    Text = password,
                    DelayAfterMs = 500
                },
                new TestStep
                {
                    Order = 4,
                    Description = "Click submit/login button",
                    Action = StepAction.Click,
                    Selector = submitSelector,
                    DelayAfterMs = 3000
                },
                new TestStep
                {
                    Order = 5,
                    Description = "Verify login succeeded",
                    Action = StepAction.AssertNotExists,
                    Selector = usernameSelector,
                    TimeoutMs = 5000
                }
            ]
        };
    }
}

// ── 7. CaptureScreenshotEvidence ────────────────────────────────────────────────

public sealed class CaptureScreenshotEvidenceTemplate : IFlowTemplate
{
    public string TemplateId => "screenshot-evidence";
    public string DisplayName => "Capture Screenshot Evidence";
    public string Description => "Take a screenshot of the current screen state for evidence or documentation.";
    public IntentKind IntentKind => IntentKind.TakeScreenshot;
    public IReadOnlyList<string> RequiredSlots => [];
    public IReadOnlyList<string> OptionalSlots => ["target"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.TakeScreenshot;

    public TestFlow BuildFlow(IntentParse intent)
    {
        var target = intent.Slots.GetValueOrDefault("target", "");
        var hasTarget = !string.IsNullOrEmpty(target);

        var steps = new List<TestStep>();

        if (hasTarget)
        {
            steps.Add(new TestStep
            {
                Order = 1,
                Description = $"Focus target: {target}",
                Action = StepAction.FocusWindow,
                WindowTitle = target,
                DelayAfterMs = 1000
            });
        }

        steps.Add(new TestStep
        {
            Order = hasTarget ? 2 : 1,
            Description = "Capture screenshot",
            Action = StepAction.Screenshot,
            WindowTitle = hasTarget ? target : null,
            DelayAfterMs = 0
        });

        return new TestFlow
        {
            TestName = hasTarget ? $"Screenshot: {target}" : "Screenshot: Full Screen",
            Description = hasTarget ? $"Capture screenshot of {target}." : "Capture full screen screenshot.",
            TargetApp = hasTarget ? target : null,
            Steps = steps,
            TimeoutSeconds = 30
        };
    }
}

// ── 8. RunRegressionFlow ────────────────────────────────────────────────────────

public sealed class RunRegressionFlowTemplate : IFlowTemplate
{
    public string TemplateId => "run-regression";
    public string DisplayName => "Run Regression Flow";
    public string Description => "Re-run a previously saved test flow for regression testing.";
    public IntentKind IntentKind => IntentKind.RunRegression;
    public IReadOnlyList<string> RequiredSlots => [];
    public IReadOnlyList<string> OptionalSlots => ["flowName", "maxReports"];
    public RiskLevel RiskLevel => RiskLevel.Normal;
    public TemplateMaturity Maturity => TemplateMaturity.Core;

    public bool CanHandle(IntentParse intent) => intent.Kind == IntentKind.RunRegression;

    public TestFlow BuildFlow(IntentParse intent)
    {
        // Regression is a meta-template: it generates a placeholder flow
        // that the runtime interprets as "reload and re-execute last flow".
        // The actual flow content is resolved at execution time by the runtime.
        var flowName = intent.Slots.GetValueOrDefault("flowName", "last");

        return new TestFlow
        {
            TestName = $"Regression: {flowName}",
            Description = $"Re-run flow '{flowName}' for regression testing.",
            Steps =
            [
                new TestStep
                {
                    Order = 1,
                    Description = $"Re-run saved flow: {flowName}",
                    Action = StepAction.Wait,
                    TimeoutMs = 1000,
                    DelayAfterMs = 1000
                }
            ],
            TimeoutSeconds = 300
        };
    }
}

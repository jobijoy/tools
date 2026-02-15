using IdolClick.Models;

namespace IdolClick.Services.Infrastructure;

// ═══════════════════════════════════════════════════════════════════════════════════
// PLAYWRIGHT BACKEND (STUB) — Future web automation via Playwright .NET.
//
// This is the contract-ready stub for Sprint 7. The actual implementation
// requires the Microsoft.Playwright NuGet package, which is intentionally
// NOT referenced in the core project to keep desktop-only installs lean.
//
// When implemented (future IdolClick.PlaywrightBackend package):
//   • InitializeAsync → creates Playwright instance, launches browser, creates page
//   • ExecuteStepAsync → locator-first calls with auto-wait actionability
//   • StartArtifactCaptureAsync → context.Tracing.StartAsync(...)
//   • StopArtifactCaptureAsync → context.Tracing.StopAsync(Path=trace.zip)
//   • InspectTargetAsync → page accessibility tree or DOM snapshot
//
// Selector strategy (from Playwright research):
//   • PlaywrightRole  → page.GetByRole(AriaRole.Button, new() { Name = "Submit" })
//   • PlaywrightLabel → page.GetByLabel("Username")
//   • PlaywrightTestId → page.GetByTestId("checkout-button")
//   • PlaywrightCss   → page.Locator("#id .class")
//   • PlaywrightText  → page.GetByText("Submit order")
//
// NuGet packaging strategy:
//   • IdolClick.Core — this interface + models + pipeline (no Playwright dep)
//   • IdolClick.PlaywrightBackend — references Microsoft.Playwright, lives here
//   • NuGet has no optional dependencies, so split packages is the correct pattern
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Stub Playwright backend. All methods throw <see cref="NotSupportedException"/>
/// until the Microsoft.Playwright package is added and the implementation is completed.
/// </summary>
public class PlaywrightBackend : IAutomationBackend
{
    private readonly LogService _log;

    public PlaywrightBackend(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "playwright";
    public string Version => "0.0.0-stub";

    public BackendCapabilities Capabilities { get; } = new()
    {
        SupportedActions = new HashSet<StepAction>
        {
            StepAction.Click,
            StepAction.Type,
            StepAction.SendKeys,
            StepAction.Wait,
            StepAction.AssertExists,
            StepAction.AssertNotExists,
            StepAction.AssertText,
            StepAction.Navigate,
            StepAction.Screenshot,
            StepAction.Scroll
            // Note: Launch and FocusWindow are desktop-only
        },
        SupportedAssertions = new HashSet<AssertionType>
        {
            AssertionType.Exists,
            AssertionType.NotExists,
            AssertionType.TextContains,
            AssertionType.TextEquals
            // WindowTitle and ProcessRunning are desktop-only
        },
        SupportedSelectorKinds = new HashSet<SelectorKind>
        {
            SelectorKind.PlaywrightCss,
            SelectorKind.PlaywrightRole,
            SelectorKind.PlaywrightText,
            SelectorKind.PlaywrightLabel,
            SelectorKind.PlaywrightTestId
        },
        SupportsTracing = true,         // Playwright has first-class tracing
        SupportsNetworkLogs = true,      // HAR capture
        SupportsScreenshots = true,
        SupportsActionabilityChecks = true // Playwright auto-waits natively
    };

    public Task InitializeAsync(BackendInitOptions options, CancellationToken ct = default)
    {
        // Future implementation:
        // 1. var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // 2. _browser = await playwright.Chromium.LaunchAsync(new() { Headless = options.Headless });
        // 3. _context = await _browser.NewContextAsync();
        // 4. _page = await _context.NewPageAsync();
        // 5. if (options.StartUrl != null) await _page.GotoAsync(options.StartUrl);

        _log.Warn("PlaywrightBackend", "Stub: Microsoft.Playwright is not installed. Add the NuGet package to enable web automation.");
        throw new NotSupportedException(
            "Playwright backend is not yet implemented. " +
            "Add 'Microsoft.Playwright' NuGet package and implement PlaywrightBackend to enable web automation. " +
            "See: https://playwright.dev/dotnet/docs/intro");
    }

    public Task<StepResult> ExecuteStepAsync(TestStep step, BackendExecutionContext ctx, CancellationToken ct = default)
    {
        // Future implementation outline:
        //
        // 1. Resolve locator from TypedSelector:
        //    var locator = step.TypedSelector?.Kind switch
        //    {
        //        SelectorKind.PlaywrightRole  => _page.GetByRole(ParseRole(sel.Value), new() { Name = sel.Extra }),
        //        SelectorKind.PlaywrightLabel  => _page.GetByLabel(sel.Value),
        //        SelectorKind.PlaywrightTestId => _page.GetByTestId(sel.Value),
        //        SelectorKind.PlaywrightText   => _page.GetByText(sel.Value),
        //        SelectorKind.PlaywrightCss    => _page.Locator(sel.Value),
        //        _ => _page.Locator(step.Selector ?? "")
        //    };
        //
        // 2. Execute action with Playwright auto-wait:
        //    StepAction.Click → await locator.ClickAsync();
        //    StepAction.Type  → await locator.FillAsync(step.Text);
        //    StepAction.Wait  → await locator.WaitForAsync();
        //
        // 3. Evaluate assertions with auto-retry:
        //    await Expect(locator).ToHaveTextAsync(expected);
        //
        // 4. Capture call log from Playwright's internal logs

        throw new NotSupportedException("Playwright backend stub: ExecuteStepAsync not implemented.");
    }

    public Task<IReadOnlyList<InspectableTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        // Future: list browser pages/frames as inspectable targets
        // var pages = _context.Pages.Select(p => new InspectableTarget { ... });
        throw new NotSupportedException("Playwright backend stub: ListTargetsAsync not implemented.");
    }

    public Task<InspectionResult> InspectTargetAsync(InspectTargetRequest request, CancellationToken ct = default)
    {
        // Future: use page accessibility tree or DOM snapshot
        // var snapshot = await _page.Accessibility.SnapshotAsync();
        throw new NotSupportedException("Playwright backend stub: InspectTargetAsync not implemented.");
    }

    public Task<BackendArtifact?> StartArtifactCaptureAsync(ArtifactOptions options, CancellationToken ct = default)
    {
        // Future implementation:
        // await _context.Tracing.StartAsync(new()
        // {
        //     Screenshots = options.Screenshots,
        //     Snapshots = options.Snapshots,
        //     Sources = options.Sources,
        //     Title = options.Title
        // });

        throw new NotSupportedException("Playwright backend stub: StartArtifactCaptureAsync not implemented.");
    }

    public Task<BackendArtifact?> StopArtifactCaptureAsync(CancellationToken ct = default)
    {
        // Future implementation:
        // var path = options.OutputPath ?? Path.Combine(_artifactDir, $"trace_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        // await _context.Tracing.StopAsync(new() { Path = path });
        // return new BackendArtifact { Type = "trace", FilePath = path, ... };

        throw new NotSupportedException("Playwright backend stub: StopArtifactCaptureAsync not implemented.");
    }

    public async ValueTask DisposeAsync()
    {
        // Future: dispose page, context, browser, playwright
        // if (_page != null) await _page.CloseAsync();
        // if (_context != null) await _context.CloseAsync();
        // if (_browser != null) await _browser.CloseAsync();
        // _playwright?.Dispose();

        _log.Debug("PlaywrightBackend", "Disposed (stub)");
        await ValueTask.CompletedTask;
    }
}

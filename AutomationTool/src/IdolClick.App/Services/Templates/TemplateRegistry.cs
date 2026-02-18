namespace IdolClick.Services.Templates;

// ═══════════════════════════════════════════════════════════════════════════════════
// TEMPLATE REGISTRY — Central registry of all available flow templates.//                     STATUS: ALPHA — part of the Commands module.//
// All 15 templates (8 Core + 7 Experimental) register here at startup.
// The registry is immutable after initialization — add-only, no removal.
//
// Used by:
//   • IntentSplitterService (scoring candidates)
//   • TemplateEndpoints (listing + lookup)
//   • Future: "Save as Template" (teach→reuse loop from Studio)
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Central registry of all flow templates. Immutable after initialization.
/// </summary>
public sealed class TemplateRegistry
{
    private readonly List<IFlowTemplate> _templates = [];
    private readonly Dictionary<string, IFlowTemplate> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// All registered templates (read-only).
    /// </summary>
    public IReadOnlyList<IFlowTemplate> All => _templates;

    /// <summary>
    /// Number of registered templates.
    /// </summary>
    public int Count => _templates.Count;

    /// <summary>
    /// Register a template. Duplicate IDs throw.
    /// </summary>
    public TemplateRegistry Register(IFlowTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (_byId.ContainsKey(template.TemplateId))
            throw new InvalidOperationException($"Template '{template.TemplateId}' is already registered.");

        _templates.Add(template);
        _byId[template.TemplateId] = template;
        return this; // Fluent API
    }

    /// <summary>
    /// Register multiple templates at once.
    /// </summary>
    public TemplateRegistry RegisterAll(params IFlowTemplate[] templates)
    {
        foreach (var t in templates)
            Register(t);
        return this;
    }

    /// <summary>
    /// Look up a template by ID. Returns null if not found.
    /// </summary>
    public IFlowTemplate? GetById(string templateId)
    {
        return _byId.TryGetValue(templateId, out var t) ? t : null;
    }

    /// <summary>
    /// Get all templates matching a maturity level.
    /// </summary>
    public IReadOnlyList<IFlowTemplate> GetByMaturity(TemplateMaturity maturity)
    {
        return _templates.Where(t => t.Maturity == maturity).ToList();
    }

    /// <summary>
    /// Get all templates matching a risk level.
    /// </summary>
    public IReadOnlyList<IFlowTemplate> GetByRisk(RiskLevel risk)
    {
        return _templates.Where(t => t.RiskLevel == risk).ToList();
    }

    /// <summary>
    /// Creates and populates a registry with all built-in templates.
    /// Called once at startup.
    /// </summary>
    public static TemplateRegistry CreateDefault()
    {
        var registry = new TemplateRegistry();

        // ── Core (8) — safe, deterministic, well-tested ─────────────────────
        registry.RegisterAll(
            new BrowserSearchTemplate(),
            new BrowserNavigateTemplate(),
            new LaunchAndFocusAppTemplate(),
            new OpenFileAndVerifyTextTemplate(),
            new WaitAndVerifyTemplate(),
            new LoginFlowBasicTemplate(),
            new CaptureScreenshotEvidenceTemplate(),
            new RunRegressionFlowTemplate()
        );

        // ── Experimental (7) — may escalate, requires confirmation ──────────
        registry.RegisterAll(
            new ToggleSystemSettingTemplate(),
            new ExportReportToFileTemplate(),
            new DragAndDropTemplate(),
            new FormFillTemplate(),
            new ChainedNavigationTemplate(),
            new MultiWindowExtractValueTemplate(),
            new SystemSettingDeepNavigationTemplate()
        );

        return registry;
    }
}

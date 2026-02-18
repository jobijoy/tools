using IdolClick.Models;
using IdolClick.Services.Templates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// INTENT SPLITTER — Structured automation compiler entry point.
//                    STATUS: ALPHA — keyword-only classifier, zero test coverage.
//
// "All intelligence produces plans. Only backends execute plans."
//
// Architecture (per RA validation):
//   IntentSplitterService is an orchestrator composed of 3 focused interfaces:
//     • IIntentClassifier  — NL → IntentParse (regex/keyword, NO LLM)
//     • ITemplateScorer    — IntentParse × TemplateRegistry → scored candidates
//     • IEscalationPolicy  — score + RiskLevel + context → EscalationDecision
//
// 3-Tier Escalation:
//   ≥0.8 confidence + no missing required slots + Normal/Low risk → Auto-execute
//   ≥0.8 confidence + High risk → Confirm (risk override)
//   0.5–0.79 or missing optional slots → Show template + confirm
//   <0.5 → LLM handoff (route to Studio)
//
// Design constraints (RA-validated):
//   • Splitter NEVER calls InspectWindow (that's Studio/LLM territory)
//   • Splitter NEVER executes flows (that's FlowEndpoints' job)
//   • Templates emit TestFlow only — no backend mutations
//   • No "LLM creep" — return escalation state, let Commands UI decide
// ═══════════════════════════════════════════════════════════════════════════════════

#region Contracts

/// <summary>
/// Classifies natural language input into a structured intent parse.
/// Pure heuristic — regex + keyword matching, no LLM calls.
/// </summary>
public interface IIntentClassifier
{
    /// <summary>
    /// Parse a natural language command into a structured intent.
    /// </summary>
    IntentParse Classify(string input);
}

/// <summary>
/// Scores candidate templates against a parsed intent.
/// </summary>
public interface ITemplateScorer
{
    /// <summary>
    /// Score all registered templates against the intent, returning ranked candidates.
    /// </summary>
    List<TemplateScoringResult> Score(IntentParse intent, IReadOnlyList<IFlowTemplate> templates);
}

/// <summary>
/// Applies escalation policy to a scored template match.
/// Considers confidence, missing slots, RiskLevel, and execution context.
/// </summary>
public interface IEscalationPolicy
{
    /// <summary>
    /// Determine the escalation tier for a given scoring result.
    /// </summary>
    EscalationDecision Evaluate(TemplateScoringResult scoring, IntentParse intent, ExecutionContext context);
}

#endregion

#region DTOs

/// <summary>
/// The kind of user intent detected by the classifier.
/// </summary>
public enum IntentKind
{
    BrowserSearch,
    BrowserNavigate,
    LaunchApp,
    FocusApp,
    ClickElement,
    TypeText,
    ExportFile,
    ValidateUiState,
    OpenFile,
    TakeScreenshot,
    ToggleSetting,
    WaitAndVerify,
    Login,
    DragAndDrop,
    FillForm,
    ChainedNavigation,
    MultiWindowExtract,
    RunRegression,
    Unknown
}

/// <summary>
/// Risk level for a template. High-risk actions require confirmation even at high confidence.
/// </summary>
public enum RiskLevel
{
    /// <summary>Safe, deterministic, read-only or trivially reversible.</summary>
    Normal,
    /// <summary>Modifies system state, writes files, or navigates destructive paths.</summary>
    High,
    /// <summary>Irreversible or security-sensitive. Always requires confirmation.</summary>
    Critical
}

/// <summary>
/// Maturity classification for templates (RA-validated).
/// Core = safe, deterministic, well-tested.
/// Experimental = may escalate more, requires confirmation, lower confidence tolerance.
/// </summary>
public enum TemplateMaturity
{
    Core,
    Experimental
}

/// <summary>
/// Execution context for escalation decisions.
/// </summary>
public class ExecutionContext
{
    /// <summary>True if running interactively (user is at the keyboard).</summary>
    public bool IsInteractive { get; init; } = true;

    /// <summary>True if running as part of a pack pipeline (background).</summary>
    public bool IsPackPipeline { get; init; }

    /// <summary>True if user has opted into "trust mode" for this session.</summary>
    public bool TrustMode { get; init; }
}

/// <summary>
/// Result of intent classification — extracted kind, slots, and raw confidence.
/// </summary>
public class IntentParse
{
    /// <summary>Detected intent kind.</summary>
    public IntentKind Kind { get; set; } = IntentKind.Unknown;

    /// <summary>Classifier confidence (0.0–1.0).</summary>
    public double Confidence { get; set; }

    /// <summary>Extracted named slots (e.g., "url" → "youtube.com", "app" → "notepad").</summary>
    public Dictionary<string, string> Slots { get; set; } = [];

    /// <summary>Original user input.</summary>
    public string RawInput { get; set; } = "";

    /// <summary>Keywords that triggered this classification.</summary>
    public List<string> MatchedKeywords { get; set; } = [];
}

/// <summary>
/// A scored template candidate with slot fill analysis.
/// </summary>
public class TemplateScoringResult
{
    /// <summary>The matched template.</summary>
    public IFlowTemplate Template { get; init; } = null!;

    /// <summary>Combined score (0.0–1.0) factoring intent match + slot coverage.</summary>
    public double Score { get; set; }

    /// <summary>Required slots that are filled.</summary>
    public List<string> FilledRequiredSlots { get; set; } = [];

    /// <summary>Required slots that are missing.</summary>
    public List<string> MissingRequiredSlots { get; set; } = [];

    /// <summary>Optional slots that are filled.</summary>
    public List<string> FilledOptionalSlots { get; set; } = [];

    /// <summary>Optional slots that are missing.</summary>
    public List<string> MissingOptionalSlots { get; set; } = [];

    /// <summary>Whether all required slots are satisfied.</summary>
    public bool AllRequiredSlotsFilled => MissingRequiredSlots.Count == 0;
}

/// <summary>
/// Escalation tiers.
/// </summary>
public enum EscalationTier
{
    /// <summary>Auto-execute without confirmation.</summary>
    AutoExecute,
    /// <summary>Show resolved template, ask user to confirm.</summary>
    Confirm,
    /// <summary>Hand off to full LLM pipeline (Studio).</summary>
    LlmHandoff
}

/// <summary>
/// The final escalation decision for a parsed intent.
/// </summary>
public class EscalationDecision
{
    /// <summary>Which tier this intent lands in.</summary>
    public EscalationTier Tier { get; set; }

    /// <summary>Human-readable reason for escalation.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The best-match template (null if LlmHandoff).</summary>
    public IFlowTemplate? Template { get; set; }

    /// <summary>The draft TestFlow built from the template (null if LlmHandoff).</summary>
    public TestFlow? DraftFlow { get; set; }

    /// <summary>All scoring results for transparency.</summary>
    public List<TemplateScoringResult> AllCandidates { get; set; } = [];

    /// <summary>The original parsed intent.</summary>
    public IntentParse Intent { get; set; } = null!;
}

/// <summary>
/// Full result from the intent splitting pipeline.
/// </summary>
public class IntentSplitResult
{
    /// <summary>The parsed intent.</summary>
    public IntentParse Intent { get; set; } = null!;

    /// <summary>The escalation decision.</summary>
    public EscalationDecision Decision { get; set; } = null!;

    /// <summary>Processing time in milliseconds.</summary>
    public long ProcessingMs { get; set; }
}

#endregion

// ═══════════════════════════════════════════════════════════════════════════════════
// IMPLEMENTATIONS
// ═══════════════════════════════════════════════════════════════════════════════════

#region IntentClassifier

/// <summary>
/// Regex + keyword heuristic classifier. Fast, deterministic, no LLM.
/// </summary>
public class KeywordIntentClassifier : IIntentClassifier
{
    private static readonly List<(IntentKind Kind, double BaseConfidence, string[] Keywords, Regex? Pattern)> _rules =
    [
        // ── Browser ────────────────────────────────────────────────
        (IntentKind.BrowserSearch, 0.9,
            ["search", "google", "look up", "find online", "search for", "web search"],
            new Regex(@"\b(?:search|google|look\s*up|find\s+online)\b.*\b(?:for|about)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.BrowserNavigate, 0.9,
            ["go to", "open url", "navigate to", "browse to", "visit"],
            new Regex(@"\b(?:go\s*to|navigate\s*to|browse\s*to|visit|open)\b\s+(?:https?://|www\.|[\w.-]+\.(?:com|org|net|io|dev|ai))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── App management ─────────────────────────────────────────
        (IntentKind.LaunchApp, 0.85,
            ["launch", "start", "open app", "run program", "execute"],
            new Regex(@"\b(?:launch|start|open|run)\b\s+(?!url|http|www)[\w\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.FocusApp, 0.85,
            ["focus", "switch to", "bring to front", "activate window"],
            new Regex(@"\b(?:focus|switch\s*to|bring.*front|activate)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── Interaction ────────────────────────────────────────────
        (IntentKind.ClickElement, 0.8,
            ["click", "press", "tap", "hit button"],
            new Regex(@"\b(?:click|press|tap|hit)\b\s+(?:on\s+|the\s+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.TypeText, 0.8,
            ["type", "enter text", "input", "fill in"],
            new Regex(@"\b(?:type|enter|input|fill\s*in)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── File/export ────────────────────────────────────────────
        (IntentKind.ExportFile, 0.8,
            ["export", "save as", "download", "save to file"],
            new Regex(@"\b(?:export|save\s*as|download|save\s*to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.OpenFile, 0.85,
            ["open file", "open document", "read file", "load file"],
            new Regex(@"\b(?:open|read|load)\b\s+(?:file|document|the\s+file)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── Validation ─────────────────────────────────────────────
        (IntentKind.ValidateUiState, 0.8,
            ["verify", "check", "assert", "confirm that", "make sure"],
            new Regex(@"\b(?:verify|check|assert|confirm|make\s*sure|validate)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.WaitAndVerify, 0.85,
            ["wait for", "wait until", "wait and check", "wait then verify"],
            new Regex(@"\b(?:wait\s+(?:for|until|and|then))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── Screenshot ─────────────────────────────────────────────
        (IntentKind.TakeScreenshot, 0.9,
            ["screenshot", "capture screen", "take a picture", "screen capture"],
            new Regex(@"\b(?:screenshot|capture\s*screen|screen\s*capture|take.*(?:picture|screenshot))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── Settings ───────────────────────────────────────────────
        (IntentKind.ToggleSetting, 0.8,
            ["toggle", "enable", "disable", "turn on", "turn off", "switch setting"],
            new Regex(@"\b(?:toggle|enable|disable|turn\s*on|turn\s*off)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── Login ──────────────────────────────────────────────────
        (IntentKind.Login, 0.85,
            ["login", "log in", "sign in", "authenticate"],
            new Regex(@"\b(?:log\s*in|sign\s*in|authenticate|login)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // ── Advanced ───────────────────────────────────────────────
        (IntentKind.DragAndDrop, 0.8,
            ["drag", "drag and drop", "move element"],
            new Regex(@"\b(?:drag\s*(?:and\s*drop)?|move\s+(?:element|item))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.FillForm, 0.8,
            ["fill form", "fill out", "complete form", "submit form"],
            new Regex(@"\b(?:fill\s*(?:out|in)?|complete|submit)\s*(?:the\s+)?form\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.ChainedNavigation, 0.75,
            ["then click", "then navigate", "and then", "after that"],
            new Regex(@"\b(?:then\s+click|then\s+navigate|and\s+then|after\s+that)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.MultiWindowExtract, 0.75,
            ["copy from", "extract from", "get value from", "read from window"],
            new Regex(@"\b(?:copy|extract|get\s+value|read)\s+from\b.*\b(?:window|app)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        (IntentKind.RunRegression, 0.85,
            ["regression", "rerun", "run again", "re-test", "run all tests"],
            new Regex(@"\b(?:regression|re-?run|re-?test|run\s+all\s+tests)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    public IntentParse Classify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new IntentParse { RawInput = input ?? "", Kind = IntentKind.Unknown, Confidence = 0 };

        var normalized = input.Trim();
        var bestKind = IntentKind.Unknown;
        var bestConfidence = 0.0;
        var matchedKeywords = new List<string>();

        foreach (var (kind, baseConf, keywords, pattern) in _rules)
        {
            // Keyword match
            var kwHits = keywords.Where(kw =>
                normalized.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();

            // Regex match
            var regexMatch = pattern?.IsMatch(normalized) ?? false;

            if (kwHits.Count == 0 && !regexMatch)
                continue;

            // Score: keyword density + regex bonus
            var kwScore = (double)kwHits.Count / keywords.Length;
            var score = baseConf * (0.6 + 0.3 * kwScore + (regexMatch ? 0.1 : 0));

            if (score > bestConfidence)
            {
                bestConfidence = score;
                bestKind = kind;
                matchedKeywords = kwHits;
            }
        }

        var result = new IntentParse
        {
            RawInput = normalized,
            Kind = bestKind,
            Confidence = Math.Round(bestConfidence, 3),
            MatchedKeywords = matchedKeywords
        };

        // Extract slots based on intent kind
        ExtractSlots(result);

        return result;
    }

    private static void ExtractSlots(IntentParse parse)
    {
        var input = parse.RawInput;

        switch (parse.Kind)
        {
            case IntentKind.BrowserSearch:
                var searchMatch = Regex.Match(input,
                    @"(?:search|google|look\s*up|find)\s+(?:for\s+|about\s+)?(.+)",
                    RegexOptions.IgnoreCase);
                if (searchMatch.Success)
                    parse.Slots["query"] = searchMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.BrowserNavigate:
                var urlMatch = Regex.Match(input,
                    @"(https?://\S+|www\.\S+|[\w.-]+\.(?:com|org|net|io|dev|ai)\S*)",
                    RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                    parse.Slots["url"] = urlMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.LaunchApp:
            case IntentKind.FocusApp:
                var appMatch = Regex.Match(input,
                    @"(?:launch|start|open|run|focus|switch\s*to|activate)\s+(?:app\s+|program\s+)?(.+?)(?:\s+app|\s+program)?$",
                    RegexOptions.IgnoreCase);
                if (appMatch.Success)
                    parse.Slots["app"] = appMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.TakeScreenshot:
                // Optional: description of what to capture
                var descMatch = Regex.Match(input,
                    @"(?:screenshot|capture)\s+(?:of\s+)?(.+)",
                    RegexOptions.IgnoreCase);
                if (descMatch.Success)
                    parse.Slots["target"] = descMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.Login:
                var siteMatch = Regex.Match(input,
                    @"(?:log\s*in|sign\s*in)\s+(?:to\s+)?(.+)",
                    RegexOptions.IgnoreCase);
                if (siteMatch.Success)
                    parse.Slots["target"] = siteMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.ToggleSetting:
                var settingMatch = Regex.Match(input,
                    @"(?:toggle|enable|disable|turn\s*on|turn\s*off)\s+(.+)",
                    RegexOptions.IgnoreCase);
                if (settingMatch.Success)
                    parse.Slots["setting"] = settingMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.OpenFile:
                var fileMatch = Regex.Match(input,
                    @"(?:open|read|load)\s+(?:file\s+|document\s+)?[""']?([^""']+)[""']?",
                    RegexOptions.IgnoreCase);
                if (fileMatch.Success)
                    parse.Slots["path"] = fileMatch.Groups[1].Value.Trim();
                break;

            case IntentKind.ExportFile:
                var exportMatch = Regex.Match(input,
                    @"(?:export|save)\s+(?:as\s+|to\s+)?(.+)",
                    RegexOptions.IgnoreCase);
                if (exportMatch.Success)
                    parse.Slots["path"] = exportMatch.Groups[1].Value.Trim();
                break;
        }
    }
}

#endregion

#region TemplateScorer

/// <summary>
/// Scores templates by intent-kind match + slot fill rate.
/// </summary>
public class DefaultTemplateScorer : ITemplateScorer
{
    public List<TemplateScoringResult> Score(IntentParse intent, IReadOnlyList<IFlowTemplate> templates)
    {
        var results = new List<TemplateScoringResult>();

        foreach (var tmpl in templates)
        {
            if (!tmpl.CanHandle(intent))
                continue;

            var scoring = new TemplateScoringResult { Template = tmpl };

            // Required slots
            foreach (var slot in tmpl.RequiredSlots)
            {
                if (intent.Slots.ContainsKey(slot))
                    scoring.FilledRequiredSlots.Add(slot);
                else
                    scoring.MissingRequiredSlots.Add(slot);
            }

            // Optional slots
            foreach (var slot in tmpl.OptionalSlots)
            {
                if (intent.Slots.ContainsKey(slot))
                    scoring.FilledOptionalSlots.Add(slot);
                else
                    scoring.MissingOptionalSlots.Add(slot);
            }

            // Score calculation:
            //   Base = intent.Confidence
            //   × required slot fill rate (mandatory multiplier)
            //   + optional slot bonus (small additive)
            var reqTotal = tmpl.RequiredSlots.Count;
            var reqFilled = scoring.FilledRequiredSlots.Count;
            var reqRate = reqTotal > 0 ? (double)reqFilled / reqTotal : 1.0;

            var optTotal = tmpl.OptionalSlots.Count;
            var optFilled = scoring.FilledOptionalSlots.Count;
            var optBonus = optTotal > 0 ? 0.05 * ((double)optFilled / optTotal) : 0;

            // Experimental templates get a small penalty
            var maturityPenalty = tmpl.Maturity == TemplateMaturity.Experimental ? 0.05 : 0;

            scoring.Score = Math.Round(
                Math.Clamp(intent.Confidence * reqRate + optBonus - maturityPenalty, 0, 1.0),
                3);

            results.Add(scoring);
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }
}

#endregion

#region EscalationPolicy

/// <summary>
/// Standard 3-tier escalation policy.
/// Considers confidence + required slots + RiskLevel + execution context.
/// </summary>
public class StandardEscalationPolicy : IEscalationPolicy
{
    public EscalationDecision Evaluate(TemplateScoringResult scoring, IntentParse intent, ExecutionContext context)
    {
        var decision = new EscalationDecision
        {
            Intent = intent,
            Template = scoring.Template
        };

        var score = scoring.Score;
        var risk = scoring.Template.RiskLevel;
        var maturity = scoring.Template.Maturity;

        // ── Tier 1: LLM Handoff (<0.5 or missing required slots) ─────────
        if (score < 0.5 || !scoring.AllRequiredSlotsFilled)
        {
            decision.Tier = EscalationTier.LlmHandoff;
            decision.Reason = score < 0.5
                ? $"Low confidence ({score:P0}). Routing to LLM for full analysis."
                : $"Missing required slots: {string.Join(", ", scoring.MissingRequiredSlots)}. Needs LLM assistance.";
            decision.Template = null;
            decision.DraftFlow = null;
            return decision;
        }

        // ── Build draft flow (needed for Confirm and AutoExecute) ─────────
        try
        {
            decision.DraftFlow = scoring.Template.BuildFlow(intent);
        }
        catch
        {
            decision.Tier = EscalationTier.LlmHandoff;
            decision.Reason = "Template failed to build flow. Routing to LLM.";
            decision.Template = null;
            return decision;
        }

        // ── Tier 3: Auto-execute ─────────────────────────────────────────
        //    ≥0.8 + all required slots + Normal risk + Core maturity
        //    (or trust mode overrides risk check for interactive sessions)
        if (score >= 0.8
            && scoring.AllRequiredSlotsFilled
            && risk == RiskLevel.Normal
            && maturity == TemplateMaturity.Core
            && !context.IsPackPipeline) // Pack pipelines always confirm
        {
            // Trust mode: auto-execute even non-Core
            decision.Tier = EscalationTier.AutoExecute;
            decision.Reason = $"High confidence ({score:P0}), normal risk, core template. Auto-executing.";
            return decision;
        }

        // ── Tier 2: Confirm (everything else 0.5–1.0) ───────────────────
        decision.Tier = EscalationTier.Confirm;

        if (risk >= RiskLevel.High)
            decision.Reason = $"Risk level is {risk}. Confirmation required regardless of confidence ({score:P0}).";
        else if (maturity == TemplateMaturity.Experimental)
            decision.Reason = $"Experimental template. Confirmation required (confidence: {score:P0}).";
        else if (score < 0.8)
            decision.Reason = $"Moderate confidence ({score:P0}). Please confirm before execution.";
        else if (context.IsPackPipeline)
            decision.Reason = $"Pack pipeline mode. All templates require confirmation (confidence: {score:P0}).";
        else
            decision.Reason = $"Confirmation required (confidence: {score:P0}).";

        return decision;
    }
}

#endregion

#region Orchestrator

/// <summary>
/// Orchestrates the full intent splitting pipeline: Classify → Score → Escalate.
/// This is the main entry point for Commands mode.
/// </summary>
public class IntentSplitterService
{
    private readonly IIntentClassifier _classifier;
    private readonly ITemplateScorer _scorer;
    private readonly IEscalationPolicy _policy;
    private readonly TemplateRegistry _registry;
    private readonly LogService _log;

    public IntentSplitterService(
        TemplateRegistry registry,
        LogService log,
        IIntentClassifier? classifier = null,
        ITemplateScorer? scorer = null,
        IEscalationPolicy? policy = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _classifier = classifier ?? new KeywordIntentClassifier();
        _scorer = scorer ?? new DefaultTemplateScorer();
        _policy = policy ?? new StandardEscalationPolicy();
    }

    /// <summary>
    /// Run the full pipeline: natural language → IntentParse → scored templates → escalation decision.
    /// </summary>
    public IntentSplitResult Split(string input, ExecutionContext? context = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        context ??= new ExecutionContext();

        // Phase 1: Classify
        var intent = _classifier.Classify(input);
        _log.Debug("IntentSplitter", $"Classified '{input}' → {intent.Kind} (confidence: {intent.Confidence:P0}, slots: {intent.Slots.Count})");

        // Phase 2: Score templates
        var candidates = _scorer.Score(intent, _registry.All);
        _log.Debug("IntentSplitter", $"Scored {candidates.Count} candidate template(s)");

        // Phase 3: Escalation decision
        EscalationDecision decision;
        if (candidates.Count == 0)
        {
            decision = new EscalationDecision
            {
                Tier = EscalationTier.LlmHandoff,
                Reason = intent.Kind == IntentKind.Unknown
                    ? "Could not classify intent. Routing to LLM."
                    : $"No template matches intent '{intent.Kind}'. Routing to LLM.",
                Intent = intent,
                AllCandidates = candidates
            };
        }
        else
        {
            decision = _policy.Evaluate(candidates[0], intent, context);
            decision.AllCandidates = candidates;
        }

        sw.Stop();
        _log.Info("IntentSplitter",
            $"Split result: {decision.Tier} | template={decision.Template?.TemplateId ?? "none"} | {sw.ElapsedMilliseconds}ms");

        return new IntentSplitResult
        {
            Intent = intent,
            Decision = decision,
            ProcessingMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Splits a compound natural-language instruction into multiple clauses and resolves
    /// each one independently. Useful for the Teach panel where users describe multi-step
    /// automations like "Open Chrome, go to YouTube, and search for music".
    /// </summary>
    /// <param name="input">The compound instruction (may contain commas, "and", "then").</param>
    /// <returns>
    /// One <see cref="MultiIntentResult"/> per detected clause. If splitting produces
    /// zero clauses the original input is returned as a single entry.
    /// </returns>
    public List<MultiIntentResult> SplitMultiple(string input)
    {
        var results = new List<MultiIntentResult>();
        if (string.IsNullOrWhiteSpace(input))
            return results;

        // ── Phase 1: Clause extraction ───────────────────────────────────────
        // Split on common delimiters: comma+space, " and ", " then ", ". "
        var delimiters = new[] { ", ", " and then ", " then ", " and ", ". " };
        var clauses = new List<string> { input };

        foreach (var delim in delimiters)
        {
            var next = new List<string>();
            foreach (var clause in clauses)
            {
                var parts = clause.Split(delim, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                next.AddRange(parts);
            }
            clauses = next;
        }

        // Remove empties and very short fragments
        clauses = clauses.Where(c => c.Length >= 3).ToList();
        if (clauses.Count == 0)
            clauses = [input]; // Fallback to original

        _log.Debug("IntentSplitter", $"SplitMultiple: {clauses.Count} clause(s) from \"{input}\"");

        // ── Phase 2: Run each clause through the single-intent pipeline ──────
        foreach (var clause in clauses)
        {
            var splitResult = Split(clause);
            var templateId = splitResult.Decision.Template?.TemplateId;
            var slots = new Dictionary<string, string>(splitResult.Intent.Slots);

            results.Add(new MultiIntentResult
            {
                Clause = clause,
                TemplateId = templateId,
                Slots = slots,
                Confidence = splitResult.Intent.Confidence,
                Tier = splitResult.Decision.Tier
            });
        }

        return results;
    }
}

/// <summary>
/// Result of a single clause in a multi-intent split.
/// </summary>
public class MultiIntentResult
{
    /// <summary>The original clause text.</summary>
    public string Clause { get; set; } = "";

    /// <summary>Matched template ID (null if none matched).</summary>
    public string? TemplateId { get; set; }

    /// <summary>Extracted slot values for this clause.</summary>
    public Dictionary<string, string> Slots { get; set; } = [];

    /// <summary>Classifier confidence for this clause.</summary>
    public double Confidence { get; set; }

    /// <summary>Escalation tier for this clause.</summary>
    public EscalationTier Tier { get; set; }
}

#endregion

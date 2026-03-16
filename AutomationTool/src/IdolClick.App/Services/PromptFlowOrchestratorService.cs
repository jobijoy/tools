using IdolClick.Models;

namespace IdolClick.Services;

public sealed class PromptFlowOrchestratorService
{
    private readonly IntentSplitterService _intentSplitter;
    private readonly StepExecutor _flowExecutor;
    private readonly ReportService _reports;
    private readonly FlowValidatorService _validator;
    private readonly LogService _log;

    public PromptFlowOrchestratorService(
        IntentSplitterService intentSplitter,
        StepExecutor flowExecutor,
        ReportService reports,
        LogService log)
    {
        _intentSplitter = intentSplitter ?? throw new ArgumentNullException(nameof(intentSplitter));
        _flowExecutor = flowExecutor ?? throw new ArgumentNullException(nameof(flowExecutor));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _validator = new FlowValidatorService(log);
    }

    public async Task<PromptFlowRunResult> RunAsync(
        string input,
        ExecutionContext? context,
        bool autoConfirm,
        CancellationToken ct = default)
    {
        var split = _intentSplitter.Split(input, context);
        var result = new PromptFlowRunResult
        {
            Input = input,
            SplitResult = split,
            TemplateId = split.Decision.Template?.TemplateId ?? string.Empty,
            TemplateName = split.Decision.Template?.DisplayName ?? string.Empty,
            Tier = split.Decision.Tier,
            Reason = split.Decision.Reason,
            DraftFlow = split.Decision.DraftFlow,
            RequiresConfirmation = split.Decision.Tier == EscalationTier.Confirm
        };

        if (split.Decision.DraftFlow == null)
        {
            result.Error = split.Decision.Reason;
            return result;
        }

        var validation = _validator.Validate(split.Decision.DraftFlow);
        result.ValidationErrors = validation.Errors;
        result.ValidationWarnings = validation.Warnings;

        if (!validation.IsValid)
        {
            result.Error = $"Draft flow validation failed: {string.Join("; ", validation.Errors)}";
            return result;
        }

        if (split.Decision.Tier == EscalationTier.LlmHandoff)
        {
            result.Error = split.Decision.Reason;
            return result;
        }

        if (split.Decision.Tier == EscalationTier.Confirm && !autoConfirm)
        {
            result.Error = "Confirmation required before execution.";
            return result;
        }

        _log.Info("PromptFlow", $"Executing prompt-driven flow for '{input}' using template '{result.TemplateId ?? "none"}'");
        var report = await _flowExecutor.ExecuteFlowAsync(split.Decision.DraftFlow, cancellationToken: ct).ConfigureAwait(false);
        result.Executed = true;
        result.Report = report;
        result.ReportPath = _reports.SaveReport(report);
        result.Succeeded = string.Equals(report.Result, "passed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(report.Result, "passed_with_warnings", StringComparison.OrdinalIgnoreCase);
        return result;
    }
}

public sealed class PromptFlowRunResult
{
    public string Input { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public EscalationTier Tier { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool RequiresConfirmation { get; set; }
    public bool Executed { get; set; }
    public bool Succeeded { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public TestFlow? DraftFlow { get; set; }
    public ExecutionReport? Report { get; set; }
    public IntentSplitResult? SplitResult { get; set; }
    public List<string> ValidationErrors { get; set; } = [];
    public List<string> ValidationWarnings { get; set; } = [];
}
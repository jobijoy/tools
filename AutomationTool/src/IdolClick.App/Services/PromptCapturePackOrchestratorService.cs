using System.Text.RegularExpressions;
using IdolClick.Models;

namespace IdolClick.Services;

public sealed class PromptCapturePackOrchestratorService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ReportService _reports;
    private readonly SnapCaptureService _snapCapture;
    private readonly CaptureAnnotationService _annotations;
    private readonly StepExecutor _flowExecutor;

    public PromptCapturePackOrchestratorService(
        ConfigService config,
        LogService log,
        ReportService reports,
        SnapCaptureService snapCapture,
        CaptureAnnotationService annotations,
        StepExecutor flowExecutor)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _snapCapture = snapCapture ?? throw new ArgumentNullException(nameof(snapCapture));
        _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        _flowExecutor = flowExecutor ?? throw new ArgumentNullException(nameof(flowExecutor));
    }

    public Task<PromptCapturePackRunResult> RunAsync(
        string input,
        bool autoConfirm,
        bool smokeMode,
        CancellationToken ct = default)
    {
        var resolution = Resolve(input);
        var result = new PromptCapturePackRunResult
        {
            Input = input,
            PackId = resolution.PackId,
            PackName = resolution.PackName,
            PackPath = resolution.PackPath,
            Reason = resolution.Reason,
            ResolvedInputs = resolution.Inputs,
            RequiresConfirmation = true
        };

        if (string.IsNullOrWhiteSpace(resolution.PackPath))
        {
            result.Error = resolution.Reason;
            return Task.FromResult(result);
        }

        if (!autoConfirm)
        {
            result.Error = "Confirmation required before execution.";
            return Task.FromResult(result);
        }

        return ExecuteAsync(result, smokeMode, ct);
    }

    public PromptCapturePackResolution Resolve(string input)
    {
        var normalized = input.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new PromptCapturePackResolution
            {
                Reason = "A capture-pack instruction is required."
            };
        }

        if (ContainsAny(normalized, "youtube", "video", "watch"))
        {
            return BuildResolution(
                "youtube-video-monitor.capture-profile.json",
                "youtube-video-monitor",
                "YouTube Video Monitor",
                "Matched a YouTube/video monitoring request.");
        }

        if (ContainsAny(normalized, "yahoo finance", "stock", "ticker", "quote", "price"))
        {
            var symbol = ExtractTickerSymbol(normalized) ?? "AAPL";
            return BuildResolution(
                "finance-yahoo-stock-monitor.capture-profile.json",
                "finance-yahoo-stock-monitor",
                "Finance Yahoo Stock Monitor",
                $"Matched a finance/quote monitoring request for {symbol}.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["symbol"] = symbol
                });
        }

        if (ContainsAny(normalized, "google", "search"))
        {
            var query = ExtractSearchQuery(normalized) ?? normalized;
            return BuildResolution(
                "google-search-monitor.capture-profile.json",
                "google-search-monitor",
                "Google Search Monitor",
                $"Matched a search monitoring request for '{query}'.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = query,
                    ["queryEncoded"] = Uri.EscapeDataString(query)
                });
        }

        return new PromptCapturePackResolution
        {
            Reason = "No canonical capture pack matched the instruction. Try a Google search, Yahoo Finance quote, or YouTube monitoring prompt."
        };
    }

    private async Task<PromptCapturePackRunResult> ExecuteAsync(
        PromptCapturePackRunResult result,
        bool smokeMode,
        CancellationToken ct)
    {
        _log.Info("PromptCapturePack", $"Executing prompt-driven capture pack for '{result.Input}' using '{result.PackId}'");
        var selectorParser = new SelectorParser(_log);
        var runner = new CaptureProfilePackRunnerService(_config, _log, _reports, _snapCapture, _annotations, selectorParser, _flowExecutor);
        var report = await runner.RunAsync(result.PackPath, smokeMode, result.ResolvedInputs, ct).ConfigureAwait(false);

        result.Executed = true;
        result.Report = report;
        result.ReportPath = report.ReportPath;
        result.Succeeded = report.Succeeded;
        result.Error = report.Error;
        return result;
    }

    private PromptCapturePackResolution BuildResolution(
        string fileName,
        string packId,
        string packName,
        string reason,
        Dictionary<string, string>? inputs = null)
    {
        var path = Path.Combine(GetCapturePackDirectory(), fileName);
        return new PromptCapturePackResolution
        {
            PackId = packId,
            PackName = packName,
            PackPath = path,
            Reason = reason,
            Inputs = inputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private string GetCapturePackDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "examples", "capture-profiles");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "examples", "capture-profiles");
    }

    private static bool ContainsAny(string input, params string[] values)
        => values.Any(value => input.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractSearchQuery(string input)
    {
        var searchPatterns = new[]
        {
            @"(?:search|google)\s+for\s+(?<query>.+)$",
            @"(?:monitor|track)\s+(?:google\s+)?search\s+(?:for\s+)?(?<query>.+)$"
        };

        foreach (var pattern in searchPatterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var query = match.Groups["query"].Value.Trim(' ', '"', '\'');
                if (!string.IsNullOrWhiteSpace(query))
                    return query;
            }
        }

        return null;
    }

    private static string? ExtractTickerSymbol(string input)
    {
        var keyedMatch = Regex.Match(input, @"(?:ticker|symbol|stock|quote)\s+(?<symbol>[A-Za-z]{1,5})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (keyedMatch.Success)
            return keyedMatch.Groups["symbol"].Value.ToUpperInvariant();

        var explicitTicker = Regex.Match(input, @"\b[A-Z]{1,5}\b", RegexOptions.CultureInvariant);
        if (explicitTicker.Success)
            return explicitTicker.Value.ToUpperInvariant();

        return null;
    }
}

public sealed class PromptCapturePackRunResult
{
    public string Input { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    public string PackPath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool RequiresConfirmation { get; set; }
    public bool Executed { get; set; }
    public bool Succeeded { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public Dictionary<string, string> ResolvedInputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CapturePackRunResult? Report { get; set; }
}

public sealed class PromptCapturePackResolution
{
    public string PackId { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    public string PackPath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

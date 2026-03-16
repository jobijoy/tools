using IdolClick.Models;

namespace IdolClick.Services;

public sealed class PromptPackHistoryService
{
    private readonly ConfigService _config;

    public PromptPackHistoryService(ConfigService config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public IReadOnlyList<PromptPackHistoryEntry> GetRecentEntries()
    {
        var capture = _config.GetConfig().Capture;
        return capture.PromptPackHistory
            .OrderByDescending(entry => entry.LastRunUtc)
            .ToList();
    }

    public void RecordRun(string prompt, PromptCapturePackRunResult result, bool smokeMode)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !result.Executed)
            return;

        var cfg = _config.GetConfig();
        var capture = cfg.Capture;
        var limit = Math.Max(1, capture.MaxPromptPackHistoryEntries);

        capture.PromptPackHistory.RemoveAll(entry =>
            string.Equals(entry.Prompt, prompt, StringComparison.OrdinalIgnoreCase));

        capture.PromptPackHistory.Insert(0, new PromptPackHistoryEntry
        {
            Prompt = prompt,
            PackId = result.PackId,
            PackName = result.PackName,
            ReportPath = result.ReportPath,
            Succeeded = result.Succeeded,
            SmokeMode = smokeMode,
            LastRunUtc = DateTime.UtcNow
        });

        if (capture.PromptPackHistory.Count > limit)
            capture.PromptPackHistory = capture.PromptPackHistory.Take(limit).ToList();

        _config.SaveConfig(cfg);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdolClick.Models;

// ═══════════════════════════════════════════════════════════════════════════════════
// SMOKE TEST MODELS — Automated end-to-end integration tests for the agent pipeline.
//
// Each SmokeTest sends one or more natural-language prompts to the LLM agent
// sequentially, waits for each tool-calling loop to complete, optionally captures
// screenshots between steps, then runs deterministic verifications against the
// actual desktop state.
//
// Tests can be defined in code (GetBuiltInTests/GetAdvancedTests) or loaded from
// external JSON files (SmokeTestFile) for fully parameterized autonomous runs.
//
// This tests the FULL pipeline: LLM reasoning → tool selection → flow generation →
// flow execution → window automation → screenshot verification.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single end-to-end smoke test case.
/// Supports both single-prompt and multi-step sequential prompt workflows.
/// </summary>
public class SmokeTest
{
    /// <summary>Short identifier (e.g., "ST-01", "CUSTOM-01").</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable test name.</summary>
    public string Name { get; set; } = "";

    /// <summary>What this test verifies.</summary>
    public string Description { get; set; } = "";

    /// <summary>Difficulty tier for filtering and grouping.</summary>
    public TestDifficulty Difficulty { get; set; } = TestDifficulty.Simple;

    /// <summary>
    /// Legacy single-prompt field. When Steps is empty, this is the sole prompt.
    /// When Steps is populated, this is ignored (use Steps[].Prompt instead).
    /// </summary>
    public string AgentPrompt { get; set; } = "";

    /// <summary>
    /// Multi-step sequential prompts. Each step is sent to the agent in order.
    /// When non-empty, <see cref="AgentPrompt"/> is ignored.
    /// Screenshots can be captured between steps for visual verification.
    /// </summary>
    public List<SmokeTestStep> Steps { get; set; } = [];

    /// <summary>Maximum time for the entire test (all steps) in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Post-execution checks that must ALL pass for the test to pass.</summary>
    public List<SmokeVerification> Verifications { get; set; } = [];

    /// <summary>Processes to kill after the test (cleanup). Use exact process names.</summary>
    public List<string> CleanupProcesses { get; set; } = [];

    /// <summary>
    /// Returns the effective steps for this test.
    /// If Steps is populated, returns Steps. Otherwise wraps AgentPrompt into a single step.
    /// </summary>
    public IReadOnlyList<SmokeTestStep> GetEffectiveSteps()
    {
        if (Steps.Count > 0) return Steps;
        if (!string.IsNullOrWhiteSpace(AgentPrompt))
            return [new SmokeTestStep { Prompt = AgentPrompt }];
        return [];
    }
}

/// <summary>
/// A single step within a multi-step smoke test.
/// Each step sends one prompt to the agent and optionally captures a screenshot.
/// </summary>
public class SmokeTestStep
{
    /// <summary>Natural-language prompt sent to the LLM agent for this step.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>When true, a verification screenshot is captured after this step completes.</summary>
    public bool ScreenshotAfter { get; set; }

    /// <summary>Optional label for the screenshot file (e.g., "after-calc-open"). Auto-generated if empty.</summary>
    public string ScreenshotLabel { get; set; } = "";

    /// <summary>Delay in ms after this step before the next one starts. Allows UI to settle.</summary>
    public int DelayAfterMs { get; set; } = 1000;

    /// <summary>When true, clear agent conversation history before this step.</summary>
    public bool ClearHistory { get; set; }

    /// <summary>Optional intermediate verifications run after this step (before the next step starts).</summary>
    public List<SmokeVerification> Verifications { get; set; } = [];
}

/// <summary>Test complexity tier.</summary>
public enum TestDifficulty
{
    /// <summary>Single-app, 1-2 steps (launch, screenshot).</summary>
    Simple,
    /// <summary>Multi-step within one or two apps.</summary>
    Medium,
    /// <summary>Multi-app workflows, chained actions, data verification.</summary>
    Complex
}

/// <summary>
/// A single verification check run after the agent completes.
/// </summary>
public class SmokeVerification
{
    /// <summary>What kind of check to perform.</summary>
    public SmokeVerificationType Type { get; set; }

    /// <summary>
    /// Target depends on Type:
    /// - ProcessRunning: process name (e.g., "CalculatorApp")
    /// - WindowTitleContains: substring to search in window titles
    /// - ResponseContains: substring to search in agent response text
    /// - ScreenshotCreated: ignored (checks _screenshots/ dir)
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>Human-readable description shown in results.</summary>
    public string? Description { get; set; }
}

public enum SmokeVerificationType
{
    /// <summary>A process with the target name is running.</summary>
    ProcessRunning,

    /// <summary>A top-level window title contains the target string.</summary>
    WindowTitleContains,

    /// <summary>The agent's final response text contains the target string.</summary>
    ResponseContains,

    /// <summary>A screenshot file was created in the last 2 minutes.</summary>
    ScreenshotCreated,

    /// <summary>
    /// Takes a fresh screenshot and uses Vision OCR to check that the target text
    /// is visible on screen. Enables visual verification of UI state.
    /// </summary>
    ScreenshotContainsText
}

/// <summary>
/// Result of running a single smoke test.
/// </summary>
public class SmokeTestResult
{
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public SmokeTestStatus Status { get; set; }
    public string AgentResponse { get; set; } = "";
    public List<string> LogEntries { get; set; } = [];
    public List<VerificationResult> Verifications { get; set; } = [];
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Result of a single verification check.
/// </summary>
public class VerificationResult
{
    public string Description { get; set; } = "";
    public bool Passed { get; set; }
    public string? Detail { get; set; }
}

public enum SmokeTestStatus
{
    NotStarted,
    Running,
    Passed,
    Failed,
    Error,
    Skipped
}

/// <summary>
/// Aggregated result of running the full smoke test suite.
/// </summary>
public class SmokeTestSuiteResult
{
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public List<SmokeTestResult> Results { get; set; } = [];
    public int PassedCount => Results.Count(r => r.Status == SmokeTestStatus.Passed);
    public int FailedCount => Results.Count(r => r.Status is SmokeTestStatus.Failed or SmokeTestStatus.Error);
    public int TotalCount => Results.Count;
    public bool AllPassed => Results.All(r => r.Status == SmokeTestStatus.Passed);
    public long TotalElapsedMs => Results.Sum(r => r.ElapsedMs);
}

// ═══════════════════════════════════════════════════════════════════════════════════
// EXTERNAL TEST FILE FORMAT — JSON-serializable smoke test definitions.
//
// Enables fully parameterized autonomous runs:
//   IdolClick.exe --smoke --file my-tests.json
//
// See schemas/smoke-test.schema.json for the full JSON Schema.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Root object for an external smoke test JSON file.
/// Deserializes from the smoke-test.schema.json format.
/// </summary>
public class SmokeTestFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("defaults")]
    public SmokeTestFileDefaults? Defaults { get; set; }

    [JsonPropertyName("tests")]
    public List<SmokeTestFileEntry> Tests { get; set; } = [];

    /// <summary>JSON deserialization options used throughout smoke test file loading.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Loads a smoke test file from disk and converts entries to <see cref="SmokeTest"/> instances.
    /// Merges file-level defaults into each test.
    /// </summary>
    public static List<SmokeTest> LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Smoke test file not found: {filePath}");

        var json = File.ReadAllText(filePath);
        var file = JsonSerializer.Deserialize<SmokeTestFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize smoke test file: {filePath}");

        if (file.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported schemaVersion {file.SchemaVersion} (expected 1)");

        if (file.Tests.Count == 0)
            throw new InvalidOperationException("Smoke test file contains no tests");

        var defaults = file.Defaults ?? new SmokeTestFileDefaults();
        var results = new List<SmokeTest>();

        foreach (var entry in file.Tests)
        {
            var test = new SmokeTest
            {
                Id = entry.Id,
                Name = entry.Name,
                Description = entry.Description ?? "",
                Difficulty = entry.Difficulty ?? TestDifficulty.Medium,
                TimeoutSeconds = entry.TimeoutSeconds ?? defaults.TimeoutSeconds,
                CleanupProcesses = entry.CleanupProcesses ?? []
            };

            // Convert steps: multi-step or single-prompt shorthand
            if (entry.Steps is { Count: > 0 })
            {
                test.Steps = entry.Steps.Select(s => new SmokeTestStep
                {
                    Prompt = s.Prompt,
                    ScreenshotAfter = s.ScreenshotAfter ?? defaults.ScreenshotAfterEachStep,
                    ScreenshotLabel = s.ScreenshotLabel ?? "",
                    DelayAfterMs = s.DelayAfterMs ?? defaults.DelayBetweenStepsMs,
                    ClearHistory = s.ClearHistory ?? false,
                    Verifications = ConvertVerifications(s.Verifications)
                }).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(entry.Prompt))
            {
                test.AgentPrompt = entry.Prompt;
            }
            else
            {
                throw new InvalidOperationException($"Test '{entry.Id}' has neither 'prompt' nor 'steps'");
            }

            // Convert verifications
            test.Verifications = ConvertVerifications(entry.Verifications);

            results.Add(test);
        }

        return results;
    }

    private static List<SmokeVerification> ConvertVerifications(List<SmokeTestFileVerification>? entries)
    {
        if (entries is null or { Count: 0 }) return [];
        return entries.Select(v => new SmokeVerification
        {
            Type = v.Type,
            Target = v.Target ?? "",
            Description = v.Description
        }).ToList();
    }
}

/// <summary>File-level defaults applied to all tests unless overridden.</summary>
public class SmokeTestFileDefaults
{
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 180;

    [JsonPropertyName("screenshotAfterEachStep")]
    public bool ScreenshotAfterEachStep { get; set; }

    [JsonPropertyName("delayBetweenStepsMs")]
    public int DelayBetweenStepsMs { get; set; } = 1000;
}

/// <summary>JSON-serializable smoke test entry (matches schema).</summary>
public class SmokeTestFileEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("difficulty")]
    public TestDifficulty? Difficulty { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("steps")]
    public List<SmokeTestFileStep>? Steps { get; set; }

    [JsonPropertyName("verifications")]
    public List<SmokeTestFileVerification>? Verifications { get; set; }

    [JsonPropertyName("cleanupProcesses")]
    public List<string>? CleanupProcesses { get; set; }
}

/// <summary>JSON-serializable test step.</summary>
public class SmokeTestFileStep
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("screenshotAfter")]
    public bool? ScreenshotAfter { get; set; }

    [JsonPropertyName("screenshotLabel")]
    public string? ScreenshotLabel { get; set; }

    [JsonPropertyName("delayAfterMs")]
    public int? DelayAfterMs { get; set; }

    [JsonPropertyName("clearHistory")]
    public bool? ClearHistory { get; set; }

    [JsonPropertyName("verifications")]
    public List<SmokeTestFileVerification>? Verifications { get; set; }
}

/// <summary>JSON-serializable verification entry.</summary>
public class SmokeTestFileVerification
{
    [JsonPropertyName("type")]
    public SmokeVerificationType Type { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

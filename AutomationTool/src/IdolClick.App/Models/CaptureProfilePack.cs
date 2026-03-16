using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdolClick.Models;

public class CaptureProfilePack
{
    public string? Schema { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string BootstrapFlowPath { get; set; } = string.Empty;
    public List<CapturePackInputDefinition> Inputs { get; set; } = [];
    public List<string> StatusSelectors { get; set; } = [];
    public List<CaptureStatusProbe> StatusProbes { get; set; } = [];
    public CaptureObservationPlan ObservationPlan { get; set; } = new();
    public CaptureQueuePlan Queue { get; set; } = new();
    public CaptureProfile CaptureProfile { get; set; } = new();

    public static CaptureProfilePack LoadFromFile(string path)
        => LoadFromFile(path, null, out _);

    public static CaptureProfilePack LoadFromFile(
        string path,
        IReadOnlyDictionary<string, string>? suppliedInputs,
        out Dictionary<string, string> resolvedInputs)
    {
        var json = File.ReadAllText(path);
        var pack = JsonSerializer.Deserialize<CaptureProfilePack>(json, CaptureProfilePackJson.Options)
            ?? throw new InvalidOperationException($"Failed to deserialize capture profile pack: {path}");
        pack = pack.ApplyInputs(suppliedInputs, out resolvedInputs);
        if (string.IsNullOrWhiteSpace(pack.PackId))
            throw new InvalidOperationException("Capture profile pack must contain packId.");
        if (pack.CaptureProfile == null || string.IsNullOrWhiteSpace(pack.CaptureProfile.Id))
            throw new InvalidOperationException("Capture profile pack must contain captureProfile payload.");
        return pack;
    }

    public CaptureProfilePack ApplyInputs(
        IReadOnlyDictionary<string, string>? suppliedInputs,
        out Dictionary<string, string> resolvedInputs)
    {
        resolvedInputs = ResolveInputs(suppliedInputs);
        var tokenValues = resolvedInputs;
        if (resolvedInputs.Count == 0)
            return this;

        var clone = JsonSerializer.Deserialize<CaptureProfilePack>(
            JsonSerializer.Serialize(this, CaptureProfilePackJson.Options),
            CaptureProfilePackJson.Options) ?? throw new InvalidOperationException("Failed to clone capture profile pack.");

        clone.Schema = ApplyInputTokens(clone.Schema, tokenValues);
        clone.PackId = ApplyInputTokens(clone.PackId, tokenValues);
        clone.Name = ApplyInputTokens(clone.Name, tokenValues);
        clone.Description = ApplyInputTokens(clone.Description, tokenValues);
        clone.Category = ApplyInputTokens(clone.Category, tokenValues);
        clone.BootstrapFlowPath = ApplyInputTokens(clone.BootstrapFlowPath, tokenValues);
        clone.StatusSelectors = clone.StatusSelectors.Select(value => ApplyInputTokens(value, tokenValues)).ToList();
        clone.StatusProbes = clone.StatusProbes.Select(probe => probe.ApplyInputs(tokenValues)).ToList();

        clone.ObservationPlan.TriggerMode = ApplyInputTokens(clone.ObservationPlan.TriggerMode, tokenValues);
        clone.ObservationPlan.StatusChangeHint = ApplyInputTokens(clone.ObservationPlan.StatusChangeHint, tokenValues);
        clone.ObservationPlan.Notes = ApplyInputTokens(clone.ObservationPlan.Notes, tokenValues);

        clone.Queue.QueueId = ApplyInputTokens(clone.Queue.QueueId, tokenValues);
        clone.Queue.ConsumerHint = ApplyInputTokens(clone.Queue.ConsumerHint, tokenValues);
        clone.Queue.AnalysisHint = ApplyInputTokens(clone.Queue.AnalysisHint, tokenValues);

        clone.CaptureProfile.Id = ApplyInputTokens(clone.CaptureProfile.Id, tokenValues);
        clone.CaptureProfile.Name = ApplyInputTokens(clone.CaptureProfile.Name, tokenValues);
        clone.CaptureProfile.FilePrefix = ApplyInputTokens(clone.CaptureProfile.FilePrefix, tokenValues);
        clone.CaptureProfile.OutputDirectory = ApplyInputTokens(clone.CaptureProfile.OutputDirectory, tokenValues);

        foreach (var target in clone.CaptureProfile.Targets)
        {
            target.Id = ApplyInputTokens(target.Id, tokenValues);
            target.Name = ApplyInputTokens(target.Name, tokenValues);
            target.ProcessName = ApplyInputTokens(target.ProcessName, tokenValues);
            target.WindowTitle = ApplyInputTokens(target.WindowTitle, tokenValues);
        }

        foreach (var input in clone.Inputs)
        {
            input.Name = ApplyInputTokens(input.Name, tokenValues);
            input.Description = ApplyInputTokens(input.Description, tokenValues);
            input.DefaultValue = ApplyInputTokens(input.DefaultValue, tokenValues);
            input.Example = ApplyInputTokens(input.Example, tokenValues);
        }

        return clone;
    }

    public Dictionary<string, string> ResolveInputs(IReadOnlyDictionary<string, string>? suppliedInputs)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        suppliedInputs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                continue;

            if (suppliedInputs.TryGetValue(input.Name, out var provided) && !string.IsNullOrWhiteSpace(provided))
            {
                resolved[input.Name] = provided;
            }
            else if (!string.IsNullOrWhiteSpace(input.DefaultValue))
            {
                resolved[input.Name] = input.DefaultValue;
            }
            else if (input.Required)
            {
                throw new InvalidOperationException($"Missing required capture pack input '{input.Name}'.");
            }
        }

        foreach (var pair in suppliedInputs)
        {
            if (!resolved.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                resolved[pair.Key] = pair.Value;
        }

        return resolved;
    }

    public static string ApplyInputTokens(string? value, IReadOnlyDictionary<string, string>? inputs)
    {
        if (string.IsNullOrEmpty(value) || inputs == null || inputs.Count == 0)
            return value ?? string.Empty;

        var resolved = value;
        foreach (var pair in inputs)
            resolved = resolved.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        return resolved;
    }

    public IReadOnlyList<CaptureStatusProbe> GetEffectiveStatusProbes()
    {
        if (StatusProbes.Count > 0)
            return StatusProbes;

        return StatusSelectors.Select(selector => new CaptureStatusProbe
        {
            Name = selector,
            Kind = CaptureStatusProbeKind.SelectorValue,
            Selector = selector,
            Required = false,
            UseForChangeDetection = true
        }).ToList();
    }
}

public class CapturePackInputDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string DefaultValue { get; set; } = string.Empty;
    public string Example { get; set; } = string.Empty;
}

public enum CaptureStatusProbeKind
{
    WindowTitle,
    SelectorExists,
    SelectorValue
}

public class CaptureStatusProbe
{
    public string Name { get; set; } = string.Empty;
    public CaptureStatusProbeKind Kind { get; set; } = CaptureStatusProbeKind.SelectorValue;
    public string Selector { get; set; } = string.Empty;
    public string Contains { get; set; } = string.Empty;
    [JsonPropertyName("equals")]
    public string EqualsValue { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool UseForChangeDetection { get; set; } = true;

    public CaptureStatusProbe ApplyInputs(IReadOnlyDictionary<string, string>? inputs)
    {
        return new CaptureStatusProbe
        {
            Name = CaptureProfilePack.ApplyInputTokens(Name, inputs),
            Kind = Kind,
            Selector = CaptureProfilePack.ApplyInputTokens(Selector, inputs),
            Contains = CaptureProfilePack.ApplyInputTokens(Contains, inputs),
            EqualsValue = CaptureProfilePack.ApplyInputTokens(EqualsValue, inputs),
            Required = Required,
            UseForChangeDetection = UseForChangeDetection
        };
    }
}

public class CaptureObservationPlan
{
    public string TriggerMode { get; set; } = "manual";
    public int IntervalSeconds { get; set; }
    public int DurationSeconds { get; set; }
    public int AudioClipSeconds { get; set; }
    public bool QueueSnapshots { get; set; }
    public string StatusChangeHint { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class CaptureQueuePlan
{
    public bool Enabled { get; set; }
    public string QueueId { get; set; } = string.Empty;
    public string ConsumerHint { get; set; } = string.Empty;
    public string AnalysisHint { get; set; } = string.Empty;
}

internal static class CaptureProfilePackJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
namespace IdolClick.Models;

/// <summary>
/// Application operating mode.
/// </summary>
public enum AppMode
{
    /// <summary>Instinct — automatic rule-based background automation (reflexes).</summary>
    Classic,
    /// <summary>Reason — AI agent mode with natural language interaction (deliberate thinking).</summary>
    Agent,
    /// <summary>Teach — Smart Sentence Builder for creating reusable automations (learning).</summary>
    Teach,
    /// <summary>Capture — reusable visual snapshots for windows and regions.</summary>
    Capture
}

/// <summary>
/// Root application configuration containing global settings and automation rules.
/// Serialized to/from config.json in the application directory.
/// </summary>
/// <remarks>
/// <para>Configuration is auto-reloaded when the file changes on disk.</para>
/// <para>Schema versioning is handled at the <see cref="Rule"/> level.</para>
/// </remarks>
public class AppConfig
{
    /// <summary>
    /// Global application settings affecting all rules and behaviors.
    /// </summary>
    public GlobalSettings Settings { get; set; } = new();

    /// <summary>
    /// Top-level AI and Foundry-facing configuration.
    /// This is the canonical location for endpoint, model, and voice settings.
    /// </summary>
    public AppAiSettings Ai { get; set; } = new();
    
    /// <summary>
    /// AI agent configuration for LLM-backed automation.
    /// Retained as a compatibility mirror for runtime code and older config files.
    /// </summary>
    public AgentSettings AgentSettings { get; set; } = new();
    
    /// <summary>
    /// Collection of automation rules to evaluate each polling cycle.
    /// </summary>
    public List<Rule> Rules { get; set; } = [];

    /// <summary>
    /// Centralized timing constants for the execution pipeline.
    /// Externalizes magic numbers so they can be tuned without recompilation.
    /// </summary>
    public TimingSettings Timing { get; set; } = new();

    /// <summary>
    /// Generic capture utility settings and saved profiles.
    /// </summary>
    public CaptureWorkspaceSettings Capture { get; set; } = new();

    /// <summary>
    /// Rolling review buffer settings for Week 1 recording.
    /// </summary>
    public ReviewBufferSettings Review { get; set; } = new();
}

/// <summary>
/// Top-level AI configuration used across the application.
/// This mirrors the runtime agent settings so the public config shape can stay app-centric.
/// </summary>
public class AppAiSettings
{
    public string Endpoint { get; set; } = "";
    public string ModelId { get; set; } = "gpt-4o";
    public string ApiKey { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; }
    public bool VisionFallbackEnabled { get; set; }
    public double VisionConfidenceThreshold { get; set; } = 0.7;
    public string VisionModelId { get; set; } = "";
    public bool VoiceInputEnabled { get; set; } = true;
    public string WhisperDeploymentId { get; set; } = "whisper";
    public string WhisperEndpoint { get; set; } = "";
    public string WhisperApiKey { get; set; } = "";
    public string VoiceLanguage { get; set; } = "";

    public bool HasExplicitValues()
    {
        return !string.IsNullOrWhiteSpace(Endpoint)
            || !string.Equals(ModelId, "gpt-4o", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(ApiKey)
            || MaxTokens != 4096
            || Temperature != 0
            || VisionFallbackEnabled
            || Math.Abs(VisionConfidenceThreshold - 0.7) > 0.0001
            || !string.IsNullOrWhiteSpace(VisionModelId)
            || VoiceInputEnabled != true
            || !string.Equals(WhisperDeploymentId, "whisper", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(WhisperEndpoint)
            || !string.IsNullOrWhiteSpace(WhisperApiKey)
            || !string.IsNullOrWhiteSpace(VoiceLanguage);
    }

    public void CopyFrom(AgentSettings agent)
    {
        Endpoint = agent.Endpoint;
        ModelId = agent.ModelId;
        ApiKey = agent.ApiKey;
        MaxTokens = agent.MaxTokens;
        Temperature = agent.Temperature;
        VisionFallbackEnabled = agent.VisionFallbackEnabled;
        VisionConfidenceThreshold = agent.VisionConfidenceThreshold;
        VisionModelId = agent.VisionModelId;
        VoiceInputEnabled = agent.VoiceInputEnabled;
        WhisperDeploymentId = agent.WhisperDeploymentId;
        WhisperEndpoint = agent.WhisperEndpoint;
        WhisperApiKey = agent.WhisperApiKey;
        VoiceLanguage = agent.VoiceLanguage;
    }

    public void CopyTo(AgentSettings agent)
    {
        agent.Endpoint = Endpoint;
        agent.ModelId = ModelId;
        agent.ApiKey = ApiKey;
        agent.MaxTokens = MaxTokens;
        agent.Temperature = Temperature;
        agent.VisionFallbackEnabled = VisionFallbackEnabled;
        agent.VisionConfidenceThreshold = VisionConfidenceThreshold;
        agent.VisionModelId = VisionModelId;
        agent.VoiceInputEnabled = VoiceInputEnabled;
        agent.WhisperDeploymentId = WhisperDeploymentId;
        agent.WhisperEndpoint = WhisperEndpoint;
        agent.WhisperApiKey = WhisperApiKey;
        agent.VoiceLanguage = VoiceLanguage;
    }
}

/// <summary>
/// Global settings controlling application behavior, UI preferences, and feature toggles.
/// </summary>
public class GlobalSettings
{
    // === Mode ===
    
    /// <summary>
    /// Current application operating mode: Classic (rules) or Agent (AI).
    /// </summary>
    public AppMode Mode { get; set; } = AppMode.Classic;

    /// <summary>
    /// Legacy startup flag retained for config compatibility.
    /// The welcome launcher is now shown by default so the app can present its
    /// utility families before loading a specific workspace.
    /// </summary>
    public bool SkipHomeScreen { get; set; }
    
    // === Core Automation ===
    
    /// <summary>
    /// Master switch for automation. When false, no rules are evaluated.
    /// </summary>
    public bool AutomationEnabled { get; set; } = true;
    
    /// <summary>
    /// Interval in milliseconds between rule evaluation cycles. Minimum 5000ms (5 seconds).
    /// </summary>
    public int PollingIntervalMs { get; set; } = 10000;
    
    /// <summary>
    /// Global hotkey to toggle automation (e.g., "Ctrl+Alt+T").
    /// </summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+T";
    
    // === Window Behavior ===
    
    /// <summary>
    /// Show the control panel window when the application starts.
    /// </summary>
    public bool ShowPanelOnStart { get; set; } = true;
    
    /// <summary>
    /// Periodically move the mouse to prevent system sleep.
    /// </summary>
    public bool GlobalMouseNudge { get; set; }
    
    // === Logging & Theme ===
    
    /// <summary>
    /// Minimum log level: Debug, Info, Warning, Error.
    /// </summary>
    public string LogLevel { get; set; } = "Info";
    
    /// <summary>
    /// UI theme: "Dark" or "Light". Currently only Dark is implemented.
    /// </summary>
    public string Theme { get; set; } = "Dark";
    
    // === UI Preferences ===
    
    /// <summary>
    /// Show the session execution count column in the rules list.
    /// </summary>
    public bool ShowExecutionCount { get; set; } = true;
    
    /// <summary>
    /// Show a "radar pulse" animation at the click point so you can observe automation actions.
    /// The overlay is fully click-through and never steals focus.
    /// </summary>
    public bool ClickRadar { get; set; } = true;
    
    // === Scripting ===
    
    /// <summary>
    /// Enable PowerShell and C# script execution for RunScript actions.
    /// </summary>
    public bool ScriptingEnabled { get; set; } = true;
    
    /// <summary>
    /// Default timeout for script execution in milliseconds.
    /// </summary>
    public int DefaultScriptTimeoutMs { get; set; } = 5000;
    
    // === Plugins ===
    
    /// <summary>
    /// Enable loading and execution of plugins from the Plugins directory.
    /// </summary>
    public bool PluginsEnabled { get; set; } = true;
    
    /// <summary>
    /// List of plugin IDs to disable even if present.
    /// </summary>
    public string[] DisabledPlugins { get; set; } = [];
    
    // === Event Timeline ===
    
    /// <summary>
    /// Enable the event timeline feature for tracking rule matches and actions.
    /// </summary>
    public bool TimelineEnabled { get; set; } = true;
    
    /// <summary>
    /// Maximum number of events to keep in memory.
    /// </summary>
    public int MaxTimelineEvents { get; set; } = 1000;
    
    /// <summary>
    /// Persist timeline events to SQLite database (future feature).
    /// </summary>
    public bool PersistTimeline { get; set; }
    
    // === Notifications ===
    
    /// <summary>
    /// Default settings for notifications.
    /// </summary>
    public NotificationDefaults NotificationDefaults { get; set; } = new();

    // === Safety ===

    /// <summary>
    /// Global kill switch hotkey (e.g., "Ctrl+Alt+Escape").
    /// Immediately stops all automation (classic engine + running flows).
    /// </summary>
    public string KillSwitchHotkey { get; set; } = "Ctrl+Alt+Escape";

    /// <summary>
    /// Process allowlist. When non-empty, automation ONLY targets these processes.
    /// Both the classic engine and flow executor enforce this.
    /// Empty list = no restriction (all processes allowed).
    /// </summary>
    public List<string> AllowedProcesses { get; set; } = [];
}

/// <summary>
/// Settings for the AI agent backend (LLM endpoint, model, credentials).
/// </summary>
public class AgentSettings
{
    /// <summary>
    /// LLM API endpoint URL (e.g., Azure OpenAI, GitHub Models, local Ollama).
    /// </summary>
    public string Endpoint { get; set; } = "";
    
    /// <summary>
    /// Model deployment/ID to use (e.g., "gpt-4o", "gpt-4o-mini").
    /// </summary>
    public string ModelId { get; set; } = "gpt-4o";
    
    /// <summary>
    /// API key for the LLM endpoint. Stored in config; future: encrypted via DPAPI.
    /// </summary>
    public string ApiKey { get; set; } = "";
    
    /// <summary>
    /// Maximum tokens per LLM response.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;
    
    /// <summary>
    /// Sampling temperature. Set to 0 for maximum determinism.
    /// Value 1.0 is omitted (lets the model use its default).
    /// Some models (e.g. o-series, gpt-5.2) only support the default value.
    /// </summary>
    public double Temperature { get; set; } = 0;

    /// <summary>
    /// Maximum number of messages to keep in the conversation history before compaction.
    /// Older tool-call/result pairs are summarized to save tokens. Default: 40.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 40;

    /// <summary>
    /// Maximum session token budget. When estimated usage exceeds this, old messages
    /// are aggressively compacted. 0 = no limit. Default: 128000 (128k).
    /// </summary>
    public int MaxSessionTokens { get; set; } = 128_000;
    
    /// <summary>
    /// System prompt prepended to every agent conversation.
    /// </summary>
    public string SystemPrompt { get; set; } = "You are IdolClick Agent, a desktop automation assistant. You can create automation rules, click UI elements, type text, and inspect windows. Be concise and action-oriented.";

    /// <summary>
    /// Enable vision-based fallback for element location when UIA selectors fail.
    /// Requires a vision-capable LLM (gpt-4o, claude-3.5-sonnet, gemini-pro-vision, etc.).
    /// </summary>
    public bool VisionFallbackEnabled { get; set; } = false;

    /// <summary>
    /// Minimum confidence threshold (0.0–1.0) for vision results to be used.
    /// Below this, the element is treated as "not found".
    /// </summary>
    public double VisionConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// Optional separate model ID for vision requests. If empty, uses the main ModelId.
    /// Useful when the main model doesn't support vision (e.g., text-only) but you have
    /// a vision model available at the same endpoint.
    /// </summary>
    public string VisionModelId { get; set; } = "";

    // === Voice Input ===

    /// <summary>
    /// Enable voice input (microphone button) in the chat UI.
    /// Requires a Whisper deployment on the Azure OpenAI endpoint.
    /// </summary>
    public bool VoiceInputEnabled { get; set; } = true;

    /// <summary>
    /// Azure OpenAI deployment name for the Whisper model (e.g., "whisper-1").
    /// Used for speech-to-text transcription of voice input.
    /// </summary>
    public string WhisperDeploymentId { get; set; } = "whisper";

    /// <summary>
    /// Optional dedicated endpoint for the Whisper API.
    /// If empty, falls back to the main agent <see cref="Endpoint"/>.
    /// Useful when the Whisper model is on a different Azure OpenAI resource.
    /// </summary>
    public string WhisperEndpoint { get; set; } = "";

    /// <summary>
    /// Optional dedicated API key for the Whisper endpoint.
    /// If empty, falls back to the main agent <see cref="ApiKey"/>.
    /// </summary>
    public string WhisperApiKey { get; set; } = "";

    /// <summary>
    /// Language hint for Whisper transcription (ISO 639-1, e.g., "en", "es", "ja").
    /// Empty or null = auto-detect language.
    /// </summary>
    public string VoiceLanguage { get; set; } = "";
}

/// <summary>
/// Persisted settings for the capture workspace.
/// </summary>
public class CaptureWorkspaceSettings
{
    /// <summary>
    /// Default directory for saved capture artifacts. Empty = app-managed default.
    /// </summary>
    public string DefaultOutputDirectory { get; set; } = "";

    /// <summary>
    /// Global hotkey used to trigger the selected capture profile.
    /// </summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>
    /// Enables the global capture hotkey when a main window is active.
    /// </summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Push-to-talk hotkey used for voice annotations.
    /// Hold the hotkey to record and release to transcribe.
    /// </summary>
    public string AnnotationHotkey { get; set; } = "Ctrl+Alt+V";

    /// <summary>
    /// Enables the annotation push-to-talk hotkey.
    /// </summary>
    public bool AnnotationHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Preferred orb placement preset. Supports BottomRight, BottomLeft, TopRight, TopLeft, and Custom.
    /// </summary>
    public string OrbPlacement { get; set; } = "BottomRight";

    /// <summary>
    /// When true, a dragged custom orb location is remembered across launches.
    /// </summary>
    public bool RememberOrbLocation { get; set; } = true;

    /// <summary>
    /// Saved orb left coordinate for custom placement.
    /// </summary>
    public double? OrbLeft { get; set; }

    /// <summary>
    /// Saved orb top coordinate for custom placement.
    /// </summary>
    public double? OrbTop { get; set; }

    /// <summary>
    /// Enables repeated automatic capture from the floating orb.
    /// </summary>
    public bool OrbIntervalCaptureEnabled { get; set; }

    /// <summary>
    /// Number of seconds between repeated orb-triggered captures.
    /// </summary>
    public int OrbIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Saved capture profiles shown in the capture workspace.
    /// </summary>
    public List<CaptureProfile> Profiles { get; set; } = [];

    /// <summary>
    /// Currently selected capture profile used by shared triggers.
    /// </summary>
    public string SelectedProfileId { get; set; } = "";

    /// <summary>
    /// Recent prompt-driven capture pack runs shared by Capture and Reason surfaces.
    /// </summary>
    public List<PromptPackHistoryEntry> PromptPackHistory { get; set; } = [];

    /// <summary>
    /// Maximum number of prompt-driven capture pack history entries to retain.
    /// </summary>
    public int MaxPromptPackHistoryEntries { get; set; } = 8;

    /// <summary>
    /// Number of recent capture events cached in memory for UI and API reads.
    /// </summary>
    public int MaxRecentEventsInMemory { get; set; } = 120;

    /// <summary>
    /// Maximum number of retained capture events on disk before pruning oldest entries.
    /// </summary>
    public int MaxSavedEvents { get; set; } = 250;

    /// <summary>
    /// Maximum number of retained journal entries in the JSONL journal.
    /// </summary>
    public int MaxJournalEntries { get; set; } = 250;

    /// <summary>
    /// Enables periodic retention maintenance for capture artifacts and journal entries.
    /// </summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>
    /// When true, capture journals are exported to the configured file adapter output directory.
    /// </summary>
    public bool SyncFileExportEnabled { get; set; }

    /// <summary>
    /// Output directory for file-based journal export and sync envelopes.
    /// Empty = app-managed default.
    /// </summary>
    public string SyncExportDirectory { get; set; } = "";

    /// <summary>
    /// When true, capture journals are posted to the configured webhook endpoint.
    /// </summary>
    public bool SyncWebhookEnabled { get; set; }

    /// <summary>
    /// Destination webhook used to forward capture and annotation envelopes to an external backend.
    /// </summary>
    public string SyncWebhookUrl { get; set; } = "";

    /// <summary>
    /// Optional bearer token used by the capture sync webhook adapter.
    /// </summary>
    public string SyncWebhookApiKey { get; set; } = "";
}

public class PromptPackHistoryEntry
{
    public string Prompt { get; set; } = "";
    public string PackId { get; set; } = "";
    public string PackName { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public bool Succeeded { get; set; }
    public bool SmokeMode { get; set; } = true;
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Settings for the low-impact rolling review buffer.
/// </summary>
public class ReviewBufferSettings
{
    /// <summary>
    /// Enables background review buffering.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Interval between captured review frames.
    /// </summary>
    public int FrameIntervalMs { get; set; } = 1500;

    /// <summary>
    /// Number of minutes to retain in the rolling buffer.
    /// </summary>
    public int BufferDurationMinutes { get; set; } = 3;

    /// <summary>
    /// Output folder for saved review bundles. Empty = app-managed default.
    /// </summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>
    /// Hotkey that saves the last N minutes of buffered review artifacts.
    /// </summary>
    public string SaveBufferHotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>
    /// Enables microphone chunk recording alongside the screen buffer.
    /// </summary>
    public bool MicEnabled { get; set; }

    /// <summary>
    /// Duration of each microphone chunk used in the rolling buffer.
    /// </summary>
    public int AudioChunkSeconds { get; set; } = 10;
}

/// <summary>
/// Reusable capture definition that can contain one or many targets.
/// </summary>
public class CaptureProfile
{
    /// <summary>Stable profile id for UI selection and artifact metadata.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Human-friendly profile name.</summary>
    public string Name { get; set; } = "New Capture";

    /// <summary>Whether the profile can be triggered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>File prefix used for generated artifact names.</summary>
    public string FilePrefix { get; set; } = "snap";

    /// <summary>
    /// Optional output directory override. Empty = workspace default.
    /// </summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>
    /// One or many targets captured as part of the same snap event.
    /// </summary>
    public List<CaptureTargetDefinition> Targets { get; set; } = [];
}

/// <summary>
/// Supported capture target types.
/// </summary>
public enum CaptureTargetKind
{
    /// <summary>Normalized region of the full desktop.</summary>
    ScreenRegion,
    /// <summary>Entire window resolved from saved process/title hints.</summary>
    Window,
    /// <summary>Normalized region inside a resolved window.</summary>
    WindowRegion
}

/// <summary>
/// One capture target inside a reusable profile.
/// </summary>
public class CaptureTargetDefinition
{
    /// <summary>Stable target id inside the profile.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Display name shown in the workspace.</summary>
    public string Name { get; set; } = "Target";

    /// <summary>What kind of capture this target performs.</summary>
    public CaptureTargetKind Kind { get; set; } = CaptureTargetKind.ScreenRegion;

    /// <summary>Process hint used for window resolution.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Window title hint used for window resolution.</summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>
    /// Best-effort native handle hint from the last time the window was seen.
    /// </summary>
    public long WindowHandleHint { get; set; }

    /// <summary>
    /// Normalized region payload. For ScreenRegion this is relative to the virtual screen.
    /// For WindowRegion this is relative to the resolved window bounds.
    /// </summary>
    public ScreenRegion? Region { get; set; }
}

/// <summary>
/// Default notification settings applied when rules don't specify their own.
/// </summary>
public class NotificationDefaults
{
    /// <summary>
    /// Show a toast notification when any rule matches.
    /// </summary>
    public bool ToastOnRuleMatch { get; set; }
    
    /// <summary>
    /// Default webhook URL for notification routing.
    /// </summary>
    public string? DefaultWebhookUrl { get; set; }
    
    /// <summary>
    /// Include timestamp in notification messages.
    /// </summary>
    public bool IncludeTimestamp { get; set; } = true;
}

/// <summary>
/// Centralized timing constants used across the execution pipeline.
/// All values are externalized from code so they can be tuned without recompilation.
/// Loaded from config.json under "TimingSettings".
/// </summary>
public class TimingSettings
{
    // ── Selector Resolution ──────────────────────────────────────────────

    /// <summary>Poll interval when waiting for elements to appear (ms). Default: 150.</summary>
    public int SelectorPollIntervalMs { get; set; } = 150;

    /// <summary>Default element wait timeout (ms). Default: 3000.</summary>
    public int DefaultElementTimeoutMs { get; set; } = 3000;

    /// <summary>Selector cache time-to-live (ms). Default: 5000 (5s).</summary>
    public int SelectorCacheTtlMs { get; set; } = 5000;

    // ── Launch / Window ──────────────────────────────────────────────────

    /// <summary>Max time to wait for a launched window to appear in UIA tree (ms). Default: 10000.</summary>
    public int LaunchWindowTimeoutMs { get; set; } = 10000;

    /// <summary>Poll interval during launch window wait (ms). Default: 150.</summary>
    public int LaunchPollIntervalMs { get; set; } = 150;

    /// <summary>Max time to wait for UIA tree stability after launch (ms). Default: 2000.</summary>
    public int LaunchUiaStabilityMs { get; set; } = 2000;

    /// <summary>Delay after WaitForInputIdle type check (ms). Default: 100.</summary>
    public int PostClickFocusDelayMs { get; set; } = 100;

    /// <summary>Delay between typed characters via SendChar (ms). Default: 20.</summary>
    public int TypeCharDelayMs { get; set; } = 20;

    // ── Navigate ─────────────────────────────────────────────────────────

    /// <summary>Max time to wait for browser title to match domain hint (ms). Default: 8000.</summary>
    public int NavigateMaxWaitMs { get; set; } = 8000;

    /// <summary>Poll interval during navigate wait (ms). Default: 200.</summary>
    public int NavigatePollIntervalMs { get; set; } = 200;

    /// <summary>Extra post-navigate delay for initial page render (ms). Default: 300.</summary>
    public int NavigatePostRenderDelayMs { get; set; } = 300;

    // ── Actionability ────────────────────────────────────────────────────

    /// <summary>Window find poll interval in DesktopBackend (ms). Default: 150.</summary>
    public int WindowFindPollIntervalMs { get; set; } = 150;

    /// <summary>Stability check frame delay (ms). Default: 50.</summary>
    public int StabilityFrameDelayMs { get; set; } = 50;

    /// <summary>Stability second-chance delay (ms). Default: 100.</summary>
    public int StabilityRetryDelayMs { get; set; } = 100;

    /// <summary>Minimum inter-step delay when delayAfterMs is unset (ms). Default: 100.</summary>
    public int MinInterStepDelayMs { get; set; } = 100;

    /// <summary>Post-ActivateWindow settle delay (ms). Default: 50.</summary>
    public int ActivateWindowDelayMs { get; set; } = 50;
}

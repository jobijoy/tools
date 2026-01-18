namespace VsCodeAllowClicker.App;

internal sealed class AppConfig
{
    public TargetConfig Target { get; set; } = new();
    public MatchingConfig Matching { get; set; } = new();
    public SearchAreaConfig SearchArea { get; set; } = new();
    public PollingConfig Polling { get; set; } = new();
    public HotkeyConfig Hotkey { get; set; } = new();
    public HotkeyConfig ConfirmHotkey { get; set; } = new() { Key = "C" };
    public UIConfig UI { get; set; } = new();
}

internal sealed class TargetConfig
{
    public string[] ProcessNames { get; set; } = ["Code"];
    public string WindowTitleContains { get; set; } = "Visual Studio Code";
    public string[] AdditionalWindowTitles { get; set; } = ["Select an account", "Sign in", "Authorize", "GitHub"];
}

internal sealed class MatchingConfig
{
    public string[] ButtonLabels { get; set; } = ["Allow"];
    public PreClickAction? PreClick { get; set; }
    public WebviewFallbackConfig? WebviewFallback { get; set; }
}

internal sealed class PreClickAction
{
    public bool Enabled { get; set; }
    public string ControlType { get; set; } = "ListItem"; // ListItem, Button, Text, etc.
    public string SelectionMode { get; set; } = "First"; // First, Last, Index, ByName
    public int Index { get; set; } = 0;
    public string? Name { get; set; }
    public int DelayAfterMs { get; set; } = 200;
}

internal sealed class WebviewFallbackConfig
{
    public bool Enabled { get; set; } = true;
    public string[] TriggerWindowTitles { get; set; } = ["Select an account", "GitHub", "Sign in"];
    public string[] KeySequence { get; set; } = ["Enter"]; // Keys to send: Tab, Enter, Down, Escape
    public int DelayBetweenKeysMs { get; set; } = 100;
}

internal sealed class SearchAreaConfig
{
    public string Mode { get; set; } = "RightFraction";
    public double RightFractionStart { get; set; } = 0.60;

    public NormalizedRect NormalizedRect { get; set; } = new() { X = 0.60, Y = 0.0, Width = 0.40, Height = 1.00 };
}

internal sealed class NormalizedRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

internal sealed class PollingConfig
{
    public int IntervalMs { get; set; } = 30000;
    public int ClickThrottleMs { get; set; } = 1500;
}

internal sealed class HotkeyConfig
{
    public bool Enabled { get; set; } = true;
    public string[] Modifiers { get; set; } = ["Ctrl", "Alt"];
    public string Key { get; set; } = "A";
}

internal sealed class UIConfig
{
    public bool AutoStartEnabled { get; set; } = false;
    public bool ShowControlPanelOnStart { get; set; } = true;
    public string ControlPanelPosition { get; set; } = "BottomLeft";
}

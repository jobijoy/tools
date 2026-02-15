using System.Diagnostics;
using System.Windows.Automation;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// SELECTOR PARSER — Resolves DSL selectors to live UI Automation elements.
//
// Selector format: "ElementType#TextOrAutomationId"
//   • "Button#Save"           — Button with name or AutomationId "Save"
//   • "TextBox#SearchField"   — TextBox with AutomationId "SearchField"
//   • "#AutomationId"         — Any element type with the given AutomationId  
//   • "Window#Untitled"       — Window by title
//
// Also handles window resolution by process name and/or title.
// Used by FlowActionExecutor and AssertionEvaluator.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parsed selector components.
/// </summary>
public record ParsedSelector(string? ElementType, string Identifier);

/// <summary>
/// Result of resolving a selector to a live UI element.
/// </summary>
public class SelectorMatch
{
    /// <summary>The matched UI Automation element.</summary>
    public AutomationElement Element { get; init; } = null!;

    /// <summary>Snapshot of the matched element for the report.</summary>
    public ElementSnapshot Snapshot { get; init; } = null!;
}

/// <summary>
/// Resolves DSL selectors to live Windows UI Automation elements.
/// Thread-safe: all methods query fresh COM objects per call.
/// </summary>
public class SelectorParser
{
    private readonly LogService _log;

    /// <summary>
    /// Maps DSL element type names to UI Automation ControlType values.
    /// </summary>
    private static readonly Dictionary<string, ControlType> ControlTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Button"] = ControlType.Button,
        ["TextBox"] = ControlType.Edit,
        ["Edit"] = ControlType.Edit,
        ["TextBlock"] = ControlType.Text,
        ["Text"] = ControlType.Text,
        ["Label"] = ControlType.Text,
        ["CheckBox"] = ControlType.CheckBox,
        ["RadioButton"] = ControlType.RadioButton,
        ["ComboBox"] = ControlType.ComboBox,
        ["ListItem"] = ControlType.ListItem,
        ["MenuItem"] = ControlType.MenuItem,
        ["TabItem"] = ControlType.TabItem,
        ["TreeItem"] = ControlType.TreeItem,
        ["Hyperlink"] = ControlType.Hyperlink,
        ["Image"] = ControlType.Image,
        ["Slider"] = ControlType.Slider,
        ["ProgressBar"] = ControlType.ProgressBar,
        ["DataGrid"] = ControlType.DataGrid,
        ["Toggle"] = ControlType.Button,      // WPF ToggleButton maps to Button
        ["Pane"] = ControlType.Pane,
        ["Group"] = ControlType.Group,
        ["ScrollBar"] = ControlType.ScrollBar,
        ["ToolBar"] = ControlType.ToolBar,
        ["StatusBar"] = ControlType.StatusBar,
        ["Window"] = ControlType.Window,
    };

    public SelectorParser(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Parses a selector string into its components.
    /// </summary>
    public static ParsedSelector Parse(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new ArgumentException("Selector cannot be empty.", nameof(selector));

        var hashIndex = selector.IndexOf('#');
        if (hashIndex < 0)
            return new ParsedSelector(null, selector);

        var elementType = hashIndex > 0 ? selector[..hashIndex] : null;
        var identifier = selector[(hashIndex + 1)..];
        return new ParsedSelector(elementType, identifier);
    }

    /// <summary>
    /// Finds the target window for a test step by process name and/or window title.
    /// </summary>
    public AutomationElement? FindWindow(string? targetApp, string? windowTitle)
    {
        AutomationElement? best = null;
        AutomationElement? titleMatch = null; // Prefer a window whose title matches

        // Search by process name first
        if (!string.IsNullOrWhiteSpace(targetApp))
        {
            var processNames = targetApp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var procName in processNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(procName); }
                catch { continue; }

                foreach (var proc in procs)
                {
                    using (proc)
                    {
                        try
                        {
                            if (proc.MainWindowHandle == IntPtr.Zero) continue;
                            var elem = AutomationElement.FromHandle(proc.MainWindowHandle);
                            var title = elem.Current.Name ?? "";

                            if (!string.IsNullOrWhiteSpace(windowTitle))
                            {
                                if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                                {
                                    titleMatch = elem; // Exact title match — highest priority
                                }
                            }

                            // Track the most recently found as fallback (last process = likely most recently created)
                            best ??= elem;
                        }
                        catch (ElementNotAvailableException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }

            // Prefer title match over first-found
            if (titleMatch != null) return titleMatch;
            if (best != null && string.IsNullOrWhiteSpace(windowTitle)) return best;
        }

        // Fallback: search all top-level windows by title (when no title-matched window was found by process name)
        if (titleMatch == null && !string.IsNullOrWhiteSpace(windowTitle))
        {
            try
            {
                var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < children.Count; i++)
                {
                    try
                    {
                        var w = children[i];
                        var title = w.Current.Name ?? "";
                        if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            best = w;
                            break;
                        }
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch { }
        }

        return best;
    }

    /// <summary>
    /// Resolves a selector to a live UI element within a window, with retry/timeout.
    /// </summary>
    /// <param name="window">The window to search in.</param>
    /// <param name="selector">DSL selector string.</param>
    /// <param name="timeoutMs">Maximum time to wait for the element.</param>
    /// <param name="retryCount">Output: how many retries were needed.</param>
    /// <returns>The matched element and snapshot, or null if not found.</returns>
    public SelectorMatch? Resolve(AutomationElement window, string selector, int timeoutMs, out int retryCount)
    {
        retryCount = 0;
        var parsed = Parse(selector);
        var sw = Stopwatch.StartNew();
        const int pollIntervalMs = 250;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var match = TryResolveOnce(window, parsed);
            if (match != null)
            {
                _log.Debug("Selector", $"Resolved '{selector}' in {sw.ElapsedMilliseconds}ms (retries: {retryCount})");
                return match;
            }

            retryCount++;
            Thread.Sleep(pollIntervalMs);
        }

        _log.Debug("Selector", $"Failed to resolve '{selector}' after {timeoutMs}ms ({retryCount} retries)");
        return null;
    }

    /// <summary>
    /// Single attempt to resolve a selector (no retry).
    /// </summary>
    public SelectorMatch? ResolveOnce(AutomationElement window, string selector)
    {
        var parsed = Parse(selector);
        return TryResolveOnce(window, parsed);
    }

    private SelectorMatch? TryResolveOnce(AutomationElement window, ParsedSelector parsed)
    {
        try
        {
            // Build search condition
            Condition condition = Condition.TrueCondition;
            if (!string.IsNullOrWhiteSpace(parsed.ElementType) && ControlTypeMap.TryGetValue(parsed.ElementType, out var ct))
            {
                condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ct);
            }

            var elements = window.FindAll(TreeScope.Descendants, condition);

            for (int i = 0; i < elements.Count; i++)
            {
                try
                {
                    var elem = elements[i];
                    var current = elem.Current;
                    var name = current.Name ?? "";
                    var automationId = current.AutomationId ?? "";

                    // Match by AutomationId first (stable), then by Name
                    bool matched = false;
                    if (!string.IsNullOrEmpty(automationId) &&
                        automationId.Equals(parsed.Identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                    }
                    else if (!string.IsNullOrEmpty(name) &&
                             (name.Equals(parsed.Identifier, StringComparison.OrdinalIgnoreCase) ||
                              name.StartsWith(parsed.Identifier + " ", StringComparison.OrdinalIgnoreCase) ||
                              name.StartsWith(parsed.Identifier + "(", StringComparison.OrdinalIgnoreCase)))
                    {
                        matched = true;
                    }

                    if (matched)
                    {
                        return new SelectorMatch
                        {
                            Element = elem,
                            Snapshot = CreateSnapshot(elem)
                        };
                    }
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (Exception ex)
        {
            _log.Debug("Selector", $"Resolve error: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Creates an <see cref="ElementSnapshot"/> from a live UI Automation element.
    /// </summary>
    public static ElementSnapshot CreateSnapshot(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            var controlType = current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "Unknown";
            var rect = current.BoundingRectangle;

            return new ElementSnapshot
            {
                ControlType = controlType,
                Name = current.Name,
                AutomationId = current.AutomationId,
                IsEnabled = current.IsEnabled,
                Bounds = rect.IsEmpty ? null : new ElementBounds
                {
                    X = (int)rect.X,
                    Y = (int)rect.Y,
                    Width = (int)rect.Width,
                    Height = (int)rect.Height
                }
            };
        }
        catch (ElementNotAvailableException)
        {
            return new ElementSnapshot { ControlType = "Unavailable" };
        }
    }

    /// <summary>
    /// Gets the text content of a UI element (Name property or ValuePattern).
    /// </summary>
    public static string GetElementText(AutomationElement element)
    {
        try
        {
            // Try ValuePattern first (for TextBox/Edit controls)
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern val)
                return val.Current.Value ?? "";

            // Fall back to Name property
            return element.Current.Name ?? "";
        }
        catch
        {
            return "";
        }
    }
}

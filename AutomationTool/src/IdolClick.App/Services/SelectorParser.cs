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

    /// <summary>Number of poll retries before the element was found (0 = first attempt).</summary>
    public int RetryCount { get; init; }
}

/// <summary>
/// Resolves DSL selectors to live Windows UI Automation elements.
/// Thread-safe: all methods query fresh COM objects per call.
/// Includes a short-lived cache (5s TTL) to avoid redundant UIA tree traversals.
/// </summary>
public class SelectorParser
{
    private readonly LogService _log;
    private TimingSettings _timing;

    /// <summary>
    /// Simple selector cache with TTL. Key = "windowHandle|selector", value = (match, timestamp).
    /// Avoids redundant UIA tree traversals when the same selector is resolved multiple
    /// times within a short window (e.g., post-step assertion re-evaluations).
    /// </summary>
    private readonly Dictionary<string, (SelectorMatch Match, long TimestampMs)> _cache = [];
    private static readonly Stopwatch _cacheClock = Stopwatch.StartNew();
    private readonly object _cacheLock = new();

    /// <summary>Cache TTL in milliseconds (default 5000ms = 5s).</summary>
    private int _cacheTtlMs = 5000;

    /// <summary>Sets the cache TTL. Called on config load.</summary>
    public void SetCacheTtl(int ttlMs) => _cacheTtlMs = Math.Max(0, ttlMs);

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
        _timing = new TimingSettings(); // defaults
    }

    /// <summary>Injects configurable timing settings.</summary>
    public void SetTiming(TimingSettings timing) => _timing = timing ?? new TimingSettings();

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
    /// Priority: title match → foreground window → first found.
    /// </summary>
    public AutomationElement? FindWindow(string? targetApp, string? windowTitle)
    {
        AutomationElement? best = null;
        AutomationElement? titleMatch = null;
        AutomationElement? foregroundMatch = null;

        // Get current foreground window handle for priority ordering
        var foregroundHwnd = Win32.GetForegroundWindow();

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

                            if (!string.IsNullOrWhiteSpace(windowTitle) &&
                                title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                            {
                                titleMatch = elem; // Title match — highest priority
                            }

                            // Check if this window is the foreground window
                            if (proc.MainWindowHandle == foregroundHwnd)
                                foregroundMatch = elem;

                            best ??= elem;
                        }
                        catch (ElementNotAvailableException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }

            // Priority: title match > foreground window > first found
            if (titleMatch != null) return titleMatch;
            if (foregroundMatch != null && string.IsNullOrWhiteSpace(windowTitle)) return foregroundMatch;
            if (best != null && string.IsNullOrWhiteSpace(windowTitle)) return best;
        }

        // Fallback: search all top-level windows by title
        if (titleMatch == null && !string.IsNullOrWhiteSpace(windowTitle))
        {
            try
            {
                var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                AutomationElement? fgFallback = null;

                for (int i = 0; i < children.Count; i++)
                {
                    try
                    {
                        var w = children[i];
                        var title = w.Current.Name ?? "";
                        if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            // Prefer the foreground window among title matches
                            var hwnd = new IntPtr(w.Current.NativeWindowHandle);
                            if (hwnd == foregroundHwnd)
                            {
                                fgFallback = w;
                                break; // Foreground + title match is the best possible
                            }
                            best ??= w;
                        }
                    }
                    catch (ElementNotAvailableException) { }
                }

                if (fgFallback != null) return fgFallback;
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
    /// <param name="exactMatch">When true, only exact name/id matches are accepted (no prefix matching).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matched element and snapshot, or null if not found.</returns>
    public async Task<SelectorMatch?> ResolveAsync(AutomationElement window, string selector, int timeoutMs, bool exactMatch = false, CancellationToken ct = default)
    {
        var parsed = Parse(selector);
        var sw = Stopwatch.StartNew();
        var pollIntervalMs = _timing.SelectorPollIntervalMs;
        int retryCount = 0;

        // Check cache first
        var cacheKey = BuildCacheKey(window, selector);
        if (TryGetCached(cacheKey, out var cached))
        {
            _log.Debug("Selector", $"Cache hit for '{selector}'");
            return cached;
        }

        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            var match = TryResolveOnce(window, parsed, exactMatch);
            if (match != null)
            {
                var result = new SelectorMatch
                {
                    Element = match.Element,
                    Snapshot = match.Snapshot,
                    RetryCount = retryCount
                };
                _log.Debug("Selector", $"Resolved '{selector}' in {sw.ElapsedMilliseconds}ms (retries: {retryCount})");
                PutCache(cacheKey, result);
                return result;
            }

            retryCount++;
            await Task.Delay(pollIntervalMs, ct).ConfigureAwait(false);
        }

        _log.Debug("Selector", $"Failed to resolve '{selector}' after {timeoutMs}ms ({retryCount} retries)");
        return null;
    }

    /// <summary>
    /// Async single-attempt resolve (no retry/timeout).
    /// </summary>
    public Task<SelectorMatch?> ResolveOnceAsync(AutomationElement window, string selector, bool exactMatch = false)
    {
        return Task.FromResult(ResolveOnce(window, selector, exactMatch));
    }

    /// <summary>
    /// Single attempt to resolve a selector (no retry).
    /// </summary>
    public SelectorMatch? ResolveOnce(AutomationElement window, string selector, bool exactMatch = false)
    {
        var parsed = Parse(selector);
        return TryResolveOnce(window, parsed, exactMatch);
    }

    private SelectorMatch? TryResolveOnce(AutomationElement window, ParsedSelector parsed, bool exactMatch = false)
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

            // ── Collect ALL matches and score them ───────────────────────────
            // Returning the first match causes non-determinism when multiple
            // elements share the same name/automationId (e.g., duplicate "OK" buttons).
            // Instead: collect candidates, score by quality, pick the best.
            var candidates = new List<(AutomationElement Element, int Score)>();

            for (int i = 0; i < elements.Count; i++)
            {
                try
                {
                    var elem = elements[i];
                    var current = elem.Current;
                    var name = current.Name ?? "";
                    var automationId = current.AutomationId ?? "";

                    // Match by AutomationId first (stable), then by Name
                    int matchScore = 0;

                    if (!string.IsNullOrEmpty(automationId) &&
                        automationId.Equals(parsed.Identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        // AutomationId exact match — highest base score
                        matchScore = 100;
                    }
                    else if (!string.IsNullOrEmpty(name) &&
                             name.Equals(parsed.Identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        // Name exact match
                        matchScore = 80;
                    }
                    else if (!exactMatch && !string.IsNullOrEmpty(name) &&
                             (name.StartsWith(parsed.Identifier + " ", StringComparison.OrdinalIgnoreCase) ||
                              name.StartsWith(parsed.Identifier + "(", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Name prefix match (weakest) — disabled when exactMatch is true
                        matchScore = 50;
                    }

                    if (matchScore > 0)
                    {
                        // Bonus: element is visible (+20), enabled (+10), has area > 0 (+5)
                        if (!current.IsOffscreen) matchScore += 20;
                        if (current.IsEnabled) matchScore += 10;
                        var rect = current.BoundingRectangle;
                        if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                            matchScore += 5;

                        candidates.Add((elem, matchScore));
                    }
                }
                catch (ElementNotAvailableException) { }
            }

            if (candidates.Count == 0)
                return null;

            // Warn on ambiguity (multiple candidates)
            if (candidates.Count > 1)
            {
                _log.Warn("Selector", $"Ambiguous selector '{parsed.ElementType}#{parsed.Identifier}': " +
                    $"{candidates.Count} candidates found — picking highest score " +
                    $"(top={candidates.Max(c => c.Score)}, spread={candidates.Max(c => c.Score) - candidates.Min(c => c.Score)})");
            }

            // Pick the highest-scoring candidate
            var best = candidates.OrderByDescending(c => c.Score).First();
            return new SelectorMatch
            {
                Element = best.Element,
                Snapshot = CreateSnapshot(best.Element)
            };
        }
        catch (ElementNotAvailableException) { }
        catch (Exception ex)
        {
            _log.Debug("Selector", $"Resolve error: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SELECTOR CACHE — short-lived TTL cache to avoid redundant UIA tree traversals
    // ═══════════════════════════════════════════════════════════════════════════════

    private string BuildCacheKey(AutomationElement window, string selector)
    {
        try
        {
            return $"{window.Current.NativeWindowHandle}|{selector}";
        }
        catch
        {
            return $"0|{selector}";
        }
    }

    private bool TryGetCached(string key, out SelectorMatch? match)
    {
        match = null;
        if (_cacheTtlMs <= 0) return false;

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (_cacheClock.ElapsedMilliseconds - entry.TimestampMs < _cacheTtlMs)
                {
                    match = entry.Match;
                    return true;
                }
                _cache.Remove(key); // Expired
            }
        }
        return false;
    }

    private void PutCache(string key, SelectorMatch match)
    {
        if (_cacheTtlMs <= 0) return;

        lock (_cacheLock)
        {
            // Evict expired entries periodically (when cache grows large)
            if (_cache.Count > 50)
                EvictExpired();

            _cache[key] = (match, _cacheClock.ElapsedMilliseconds);
        }
    }

    private void EvictExpired()
    {
        var now = _cacheClock.ElapsedMilliseconds;
        // Iterate keys, collect expired, then remove — avoids LINQ allocation
        List<string>? expired = null;
        foreach (var kv in _cache)
        {
            if (now - kv.Value.TimestampMs >= _cacheTtlMs)
                (expired ??= []).Add(kv.Key);
        }
        if (expired != null)
            foreach (var key in expired)
                _cache.Remove(key);
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

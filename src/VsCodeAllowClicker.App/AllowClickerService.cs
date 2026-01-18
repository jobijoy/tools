using System.Diagnostics;
using System.Windows.Automation;

namespace VsCodeAllowClicker.App;

internal sealed class AllowClickerService : IDisposable
{
    private readonly JsonConfigProvider _configProvider;
    private readonly AutomationLogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private volatile bool _enabled;
    private DateTimeOffset _lastClick = DateTimeOffset.MinValue;
    private string _lastTargetWindowStatus = "Not started";

    public AllowClickerService(JsonConfigProvider configProvider, AutomationLogger logger)
    {
        _configProvider = configProvider;
        _logger = logger;
    }

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _logger.Log(LogLevel.Info, "Service", "Automation service started");
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled != enabled)
        {
            _enabled = enabled;
            _logger.LogStateChange(enabled);
        }
    }

    public string GetTargetWindowStatus() => _lastTargetWindowStatus;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_enabled && _configProvider.TryGetConfig(out var cfg))
            {
                try
                {
                    _logger.Log(LogLevel.Debug, "Polling", $"Scan cycle starting (enabled={_enabled})");
                    TryFindAndClick(cfg);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Automation", "Error during button scan/click", ex);
                }

                await Task.Delay(Math.Max(50, cfg.Polling.IntervalMs), token);
                continue;
            }

            await Task.Delay(250, token);
        }
    }

    private void TryFindAndClick(AppConfig cfg)
    {
        if (DateTimeOffset.UtcNow - _lastClick < TimeSpan.FromMilliseconds(cfg.Polling.ClickThrottleMs))
        {
            return;
        }

        // Get ALL VS Code windows, not just the first one
        var targetWindows = GetAllTargetWindows(cfg);
        if (targetWindows.Count == 0)
        {
            if (_lastTargetWindowStatus != "Not found")
            {
                _lastTargetWindowStatus = "Not found";
                _logger.LogWindowNotFound();
            }
            return;
        }

        _logger.Log(LogLevel.Debug, "WindowSearch", $"Found {targetWindows.Count} VS Code window(s) to check");
        _lastTargetWindowStatus = $"Checking {targetWindows.Count} window(s)";

        foreach (var targetWindow in targetWindows)
        {
            var windowTitle = targetWindow.Current.Name ?? "Unknown";
            var windowHandle = new IntPtr(targetWindow.Current.NativeWindowHandle);
            
            _logger.Log(LogLevel.Debug, "WindowSearch", $"Checking window: '{windowTitle}'");

            var windowRect = targetWindow.Current.BoundingRectangle;
            if (windowRect.IsEmpty)
            {
                continue;
            }

            // Special handling for "Select an account" popup dialogs
            if (TryHandleAccountSelectionDialog(targetWindow, windowHandle, windowTitle))
            {
                _lastClick = DateTimeOffset.UtcNow;
                _lastTargetWindowStatus = $"Clicked in: {windowTitle}";
                return; // Successfully handled
            }

            // Check for webview dialogs that need keyboard fallback
            if (TryWebviewKeyboardFallback(targetWindow, cfg, windowHandle, windowTitle))
            {
                _lastClick = DateTimeOffset.UtcNow;
                _lastTargetWindowStatus = $"Clicked in: {windowTitle}";
                return; // Successfully handled
            }

            var searchRegion = ComputeSearchRegion(cfg.SearchArea, windowRect);

            var buttonCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true));

            var buttons = targetWindow.FindAll(TreeScope.Descendants, buttonCondition);
            var matchingButtons = 0;
            
            // Log buttons found for diagnostics
            if (buttons.Count > 0)
            {
                var buttonNames = new List<string>();
                for (var i = 0; i < buttons.Count && i < 15; i++)
                {
                    var name = buttons[i].Current.Name ?? "(no name)";
                    buttonNames.Add(name);
                }
                _logger.Log(LogLevel.Debug, "ButtonScan", $"Window '{windowTitle}' has {buttons.Count} buttons: [{string.Join(", ", buttonNames)}...]");
            }
            
            for (var i = 0; i < buttons.Count; i++)
            {
                var btn = buttons[i];
                var name = (btn.Current.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (!MatchesAny(name, cfg.Matching.ButtonLabels))
                {
                    continue;
                }

                var rect = btn.Current.BoundingRectangle;
                if (rect.IsEmpty)
                {
                    continue;
                }

                if (!IsInside(rect, searchRegion))
                {
                    continue;
                }

                matchingButtons++;
                _logger.Log(LogLevel.Info, "ButtonMatch", $"Found matching button '{name}' in '{windowTitle}' at ({rect.Left}, {rect.Top})");
                
                // Activate window before clicking
                if (!IsWindowActive(windowHandle))
                {
                    ActivateWindow(windowHandle, windowTitle);
                    Thread.Sleep(200);
                }
                
                if (TryInvoke(btn))
                {
                    _lastClick = DateTimeOffset.UtcNow;
                    _lastTargetWindowStatus = $"Clicked '{name}' in: {windowTitle}";
                    _logger.LogButtonClicked(name, windowTitle);
                    return; // Successfully clicked
                }
            }
            
            if (matchingButtons > 0)
            {
                _logger.LogButtonScan(buttons.Count, matchingButtons);
            }
        }
    }

    private List<AutomationElement> GetAllTargetWindows(AppConfig cfg)
    {
        var results = new List<AutomationElement>();
        var seenHandles = new HashSet<int>();
        
        _logger.Log(LogLevel.Debug, "WindowSearch", "Starting window search (all instances)...");
        
        // Get all processes and filter by name
        var allProcesses = Process.GetProcesses();
        
        foreach (var processName in cfg.Target.ProcessNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            var matchingProcesses = allProcesses
                .Where(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase) ||
                            p.ProcessName.Replace(" ", "").Equals(processName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            _logger.Log(LogLevel.Debug, "WindowSearch", $"Found {matchingProcesses.Count} processes matching '{processName}'");
            
            foreach (var p in matchingProcesses)
            {
                if (p.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                var handle = p.MainWindowHandle.ToInt32();
                if (seenHandles.Contains(handle))
                {
                    continue;
                }

                try
                {
                    var element = AutomationElement.FromHandle(p.MainWindowHandle);
                    var title = element.Current.Name ?? "(no title)";
                    results.Add(element);
                    seenHandles.Add(handle);
                    _logger.Log(LogLevel.Debug, "WindowSearch", $"Added window: '{title}' (PID: {p.Id})");
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Debug, "WindowSearch", $"Failed to get element for process {p.Id}: {ex.Message}");
                }
            }
        }

        // Also search by window title to catch any missed windows
        try
        {
            var root = AutomationElement.RootElement;
            var allWindows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            
            for (var i = 0; i < allWindows.Count; i++)
            {
                var w = allWindows[i];
                var handle = w.Current.NativeWindowHandle;
                
                if (seenHandles.Contains(handle))
                {
                    continue;
                }
                
                var title = w.Current.Name ?? string.Empty;
                
                // Check if this is a VS Code window by title
                if (!string.IsNullOrEmpty(cfg.Target.WindowTitleContains) && 
                    title.IndexOf(cfg.Target.WindowTitleContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(w);
                    seenHandles.Add(handle);
                    _logger.Log(LogLevel.Debug, "WindowSearch", $"Added window by title: '{title}'");
                    continue;
                }
                
                // Check for additional popup windows (auth dialogs, etc.)
                foreach (var additionalTitle in cfg.Target.AdditionalWindowTitles ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(additionalTitle) &&
                        title.IndexOf(additionalTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        results.Add(w);
                        seenHandles.Add(handle);
                        _logger.Log(LogLevel.Info, "WindowSearch", $"Added popup window: '{title}' (matched '{additionalTitle}')");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Debug, "WindowSearch", $"Error searching windows by title: {ex.Message}");
        }

        return results;
    }

    private bool TryHandleAccountSelectionDialog(AutomationElement targetWindow, IntPtr windowHandle, string windowTitle)
    {
        // Only handle windows that look like account selection dialogs
        if (windowTitle.IndexOf("Select an account", StringComparison.OrdinalIgnoreCase) < 0 &&
            windowTitle.IndexOf("Select account", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        _logger.Log(LogLevel.Info, "AccountSelection", $"Handling account selection dialog: '{windowTitle}'");

        try
        {
            // Bring window to front
            NativeMethods.SetForegroundWindow(windowHandle);
            Thread.Sleep(200);

            // Try to find ListItem elements (the account options)
            var listItemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            var listItems = targetWindow.FindAll(TreeScope.Descendants, listItemCondition);

            if (listItems.Count > 0)
            {
                _logger.Log(LogLevel.Info, "AccountSelection", $"Found {listItems.Count} list items (accounts)");
                
                // Click the first account
                var firstItem = listItems[0];
                var itemName = firstItem.Current.Name ?? "(unnamed)";
                var itemRect = firstItem.Current.BoundingRectangle;
                
                if (!itemRect.IsEmpty)
                {
                    var clickX = (int)(itemRect.Left + itemRect.Width / 2);
                    var clickY = (int)(itemRect.Top + itemRect.Height / 2);
                    
                    _logger.Log(LogLevel.Info, "AccountSelection", $"Clicking first account '{itemName}' at ({clickX}, {clickY})");
                    NativeMethods.Click(clickX, clickY);
                    Thread.Sleep(300);
                    
                    // Now find and click Continue button
                    var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                    var buttons = targetWindow.FindAll(TreeScope.Descendants, buttonCondition);
                    
                    for (var i = 0; i < buttons.Count; i++)
                    {
                        var btnName = buttons[i].Current.Name ?? "";
                        if (btnName.StartsWith("Continue", StringComparison.OrdinalIgnoreCase))
                        {
                            var btnRect = buttons[i].Current.BoundingRectangle;
                            if (!btnRect.IsEmpty)
                            {
                                var btnX = (int)(btnRect.Left + btnRect.Width / 2);
                                var btnY = (int)(btnRect.Top + btnRect.Height / 2);
                                
                                _logger.Log(LogLevel.Info, "AccountSelection", $"Clicking Continue button at ({btnX}, {btnY})");
                                Thread.Sleep(200);
                                NativeMethods.Click(btnX, btnY);
                                return true;
                            }
                        }
                    }
                    
                    // Fallback: press Enter after selecting account
                    _logger.Log(LogLevel.Info, "AccountSelection", "Continue button not found, sending Enter key");
                    Thread.Sleep(200);
                    NativeMethods.SendKey(NativeMethods.VK_RETURN);
                    return true;
                }
            }

            // Fallback: Try clicking in likely locations or sending keys
            // The GitHub "Select an account" dialog layout:
            // - Title at top (~15%)
            // - "Why am I being asked" link (~20%)
            // - Account list starts ~35%, each account ~10% height
            // - Continue button at ~75-80%
            // - "Add a new account" at bottom
            var windowRect = targetWindow.Current.BoundingRectangle;
            _logger.Log(LogLevel.Info, "AccountSelection", $"Dialog rect: {windowRect}");
            
            if (!windowRect.IsEmpty && windowRect.Width > 50 && windowRect.Height > 50)
            {
                // Click the first account (about 38% from top)
                var accountX = (int)(windowRect.Left + windowRect.Width / 2);
                var accountY = (int)(windowRect.Top + windowRect.Height * 0.38);
                
                _logger.Log(LogLevel.Info, "AccountSelection", $"Clicking first account at ({accountX}, {accountY})");
                NativeMethods.Click(accountX, accountY);
                Thread.Sleep(400);
                
                // Click Continue button (about 78% from top)
                var continueY = (int)(windowRect.Top + windowRect.Height * 0.78);
                _logger.Log(LogLevel.Info, "AccountSelection", $"Clicking Continue button at ({accountX}, {continueY})");
                NativeMethods.Click(accountX, continueY);
                
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("AccountSelection", "Failed to handle account selection dialog", ex);
            return false;
        }
    }

    private bool TryWebviewKeyboardFallback(AutomationElement targetWindow, AppConfig cfg, IntPtr windowHandle, string windowTitle)
    {
        var fallback = cfg.Matching.WebviewFallback;
        if (fallback?.Enabled != true)
        {
            return false;
        }

        try
        {
            // VS Code webview dialogs are not accessible via UI Automation
            // We need to detect them indirectly and use keyboard/mouse fallback
            
            // Strategy: Look for Document/Pane controls that might be webviews
            var documentCondition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));
            
            var documents = targetWindow.FindAll(TreeScope.Descendants, documentCondition);
            
            _logger.Log(LogLevel.Debug, "WebviewFallback", $"Checking {documents.Count} document/pane elements in '{windowTitle}'");
            
            bool foundAuthDialog = false;
            string foundDialogName = "";
            System.Windows.Rect dialogRect = System.Windows.Rect.Empty;
            System.Windows.Rect chatPanelRect = System.Windows.Rect.Empty;
            
            // Log all document/pane elements to understand what we're seeing
            for (var i = 0; i < documents.Count; i++)
            {
                var doc = documents[i];
                var docName = doc.Current.Name ?? "";
                var docClass = doc.Current.ClassName ?? "";
                var docRect = doc.Current.BoundingRectangle;
                var docType = doc.Current.ControlType.ProgrammaticName;
                
                _logger.Log(LogLevel.Debug, "WebviewFallback", $"  Element {i}: Type={docType}, Name='{docName}', Class='{docClass}', Rect={docRect.Width}x{docRect.Height}");
                
                // Check if this looks like a Copilot/GitHub auth webview
                foreach (var trigger in fallback.TriggerWindowTitles)
                {
                    if (docName.IndexOf(trigger, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foundAuthDialog = true;
                        foundDialogName = docName;
                        dialogRect = docRect;
                        _logger.Log(LogLevel.Info, "WebviewFallback", $"Found dialog by name: '{docName}' in '{windowTitle}'");
                        break;
                    }
                }
                
                // Check for Chrome_RenderWidgetHostHWND which is the Chromium webview
                if (!foundAuthDialog && (docClass.Contains("Chrome") || docClass.Contains("Intermediate")))
                {
                    var windowRect = targetWindow.Current.BoundingRectangle;
                    // Check if it's in the right portion of the window (chat panel)
                    if (!docRect.IsEmpty && docRect.Left > windowRect.Left + (windowRect.Width * 0.5) && docRect.Height > 200)
                    {
                        chatPanelRect = docRect;
                        _logger.Log(LogLevel.Debug, "WebviewFallback", $"Found potential chat webview at right side: {docRect}");
                    }
                }
                
                if (foundAuthDialog) break;
            }

            // If we didn't find a named dialog but found a chat panel webview, try clicking it
            if (!foundAuthDialog && !chatPanelRect.IsEmpty)
            {
                // The "Select an account" dialog appears in the chat panel
                // We'll try clicking where the "Continue" button would be
                foundAuthDialog = true;
                foundDialogName = "Chat Panel (potential auth dialog)";
                dialogRect = chatPanelRect;
                _logger.Log(LogLevel.Info, "WebviewFallback", $"Using chat panel webview for potential auth dialog in '{windowTitle}'");
            }

            // If we still haven't found anything, check the window title for triggers
            if (!foundAuthDialog)
            {
                foreach (var trigger in fallback.TriggerWindowTitles)
                {
                    if (windowTitle.IndexOf(trigger, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foundAuthDialog = true;
                        foundDialogName = windowTitle;
                        _logger.Log(LogLevel.Info, "WebviewFallback", $"Found trigger in window title: '{windowTitle}'");
                        break;
                    }
                }
            }

            if (!foundAuthDialog)
            {
                return false;
            }

            _logger.Log(LogLevel.Info, "WebviewFallback", $"Attempting interaction for '{foundDialogName}' in '{windowTitle}'");

            // Ensure window is focused
            NativeMethods.SetForegroundWindow(windowHandle);
            Thread.Sleep(150);

            // If we have a dialog rect, try clicking in the button area
            if (!dialogRect.IsEmpty && dialogRect.Width > 100 && dialogRect.Height > 100)
            {
                // For the GitHub account selection dialog:
                // - The "Continue" button is roughly at the center horizontally
                // - And about 60-80px from the bottom of the dialog area
                // - First account is already selected, so just need to click Continue
                
                var clickX = (int)(dialogRect.Left + (dialogRect.Width / 2));
                var clickY = (int)(dialogRect.Top + (dialogRect.Height * 0.7)); // 70% down for button area
                
                _logger.Log(LogLevel.Info, "WebviewFallback", $"Clicking at ({clickX}, {clickY}) in dialog rect {dialogRect}");
                NativeMethods.Click(clickX, clickY);
                Thread.Sleep(300);
                
                // Also try Enter key as backup
                NativeMethods.SendKey(NativeMethods.VK_RETURN);
                _logger.Log(LogLevel.Debug, "WebviewFallback", "Also sent Enter key");
                
                return true;
            }

            // Fallback: Send keyboard sequence
            foreach (var keyName in fallback.KeySequence)
            {
                var keyCode = keyName.ToLowerInvariant() switch
                {
                    "tab" => NativeMethods.VK_TAB,
                    "enter" or "return" => NativeMethods.VK_RETURN,
                    "down" => NativeMethods.VK_DOWN,
                    "escape" or "esc" => NativeMethods.VK_ESCAPE,
                    _ => (ushort)0
                };

                if (keyCode != 0)
                {
                    NativeMethods.SendKey(keyCode);
                    _logger.Log(LogLevel.Debug, "WebviewFallback", $"Sent key: {keyName}");
                    Thread.Sleep(fallback.DelayBetweenKeysMs);
                }
            }

            _logger.Log(LogLevel.Info, "WebviewFallback", $"Sent keyboard sequence to '{foundDialogName}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("WebviewFallback", "Failed to handle webview dialog", ex);
            return false;
        }
    }

    private static bool IsWindowActive(IntPtr hWnd)
    {
        try
        {
            return NativeMethods.IsWindowVisible(hWnd) && !NativeMethods.IsIconic(hWnd);
        }
        catch
        {
            return true;
        }
    }

    private bool ActivateWindow(IntPtr hWnd, string windowTitle)
    {
        try
        {
            bool wasMinimized = NativeMethods.IsIconic(hWnd);
            
            if (wasMinimized)
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
                _logger.Log(LogLevel.Info, "WindowActivation", $"Restored minimized window '{windowTitle}'");
            }
            
            if (NativeMethods.SetForegroundWindow(hWnd))
            {
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("WindowActivation", $"Failed to activate window '{windowTitle}'", ex);
            return false;
        }
    }

    private static System.Windows.Rect ComputeSearchRegion(SearchAreaConfig cfg, System.Windows.Rect windowRect)
    {
        var mode = (cfg.Mode ?? string.Empty).Trim();
        if (mode.Equals("NormalizedRect", StringComparison.OrdinalIgnoreCase))
        {
            var nr = cfg.NormalizedRect ?? new NormalizedRect { X = 0.6, Y = 0.0, Width = 0.4, Height = 1.0 };
            return new System.Windows.Rect(
                windowRect.Left + (windowRect.Width * Clamp01(nr.X)),
                windowRect.Top + (windowRect.Height * Clamp01(nr.Y)),
                windowRect.Width * Clamp01(nr.Width),
                windowRect.Height * Clamp01(nr.Height));
        }

        var start = Clamp01(cfg.RightFractionStart);
        var left = windowRect.Left + (windowRect.Width * start);
        return new System.Windows.Rect(left, windowRect.Top, windowRect.Right - left, windowRect.Height);
    }

    // Buttons to exclude from clicking (VS Code UI buttons that shouldn't be auto-clicked)
    private static readonly string[] ExcludedButtonPatterns = [
        "Continue Chat in",
        "Continue in",
        "Chat in"
    ];

    private static bool MatchesAny(string value, string[] labels)
    {
        // First check exclusions
        foreach (var excluded in ExcludedButtonPatterns)
        {
            if (value.IndexOf(excluded, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }
        
        foreach (var label in labels ?? [])
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var trimmedLabel = label.Trim();
            
            // Support both exact match and "starts with" for buttons like "Allow (Ctrl+Enter)"
            if (value.Equals(trimmedLabel, StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(trimmedLabel + " ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(trimmedLabel + "(", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(System.Windows.Rect elementRect, System.Windows.Rect searchRegion)
    {
        var cx = elementRect.Left + (elementRect.Width / 2.0);
        var cy = elementRect.Top + (elementRect.Height / 2.0);
        return cx >= searchRegion.Left && cx <= searchRegion.Right && cy >= searchRegion.Top && cy <= searchRegion.Bottom;
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
            {
                inv.Invoke();
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var pt = element.GetClickablePoint();
            NativeMethods.Click((int)pt.X, (int)pt.Y);
            return true;
        }
        catch
        {
        }

        try
        {
            var r = element.Current.BoundingRectangle;
            if (!r.IsEmpty)
            {
                var x = (int)(r.Left + (r.Width / 2.0));
                var y = (int)(r.Top + (r.Height / 2.0));
                NativeMethods.Click(x, y);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static double Clamp01(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v < 0) return 0;
        if (v > 1) return 1;
        return v;
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _worker?.Wait(1500);
        }
        catch
        {
        }

        _cts?.Dispose();
    }
}

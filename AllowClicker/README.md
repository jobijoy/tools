# VS Code Allow Clicker

A Windows tray application that automatically clicks "Allow" buttons (and similar permission dialogs) in Visual Studio Code using UI Automation.

## Features

- **Background Monitoring**: Runs quietly in the system tray
- **Control Panel**: Real-time log viewer, statistics, and configuration controls
- **Multi-Step Dialogs**: Handle complex flows like "select list item → click button"
- **Comprehensive Logging**: 
  - File-based logs for regression analysis
  - Live log viewer with filterable levels (Debug/Info/Warning/Error)
  - Timestamped events with metadata
  - Logs button clicks, window detection, scan results, errors
- **UI Automation**: Uses Windows UI Automation API to reliably find and click buttons
- **Configurable Targeting**: 
  - Target specific process names (Code, Code - Insiders)
  - Match multiple button labels (Allow, Yes, OK, Authorize, Accept)
  - Restrict search to right panel or custom screen regions
- **Click Throttling**: Prevents repeated clicks with configurable cooldown
- **Toggle On/Off**: 
  - Double-click tray icon
  - Use context menu
  - Global hotkey (default: Ctrl+Alt+A)
- **Zero UI Overhead**: No main window, runs entirely in tray

## Requirements

- Windows 10/11
- .NET 8.0 Runtime (Windows Desktop)
- Visual Studio Code (or target application)

## Installation

### Option 1: Build from Source

```powershell
# Clone or download this repository
cd C:\Data\gitP\utils\AllowClicker

# Build the release version
dotnet build VsCodeAllowClicker.sln -c Release

# Run the executable
.\src\VsCodeAllowClicker.App\bin\Release\net8.0-windows\VsCodeAllowClicker.App.exe
```

### Option 2: Run Directly

```powershell
cd C:\Data\gitP\utils\AllowClicker
dotnet run --project src\VsCodeAllowClicker.App\VsCodeAllowClicker.App.csproj -c Release
```

## Configuration

Edit `appsettings.json` in the application directory:

```json
{
  "Target": {
    "ProcessNames": [ "Code", "Code - Insiders" ],
    "WindowTitleContains": "Visual Studio Code"
  },
  "Matching": {
    "ButtonLabels": [ "Allow", "Yes", "OK", "Authorize", "Accept" ]
  },
  "SearchArea": {
    "Mode": "RightFraction",
    "RightFractionStart": 0.60,
    "NormalizedRect": { "X": 0.60, "Y": 0.00, "Width": 0.40, "Height": 1.00 }
  },
  "Polling": {
    "IntervalMs": 400,
    "ClickThrottleMs": 1500
  },
  "Hotkey": {
    "Enabled": true,
    "Modifiers": [ "Ctrl", "Alt" ],
    "Key": "A"
  }
}
```

### Configuration Options

#### Target
- **ProcessNames**: Array of process names to monitor (without .exe)
- **WindowTitleContains**: Fallback window title search string

#### Matching
- **ButtonLabels**: Array of button text variants to match (case-insensitive)
- **PreClick**: Optional pre-click action for multi-step dialogs
  - **Enabled**: Turn pre-click on/off
  - **ControlType**: Type of control to click first (`ListItem`, `Button`, etc.)
  - **SelectionMode**: How to select (`First`, `Last`, `Index`, `ByName`)
  - **Index**: Position in list (0-based) for Index mode
  - **Name**: Text to match for ByName mode
  - **DelayAfterMs**: Wait time after pre-click before clicking main button

#### SearchArea
- **Mode**: `"RightFraction"` (right portion of window) or `"NormalizedRect"` (custom rectangle)
- **RightFractionStart**: For RightFraction mode, where the search region starts (0.0 = left edge, 1.0 = right edge)
- **NormalizedRect**: For NormalizedRect mode, custom region in normalized coordinates (0.0-1.0)

#### Polling
- **IntervalMs**: How often to scan for buttons (milliseconds) - Default: 30000 (30 seconds to prevent mouse hijacking)
- **ClickThrottleMs**: Minimum time between clicks (prevents double-clicking)

#### Hotkey
- **Enabled**: Enable/disable global hotkey
- **Modifiers**: Array of modifier keys: `"Ctrl"`, `"Alt"`, `"Shift"`, `"Win"`
- **Key**: Letter (A-Z), digit (0-9), or function key (F1-F12)

#### UI
- **AutoStartEnabled**: If `true`, automation starts immediately on launch. If `false` (default), you must manually start it.
- **ShowControlPanelOnStart**: If `true` (default), Control Panel window opens on startup
- **ControlPanelPosition**: Where to position the window: `"BottomLeft"`, `"BottomRight"`, `"TopLeft"`, `"TopRight"`, `"Center"`

## Usage

1. **Start the application** - A shield icon appears in the system tray
2. **Control Panel opens at bottom-left** (automation is DISABLED by default)
3. **Press Ctrl+Alt+A to START automation** (press again to STOP)
4. **Control Panel features**:
   - **Live log viewer**: See all automation events in real-time
   - **Status display**: Uptime, click count, target window status
   - **Log level filter**: Debug/Info/Warning/Error
   - **Quick actions**: Clear logs, open log file, edit config, reload settings
   - **Enable/Disable toggle**: Checkbox to pause automation
5. **View logs**: 
   - Control panel for live viewing
   - Right-click tray → "Open logs folder" for file access
   - Logs saved as `logs/automation_YYYYMMDD_HHMMSS.log`
6. **Exit**: Right-click → Exit

⚠️ **Default behavior**: Automation is **DISABLED on startup** to prevent mouse hijacking. You must explicitly enable it via hotkey (`Ctrl+Alt+A`) or the Control Panel checkbox.

## How It Works

1. **Window Detection**: Finds the target application window by process name or title
2. **Button Scanning**: Uses UI Automation to enumerate all Button controls in the window
3. **Filtering**: 
   - Matches button name against configured labels
   - Checks if button is within the configured search region
   - Applies click throttle to prevent spam
4. **Clicking**: 
   - Primary: Uses UI Automation `InvokePattern.Invoke()` (most reliable)
   - Fallback 1: Gets clickable point and simulates mouse click
   - Fallback 2: Clicks center of button's bounding rectangle
5. **Logging**: All events logged with timestamps, metadata, and severity levels
   - Button clicks: Records button name, window title, timestamp
   - Window detection: Logs when target found/lost
   - Button scans: Shows total buttons found vs matching buttons
   - Errors: Captures exceptions with stack traces

## Troubleshooting

### Using the Control Panel for Debugging

1. Open the Control Panel (double-click tray icon)
2. Set log level to "Debug" to see detailed scanning activity
3. Watch the live log for:
   - "WindowDetection" events (is target window found?)
   - "ButtonScan" events (how many buttons scanned/matched?)
   - "ButtonClick" events (were buttons actually clicked?)
4. Check statistics: uptime, click count, current target status
5. Export logs: Click "Open Log File" to view full session history

### "No buttons found"
- Use Windows **Inspect.exe** tool (from Windows SDK) to verify the button is exposed to UI Automation
- Check that the button's `Name` property matches one of your configured labels
- Try expanding the search region (lower `RightFractionStart` value)

### "Button found but not clicked"
- Verify the button is enabled (`IsEnabled = true`)
- Check if the button supports `InvokePattern` in Inspect.exe
- Try adjusting `ClickThrottleMs` if clicks seem delayed

### "Process not found"
- Ensure process name matches exactly (check Task Manager)
- Try adding more process name variants to `ProcessNames` array
- Verify `WindowTitleContains` is a substring of the actual window title

### "Process not found"
- Ensure process name matches exactly (check Task Manager)
- Try adding more process name variants to `ProcessNames` array
- Verify `WindowTitleContains` is a substring of the actual window title
- Check Control Panel logs for "WindowDetection" events

### Analyzing Regression Test Runs

1. Each application session creates a new log file in `logs/` folder
2. Log files named: `automation_YYYYMMDD_HHMMSS.log`
3. Search logs for specific events:
   ```powershell
   # Find all button clicks
   Select-String -Path "logs\*.log" -Pattern "ButtonClick"
   
   # Count automation sessions
   Get-ChildItem logs\*.log | Measure-Object
   
   # Find errors
   Select-String -Path "logs\*.log" -Pattern "ERROR"
   ```
4. Use the metadata fields for detailed analysis (ButtonName, WindowTitle, timestamps)

### Global hotkey not working
- Check if another application is using the same hotkey combination
- Try a different key combination in `appsettings.json`
- Restart the application after changing hotkey settings

## Advanced: OCR Fallback

If UI Automation fails for certain dialogs, you can extend the app with OCR:

1. Add **Tesseract** NuGet package
2. Capture screenshot of search region using `Graphics.CopyFromScreen`
3. Run OCR to find "Allow" text coordinates
4. Simulate click at detected position

This fallback isn't included by default to keep the app lightweight and reliable.

## Technical Details

- **Language**: C# / .NET 8.0
- **Framework**: Windows Forms (tray icon), WPF (System.Windows.Rect)
- **APIs**: 
  - `System.Windows.Automation` - UI element discovery and invocation
  - `user32.dll` P/Invoke - Mouse simulation and global hotkeys
- **Architecture**:
  - Tray-only application (no main window)
  - Background polling loop with cancellation token
  - Thread-safe enable/disable toggle

## License

This project is provided as-is for educational and automation purposes. Use responsibly and ensure compliance with your organization's policies regarding UI automation.

## Credits

Built following best practices from:
- Microsoft UI Automation documentation
- Stack Overflow UI automation community
- Windows Accessibility guidelines

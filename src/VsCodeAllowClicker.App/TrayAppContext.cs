using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace VsCodeAllowClicker.App;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _enabledMenuItem;

    private readonly AllowClickerService _service;
    private readonly AutomationLogger _logger;
    private readonly HotkeyWindow? _hotkeyWindow;
    private readonly HotkeyWindow? _confirmHotkeyWindow;
    private readonly ControlPanelForm _controlPanel;
    private readonly JsonConfigProvider _configProvider;

    private bool _enabled = false;

    public TrayAppContext()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _configProvider = new JsonConfigProvider(configPath);

        _logger = new AutomationLogger();
        _service = new AllowClickerService(_configProvider, _logger);
        _controlPanel = new ControlPanelForm(_service, _logger, _configProvider, SetEnabled);

        _enabledMenuItem = new ToolStripMenuItem("Enabled")
        {
            Checked = _enabled,
            CheckOnClick = true
        };
        _enabledMenuItem.CheckedChanged += (_, _) => SetEnabled(_enabledMenuItem.Checked);

        var showControlPanelItem = new ToolStripMenuItem("Show Control Panel");
        showControlPanelItem.Click += (_, _) =>
        {
            _controlPanel.Show();
            _controlPanel.BringToFront();
        };

        var openConfigItem = new ToolStripMenuItem("Open config folder");
        openConfigItem.Click += (_, _) =>
        {
            var folder = AppContext.BaseDirectory;
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        };

        var openLogsItem = new ToolStripMenuItem("Open logs folder");
        openLogsItem.Click += (_, _) =>
        {
            var logsFolder = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", logsFolder) { UseShellExecute = true });
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showControlPanelItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "VS Code Allow Clicker",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            _controlPanel.Show();
            _controlPanel.BringToFront();
        };

        _logger.Log(LogLevel.Info, "Startup", "VS Code Allow Clicker started");
        
        // Check AutoStart config
        if (_configProvider.TryGetConfig(out var startCfg))
        {
            _enabled = startCfg.UI.AutoStartEnabled;
            
            // Position control panel
            PositionControlPanel(startCfg.UI.ControlPanelPosition);
            
            // Show control panel on start if configured
            if (startCfg.UI.ShowControlPanelOnStart)
            {
                _controlPanel.Show();
            }
        }
        
        _service.SetEnabled(_enabled);
        _service.Start();

        if (_configProvider.TryGetConfig(out var cfg) && cfg.Hotkey.Enabled)
        {
            _hotkeyWindow = new HotkeyWindow(() =>
            {
                SetEnabled(!_enabled);
                if (!_controlPanel.Visible)
                {
                    _controlPanel.Show();
                    _controlPanel.BringToFront();
                }
            });
            _hotkeyWindow.TryRegister(cfg.Hotkey);
        }

        // Register confirm hotkey (Ctrl+Alt+C by default) to send Enter key
        if (_configProvider.TryGetConfig(out var cfg2) && cfg2.ConfirmHotkey.Enabled)
        {
            _confirmHotkeyWindow = new HotkeyWindow(() =>
            {
                _logger.Log(LogLevel.Info, "ConfirmHotkey", "Sending Enter key to confirm dialog");
                NativeMethods.SendKey(NativeMethods.VK_RETURN);
            });
            _confirmHotkeyWindow.TryRegister(cfg2.ConfirmHotkey);
        }

        var statusMsg = _enabled ? "Automation ENABLED. Press Ctrl+Alt+A to stop." : "Automation DISABLED. Press Ctrl+Alt+A to start.";
        _notifyIcon.ShowBalloonTip(2000, "VS Code Allow Clicker", statusMsg, ToolTipIcon.Info);
    }

    private void PositionControlPanel(string position)
    {
        var screen = Screen.PrimaryScreen.WorkingArea;
        
        switch (position.ToLowerInvariant())
        {
            case "bottomleft":
                _controlPanel.StartPosition = FormStartPosition.Manual;
                _controlPanel.Location = new Point(screen.Left + 20, screen.Bottom - _controlPanel.Height - 20);
                break;
            case "bottomright":
                _controlPanel.StartPosition = FormStartPosition.Manual;
                _controlPanel.Location = new Point(screen.Right - _controlPanel.Width - 20, screen.Bottom - _controlPanel.Height - 20);
                break;
            case "topleft":
                _controlPanel.StartPosition = FormStartPosition.Manual;
                _controlPanel.Location = new Point(screen.Left + 20, screen.Top + 20);
                break;
            case "topright":
                _controlPanel.StartPosition = FormStartPosition.Manual;
                _controlPanel.Location = new Point(screen.Right - _controlPanel.Width - 20, screen.Top + 20);
                break;
            case "center":
            default:
                _controlPanel.StartPosition = FormStartPosition.CenterScreen;
                break;
        }
    }

    private void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _enabledMenuItem.Checked = enabled;
        _controlPanel.SetEnabled(enabled);
        _service.SetEnabled(enabled);
        _notifyIcon.Text = enabled ? "VS Code Allow Clicker (Enabled)" : "VS Code Allow Clicker (Disabled)";
    }

    protected override void ExitThreadCore()
    {
        _logger.Log(LogLevel.Info, "Shutdown", "Application shutting down");
        
        _controlPanel?.Dispose();
        _service.Dispose();
        _hotkeyWindow?.Dispose();
        _confirmHotkeyWindow?.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        base.ExitThreadCore();
    }
}

using System.Diagnostics;
using System.Drawing;
using System.IO;
using VsCodeAllowClicker.App.Services;
using VsCodeAllowClicker.App.UI;

namespace VsCodeAllowClicker.App;

/// <summary>
/// System tray application context.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly AutomationEngine _engine;
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly MainForm _mainForm;
    private readonly HotkeyService? _hotkey;
    private readonly ToolStripMenuItem _enabledItem;

    public TrayApp()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _config = new ConfigService(configPath);
        _log = new LogService();
        _engine = new AutomationEngine(_config, _log);

        var cfg = _config.GetConfig();
        _log.SetLevel(cfg.Settings.LogLevel);

        _mainForm = new MainForm(_engine, _config, _log, SetEnabled);

        // Setup tray
        _enabledItem = new ToolStripMenuItem("Enabled") { CheckOnClick = true };
        _enabledItem.CheckedChanged += (s, e) => SetEnabled(_enabledItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Panel", null, (s, e) => ShowPanel());
        menu.Items.Add("Open Config Folder", null, (s, e) => Process.Start("explorer.exe", AppContext.BaseDirectory));
        menu.Items.Add("Open Logs Folder", null, (s, e) => { Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs")); Process.Start("explorer.exe", Path.Combine(AppContext.BaseDirectory, "logs")); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitThread());

        _tray = new NotifyIcon
        {
            Text = "UI Automation Tool",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (s, e) => ShowPanel();

        // Register hotkey
        if (!string.IsNullOrEmpty(cfg.Settings.ToggleHotkey))
        {
            _hotkey = new HotkeyService(cfg.Settings.ToggleHotkey, () =>
            {
                SetEnabled(!_engine.IsEnabled);
                ShowPanel();
            });
        }

        // Wire up alerts
        _engine.OnAlert += (rule, msg) =>
        {
            _tray.ShowBalloonTip(3000, $"Alert: {rule.Name}", msg, ToolTipIcon.Warning);
            _log.Warn("Alert", msg);
        };

        _engine.OnConfirmRequired += (rule, element) =>
        {
            var result = MessageBox.Show(
                $"Rule '{rule.Name}' wants to click '{element}'.\n\nProceed?",
                "Confirm Action",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            return result == DialogResult.Yes;
        };

        // Start
        _engine.Start();
        if (cfg.Settings.AutoStart)
            SetEnabled(true);
        if (cfg.Settings.ShowPanelOnStart)
            ShowPanel();

        _log.Info("App", "UI Automation Tool started");
        _tray.ShowBalloonTip(2000, "UI Automation Tool", $"Press {cfg.Settings.ToggleHotkey} to toggle", ToolTipIcon.Info);
    }

    private void ShowPanel()
    {
        _mainForm.Show();
        _mainForm.BringToFront();
        _mainForm.WindowState = FormWindowState.Normal;
    }

    private void SetEnabled(bool enabled)
    {
        _engine.SetEnabled(enabled);
        _enabledItem.Checked = enabled;
        _mainForm.SetEnabled(enabled);
        _tray.Text = enabled ? "UI Automation (Enabled)" : "UI Automation (Disabled)";
    }

    protected override void ExitThreadCore()
    {
        _log.Info("App", "Shutting down");
        _mainForm.Dispose();
        _engine.Dispose();
        _hotkey?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}

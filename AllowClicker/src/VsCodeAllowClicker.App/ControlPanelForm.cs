using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace VsCodeAllowClicker.App;

internal sealed class ControlPanelForm : Form
{
    private readonly AllowClickerService _service;
    private readonly AutomationLogger _logger;
    private readonly JsonConfigProvider _configProvider;
    private readonly Action<bool> _onEnabledChanged;

    private ListBox _logListBox = null!;
    private Label _statusLabel = null!;
    private Label _statsLabel = null!;
    private CheckBox _enabledCheckBox = null!;
    private ComboBox _logLevelComboBox = null!;
    private Button _clearLogsButton = null!;
    private Button _openLogFileButton = null!;
    private Button _openConfigButton = null!;
    private Button _reloadConfigButton = null!;
    
    private LogLevel _currentLogLevel = LogLevel.Info;
    private int _clickCount;
    private DateTime _startTime = DateTime.Now;
    private System.Windows.Forms.Timer _updateTimer = null!;

    public ControlPanelForm(AllowClickerService service, AutomationLogger logger, JsonConfigProvider configProvider, Action<bool> onEnabledChanged)
    {
        _service = service;
        _logger = logger;
        _configProvider = configProvider;
        _onEnabledChanged = onEnabledChanged;

        InitializeComponents();
        SubscribeToEvents();
        LoadInitialLogs();
        StartUpdateTimer();
    }

    private void InitializeComponents()
    {
        Text = "VS Code Allow Clicker - Control Panel";
        Size = new Size(900, 650);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Shield;

        // Top control panel
        var controlPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 120,
            Padding = new Padding(10)
        };

        _statusLabel = new Label
        {
            Text = "Status: Running",
            Location = new Point(10, 10),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold)
        };

        _statsLabel = new Label
        {
            Text = "Uptime: 0s | Clicks: 0 | Target: Not found",
            Location = new Point(10, 35),
            AutoSize = true,
            ForeColor = Color.DarkSlateGray
        };

        _enabledCheckBox = new CheckBox
        {
            Text = "Automation Enabled",
            Location = new Point(10, 60),
            AutoSize = true,
            Checked = false  // Start unchecked, will be set by TrayAppContext
        };
        _enabledCheckBox.CheckedChanged += (s, e) =>
        {
            _onEnabledChanged(_enabledCheckBox.Checked);
        };

        _logLevelComboBox = new ComboBox
        {
            Location = new Point(10, 85),
            Width = 120,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _logLevelComboBox.Items.AddRange(new object[] { "Debug", "Info", "Warning", "Error" });
        _logLevelComboBox.SelectedIndex = 1;
        _logLevelComboBox.SelectedIndexChanged += (s, e) =>
        {
            _currentLogLevel = (LogLevel)_logLevelComboBox.SelectedIndex;
            RefreshLogDisplay();
        };

        var logLevelLabel = new Label
        {
            Text = "Log Level:",
            Location = new Point(140, 88),
            AutoSize = true
        };

        _clearLogsButton = new Button
        {
            Text = "Clear Logs",
            Location = new Point(250, 83),
            Width = 100
        };
        _clearLogsButton.Click += (s, e) =>
        {
            _logListBox.Items.Clear();
            _logger.Log(LogLevel.Info, "UI", "Logs cleared from display");
        };

        _openLogFileButton = new Button
        {
            Text = "Open Log File",
            Location = new Point(360, 83),
            Width = 120
        };
        _openLogFileButton.Click += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(_logger.GetLogFilePath()) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open log file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        _openConfigButton = new Button
        {
            Text = "Edit Config",
            Location = new Point(490, 83),
            Width = 100
        };
        _openConfigButton.Click += (s, e) =>
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open config: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        _reloadConfigButton = new Button
        {
            Text = "Reload Config",
            Location = new Point(600, 83),
            Width = 110
        };
        _reloadConfigButton.Click += (s, e) =>
        {
            _logger.LogConfigReload();
            MessageBox.Show("Configuration reloaded. Note: Some settings require restart.", "Config Reloaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var exitButton = new Button
        {
            Text = "Exit App",
            Location = new Point(720, 83),
            Width = 100,
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        exitButton.Click += (s, e) =>
        {
            if (MessageBox.Show("Are you sure you want to exit VS Code Allow Clicker?", "Exit Application", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Application.Exit();
            }
        };

        controlPanel.Controls.AddRange(new Control[] { _statusLabel, _statsLabel, _enabledCheckBox, _logLevelComboBox, logLevelLabel, _clearLogsButton, _openLogFileButton, _openConfigButton, _reloadConfigButton, exitButton });

        // Log viewer
        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };

        var logLabel = new Label
        {
            Text = "Automation Log:",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font(Font.FontFamily, 9, FontStyle.Bold)
        };

        _logListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 8.5f),
            HorizontalScrollbar = true,
            SelectionMode = SelectionMode.MultiExtended
        };
        _logListBox.DoubleClick += (s, e) =>
        {
            if (_logListBox.SelectedItem is string selectedLog)
            {
                Clipboard.SetText(selectedLog);
                MessageBox.Show("Log entry copied to clipboard.", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        logPanel.Controls.Add(_logListBox);
        logPanel.Controls.Add(logLabel);

        Controls.Add(logPanel);
        Controls.Add(controlPanel);
    }

    private void SubscribeToEvents()
    {
        _logger.LogAdded += OnLogAdded;
    }

    private void OnLogAdded(LogEntry entry)
    {
        if (entry.Level < _currentLogLevel)
        {
            return;
        }

        if (entry.Category == "ButtonClick")
        {
            _clickCount++;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AddLogToDisplay(entry));
        }
        else
        {
            AddLogToDisplay(entry);
        }
    }

    private void AddLogToDisplay(LogEntry entry)
    {
        var levelIcon = entry.Level switch
        {
            LogLevel.Debug => "🔍",
            LogLevel.Info => "ℹ️",
            LogLevel.Warning => "⚠️",
            LogLevel.Error => "❌",
            _ => "•"
        };

        var line = $"[{entry.Timestamp:HH:mm:ss.fff}] {levelIcon} {entry.Category,-20} {entry.Message}";
        _logListBox.Items.Add(line);

        // Auto-scroll to bottom
        _logListBox.TopIndex = _logListBox.Items.Count - 1;

        // Keep list size manageable
        while (_logListBox.Items.Count > 1000)
        {
            _logListBox.Items.RemoveAt(0);
        }
    }

    private void LoadInitialLogs()
    {
        var recentLogs = _logger.GetRecentLogs(100);
        foreach (var entry in recentLogs)
        {
            if (entry.Level >= _currentLogLevel)
            {
                AddLogToDisplay(entry);
            }
        }
    }

    private void RefreshLogDisplay()
    {
        _logListBox.Items.Clear();
        LoadInitialLogs();
    }

    private void StartUpdateTimer()
    {
        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _updateTimer.Tick += (s, e) => UpdateStatus();
        _updateTimer.Start();
    }

    private void UpdateStatus()
    {
        var uptime = DateTime.Now - _startTime;
        var uptimeStr = uptime.TotalHours >= 1 
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
            : uptime.TotalMinutes >= 1
                ? $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s"
                : $"{uptime.Seconds}s";

        var targetStatus = _service.GetTargetWindowStatus();
        
        _statsLabel.Text = $"Uptime: {uptimeStr} | Clicks: {_clickCount} | Target: {targetStatus}";
        _statusLabel.Text = $"Status: {(_enabledCheckBox.Checked ? "Running" : "Paused")}";
        _statusLabel.ForeColor = _enabledCheckBox.Checked ? Color.Green : Color.Orange;
    }

    public void SetEnabled(bool enabled)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => _enabledCheckBox.Checked = enabled);
        }
        else
        {
            _enabledCheckBox.Checked = enabled;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Don't close, just hide
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updateTimer?.Stop();
            _updateTimer?.Dispose();
            _logger.LogAdded -= OnLogAdded;
        }

        base.Dispose(disposing);
    }
}

using System.Diagnostics;
using System.Drawing;
using VsCodeAllowClicker.App.Services;

namespace VsCodeAllowClicker.App.UI;

/// <summary>
/// Main control panel with live log, status, and rule management.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AutomationEngine _engine;
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly Action<bool> _onToggle;

    private ListBox _logList = null!;
    private Label _statusLabel = null!;
    private CheckBox _enabledCheck = null!;
    private ListView _rulesList = null!;
    private System.Windows.Forms.Timer _timer = null!;
    private int _clickCount;

    public MainForm(AutomationEngine engine, ConfigService config, LogService log, Action<bool> onToggle)
    {
        _engine = engine;
        _config = config;
        _log = log;
        _onToggle = onToggle;
        InitUI();
        LoadRules();
        _log.OnLog += OnLog;
    }

    private void InitUI()
    {
        Text = "UI Automation Tool";
        Size = new Size(800, 600);
        MinimumSize = new Size(600, 400);
        Icon = SystemIcons.Shield;

        // === Top Panel ===
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10) };

        _statusLabel = new Label
        {
            Text = "Status: Stopped",
            Location = new Point(10, 15),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold)
        };

        _enabledCheck = new CheckBox
        {
            Text = "Enabled",
            Location = new Point(200, 13),
            AutoSize = true
        };
        _enabledCheck.CheckedChanged += (s, e) => _onToggle(_enabledCheck.Checked);

        var intervalLabel = new Label { Text = "Interval (ms):", Location = new Point(300, 15), AutoSize = true };
        var intervalBox = new NumericUpDown
        {
            Location = new Point(390, 12),
            Width = 80,
            Minimum = 100,
            Maximum = 60000,
            Value = _config.GetConfig().Settings.PollingIntervalMs
        };
        intervalBox.ValueChanged += (s, e) =>
        {
            var cfg = _config.GetConfig();
            cfg.Settings.PollingIntervalMs = (int)intervalBox.Value;
            _config.SaveConfig(cfg);
        };

        var exitBtn = new Button
        {
            Text = "Exit",
            Location = new Point(700, 10),
            Width = 60,
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        exitBtn.Click += (s, e) => Application.Exit();

        topPanel.Controls.AddRange([_statusLabel, _enabledCheck, intervalLabel, intervalBox, exitBtn]);

        // === Split Container ===
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 200
        };

        // === Rules Panel (Top) ===
        var rulesLabel = new Label { Text = "Rules:", Dock = DockStyle.Top, Height = 20, Padding = new Padding(5) };
        
        _rulesList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            CheckBoxes = true
        };
        _rulesList.Columns.Add("Name", 150);
        _rulesList.Columns.Add("Target", 200);
        _rulesList.Columns.Add("Action", 100);
        _rulesList.Columns.Add("Safety", 150);
        _rulesList.ItemChecked += OnRuleChecked;

        var rulesToolbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 35 };
        var addBtn = new Button { Text = "Add Rule", Width = 80 };
        var editBtn = new Button { Text = "Edit", Width = 60 };
        var deleteBtn = new Button { Text = "Delete", Width = 60 };
        var pickBtn = new Button { Text = "Pick from Screen...", Width = 120 };

        addBtn.Click += (s, e) => EditRule(null);
        editBtn.Click += (s, e) => { if (_rulesList.SelectedItems.Count > 0) EditRule(_rulesList.SelectedItems[0].Tag as Models.Rule); };
        deleteBtn.Click += (s, e) => DeleteSelectedRule();
        pickBtn.Click += (s, e) => ShowScreenPicker();

        rulesToolbar.Controls.AddRange([addBtn, editBtn, deleteBtn, pickBtn]);

        split.Panel1.Controls.Add(_rulesList);
        split.Panel1.Controls.Add(rulesToolbar);
        split.Panel1.Controls.Add(rulesLabel);

        // === Log Panel (Bottom) ===
        var logLabel = new Label { Text = "Log:", Dock = DockStyle.Top, Height = 20, Padding = new Padding(5) };
        
        _logList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 8.5f),
            HorizontalScrollbar = true
        };

        var logToolbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 35 };
        var clearBtn = new Button { Text = "Clear", Width = 60 };
        var openLogBtn = new Button { Text = "Open Log File", Width = 100 };
        
        clearBtn.Click += (s, e) => _logList.Items.Clear();
        openLogBtn.Click += (s, e) => Process.Start(new ProcessStartInfo(_log.GetLogPath()) { UseShellExecute = true });

        logToolbar.Controls.AddRange([clearBtn, openLogBtn]);

        split.Panel2.Controls.Add(_logList);
        split.Panel2.Controls.Add(logToolbar);
        split.Panel2.Controls.Add(logLabel);

        Controls.Add(split);
        Controls.Add(topPanel);

        // Timer for status updates
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (s, e) => UpdateStatus();
        _timer.Start();
    }

    private void LoadRules()
    {
        _rulesList.Items.Clear();
        foreach (var rule in _config.GetConfig().Rules)
        {
            var item = new ListViewItem(rule.Name) { Tag = rule, Checked = rule.Enabled };
            item.SubItems.Add($"{rule.Target.ElementType}: {string.Join(", ", rule.Target.TextPatterns)}");
            item.SubItems.Add(rule.Action.Type);
            item.SubItems.Add(rule.Safety.ConfirmBeforeAction ? "Confirm" : rule.Safety.CooldownMs + "ms");
            _rulesList.Items.Add(item);
        }
    }

    private void OnRuleChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (e.Item.Tag is Models.Rule rule)
        {
            rule.Enabled = e.Item.Checked;
            _config.SaveConfig(_config.GetConfig());
        }
    }

    private void EditRule(Models.Rule? rule)
    {
        using var form = new RuleEditorForm(rule);
        if (form.ShowDialog() == DialogResult.OK && form.Rule != null)
        {
            var cfg = _config.GetConfig();
            if (rule == null)
                cfg.Rules.Add(form.Rule);
            else
            {
                var idx = cfg.Rules.FindIndex(r => r.Id == rule.Id);
                if (idx >= 0) cfg.Rules[idx] = form.Rule;
            }
            _config.SaveConfig(cfg);
            LoadRules();
        }
    }

    private void DeleteSelectedRule()
    {
        if (_rulesList.SelectedItems.Count == 0) return;
        if (_rulesList.SelectedItems[0].Tag is not Models.Rule rule) return;
        
        if (MessageBox.Show($"Delete rule '{rule.Name}'?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            var cfg = _config.GetConfig();
            cfg.Rules.RemoveAll(r => r.Id == rule.Id);
            _config.SaveConfig(cfg);
            LoadRules();
        }
    }

    private void ShowScreenPicker()
    {
        MessageBox.Show(
            "Screen Picker:\n\n" +
            "1. Press OK to start picking\n" +
            "2. Move mouse over target button/element\n" +
            "3. Press Enter to capture\n" +
            "4. A new rule will be created",
            "Pick from Screen",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        // TODO: Implement visual screen picker overlay
        // For now, show instructions
    }

    private void OnLog(LogEntry entry)
    {
        if (entry.Category == "Action") _clickCount++;
        
        if (InvokeRequired)
            BeginInvoke(() => AddLog(entry));
        else
            AddLog(entry);
    }

    private void AddLog(LogEntry entry)
    {
        var icon = entry.Level switch
        {
            LogLevel.Debug => "🔍",
            LogLevel.Info => "ℹ️",
            LogLevel.Warning => "⚠️",
            LogLevel.Error => "❌",
            _ => "•"
        };
        _logList.Items.Add($"[{entry.Time:HH:mm:ss}] {icon} {entry.Category}: {entry.Message}");
        _logList.TopIndex = _logList.Items.Count - 1;
        while (_logList.Items.Count > 500) _logList.Items.RemoveAt(0);
    }

    private void UpdateStatus()
    {
        _statusLabel.Text = $"Status: {_engine.Status} | Actions: {_clickCount}";
        _statusLabel.ForeColor = _engine.IsEnabled ? Color.Green : Color.Gray;
    }

    public void SetEnabled(bool enabled) => _enabledCheck.Checked = enabled;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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
            _timer?.Dispose();
            _log.OnLog -= OnLog;
        }
        base.Dispose(disposing);
    }
}

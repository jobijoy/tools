using System.Drawing;
using VsCodeAllowClicker.App.Models;

namespace VsCodeAllowClicker.App.UI;

/// <summary>
/// Dialog for creating/editing automation rules.
/// </summary>
public sealed class RuleEditorForm : Form
{
    public Rule? Rule { get; private set; }

    private TextBox _nameBox = null!;
    private TextBox _processBox = null!;
    private TextBox _windowTitleBox = null!;
    private ComboBox _elementTypeBox = null!;
    private TextBox _patternsBox = null!;
    private TextBox _excludeBox = null!;
    private ComboBox _actionTypeBox = null!;
    private TextBox _keysBox = null!;
    private NumericUpDown _cooldownBox = null!;
    private CheckBox _confirmCheck = null!;
    private TextBox _alertPatternsBox = null!;

    public RuleEditorForm(Rule? existing = null)
    {
        Rule = existing;
        InitUI();
        if (existing != null) LoadRule(existing);
    }

    private void InitUI()
    {
        Text = Rule == null ? "New Rule" : "Edit Rule";
        Size = new Size(500, 550);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var y = 15;
        const int labelX = 15, inputX = 140, inputWidth = 320;

        // Name
        AddLabel("Name:", labelX, y);
        _nameBox = AddTextBox(inputX, y, inputWidth);
        y += 35;

        // Target Section
        AddLabel("── Target ──", labelX, y, true);
        y += 25;

        AddLabel("Process Names:", labelX, y);
        _processBox = AddTextBox(inputX, y, inputWidth);
        _processBox.PlaceholderText = "Code, Code - Insiders";
        y += 35;

        AddLabel("Window Title:", labelX, y);
        _windowTitleBox = AddTextBox(inputX, y, inputWidth);
        _windowTitleBox.PlaceholderText = "Contains... (optional)";
        y += 35;

        AddLabel("Element Type:", labelX, y);
        _elementTypeBox = new ComboBox { Location = new Point(inputX, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _elementTypeBox.Items.AddRange(["Button", "ListItem", "Text", "Link", "Any"]);
        _elementTypeBox.SelectedIndex = 0;
        Controls.Add(_elementTypeBox);
        y += 35;

        AddLabel("Match Patterns:", labelX, y);
        _patternsBox = AddTextBox(inputX, y, inputWidth);
        _patternsBox.PlaceholderText = "Allow, Continue, OK (comma-separated)";
        y += 35;

        AddLabel("Exclude:", labelX, y);
        _excludeBox = AddTextBox(inputX, y, inputWidth);
        _excludeBox.PlaceholderText = "Continue Chat in (optional)";
        y += 35;

        // Action Section
        AddLabel("── Action ──", labelX, y, true);
        y += 25;

        AddLabel("Action Type:", labelX, y);
        _actionTypeBox = new ComboBox { Location = new Point(inputX, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _actionTypeBox.Items.AddRange(["Click", "SendKeys", "Alert", "ReadAndAlert"]);
        _actionTypeBox.SelectedIndex = 0;
        _actionTypeBox.SelectedIndexChanged += (s, e) => _keysBox.Enabled = _actionTypeBox.Text == "SendKeys";
        Controls.Add(_actionTypeBox);
        y += 35;

        AddLabel("Keys (SendKeys):", labelX, y);
        _keysBox = AddTextBox(inputX, y, inputWidth);
        _keysBox.PlaceholderText = "Tab, Enter (comma-separated)";
        _keysBox.Enabled = false;
        y += 35;

        // Safety Section
        AddLabel("── Safety ──", labelX, y, true);
        y += 25;

        AddLabel("Cooldown (ms):", labelX, y);
        _cooldownBox = new NumericUpDown { Location = new Point(inputX, y), Width = 100, Minimum = 0, Maximum = 60000, Value = 1500 };
        Controls.Add(_cooldownBox);
        y += 35;

        _confirmCheck = new CheckBox { Text = "Confirm before action", Location = new Point(inputX, y), AutoSize = true };
        Controls.Add(_confirmCheck);
        y += 30;

        AddLabel("Alert if contains:", labelX, y);
        _alertPatternsBox = AddTextBox(inputX, y, inputWidth);
        _alertPatternsBox.PlaceholderText = "error, warning (optional)";
        y += 45;

        // Buttons
        var saveBtn = new Button { Text = "Save", Location = new Point(280, y), Width = 80, DialogResult = DialogResult.OK };
        var cancelBtn = new Button { Text = "Cancel", Location = new Point(370, y), Width = 80, DialogResult = DialogResult.Cancel };
        
        saveBtn.Click += (s, e) => SaveRule();
        
        Controls.AddRange([saveBtn, cancelBtn]);
        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }

    private Label AddLabel(string text, int x, int y, bool bold = false)
    {
        var label = new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true };
        if (bold) label.Font = new Font(label.Font, FontStyle.Bold);
        Controls.Add(label);
        return label;
    }

    private TextBox AddTextBox(int x, int y, int width)
    {
        var box = new TextBox { Location = new Point(x, y), Width = width };
        Controls.Add(box);
        return box;
    }

    private void LoadRule(Rule r)
    {
        _nameBox.Text = r.Name;
        _processBox.Text = string.Join(", ", r.Target.ProcessNames);
        _windowTitleBox.Text = r.Target.WindowTitleContains ?? "";
        _elementTypeBox.Text = r.Target.ElementType;
        _patternsBox.Text = string.Join(", ", r.Target.TextPatterns);
        _excludeBox.Text = string.Join(", ", r.Target.ExcludePatterns);
        _actionTypeBox.Text = r.Action.Type;
        _keysBox.Text = string.Join(", ", r.Action.Keys ?? []);
        _cooldownBox.Value = r.Safety.CooldownMs;
        _confirmCheck.Checked = r.Safety.ConfirmBeforeAction;
        _alertPatternsBox.Text = string.Join(", ", r.Safety.AlertIfContains ?? []);
    }

    private void SaveRule()
    {
        Rule = new Rule
        {
            Id = Rule?.Id ?? Guid.NewGuid().ToString("N")[..8],
            Name = _nameBox.Text.Trim(),
            Enabled = Rule?.Enabled ?? true,
            Target = new TargetMatch
            {
                ProcessNames = _processBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                WindowTitleContains = string.IsNullOrWhiteSpace(_windowTitleBox.Text) ? null : _windowTitleBox.Text.Trim(),
                ElementType = _elementTypeBox.Text,
                TextPatterns = _patternsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                ExcludePatterns = _excludeBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            },
            Action = new RuleAction
            {
                Type = _actionTypeBox.Text,
                Keys = _keysBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            },
            Safety = new SafetySettings
            {
                CooldownMs = (int)_cooldownBox.Value,
                ConfirmBeforeAction = _confirmCheck.Checked,
                AlertIfContains = _alertPatternsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            }
        };

        if (string.IsNullOrWhiteSpace(Rule.Name))
        {
            MessageBox.Show("Please enter a rule name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        }
    }
}

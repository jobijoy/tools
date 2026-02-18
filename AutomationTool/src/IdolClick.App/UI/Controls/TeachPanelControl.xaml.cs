using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using IdolClick.Models;
using IdolClick.Services;

namespace IdolClick.UI.Controls;

public partial class TeachPanelControl : UserControl
{
    private readonly List<TeachStepData> _teachSteps = [];
    private bool _isRecordingTeach;

    public TeachPanelControl()
    {
        InitializeComponent();
    }

    // ── Public API ────────────────────────────────────────────────────

    public void UpdateMicButtonVisibility()
    {
        var voiceOk = App.Voice?.IsConfigured == true;
        TeachMicButton.Visibility = voiceOk ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Types ─────────────────────────────────────────────────────────

    private class TeachStepData
    {
        public int Order { get; set; }
        public StepAction Action { get; set; } = StepAction.Click;
        public Dictionary<string, string> Slots { get; set; } = [];
        public Border? Card { get; set; }
        public string? ValidationError { get; set; }
        public string? ValidationWarning { get; set; }
    }

    private record SentenceTemplate(string Verb, List<SlotDef> Slots);
    private record SlotDef(string Name, string Placeholder, bool Required);

    private static readonly Dictionary<StepAction, SentenceTemplate> SentenceTemplates = new()
    {
        [StepAction.Launch] = new("Open", [new("app", "Application path or name", true)]),
        [StepAction.Navigate] = new("Go to", [new("url", "URL to navigate to", true), new("browser", "Browser (chrome, edge)", false)]),
        [StepAction.Click] = new("Click", [new("element", "UI element selector", true), new("window", "Window name", false)]),
        [StepAction.Type] = new("Type", [new("text", "Text to type", true), new("element", "Target field", false), new("window", "Window name", false)]),
        [StepAction.SendKeys] = new("Press", [new("keys", "Key combination (e.g. Ctrl+S)", true), new("window", "Window name", false)]),
        [StepAction.Wait] = new("Wait for", [new("duration", "Duration in ms", true)]),
        [StepAction.AssertExists] = new("Verify", [new("element", "Element to check", true), new("window", "Window name", false)]),
        [StepAction.AssertNotExists] = new("Verify gone", [new("element", "Element that should not exist", true), new("window", "Window name", false)]),
        [StepAction.AssertText] = new("Check text", [new("element", "Element to read", true), new("expected", "Expected text", true)]),
        [StepAction.AssertWindow] = new("Confirm window", [new("window", "Window title", true)]),
        [StepAction.Screenshot] = new("Take screenshot of", [new("window", "Window name", false)]),
        [StepAction.Scroll] = new("Scroll", [new("direction", "up or down", true), new("element", "Scrollable element", false)]),
        [StepAction.FocusWindow] = new("Switch to", [new("window", "Window name", true)]),
    };

    // ── NL Parsing ────────────────────────────────────────────────────

    private async void TeachParse_Click(object sender, RoutedEventArgs e)
    {
        var nlText = TeachNLInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(nlText)) return;

        TeachParseBtn.IsEnabled = false;
        TeachParseBtn.Content = "⏳ Parsing...";

        try
        {
            var intents = await Task.Run(() => App.IntentSplitter.SplitMultiple(nlText));
            
            _teachSteps.Clear();
            TeachStepsPanel.Children.Clear();
            TeachEmptyState.Visibility = Visibility.Collapsed;

            int order = 1;
            foreach (var intent in intents)
            {
                var stepData = new TeachStepData
                {
                    Order = order++,
                    Action = MapIntentToAction(intent.TemplateId),
                    Slots = new Dictionary<string, string>(intent.Slots ?? [])
                };
                _teachSteps.Add(stepData);
                AddTeachStepCard(stepData);
            }

            if (_teachSteps.Count == 0)
            {
                var step = new TeachStepData { Order = 1, Action = StepAction.Click };
                _teachSteps.Add(step);
                AddTeachStepCard(step);
            }

            ValidateTeachFlow();
        }
        catch (Exception ex)
        {
            App.Log.Error("Teach", $"Parse failed: {ex.Message}");
        }
        finally
        {
            TeachParseBtn.Content = "⚡ Parse";
            TeachParseBtn.IsEnabled = true;
        }
    }

    private static StepAction MapIntentToAction(string? templateId)
    {
        return templateId?.ToLowerInvariant() switch
        {
            "browser-navigate" or "browser-search" => StepAction.Navigate,
            "launch-app" => StepAction.Launch,
            "type-in-field" => StepAction.Type,
            "click-element" => StepAction.Click,
            "login-form" => StepAction.Navigate,
            "search-in-app" => StepAction.Type,
            "close-window" => StepAction.SendKeys,
            "save-file" => StepAction.SendKeys,
            "copy-paste" => StepAction.SendKeys,
            "switch-window" => StepAction.FocusWindow,
            "scroll-to-element" => StepAction.Scroll,
            "take-screenshot" => StepAction.Screenshot,
            "open-file-dialog" => StepAction.SendKeys,
            "run-command" => StepAction.Launch,
            _ => StepAction.Click
        };
    }

    private void TeachNLInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(TeachNLInput.Text))
        {
            TeachParse_Click(sender, e);
            e.Handled = true;
        }
    }

    // ── Step management ───────────────────────────────────────────────

    private void TeachAddStep_Click(object sender, RoutedEventArgs e)
    {
        TeachEmptyState.Visibility = Visibility.Collapsed;
        var step = new TeachStepData { Order = _teachSteps.Count + 1, Action = StepAction.Click };
        _teachSteps.Add(step);
        AddTeachStepCard(step);
        ValidateTeachFlow();
    }

    private void TeachClear_Click(object sender, RoutedEventArgs e)
    {
        _teachSteps.Clear();
        TeachStepsPanel.Children.Clear();
        TeachStepsPanel.Children.Add(TeachEmptyState);
        TeachEmptyState.Visibility = Visibility.Visible;
        TeachTestBtn.IsEnabled = false;
        TeachSaveBtn.IsEnabled = false;
        TeachValidationStatus.Text = "";
    }

    private void AddTeachStepCard(TeachStepData step)
    {
        var card = new Border
        {
            Style = (Style)FindResource("SentenceStepCard"),
            Tag = step
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stepNum = new TextBlock
        {
            Text = $"{step.Order}.",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (SolidColorBrush)FindResource("PrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(stepNum, 0);
        grid.Children.Add(stepNum);

        var sentencePanel = BuildSentencePanel(step);
        Grid.SetColumn(sentencePanel, 1);
        grid.Children.Add(sentencePanel);

        var deleteBtn = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("SmallIconButton"),
            Width = 22, Height = 22,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Top,
            Tag = step,
            ToolTip = "Remove step"
        };
        deleteBtn.Click += (s, _) =>
        {
            var st = (TeachStepData)((Button)s!).Tag;
            _teachSteps.Remove(st);
            if (st.Card != null)
                TeachStepsPanel.Children.Remove(st.Card);
            RenumberTeachSteps();
            ValidateTeachFlow();
            if (_teachSteps.Count == 0)
            {
                TeachStepsPanel.Children.Add(TeachEmptyState);
                TeachEmptyState.Visibility = Visibility.Visible;
            }
        };
        Grid.SetColumn(deleteBtn, 2);
        grid.Children.Add(deleteBtn);

        card.Child = grid;
        step.Card = card;

        if (TeachEmptyState.Visibility == Visibility.Visible)
            TeachStepsPanel.Children.Insert(TeachStepsPanel.Children.IndexOf(TeachEmptyState), card);
        else
            TeachStepsPanel.Children.Add(card);
    }

    private WrapPanel BuildSentencePanel(TeachStepData step)
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        if (!SentenceTemplates.TryGetValue(step.Action, out var template))
        {
            panel.Children.Add(new TextBlock { Text = step.Action.ToString(), Foreground = (SolidColorBrush)FindResource("TextBrush") });
            return panel;
        }

        var actionCombo = new ComboBox
        {
            FontSize = 11,
            Width = 120,
            Margin = new Thickness(0, 2, 6, 2),
            Tag = step
        };
        foreach (var action in SentenceTemplates.Keys)
        {
            var tmpl = SentenceTemplates[action];
            actionCombo.Items.Add(new ComboBoxItem { Content = $"{tmpl.Verb} ({action})", Tag = action });
        }
        foreach (ComboBoxItem item in actionCombo.Items)
        {
            if ((StepAction)item.Tag == step.Action)
            {
                actionCombo.SelectedItem = item;
                break;
            }
        }
        actionCombo.SelectionChanged += (s, _) =>
        {
            if (actionCombo.SelectedItem is ComboBoxItem sel)
            {
                step.Action = (StepAction)sel.Tag;
                RebuildStepCard(step);
                ValidateTeachFlow();
            }
        };
        panel.Children.Add(actionCombo);

        foreach (var slot in template.Slots)
        {
            var slotValue = step.Slots.GetValueOrDefault(slot.Name, "");

            var label = new TextBlock
            {
                Text = slot.Required ? "" : $" {slot.Name}: ",
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            if (!slot.Required || !string.IsNullOrEmpty(label.Text.Trim()))
                panel.Children.Add(label);

            var slotBox = new TextBox
            {
                Text = slotValue,
                MinWidth = slot.Name == "url" || slot.Name == "text" ? 180 : 120,
                FontSize = 11,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(2),
                Tag = (step, slot.Name)
            };

            if (string.IsNullOrEmpty(slotValue))
            {
                slotBox.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                slotBox.Text = slot.Placeholder;
                slotBox.GotFocus += (s, _) =>
                {
                    var tb = (TextBox)s!;
                    if (tb.Text == slot.Placeholder)
                    {
                        tb.Text = "";
                        tb.Foreground = (SolidColorBrush)FindResource("TextBrush");
                    }
                };
                slotBox.LostFocus += (s, _) =>
                {
                    var tb = (TextBox)s!;
                    if (string.IsNullOrWhiteSpace(tb.Text))
                    {
                        tb.Text = slot.Placeholder;
                        tb.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                    }
                };
            }

            slotBox.TextChanged += (s, _) =>
            {
                var tb = (TextBox)s!;
                var (st, slotName) = ((TeachStepData, string))tb.Tag;
                var val = tb.Text;
                if (val == slot.Placeholder) val = "";
                st.Slots[slotName] = val;
                ValidateTeachFlow();
            };

            if (slot.Required && string.IsNullOrEmpty(slotValue))
            {
                slotBox.BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush");
                slotBox.BorderThickness = new Thickness(1.5);
            }

            panel.Children.Add(slotBox);
        }

        return panel;
    }

    private void RebuildStepCard(TeachStepData step)
    {
        if (step.Card == null) return;
        var idx = TeachStepsPanel.Children.IndexOf(step.Card);
        if (idx < 0) return;

        TeachStepsPanel.Children.RemoveAt(idx);
        step.Card = null;
        AddTeachStepCard(step);
        if (step.Card != null && TeachStepsPanel.Children.Contains(step.Card))
        {
            TeachStepsPanel.Children.Remove(step.Card);
            TeachStepsPanel.Children.Insert(idx, step.Card);
        }
    }

    private void RenumberTeachSteps()
    {
        for (int i = 0; i < _teachSteps.Count; i++)
            _teachSteps[i].Order = i + 1;
    }

    // ── Validation & Compilation ──────────────────────────────────────

    private void ValidateTeachFlow()
    {
        var flow = CompileTeachFlow();
        if (flow == null)
        {
            TeachValidationStatus.Text = "⚠ No steps";
            TeachTestBtn.IsEnabled = false;
            TeachSaveBtn.IsEnabled = false;
            return;
        }

        var validator = new FlowValidatorService(App.Log);
        var result = validator.Validate(flow);

        foreach (var step in _teachSteps)
        {
            if (step.Card == null) continue;
            var hasError = result.Errors.Any(err => err.Contains($"Step {step.Order}"));
            var hasWarning = result.Warnings.Any(w => w.Contains($"Step {step.Order}"));

            step.Card.BorderBrush = hasError
                ? (SolidColorBrush)FindResource("ErrorBrush")
                : hasWarning
                    ? (SolidColorBrush)FindResource("WarningBrush")
                    : (SolidColorBrush)FindResource("AccentBrush");
        }

        if (result.IsValid)
        {
            var warnCount = result.Warnings.Count;
            TeachValidationStatus.Text = warnCount > 0
                ? $"✓ Valid ({_teachSteps.Count} steps, {warnCount} warning{(warnCount > 1 ? "s" : "")})"
                : $"✓ Valid ({_teachSteps.Count} steps)";
            TeachValidationStatus.Foreground = (SolidColorBrush)FindResource("AccentBrush");
            TeachTestBtn.IsEnabled = true;
            TeachSaveBtn.IsEnabled = true;
        }
        else
        {
            TeachValidationStatus.Text = $"✗ {result.Errors.Count} error{(result.Errors.Count > 1 ? "s" : "")}";
            TeachValidationStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TeachTestBtn.IsEnabled = false;
            TeachSaveBtn.IsEnabled = false;
        }
    }

    private TestFlow? CompileTeachFlow()
    {
        if (_teachSteps.Count == 0) return null;

        return new TestFlow
        {
            SchemaVersion = 1,
            TestName = "Teach Flow",
            Description = TeachNLInput.Text?.Trim() ?? "Automation created in Teach mode",
            Backend = "desktop",
            Steps = _teachSteps.Select(CompileStep).ToList()
        };
    }

    private TestStep CompileStep(TeachStepData data)
    {
        var step = new TestStep
        {
            Order = data.Order,
            Action = data.Action,
            Description = $"{SentenceTemplates.GetValueOrDefault(data.Action)?.Verb ?? data.Action.ToString()} step"
        };

        var slots = data.Slots;
        switch (data.Action)
        {
            case StepAction.Launch:
                step.ProcessPath = slots.GetValueOrDefault("app", "");
                step.App = Path.GetFileNameWithoutExtension(step.ProcessPath ?? "");
                break;
            case StepAction.Navigate:
                step.Url = slots.GetValueOrDefault("url", "");
                step.App = slots.GetValueOrDefault("browser", "chrome");
                break;
            case StepAction.Click:
                step.Selector = slots.GetValueOrDefault("element", "");
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
            case StepAction.Type:
                step.Text = slots.GetValueOrDefault("text", "");
                step.Selector = slots.GetValueOrDefault("element", "");
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
            case StepAction.SendKeys:
                step.Keys = slots.GetValueOrDefault("keys", "");
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
            case StepAction.Wait:
                if (int.TryParse(slots.GetValueOrDefault("duration", "1000"), out var ms))
                    step.TimeoutMs = ms;
                break;
            case StepAction.AssertExists:
            case StepAction.AssertNotExists:
                step.Selector = slots.GetValueOrDefault("element", "");
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
            case StepAction.AssertText:
                step.Selector = slots.GetValueOrDefault("element", "");
                step.Contains = slots.GetValueOrDefault("expected", "");
                break;
            case StepAction.AssertWindow:
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
            case StepAction.Screenshot:
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
            case StepAction.Scroll:
                step.Direction = slots.GetValueOrDefault("direction", "down");
                step.Selector = slots.GetValueOrDefault("element", "");
                break;
            case StepAction.FocusWindow:
                step.WindowTitle = slots.GetValueOrDefault("window", "");
                break;
        }
        return step;
    }

    // ── Test & Save ───────────────────────────────────────────────────

    private async void TeachTest_Click(object sender, RoutedEventArgs e)
    {
        var flow = CompileTeachFlow();
        if (flow == null) return;

        TeachTestBtn.IsEnabled = false;
        TeachTestBtn.Content = "⏳ Running...";

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(flow.TimeoutSeconds));
            App.ActiveFlowCts = cts;

            var report = await App.FlowExecutor.ExecuteFlowAsync(flow, cancellationToken: cts.Token);

            for (int i = 0; i < _teachSteps.Count && i < report.Steps.Count; i++)
            {
                var stepResult = report.Steps[i];
                var teachStep = _teachSteps[i];
                if (teachStep.Card == null) continue;

                teachStep.Card.BorderBrush = stepResult.Status switch
                {
                    StepStatus.Passed => (SolidColorBrush)FindResource("AccentBrush"),
                    StepStatus.Failed or StepStatus.Error => (SolidColorBrush)FindResource("ErrorBrush"),
                    StepStatus.Warning => (SolidColorBrush)FindResource("WarningBrush"),
                    _ => (SolidColorBrush)FindResource("BorderBrush")
                };
            }

            var passed = report.Steps.Count(s => s.Status == StepStatus.Passed);
            TeachValidationStatus.Text = $"✓ Executed: {passed}/{report.Steps.Count} passed ({report.TotalTimeMs}ms)";
            TeachValidationStatus.Foreground = report.Result == "passed"
                ? (SolidColorBrush)FindResource("AccentBrush")
                : (SolidColorBrush)FindResource("ErrorBrush");

            App.Log.Info("Teach", $"Flow executed: {report.Result} — {passed}/{report.Steps.Count} steps passed");
        }
        catch (OperationCanceledException)
        {
            TeachValidationStatus.Text = "⚠ Execution cancelled";
            TeachValidationStatus.Foreground = (SolidColorBrush)FindResource("WarningBrush");
        }
        catch (Exception ex)
        {
            TeachValidationStatus.Text = $"✗ Error: {ex.Message}";
            TeachValidationStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
        }
        finally
        {
            App.ActiveFlowCts = null;
            TeachTestBtn.Content = "▶ Test";
            TeachTestBtn.IsEnabled = true;
        }
    }

    private void TeachViewJson_Click(object sender, RoutedEventArgs e)
    {
        var flow = CompileTeachFlow();
        if (flow == null) return;

        var json = JsonSerializer.Serialize(flow, new JsonSerializerOptions { WriteIndented = true });
        Clipboard.SetText(json);
        TeachValidationStatus.Text = "📋 JSON copied to clipboard";
        TeachValidationStatus.Foreground = (SolidColorBrush)FindResource("PrimaryBrush");
    }

    private void TeachSave_Click(object sender, RoutedEventArgs e)
    {
        var flow = CompileTeachFlow();
        if (flow == null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "JSON flow|*.json",
            FileName = $"{flow.TestName?.Replace(' ', '-') ?? "teach-flow"}.json",
            Title = "Save Automation Flow"
        };
        if (dlg.ShowDialog() == true)
        {
            var json = JsonSerializer.Serialize(flow, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            TeachValidationStatus.Text = $"💾 Saved to {Path.GetFileName(dlg.FileName)}";
            TeachValidationStatus.Foreground = (SolidColorBrush)FindResource("AccentBrush");
            App.Log.Info("Teach", $"Flow saved: {dlg.FileName}");
        }
    }

    // ── Voice ─────────────────────────────────────────────────────────

    private async void TeachMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Voice == null || !App.Voice.IsConfigured) return;

        if (!_isRecordingTeach)
        {
            _isRecordingTeach = true;
            TeachMicButton.Content = "🔴";
            TeachMicButton.Style = (Style)FindResource("MicButtonRecording");

            App.Voice.OnTranscriptionReady -= OnTeachTranscription;
            App.Voice.OnTranscriptionReady += OnTeachTranscription;

            App.Voice.StartRecording();
        }
        else
        {
            _isRecordingTeach = false;
            TeachMicButton.Content = "⏳";
            TeachMicButton.Style = (Style)FindResource("MicButtonTranscribing");
            TeachMicButton.IsEnabled = false;

            await App.Voice.StopRecordingAndTranscribeAsync();

            TeachMicButton.Content = "🎤";
            TeachMicButton.Style = (Style)FindResource("MicButton");
            TeachMicButton.IsEnabled = true;
        }
    }

    private void OnTeachTranscription(string text)
    {
        Dispatcher.Invoke(() =>
        {
            TeachNLInput.Text = text;
            TeachNLInput.Focus();
        });
    }
}

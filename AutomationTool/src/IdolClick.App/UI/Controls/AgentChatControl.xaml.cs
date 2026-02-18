using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using IdolClick.Models;
using IdolClick.Services;

namespace IdolClick.UI.Controls;

public partial class AgentChatControl : UserControl
{
    private CancellationTokenSource? _chatCts;
    private bool _isRecordingChat;
    private LogLevel _agentLogLevel = LogLevel.Info;

    public AgentChatControl()
    {
        InitializeComponent();
    }

    // ── Public API ────────────────────────────────────────────────────

    public void UpdateAgentStatus()
    {
        var agent = App.Agent;
        if (agent == null) return;
        AgentStatusText.Text = $"  \u2022  {agent.StatusText}";
    }

    public void UpdateMicButtonVisibility()
    {
        var voiceOk = App.Voice?.IsConfigured == true;
        MicButton.Visibility = voiceOk ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AddLogEntry(string text, LogLevel level)
    {
        if (level < _agentLogLevel) return;

        var color = level switch
        {
            LogLevel.Error => Color.FromRgb(255, 100, 100),
            LogLevel.Warning => Color.FromRgb(255, 200, 50),
            LogLevel.Debug => Color.FromRgb(130, 130, 130),
            _ => Color.FromRgb(200, 200, 200)
        };

        var item = new ListBoxItem
        {
            Content = text,
            Foreground = new SolidColorBrush(color),
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 9
        };
        AgentLogListBox.Items.Add(item);

        if (AgentLogListBox.Items.Count > 1000)
            AgentLogListBox.Items.RemoveAt(0);

        AgentLogListBox.ScrollIntoView(item);
    }

    public void CollapseLogPanel()
    {
        AgentLogPanelCol.Width = new GridLength(0);
        AgentLogPanelCol.MinWidth = 0;
        AgentLogSplitter.Visibility = Visibility.Collapsed;
        AgentLogPanel.Visibility = Visibility.Collapsed;
    }

    public void SetupTimeline(System.Collections.IEnumerable events)
    {
        AgentTimelineListView.ItemsSource = events;
    }

    // ── Chat ──────────────────────────────────────────────────────────

    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendChatMessage();

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var caretIndex = ChatInputBox.CaretIndex;
                ChatInputBox.Text = ChatInputBox.Text.Insert(caretIndex, Environment.NewLine);
                ChatInputBox.CaretIndex = caretIndex + Environment.NewLine.Length;
                e.Handled = true;
            }
            else if (!string.IsNullOrWhiteSpace(ChatInputBox.Text))
            {
                SendChatMessage();
                e.Handled = true;
            }
        }
    }

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ChatSendBtn.IsEnabled = !string.IsNullOrWhiteSpace(ChatInputBox.Text);
    }

    private async void SendChatMessage()
    {
        var text = ChatInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        AddChatMessage(text, isUser: true);
        ChatInputBox.Text = "";
        ChatSendBtn.IsEnabled = false;
        ChatInputBox.IsEnabled = false;

        AgentWelcomePanel.Visibility = Visibility.Collapsed;

        var typingBubble = AddChatMessage("\u2219\u2219\u2219 thinking", isUser: false);
        var typingTextBlock = typingBubble.Child as TextBlock;

        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();

        var intermediateMessages = new List<Border>();

        void OnProgress(AgentProgress progress)
        {
            Dispatcher.BeginInvoke(() =>
            {
                switch (progress.Kind)
                {
                    case AgentProgressKind.IntermediateText:
                        if (!string.IsNullOrWhiteSpace(progress.IntermediateText))
                        {
                            var msg = AddChatMessage(progress.IntermediateText, isUser: false);
                            intermediateMessages.Add(msg);
                        }
                        break;
                    case AgentProgressKind.ToolCallStarting:
                        if (typingTextBlock != null)
                            typingTextBlock.Text = progress.Message;
                        break;
                    case AgentProgressKind.ToolCallCompleted:
                        if (typingTextBlock != null)
                            typingTextBlock.Text = progress.Message;
                        break;
                    case AgentProgressKind.NewIteration:
                        if (typingTextBlock != null)
                            typingTextBlock.Text = progress.Message;
                        break;
                }
                ChatScrollViewer.ScrollToEnd();
            });
        }

        App.Agent.OnProgress += OnProgress;

        try
        {
            var response = await App.Agent.SendMessageAsync(text, _chatCts.Token);
            ChatMessagesPanel.Children.Remove(typingBubble);
            AddChatMessage(response.Text, isUser: false, isError: response.IsError);
            if (response.HasFlow)
                AddFlowActionBar(response.Flow!);
            AddQuickReplyButtons(response.Text);
            UpdateAgentStatus();
        }
        catch (OperationCanceledException)
        {
            ChatMessagesPanel.Children.Remove(typingBubble);
        }
        catch (Exception ex)
        {
            ChatMessagesPanel.Children.Remove(typingBubble);
            AddChatMessage($"Unexpected error: {ex.Message}", isUser: false, isError: true);
        }
        finally
        {
            App.Agent.OnProgress -= OnProgress;
            ChatInputBox.IsEnabled = true;
            ChatInputBox.Focus();
        }
    }

    private Border AddChatMessage(string text, bool isUser, bool isError = false)
    {
        var bgColor = isUser
            ? Color.FromRgb(0, 120, 212)
            : isError
                ? Color.FromRgb(80, 30, 30)
                : Color.FromRgb(54, 54, 54);

        var bubble = new Border
        {
            Background = new SolidColorBrush(bgColor),
            CornerRadius = new CornerRadius(12, 12, isUser ? 2 : 12, isUser ? 12 : 2),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(isUser ? 60 : 0, 2, isUser ? 0 : 60, 2),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 500,
            Tag = text
        };

        var copyMsg = new MenuItem { Header = "📋 Copy Message", InputGestureText = "Ctrl+C" };
        copyMsg.Click += (s, e) =>
        {
            if (bubble.Tag is string raw && !string.IsNullOrEmpty(raw))
                Clipboard.SetText(raw);
        };
        var copyAll = new MenuItem { Header = "📋 Copy All Chat" };
        copyAll.Click += CopyAllChat_Click;
        bubble.ContextMenu = new ContextMenu { Items = { copyMsg, new Separator(), copyAll } };

        if (isUser)
        {
            bubble.Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
        }
        else
        {
            bubble.Child = RenderMarkdownContent(text, isError);
        }

        ChatMessagesPanel.Children.Add(bubble);
        ChatScrollViewer.ScrollToEnd();
        return bubble;
    }

    private UIElement RenderMarkdownContent(string text, bool isError)
    {
        var panel = new StackPanel();
        var lines = text.Split('\n');
        var defaultFg = isError ? Brushes.Salmon : Brushes.White;
        var mutedFg = new SolidColorBrush(Color.FromRgb(170, 170, 170));
        var accentFg = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        var headingFg = new SolidColorBrush(Color.FromRgb(100, 200, 255));

        string? lastDirPath = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (line.TrimStart().StartsWith("```")) continue;

            var dirMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"([A-Za-z]:\\[^'\""*<>|]+\\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dirMatch.Success)
            {
                var candidate = dirMatch.Value.TrimEnd('\'', '`', ' ');
                if (System.IO.Directory.Exists(candidate))
                    lastDirPath = candidate;
            }

            if (line.TrimStart().StartsWith("##"))
            {
                var headingText = line.TrimStart('#', ' ');
                panel.Children.Add(new TextBlock
                {
                    Text = headingText,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = headingFg,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 2)
                });
                continue;
            }

            var pathMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"([A-Za-z]:\\[^'\""\s*<>|]+\.(png|jpg|jpeg|bmp|json|txt|log))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            string? filePath = null;
            if (pathMatch.Success)
            {
                filePath = pathMatch.Value.TrimEnd('\'', '`', '*');
            }
            else
            {
                var bareMatch = System.Text.RegularExpressions.Regex.Match(line,
                    @"['\x22`]?([\w\-]+\.(png|jpg|jpeg|bmp|json|txt|log))['\x22`]?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (bareMatch.Success)
                {
                    var bareName = bareMatch.Groups[1].Value;
                    if (lastDirPath != null)
                    {
                        var fullPath = System.IO.Path.Combine(lastDirPath, bareName);
                        if (System.IO.File.Exists(fullPath))
                            filePath = fullPath;
                    }
                    if (filePath == null)
                    {
                        var screenshotsDir = System.IO.Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory, "reports", "_screenshots");
                        if (System.IO.Directory.Exists(screenshotsDir))
                        {
                            var found = System.IO.Path.Combine(screenshotsDir, bareName);
                            if (System.IO.File.Exists(found))
                                filePath = found;
                        }
                    }
                }
            }

            if (filePath != null)
            {
                var isImage = filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                              filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                              filePath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);

                var inlineTb = BuildInlineTextBlock(line, filePath, defaultFg, accentFg);
                panel.Children.Add(inlineTb);

                if (isImage && System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(filePath);
                        bitmap.DecodePixelWidth = 400;
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        var img = new Image
                        {
                            Source = bitmap,
                            MaxWidth = 400,
                            MaxHeight = 250,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 4, 0, 4),
                            Cursor = Cursors.Hand,
                            ToolTip = "Click to open full image"
                        };
                        var capturedPath = filePath;
                        img.MouseLeftButtonUp += (s, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo(capturedPath) { UseShellExecute = true }); }
                            catch { }
                        };

                        var imgBorder = new Border
                        {
                            CornerRadius = new CornerRadius(6),
                            ClipToBounds = true,
                            Child = img,
                            Margin = new Thickness(0, 2, 0, 2)
                        };
                        panel.Children.Add(imgBorder);
                    }
                    catch { }
                }
                continue;
            }

            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1)
            };

            bool isBullet = line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("• ");
            if (isBullet)
                line = "  \u2022 " + line.TrimStart('-', '•', ' ');

            var parts = System.Text.RegularExpressions.Regex.Split(line, @"(\*\*.*?\*\*)");
            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                {
                    tb.Inlines.Add(new System.Windows.Documents.Run(part[2..^2])
                    {
                        FontWeight = FontWeights.Bold,
                        Foreground = defaultFg
                    });
                }
                else
                {
                    tb.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = defaultFg });
                }
            }
            panel.Children.Add(tb);
        }

        return panel;
    }

    private static TextBlock BuildInlineTextBlock(string line, string filePath, Brush defaultFg, Brush linkFg)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 1)
        };

        var fileName = System.IO.Path.GetFileName(filePath);
        var idx = line.IndexOf(filePath, StringComparison.OrdinalIgnoreCase);
        var matchedText = filePath;
        if (idx < 0)
        {
            idx = line.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
            matchedText = fileName;
        }

        if (idx >= 0)
        {
            if (idx > 0)
            {
                var before = line[..idx].TrimEnd('\'', '`', ' ');
                if (!string.IsNullOrWhiteSpace(before))
                    tb.Inlines.Add(new System.Windows.Documents.Run(before + " ") { Foreground = defaultFg });
            }

            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(fileName))
            {
                Foreground = linkFg,
                TextDecorations = null,
                Cursor = Cursors.Hand
            };
            link.ToolTip = filePath;
            var captured = filePath;
            link.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(captured) { UseShellExecute = true }); }
                catch { }
            };
            tb.Inlines.Add(link);

            var afterIdx = idx + matchedText.Length;
            if (afterIdx < line.Length)
            {
                var after = line[afterIdx..].TrimStart('\'', '`');
                if (!string.IsNullOrWhiteSpace(after))
                    tb.Inlines.Add(new System.Windows.Documents.Run(after) { Foreground = defaultFg });
            }
        }
        else
        {
            tb.Inlines.Add(new System.Windows.Documents.Run(line) { Foreground = defaultFg });
        }
        return tb;
    }

    private void AddFlowActionBar(TestFlow flow)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 60, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var runBtn = new Button
        {
            Content = $"\u25B6 Run '{flow.TestName}' ({flow.Steps.Count} steps)",
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        runBtn.Click += async (s, e) =>
        {
            runBtn.IsEnabled = false;
            runBtn.Content = "\u23f3 Running...";
            App.Log.Info("Agent", $"Executing flow '{flow.TestName}' ({flow.Steps.Count} steps)");

            try
            {
                var report = await Task.Run(() => App.FlowExecutor.ExecuteFlowAsync(flow,
                    onStepComplete: (step, total, result) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            var icon = result.Status == StepStatus.Passed ? "\u2705" :
                                       result.Status == StepStatus.Failed ? "\u274c" :
                                       result.Status == StepStatus.Skipped ? "\u23ed" : "\u26a0";
                            runBtn.Content = $"{icon} Step {step}/{total}: {result.Action} ({result.TimeMs}ms)";
                        });
                    }));

                string? savedPath = null;
                try { savedPath = App.Reports?.SaveReport(report); } catch { }

                var icon2 = report.Result == "passed" ? "\u2705" : "\u274c";
                var reportJson = System.Text.Json.JsonSerializer.Serialize(report, FlowJson.Options);
                var savedMsg = savedPath != null ? $"\n📁 Report saved: {savedPath}" : "";
                AddChatMessage(
                    $"{icon2} **{report.Result.ToUpperInvariant()}** — {report.PassedCount} passed, {report.FailedCount} failed, {report.SkippedCount} skipped ({report.TotalTimeMs}ms){savedMsg}\n\n" +
                    $"```json\n{reportJson}\n```",
                    isUser: false);
            }
            catch (Exception ex)
            {
                AddChatMessage($"\u274c Flow execution error: {ex.Message}", isUser: false);
                App.Log.Error("Agent", $"Flow execution failed: {ex.Message}");
            }
            finally
            {
                runBtn.Content = $"\u25B6 Run '{flow.TestName}' ({flow.Steps.Count} steps)";
                runBtn.IsEnabled = true;
            }
        };

        var copyBtn = new Button
        {
            Content = "\ud83d\udccb Copy JSON",
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(54, 54, 54)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        copyBtn.Click += (s, e) =>
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(flow, FlowJson.Options);
                Clipboard.SetText(json);
                App.Log.Info("Agent", $"Test flow '{flow.TestName}' copied to clipboard");
            }
            catch (Exception ex)
            {
                App.Log.Error("Agent", $"Failed to copy flow: {ex.Message}");
            }
        };

        bar.Children.Add(runBtn);
        bar.Children.Add(copyBtn);
        ChatMessagesPanel.Children.Add(bar);
        ChatScrollViewer.ScrollToEnd();
    }

    private void AddQuickReplyButtons(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return;

        var lower = responseText.ToLowerInvariant();
        var buttons = new List<(string label, string reply)>();

        var quotedPatterns = System.Text.RegularExpressions.Regex.Matches(
            responseText,
            @"(?:reply\s+with|respond\s+with|type|say)\s*[:\-]?\s*[""**`]*([^""*`\n]{3,80})[""**`]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in quotedPatterns)
        {
            var phrase = m.Groups[1].Value.Trim(' ', '"', '*', '`', '.');
            if (!string.IsNullOrWhiteSpace(phrase) && phrase.Length <= 80)
                buttons.Add(($"✅ {phrase}", phrase));
        }

        var bulletChoices = System.Text.RegularExpressions.Regex.Matches(
            responseText,
            @"^[\s\-•*]+(?:✅|❌|👉)?\s*[""**`]*([^""*`\n]{3,80})[""**`]*\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in bulletChoices)
        {
            var phrase = m.Groups[1].Value.Trim(' ', '"', '*', '`', '.');
            if (!string.IsNullOrWhiteSpace(phrase) && phrase.Length <= 80 &&
                !phrase.Contains("means", StringComparison.OrdinalIgnoreCase) &&
                !phrase.Contains("because", StringComparison.OrdinalIgnoreCase))
            {
                if (!buttons.Any(b => b.reply.Equals(phrase, StringComparison.OrdinalIgnoreCase)))
                    buttons.Add(($"👉 {phrase}", phrase));
            }
        }

        if (buttons.Count == 0 &&
            (lower.Contains("confirm") || lower.Contains("proceed") || lower.Contains("should i") ||
             lower.Contains("reply with") || lower.Contains("do you want")))
        {
            buttons.Add(("✅ Yes, proceed", "Yes, proceed"));
            buttons.Add(("❌ Stop", "Stop"));
        }

        if (buttons.Count == 0) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueButtons = new List<(string label, string reply)>();
        foreach (var b in buttons)
        {
            if (seen.Add(b.reply))
                uniqueButtons.Add(b);
            if (uniqueButtons.Count >= 5) break;
        }

        var bar = new WrapPanel
        {
            Margin = new Thickness(0, 4, 60, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        foreach (var (label, reply) in uniqueButtons)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 11,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            btn.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));

            var capturedReply = reply;
            btn.Click += (s, e) =>
            {
                foreach (UIElement child in bar.Children)
                {
                    if (child is Button b) b.IsEnabled = false;
                }
                ChatInputBox.Text = capturedReply;
                SendChatMessage();
            };
            bar.Children.Add(btn);
        }

        ChatMessagesPanel.Children.Add(bar);
        ChatScrollViewer.ScrollToEnd();
    }

    // ── Flow loading ──────────────────────────────────────────────────

    private void LoadFlow_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load Test Flow",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var flow = ReportService.LoadFlowFromFile(dlg.FileName);
            if (flow == null)
            {
                AddChatMessage("\u274c Could not parse flow from file. Ensure it's valid TestFlow JSON.", isUser: false);
                return;
            }
            ImportFlow(flow, $"\ud83d\udcc2 Loaded from: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            AddChatMessage($"\u274c Error loading flow: {ex.Message}", isUser: false);
            App.Log.Error("Agent", $"Load flow failed: {ex.Message}");
        }
    }

    private void PasteFlow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var clipText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                AddChatMessage("\u274c Clipboard is empty.", isUser: false);
                return;
            }
            var flow = ReportService.ParseFlowFromJson(clipText);
            if (flow == null)
            {
                AddChatMessage("\u274c Could not parse flow from clipboard. Ensure it's valid TestFlow JSON.", isUser: false);
                return;
            }
            ImportFlow(flow, "\ud83d\udccb Pasted from clipboard");
        }
        catch (Exception ex)
        {
            AddChatMessage($"\u274c Error parsing clipboard flow: {ex.Message}", isUser: false);
            App.Log.Error("Agent", $"Paste flow failed: {ex.Message}");
        }
    }

    private void ImportFlow(TestFlow flow, string sourceLabel)
    {
        AgentWelcomePanel.Visibility = Visibility.Collapsed;

        var validator = new FlowValidatorService(App.Log);
        var result = validator.Validate(flow);

        if (!result.IsValid)
        {
            var errors = string.Join("\n", result.Errors.Select(err => $"  \u2022 {err}"));
            AddChatMessage(
                $"{sourceLabel}\n\n\u26a0 **Validation failed** ({result.Errors.Count} errors):\n{errors}",
                isUser: false);
            return;
        }

        var warnings = result.Warnings.Count > 0
            ? $"\n\u26a0 {result.Warnings.Count} warning(s)"
            : "";

        AddChatMessage(
            $"{sourceLabel}\n\n\u2705 **{flow.TestName}** — {flow.Steps.Count} steps, validated OK{warnings}",
            isUser: false);

        AddFlowActionBar(flow);
        App.Log.Info("Agent", $"Imported flow '{flow.TestName}' ({flow.Steps.Count} steps): {sourceLabel}");
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        ChatMessagesPanel.Children.Clear();
        AgentWelcomePanel.Visibility = Visibility.Visible;
        App.Agent?.ClearHistory();
        UpdateAgentStatus();
    }

    private void SmokeTest_Click(object sender, RoutedEventArgs e)
    {
        var window = new SmokeTestWindow { Owner = Window.GetWindow(this) };
        window.Show();
    }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        var window = new DemoWindow { Owner = Window.GetWindow(this) };
        window.Show();
    }

    // ── Log panel ─────────────────────────────────────────────────────

    private void CopyAllChat_Click(object sender, RoutedEventArgs e)
    {
        var messages = new List<string>();
        foreach (UIElement child in ChatMessagesPanel.Children)
        {
            if (child is Border border && border.Tag is string text && !string.IsNullOrWhiteSpace(text))
            {
                var isUser = border.HorizontalAlignment == HorizontalAlignment.Right;
                messages.Add($"{(isUser ? "You" : "Agent")}: {text}");
            }
        }
        if (messages.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, messages));
            App.Log.Debug("UI", $"Copied entire chat ({messages.Count} messages) to clipboard");
        }
    }

    private void AgentLogLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AgentLogLevelCombo.SelectedItem is ComboBoxItem item)
        {
            _agentLogLevel = item.Content?.ToString() switch
            {
                "Debug" => LogLevel.Debug,
                "Info" => LogLevel.Info,
                "Warning" => LogLevel.Warning,
                "Error" => LogLevel.Error,
                _ => LogLevel.Info
            };
        }
    }

    private void ClearAgentLog_Click(object sender, RoutedEventArgs e) => AgentLogListBox.Items.Clear();

    private void ToggleAgentLog_Click(object sender, RoutedEventArgs e)
    {
        var isVisible = AgentLogPanelCol.Width.Value > 0;
        if (isVisible)
        {
            CollapseLogPanel();
        }
        else
        {
            AgentLogPanelCol.Width = new GridLength(350);
            AgentLogPanelCol.MinWidth = 200;
            AgentLogSplitter.Visibility = Visibility.Visible;
            AgentLogPanel.Visibility = Visibility.Visible;
        }
    }

    private void AgentTimelineFilter_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ClearAgentTimeline_Click(object sender, RoutedEventArgs e) => AgentTimelineListView.Items.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(App.Log.GetLogPath()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ListBox? GetLogListBoxFromSender(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm)
            return cm.PlacementTarget as ListBox;
        return null;
    }

    private void CopySelectedLog_Click(object sender, RoutedEventArgs e)
    {
        var lb = GetLogListBoxFromSender(sender);
        if (lb == null || lb.SelectedItems.Count == 0) return;
        var lines = lb.SelectedItems.Cast<ListBoxItem>().Select(item => item.Content?.ToString() ?? "").ToList();
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void CopyAllLog_Click(object sender, RoutedEventArgs e)
    {
        var lb = GetLogListBoxFromSender(sender);
        if (lb == null || lb.Items.Count == 0) return;
        var lines = lb.Items.Cast<ListBoxItem>().Select(item => item.Content?.ToString() ?? "").ToList();
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void SelectAllLog_Click(object sender, RoutedEventArgs e)
    {
        var lb = GetLogListBoxFromSender(sender);
        lb?.SelectAll();
    }

    // ── Voice ─────────────────────────────────────────────────────────

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Voice == null || !App.Voice.IsConfigured) return;

        if (!_isRecordingChat)
        {
            _isRecordingChat = true;
            MicButton.Content = "🔴";
            MicButton.Style = (Style)FindResource("MicButtonRecording");
            MicButton.ToolTip = "Recording... click to stop";

            App.Voice.OnTranscriptionReady -= OnChatTranscription;
            App.Voice.OnTranscriptionReady += OnChatTranscription;
            App.Voice.OnError -= OnChatVoiceError;
            App.Voice.OnError += OnChatVoiceError;
            App.Voice.OnSilenceDetected -= OnChatSilenceDetected;
            App.Voice.OnSilenceDetected += OnChatSilenceDetected;

            App.Voice.StartRecording();
        }
        else
        {
            _isRecordingChat = false;
            MicButton.Content = "⏳";
            MicButton.Style = (Style)FindResource("MicButtonTranscribing");
            MicButton.ToolTip = "Transcribing...";
            MicButton.IsEnabled = false;

            await App.Voice.StopRecordingAndTranscribeAsync();
            ResetMicButton();
        }
    }

    private void OnChatTranscription(string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(ChatInputBox.Text))
                ChatInputBox.Text += " ";
            ChatInputBox.Text += text;
            ChatInputBox.CaretIndex = ChatInputBox.Text.Length;
            ChatInputBox.Focus();
        });
    }

    private void OnChatVoiceError(string error)
    {
        Dispatcher.Invoke(() =>
        {
            ResetMicButton();
            AgentStatusText.Text = $"  •  🎤 {error}";
        });
    }

    private void OnChatSilenceDetected()
    {
        Dispatcher.Invoke(() =>
        {
            if (_isRecordingChat)
                MicButton.ToolTip = "Silence detected — click to stop and transcribe";
        });
    }

    private void ResetMicButton()
    {
        MicButton.Content = "🎤";
        MicButton.Style = (Style)FindResource("MicButton");
        MicButton.ToolTip = "Voice input — click to start recording";
        MicButton.IsEnabled = true;
    }
}

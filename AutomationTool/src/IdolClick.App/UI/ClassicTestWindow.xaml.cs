using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;

namespace IdolClick.UI;

/// <summary>
/// Test surface window for classic rule engine integration tests.
/// Exposes buttons, checkboxes, text fields, and other controls that the
/// engine can find and click. Tracks every click for test verification.
/// </summary>
public partial class ClassicTestWindow : Window
{
    /// <summary>
    /// Thread-safe log of element names that were clicked, in order.
    /// The test runner reads this to verify which actions were executed.
    /// </summary>
    public ConcurrentQueue<string> ClickLog { get; } = new();

    /// <summary>
    /// Signals the test runner that at least one click has been received.
    /// </summary>
    public ManualResetEventSlim ClickReceived { get; } = new(false);

    public ClassicTestWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shared click handler for all test buttons. Records the click and updates the log display.
    /// </summary>
    private void OnTestButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var name = btn.Content?.ToString() ?? btn.Name;
            ClickLog.Enqueue(name);
            ClickReceived.Set();

            TxtClickLog.Text += $"[{DateTime.Now:HH:mm:ss.fff}] Clicked: {name}\n";
        }
    }

    /// <summary>
    /// Resets click tracking state between tests.
    /// </summary>
    public void ResetLog()
    {
        while (ClickLog.TryDequeue(out _)) { }
        ClickReceived.Reset();
        TxtClickLog.Text = "";
    }

    /// <summary>
    /// Sets the text of the input TextBox (for verifying type/sendkeys actions).
    /// </summary>
    public string InputText
    {
        get => TxtInput.Text;
        set => TxtInput.Text = value;
    }

    /// <summary>
    /// Sets the status text block content.
    /// </summary>
    public void SetStatus(string text)
    {
        TxtStatus.Text = text;
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// REPORT SERVICE — Saves execution reports and screenshots to disk.
//
// Report directory: {appDir}/reports/{flowName}_{timestamp}/
//   report.json   — Machine-readable ExecutionReport v1
//   step_01.png   — Screenshot for step 1 (if captured)
//   step_02.png   — Screenshot for step 2 (if captured)
//
// Designed for:
//   • AI consumption — coding agents read report.json from disk
//   • CI pipelines — structured output for test runners
//   • Debugging — screenshots + reports in one folder
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Manages execution report persistence and screenshot capture.
/// </summary>
public class ReportService
{
    private readonly LogService _log;
    private readonly string _reportsDir;

    public ReportService(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        _reportsDir = Path.Combine(appDir, "reports");
    }

    /// <summary>
    /// Gets the base reports directory path.
    /// </summary>
    public string ReportsDirectory => _reportsDir;

    /// <summary>
    /// Saves an execution report to disk in a timestamped folder.
    /// Returns the path to the report.json file.
    /// </summary>
    public string SaveReport(ExecutionReport report)
    {
        try
        {
            var safeName = SanitizeFileName(report.TestName);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var folderName = $"{safeName}_{timestamp}";
            var reportDir = Path.Combine(_reportsDir, folderName);
            Directory.CreateDirectory(reportDir);

            var reportPath = Path.Combine(reportDir, "report.json");
            var json = JsonSerializer.Serialize(report, FlowJson.Options);
            File.WriteAllText(reportPath, json);

            _log.Info("Report", $"Report saved: {reportPath}");
            return reportPath;
        }
        catch (Exception ex)
        {
            _log.Error("Report", $"Failed to save report: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Captures a screenshot of the entire virtual screen (all monitors).
    /// Returns the file path, or null on failure.
    /// </summary>
    public string? CaptureScreenshot(string? outputDir = null, string? fileName = null)
    {
        try
        {
            var dir = outputDir ?? Path.Combine(_reportsDir, "_screenshots");
            Directory.CreateDirectory(dir);

            var file = fileName ?? $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var path = Path.Combine(dir, file);

            // Get virtual screen bounds (all monitors)
            var left = (int)SystemParameters.VirtualScreenLeft;
            var top = (int)SystemParameters.VirtualScreenTop;
            var width = (int)SystemParameters.VirtualScreenWidth;
            var height = (int)SystemParameters.VirtualScreenHeight;

            // Capture using GDI via Win32
            var hdcScreen = GetDC(IntPtr.Zero);
            var hdcMem = CreateCompatibleDC(hdcScreen);
            var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            var hOld = SelectObject(hdcMem, hBitmap);

            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY);

            SelectObject(hdcMem, hOld);

            // Convert HBITMAP to WPF BitmapSource and save as PNG
            var bmpSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            using var stream = new FileStream(path, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            encoder.Save(stream);

            // Clean up GDI resources
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);

            _log.Debug("Report", $"Screenshot captured: {path} ({width}x{height})");
            return path;
        }
        catch (Exception ex)
        {
            _log.Error("Report", $"Screenshot capture failed: {ex.Message}");
            return null;
        }
    }

    // GDI32 / User32 interop for screen capture
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Captures a screenshot for a specific step during flow execution.
    /// Saves to the report's folder with step numbering.
    /// </summary>
    public string? CaptureStepScreenshot(string reportDir, int stepNumber)
    {
        return CaptureScreenshot(reportDir, $"step_{stepNumber:D2}.png");
    }

    /// <summary>
    /// Lists saved reports in reverse chronological order.
    /// Returns (folderName, reportJsonPath, result, testName) tuples.
    /// </summary>
    public List<(string Folder, string Path, string Result, string TestName)> ListReports(int maxCount = 20)
    {
        var results = new List<(string, string, string, string)>();

        if (!Directory.Exists(_reportsDir))
            return results;

        var dirs = Directory.GetDirectories(_reportsDir)
            .OrderByDescending(d => d)
            .Take(maxCount);

        foreach (var dir in dirs)
        {
            var reportPath = Path.Combine(dir, "report.json");
            if (!File.Exists(reportPath)) continue;

            try
            {
                var json = File.ReadAllText(reportPath);
                var report = JsonSerializer.Deserialize<ExecutionReport>(json, FlowJson.Options);
                if (report != null)
                {
                    results.Add((Path.GetFileName(dir), reportPath, report.Result, report.TestName));
                }
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Loads a saved report from disk.
    /// </summary>
    public ExecutionReport? LoadReport(string reportJsonPath)
    {
        try
        {
            var json = File.ReadAllText(reportJsonPath);
            return JsonSerializer.Deserialize<ExecutionReport>(json, FlowJson.Options);
        }
        catch (Exception ex)
        {
            _log.Error("Report", $"Failed to load report '{reportJsonPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads a test flow from a JSON file.
    /// </summary>
    public static TestFlow? LoadFlowFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<TestFlow>(json, FlowJson.Options);
    }

    /// <summary>
    /// Parses a test flow from a JSON string (clipboard content, etc.).
    /// </summary>
    public static TestFlow? ParseFlowFromJson(string json)
    {
        try
        {
            // Try direct parse
            var flow = JsonSerializer.Deserialize<TestFlow>(json.Trim(), FlowJson.Options);
            if (flow != null && flow.Steps.Count > 0)
                return flow;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 50 ? sanitized[..50] : sanitized;
    }
}

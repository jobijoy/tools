using System.Runtime.InteropServices;
using System.Windows;
using AutomationTool.UI;

namespace AutomationTool.Services;

/// <summary>
/// Implementation of region capture using transparent overlay.
/// </summary>
public class RegionCaptureService : IRegionCaptureService
{
    private readonly LogService _log;

    public RegionCaptureService(LogService log)
    {
        _log = log;
    }

    public async Task<CapturedRegion?> CaptureRegionAsync()
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var overlay = new RegionSelectorOverlay();
                var result = overlay.ShowDialog();

                if (result == true && overlay.SelectedRegion != null)
                {
                    _log.Info("RegionCapture", $"Region selected: {overlay.SelectedRegion}");
                    return overlay.SelectedRegion;
                }

                _log.Debug("RegionCapture", "Selection cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _log.Error("RegionCapture", $"Failed to capture region: {ex.Message}");
                return null;
            }
        });
    }

    public async Task<CapturedRegion?> CaptureRegionForWindowAsync(IntPtr windowHandle)
    {
        // Get window bounds
        if (!GetWindowRect(windowHandle, out var rect))
        {
            _log.Warn("RegionCapture", "Could not get window bounds");
            return await CaptureRegionAsync();
        }

        var captured = await CaptureRegionAsync();
        if (captured == null) return null;

        // Adjust coordinates relative to window
        captured.X -= rect.Left;
        captured.Y -= rect.Top;

        return captured;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}

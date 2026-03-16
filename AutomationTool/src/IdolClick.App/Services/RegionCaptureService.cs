using System.Runtime.InteropServices;
using System.Windows;
using IdolClick.UI;

namespace IdolClick.Services;

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
        if (windowHandle == IntPtr.Zero)
            return await CaptureRegionAsync();

        // Get window bounds
        if (!GetWindowRect(windowHandle, out var rect))
        {
            _log.Warn("RegionCapture", "Could not get window bounds");
            return await CaptureRegionAsync();
        }

        await Application.Current.Dispatcher.InvokeAsync(() => Win32.ForceActivateWindow(windowHandle));
        await Task.Delay(150);

        var captured = await CaptureRegionAsync();
        if (captured == null) return null;

        var absoluteLeft = Math.Max(captured.X, rect.Left);
        var absoluteTop = Math.Max(captured.Y, rect.Top);
        var absoluteRight = Math.Min(captured.X + captured.Width, rect.Right);
        var absoluteBottom = Math.Min(captured.Y + captured.Height, rect.Bottom);

        if (absoluteRight <= absoluteLeft || absoluteBottom <= absoluteTop)
        {
            _log.Warn("RegionCapture", "Selected region did not intersect the target window");
            return null;
        }

        return new CapturedRegion
        {
            X = absoluteLeft - rect.Left,
            Y = absoluteTop - rect.Top,
            Width = absoluteRight - absoluteLeft,
            Height = absoluteBottom - absoluteTop
        };
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}

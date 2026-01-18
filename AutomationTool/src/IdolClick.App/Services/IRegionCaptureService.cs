namespace IdolClick.Services;

/// <summary>
/// Service for capturing screen regions interactively.
/// </summary>
public interface IRegionCaptureService
{
    /// <summary>
    /// Show an overlay and let user select a region.
    /// </summary>
    /// <returns>Selected region with screen coordinates, or null if cancelled.</returns>
    Task<CapturedRegion?> CaptureRegionAsync();

    /// <summary>
    /// Capture region relative to a specific window.
    /// </summary>
    Task<CapturedRegion?> CaptureRegionForWindowAsync(IntPtr windowHandle);
}

/// <summary>
/// Screen region captured by user with absolute pixel coordinates.
/// </summary>
public class CapturedRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// Convert to normalized region (0-1) relative to a window bounds.
    /// </summary>
    public Models.ScreenRegion ToNormalized(int windowX, int windowY, int windowWidth, int windowHeight)
    {
        return new Models.ScreenRegion
        {
            X = (double)(X - windowX) / windowWidth,
            Y = (double)(Y - windowY) / windowHeight,
            Width = (double)Width / windowWidth,
            Height = (double)Height / windowHeight
        };
    }

    /// <summary>
    /// Convert to normalized region using screen bounds.
    /// </summary>
    public Models.ScreenRegion ToScreenNormalized(int screenWidth, int screenHeight)
    {
        return new Models.ScreenRegion
        {
            X = (double)X / screenWidth,
            Y = (double)Y / screenHeight,
            Width = (double)Width / screenWidth,
            Height = (double)Height / screenHeight
        };
    }

    public override string ToString() => $"({X}, {Y}) {Width}x{Height}";
}

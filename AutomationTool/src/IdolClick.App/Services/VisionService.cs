using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using IdolClick.Models;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// VISION SERVICE — LLM Vision-based element location (STRICTLY FALLBACK).
//
// Resolution chain: UIA Selector → (future: DOM) → Vision Fallback
//
// Design principles:
//   • Vision is NEVER the primary resolution path — UIA always goes first
//   • Captures a window/screen region screenshot
//   • Sends screenshot + natural language description to LLM vision API
//   • LLM returns bounding box coordinates + confidence score
//   • Confidence threshold gates whether the result is used
//   • All vision calls logged in BackendCallLog for diagnostics
//
// Uses Microsoft.Extensions.AI IChatClient — works with any vision-capable model:
//   gpt-4o, gpt-4o-mini, claude-3.5-sonnet, gemini-pro-vision, etc.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Result of a vision-based element location attempt.
/// </summary>
public class VisionLocateResult
{
    /// <summary>Whether an element was found with sufficient confidence.</summary>
    public bool Found { get; set; }

    /// <summary>Bounding box of the located element in screen coordinates.</summary>
    public ElementBounds? Bounds { get; set; }

    /// <summary>Center X in screen coordinates (click target).</summary>
    public int CenterX { get; set; }

    /// <summary>Center Y in screen coordinates (click target).</summary>
    public int CenterY { get; set; }

    /// <summary>Confidence score (0.0 - 1.0). Higher = more certain.</summary>
    public double Confidence { get; set; }

    /// <summary>LLM's description of what it found.</summary>
    public string Description { get; set; } = "";

    /// <summary>Error message if location failed.</summary>
    public string? Error { get; set; }

    /// <summary>Path to the screenshot that was analyzed.</summary>
    public string? ScreenshotPath { get; set; }

    /// <summary>Raw LLM response text for diagnostics.</summary>
    public string? RawResponse { get; set; }
}

/// <summary>
/// Vision-based element location service using LLM multimodal capabilities.
/// Strictly a fallback — only used when UIA selector resolution fails.
/// </summary>
public class VisionService
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private IChatClient? _visionClient;

    /// <summary>
    /// Minimum confidence threshold to consider a vision result usable.
    /// Below this, the result is treated as "not found".
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// Whether vision fallback is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    public VisionService(ConfigService config, LogService log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Reconfigure();
    }

    /// <summary>
    /// Reconfigure the vision client from current settings.
    /// </summary>
    public void Reconfigure()
    {
        var settings = _config.GetConfig().AgentSettings;
        IsEnabled = settings.VisionFallbackEnabled;
        ConfidenceThreshold = settings.VisionConfidenceThreshold;

        if (!IsEnabled || string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _visionClient = null;
            return;
        }

        // Use the same model or a vision-specific model
        var modelId = string.IsNullOrWhiteSpace(settings.VisionModelId)
            ? settings.ModelId
            : settings.VisionModelId;

        try
        {
            var credential = new System.ClientModel.ApiKeyCredential(settings.ApiKey);
            OpenAI.Chat.ChatClient chatClient;

            // Extract base URL — users often paste the full deployment/chat-completions URL
            var endpointUrl = settings.Endpoint.TrimEnd('/');
            if (Uri.TryCreate(endpointUrl, UriKind.Absolute, out var parsedUri))
                endpointUrl = $"{parsedUri.Scheme}://{parsedUri.Host}";

            if (IsAzureOpenAIEndpoint(settings.Endpoint))
            {
                var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
                    new Uri(endpointUrl), credential);
                chatClient = azureClient.GetChatClient(modelId);
            }
            else
            {
                var openAiClient = new OpenAI.OpenAIClient(credential, new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(endpointUrl)
                });
                chatClient = openAiClient.GetChatClient(modelId);
            }

            _visionClient = chatClient.AsIChatClient();
            _log.Info("Vision", $"Vision service configured: {modelId} → {endpointUrl}");
        }
        catch (Exception ex)
        {
            _visionClient = null;
            _log.Error("Vision", $"Failed to create vision client: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates a UI element by sending a screenshot to the LLM vision API
    /// with a description of what to find.
    /// </summary>
    /// <param name="description">Natural language description of the element (e.g., "the Save button", "the search text box").</param>
    /// <param name="windowBounds">Screen bounds of the target window (to crop screenshot).</param>
    /// <param name="screenshotDir">Directory to save the analyzed screenshot.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Location result with coordinates and confidence.</returns>
    public async Task<VisionLocateResult> LocateElementAsync(
        string description,
        ElementBounds? windowBounds = null,
        string? screenshotDir = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || _visionClient == null)
        {
            return new VisionLocateResult
            {
                Found = false,
                Error = "Vision fallback is not enabled or not configured."
            };
        }

        try
        {
            _log.Debug("Vision", $"Attempting vision locate: '{description}'");

            // 1. Capture screenshot (window region or full screen)
            var screenshotPath = CaptureRegion(windowBounds, screenshotDir);
            if (screenshotPath == null)
            {
                return new VisionLocateResult
                {
                    Found = false,
                    Error = "Failed to capture screenshot for vision analysis."
                };
            }

            // 2. Load image and encode as base64
            var imageBytes = await File.ReadAllBytesAsync(screenshotPath, ct).ConfigureAwait(false);
            var base64 = Convert.ToBase64String(imageBytes);

            // Get image dimensions for coordinate mapping
            int imgWidth, imgHeight;
            using (var stream = new MemoryStream(imageBytes))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.Default);
                imgWidth = decoder.Frames[0].PixelWidth;
                imgHeight = decoder.Frames[0].PixelHeight;
            }

            // 3. Build vision prompt
            var prompt = BuildVisionPrompt(description, imgWidth, imgHeight);

            // 4. Send to LLM vision API
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, [
                    new TextContent(prompt),
                    new DataContent(imageBytes, "image/png")
                ])
            };

            var options = new ChatOptions
            {
                MaxOutputTokens = 300
                // Temperature omitted — some models (e.g. o-series, gpt-5.2) reject custom values
            };

            var response = await _visionClient.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            var responseText = response.Text ?? "";

            _log.Debug("Vision", $"Vision response: {responseText}");

            // 5. Parse coordinates from response
            var result = ParseVisionResponse(responseText, imgWidth, imgHeight, windowBounds);
            result.ScreenshotPath = screenshotPath;
            result.RawResponse = responseText;

            if (result.Found && result.Confidence >= ConfidenceThreshold)
            {
                _log.Info("Vision", $"Vision located element: ({result.CenterX},{result.CenterY}) confidence={result.Confidence:F2} — {result.Description}");
            }
            else if (result.Found)
            {
                _log.Warn("Vision", $"Vision found element but confidence too low: {result.Confidence:F2} < {ConfidenceThreshold:F2}");
                result.Found = false;
                result.Error = $"Confidence too low: {result.Confidence:F2} < threshold {ConfidenceThreshold:F2}";
            }
            else
            {
                _log.Debug("Vision", $"Vision did not locate element: {result.Error}");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return new VisionLocateResult { Found = false, Error = "Vision request cancelled." };
        }
        catch (Exception ex)
        {
            _log.Error("Vision", $"Vision locate failed: {ex.Message}");
            return new VisionLocateResult { Found = false, Error = $"Vision error: {ex.Message}" };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SCREENSHOT CAPTURE (REGION)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures a screenshot of a specific screen region (or full screen if bounds is null).
    /// </summary>
    private string? CaptureRegion(ElementBounds? bounds, string? outputDir)
    {
        try
        {
            var dir = outputDir ?? Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "reports", "_vision");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"vision_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            int left, top, width, height;
            if (bounds != null)
            {
                left = bounds.X;
                top = bounds.Y;
                width = bounds.Width;
                height = bounds.Height;
            }
            else
            {
                left = (int)SystemParameters.VirtualScreenLeft;
                top = (int)SystemParameters.VirtualScreenTop;
                width = (int)SystemParameters.VirtualScreenWidth;
                height = (int)SystemParameters.VirtualScreenHeight;
            }

            if (width <= 0 || height <= 0) return null;

            var hdcScreen = GetDC(IntPtr.Zero);
            var hdcMem = CreateCompatibleDC(hdcScreen);
            var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            var hOld = SelectObject(hdcMem, hBitmap);

            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY);
            SelectObject(hdcMem, hOld);

            var bmpSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            using var stream = new FileStream(path, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            encoder.Save(stream);

            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);

            return path;
        }
        catch (Exception ex)
        {
            _log.Error("Vision", $"Region capture failed: {ex.Message}");
            return null;
        }
    }

    private const int SRCCOPY = 0x00CC0020;
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    // ═══════════════════════════════════════════════════════════════════════════════
    // VISION PROMPT + RESPONSE PARSING
    // ═══════════════════════════════════════════════════════════════════════════════

    private static bool IsAzureOpenAIEndpoint(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string BuildVisionPrompt(string description, int imgWidth, int imgHeight)
    {
        return $@"You are a precise UI element locator. Analyze the screenshot and find the UI element described below.

ELEMENT TO FIND: {description}

IMAGE SIZE: {imgWidth}x{imgHeight} pixels

RESPOND IN EXACTLY THIS JSON FORMAT (no other text):
{{
  ""found"": true,
  ""x"": <left edge pixel X>,
  ""y"": <top edge pixel Y>,
  ""width"": <element width in pixels>,
  ""height"": <element height in pixels>,
  ""confidence"": <0.0 to 1.0>,
  ""description"": ""<what you found>""
}}

If you cannot find the element, respond:
{{
  ""found"": false,
  ""confidence"": 0.0,
  ""description"": ""<why not found>""
}}

RULES:
- Coordinates are in pixel space of the provided image
- Confidence 1.0 = absolutely certain, 0.5 = unsure, 0.0 = not found
- Be precise with bounding box — it should tightly wrap the element
- If multiple matches, pick the most prominent/visible one
- Only return JSON, no other text";
    }

    /// <summary>
    /// Parses the LLM vision response into a VisionLocateResult.
    /// Handles coordinate mapping from image space to screen space.
    /// </summary>
    private VisionLocateResult ParseVisionResponse(
        string responseText,
        int imgWidth, int imgHeight,
        ElementBounds? windowBounds)
    {
        try
        {
            // Strip markdown code fences if present
            var json = responseText.Trim();
            if (json.StartsWith("```"))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var found = root.GetProperty("found").GetBoolean();
            var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.0;
            var desc = root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";

            if (!found)
            {
                return new VisionLocateResult
                {
                    Found = false,
                    Confidence = confidence,
                    Description = desc,
                    Error = $"Element not found by vision: {desc}"
                };
            }

            var imgX = root.GetProperty("x").GetInt32();
            var imgY = root.GetProperty("y").GetInt32();
            var imgW = root.TryGetProperty("width", out var wProp) ? wProp.GetInt32() : 20;
            var imgH = root.TryGetProperty("height", out var hProp) ? hProp.GetInt32() : 20;

            // Map image coordinates to screen coordinates
            int screenX, screenY;
            if (windowBounds != null)
            {
                // Image was a window crop — add window offset
                screenX = windowBounds.X + imgX;
                screenY = windowBounds.Y + imgY;
            }
            else
            {
                // Image was full screen — coordinates are direct
                screenX = (int)SystemParameters.VirtualScreenLeft + imgX;
                screenY = (int)SystemParameters.VirtualScreenTop + imgY;
            }

            return new VisionLocateResult
            {
                Found = true,
                Bounds = new ElementBounds
                {
                    X = screenX,
                    Y = screenY,
                    Width = imgW,
                    Height = imgH
                },
                CenterX = screenX + imgW / 2,
                CenterY = screenY + imgH / 2,
                Confidence = confidence,
                Description = desc
            };
        }
        catch (JsonException ex)
        {
            _log.Warn("Vision", $"Failed to parse vision response JSON: {ex.Message}");
            return new VisionLocateResult
            {
                Found = false,
                Error = $"Failed to parse vision response: {ex.Message}",
                RawResponse = responseText
            };
        }
        catch (Exception ex)
        {
            return new VisionLocateResult
            {
                Found = false,
                Error = $"Vision response parsing error: {ex.Message}",
                RawResponse = responseText
            };
        }
    }
}

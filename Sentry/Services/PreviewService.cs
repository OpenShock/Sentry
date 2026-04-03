using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using OpenShock.Sentry.Models;
using Size = OpenCvSharp.Size;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Provides screen capture frames as base64 JPEG images for Blazor UI preview.
/// Draws detection region overlays and bounding boxes on the preview.
/// </summary>
public sealed class PreviewService : IDisposable
{
    private readonly ILogger<PreviewService> _logger;
    private readonly ScreenCaptureService _screenCapture;

    private readonly Lock _overlayLock = new();
    private readonly List<RegionOverlay> _regionOverlays = [];
    private readonly List<DetectionOverlay> _detectionOverlays = [];

    /// <summary>Scale factor for the preview image (1.0 = full resolution)</summary>
    public float PreviewScale { get; set; } = 0.35f;

    public PreviewService(ILogger<PreviewService> logger, ScreenCaptureService screenCapture)
    {
        _logger = logger;
        _screenCapture = screenCapture;
    }

    /// <summary>
    /// Capture a single frame, draw overlays, and return as base64 JPEG data URI.
    /// </summary>
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    public string? CapturePreviewFrame()
    {
        try
        {
            if (_screenCapture.ScreenWidth == 0) return null;

            // Use latest frame from capture loop, or grab one if loop isn't running
            var frame = _screenCapture.IsRunning
                ? _screenCapture.GetLatestFrame()
                : _screenCapture.CaptureOneFrame();
            if (frame is null) return null;
            var (colorFrame, _) = frame.Value;

            // Draw region overlays
            lock (_overlayLock)
            {
                foreach (var region in _regionOverlays)
                {
                    var pixelRect = region.Region.ToPixelRect(
                        _screenCapture.ScreenWidth, _screenCapture.ScreenHeight);

                    // Color the border based on detection status
                    var borderColor = region.Triggered switch
                    {
                        true => new Scalar(0, 255, 0),    // Green = triggered
                        false => new Scalar(0, 0, 255),   // Red = not triggered
                        null => region.Color               // Default = not running
                    };
                    Cv2.Rectangle(colorFrame, pixelRect, borderColor, 2);

                    // Label (top-left, above the rect)
                    if (!string.IsNullOrEmpty(region.Label))
                    {
                        var labelPos = new OpenCvSharp.Point(pixelRect.X + 4, pixelRect.Y - 8);
                        Cv2.PutText(colorFrame, region.Label, labelPos,
                            HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 0, 0), 5);
                        Cv2.PutText(colorFrame, region.Label, labelPos,
                            HersheyFonts.HersheySimplex, 0.6, region.Color, 2);
                    }

                    // Detection status + confidence (bottom-left, inside the rect)
                    if (region.Triggered.HasValue)
                    {
                        var statusText = region.Triggered.Value
                            ? $"TRIGGERED {region.Confidence:P0}"
                            : $"{region.Confidence:P0}";
                        var statusPos = new OpenCvSharp.Point(pixelRect.X + 4, pixelRect.Y + pixelRect.Height - 8);
                        Cv2.PutText(colorFrame, statusText, statusPos,
                            HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 0), 4);
                        Cv2.PutText(colorFrame, statusText, statusPos,
                            HersheyFonts.HersheySimplex, 0.5, borderColor, 2);
                    }

                    // OCR text (below the region)
                    if (!string.IsNullOrEmpty(region.Text))
                    {
                        var displayText = region.Text.ReplaceLineEndings(" ");
                        if (displayText.Length > 60)
                            displayText = displayText[..57] + "...";
                        var textPos = new OpenCvSharp.Point(pixelRect.X + 4, pixelRect.Y + pixelRect.Height + 20);
                        // Dark outline for readability
                        Cv2.PutText(colorFrame, displayText, textPos,
                            HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 0), 4);
                        Cv2.PutText(colorFrame, displayText, textPos,
                            HersheyFonts.HersheySimplex, 0.5, region.Color, 2);
                    }
                }

                foreach (var det in _detectionOverlays)
                {
                    Cv2.Rectangle(colorFrame, det.BoundingBox,
                        det.Triggered ? new Scalar(0, 255, 0) : new Scalar(0, 165, 255), 3);
                }
            }

            // Resize for preview
            var previewWidth = (int)(_screenCapture.ScreenWidth * PreviewScale);
            var previewHeight = (int)(_screenCapture.ScreenHeight * PreviewScale);

            using var resized = new Mat();
            Cv2.Resize(colorFrame, resized, new Size(previewWidth, previewHeight));

            // Encode to JPEG
            Cv2.ImEncode(".jpg", resized, out var buf, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)]);
            return $"data:image/jpeg;base64,{Convert.ToBase64String(buf)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture preview frame");
            return null;
        }
    }

    public void SetRegionOverlays(IEnumerable<RegionOverlay> overlays)
    {
        lock (_overlayLock)
        {
            _regionOverlays.Clear();
            _regionOverlays.AddRange(overlays);
        }
    }

    public void SetDetectionOverlays(IEnumerable<DetectionOverlay> overlays)
    {
        lock (_overlayLock)
        {
            _detectionOverlays.Clear();
            _detectionOverlays.AddRange(overlays);
        }
    }

    public void ClearOverlays()
    {
        lock (_overlayLock)
        {
            _regionOverlays.Clear();
            _detectionOverlays.Clear();
        }
    }

    public void Dispose() { }
}

public sealed class RegionOverlay
{
    public required NormalizedRegion Region { get; init; }
    public required string Label { get; init; }
    public Scalar Color { get; init; } = new(255, 200, 0); // Cyan-ish
    public bool? Triggered { get; set; }
    public float Confidence { get; set; }
    public string? Text { get; set; }
}

public sealed class DetectionOverlay
{
    public required Rect BoundingBox { get; init; }
    public bool Triggered { get; init; }
}

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using EmbedIO;
using EmbedIO.Actions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Models;
using Swan.Logging;
using Size = OpenCvSharp.Size;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Provides screen capture frames with detection overlays.
/// Serves preview as an MJPEG stream over a local HTTP endpoint using EmbedIO.
/// </summary>
public sealed class PreviewService : IDisposable
{
    private readonly ILogger<PreviewService> _logger;
    private readonly ScreenCaptureService _screenCapture;

    private readonly Lock _overlayLock = new();
    private readonly List<RegionOverlay> _regionOverlays = [];
    private readonly List<DetectionOverlay> _detectionOverlays = [];

    // Pre-allocated buffers to reduce per-frame GC pressure
    private Mat _resizedMat = new();
    private static readonly ImageEncodingParam[] JpegParams = [new(ImwriteFlags.JpegQuality, 85)];

    // MJPEG stream
    private WebServer? _server;
    private readonly SemaphoreSlim _streamLock = new SemaphoreSlim(1,1 );
    private readonly List<MjpegClient> _clients = [];
    private byte[]? _latestJpeg;
    private static readonly byte[] BoundaryEnd = "\r\n"u8.ToArray();
    private static readonly byte[] BoundaryPrefix = "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: "u8.ToArray();
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    /// <summary>Scale factor for the preview image (1.0 = full resolution)</summary>
    public float PreviewScale { get; set; } = 0.35f;

    /// <summary>The local URL the MJPEG stream is served on.</summary>
    public string? StreamUrl { get; private set; }

    public PreviewService(ILogger<PreviewService> logger, ScreenCaptureService screenCapture)
    {
        _logger = logger;
        _screenCapture = screenCapture;

        // Suppress Swan's default console logging
        Swan.Logging.Logger.NoLogging();
    }

    // ── MJPEG Server ──────────────────────────────────────────────────

    public void StartStream(int port = 0)
    {
        if (_server is not null) return;

        if (port == 0)
        {
            using var temp = new TcpListener(IPAddress.Loopback, 0);
            temp.Start();
            port = ((IPEndPoint)temp.LocalEndpoint).Port;
            temp.Stop();
        }

        var url = $"http://localhost:{port}";
        StreamUrl = $"{url}/preview";

        _server = new WebServer(o => o
                .WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO))
            .WithAction("/preview", HttpVerbs.Get, HandlePreviewRequest);

        _server.RunAsync();
        _logger.LogInformation("MJPEG preview stream started on {Url}", StreamUrl);
    }

    public async Task StopStream()
    {
        await _streamLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
        }
        finally
        {
            _streamLock.Release();
        }

        _server?.Dispose();
        _server = null;
        StreamUrl = null;
    }

    private async Task HandlePreviewRequest(IHttpContext context)
    {
        // Single-frame snapshot
        if (context.Request.QueryString["snapshot"] is not null)
        {
            var jpeg = _latestJpeg;
            if (jpeg is null)
            {
                context.Response.StatusCode = 503;
                return;
            }

            context.Response.ContentType = "image/jpeg";
            context.Response.ContentLength64 = jpeg.Length;
            await context.Response.OutputStream.WriteAsync(jpeg).ConfigureAwait(false);
            return;
        }

        // MJPEG stream — keep connection alive
        context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";

        var client = new MjpegClient(context.Response.OutputStream);
        await _streamLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _clients.Add(client);
        }
        finally
        {
            _streamLock.Release();
        }

        _logger.LogDebug("MJPEG client connected, total: {Count}", _clients.Count);

        // Keep the handler alive until the client disconnects
        try
        {
            await client.DisconnectedTask.ConfigureAwait(false);
        }
        catch
        {
            // Client disconnected
        }
    }

    /// <summary>
    /// Push the latest JPEG frame to all connected MJPEG clients.
    /// </summary>
    private async Task BroadcastFrame(byte[] jpeg)
    {
        _latestJpeg = jpeg;

        var contentLengthBytes = new byte[20];
        jpeg.Length.TryFormat(contentLengthBytes, out var contentLengthLen);

        await _streamLock.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    var stream = _clients[i].OutputStream;
                    await stream.WriteAsync(BoundaryPrefix).ConfigureAwait(false);
                    await stream.WriteAsync(contentLengthBytes.AsMemory(0, contentLengthLen)).ConfigureAwait(false);
                    await stream.WriteAsync(HeaderEnd).ConfigureAwait(false);
                    await stream.WriteAsync(jpeg).ConfigureAwait(false);
                    await stream.WriteAsync(BoundaryEnd).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    _clients[i].Dispose();
                    _clients.RemoveAt(i);
                }
            }
        }
        finally
        {
            _streamLock.Release();
        }
    }

    // ── Frame Rendering ───────────────────────────────────────────────

    /// <summary>
    /// Capture a frame, draw overlays, encode to JPEG, and broadcast to MJPEG clients.
    /// </summary>
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    public async Task<bool> RenderAndBroadcastFrame()
    {
        try
        {
            if (_screenCapture.ScreenWidth == 0) return false;

            var frame = _screenCapture.GetLatestFrame();
            if (frame is null) return false;
            var (colorFrame, _) = frame.Value;

            DrawOverlays(colorFrame);

            var previewWidth = (int)(_screenCapture.ScreenWidth * PreviewScale);
            var previewHeight = (int)(_screenCapture.ScreenHeight * PreviewScale);
            Cv2.Resize(colorFrame, _resizedMat, new Size(previewWidth, previewHeight));

            Cv2.ImEncode(".jpg", _resizedMat, out var jpeg, JpegParams);

            await BroadcastFrame(jpeg).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render preview frame");
            return false;
        }
    }

    /// <summary>
    /// Capture a single frame and return as base64 JPEG data URI (for snapshot use).
    /// </summary>
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    public string? CapturePreviewFrame()
    {
        try
        {
            if (_screenCapture.ScreenWidth == 0) return null;

            var frame = _screenCapture.IsRunning
                ? _screenCapture.GetLatestFrame()
                : _screenCapture.CaptureOneFrame();
            if (frame is null) return null;
            var (colorFrame, _) = frame.Value;

            DrawOverlays(colorFrame);

            var previewWidth = (int)(_screenCapture.ScreenWidth * PreviewScale);
            var previewHeight = (int)(_screenCapture.ScreenHeight * PreviewScale);
            Cv2.Resize(colorFrame, _resizedMat, new Size(previewWidth, previewHeight));

            Cv2.ImEncode(".jpg", _resizedMat, out var buf, JpegParams);
            return string.Concat("data:image/jpeg;base64,", Convert.ToBase64String(buf));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture preview frame");
            return null;
        }
    }

    private void DrawOverlays(Mat colorFrame)
    {
        lock (_overlayLock)
        {
            foreach (var region in _regionOverlays)
            {
                var pixelRect = region.Region.ToPixelRect(
                    _screenCapture.ScreenWidth, _screenCapture.ScreenHeight);

                var borderColor = region.Triggered switch
                {
                    true => new Scalar(0, 255, 0),
                    false => new Scalar(0, 0, 255),
                    null => region.Color
                };
                Cv2.Rectangle(colorFrame, pixelRect, borderColor, 2);

                if (!string.IsNullOrEmpty(region.Label))
                {
                    var labelPos = new OpenCvSharp.Point(pixelRect.X + 4, pixelRect.Y - 8);
                    Cv2.PutText(colorFrame, region.Label, labelPos,
                        HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 0, 0), 5);
                    Cv2.PutText(colorFrame, region.Label, labelPos,
                        HersheyFonts.HersheySimplex, 0.6, region.Color, 2);
                }

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

                if (!string.IsNullOrEmpty(region.Text))
                {
                    var displayText = region.Text.ReplaceLineEndings(" ");
                    if (displayText.Length > 60)
                        displayText = displayText[..57] + "...";
                    var textPos = new OpenCvSharp.Point(pixelRect.X + 4, pixelRect.Y + pixelRect.Height + 20);
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
    }

    // ── Overlay Management ────────────────────────────────────────────

    public void SetRegionOverlays(List<RegionOverlay> overlays)
    {
        lock (_overlayLock)
        {
            _regionOverlays.Clear();
            _regionOverlays.AddRange(overlays);
        }
    }

    public void SetDetectionOverlays(List<DetectionOverlay> overlays)
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

    public void Dispose()
    {
        StopStream();
        _resizedMat.Dispose();
    }
}

public sealed class RegionOverlay
{
    public required NormalizedRegion Region { get; init; }
    public required string Label { get; init; }
    public Scalar Color { get; init; } = new(255, 200, 0);
    public bool? Triggered { get; set; }
    public float Confidence { get; set; }
    public string? Text { get; set; }
}

public struct DetectionOverlay
{
    public Rect BoundingBox;
    public bool Triggered;
}

/// <summary>
/// Tracks a connected MJPEG client's output stream and disconnect state.
/// </summary>
sealed class MjpegClient : IDisposable
{
    public Stream OutputStream { get; }
    private readonly TaskCompletionSource _disconnected = new();
    public Task DisconnectedTask => _disconnected.Task;

    public MjpegClient(Stream outputStream)
    {
        OutputStream = outputStream;
    }

    public void Dispose()
    {
        _disconnected.TrySetResult();
        try { OutputStream.Close(); } catch { /* ignored */ }
    }
}

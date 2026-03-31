using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Size = System.Drawing.Size;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Single capture loop with FPS limiting and frame versioning.
/// Consumers poll for new frames via frame counter to avoid redundant work.
/// </summary>
[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public sealed class ScreenCaptureService : IDisposable
{
    private Bitmap? _captureBitmap;
    private Graphics? _graphics;

    // Double buffer: capture writes to _back, then swaps to _front
    private Mat? _frontColor;
    private Mat? _frontGray;
    private Mat? _backColor;
    private Mat? _backGray;
    private readonly Lock _swapLock = new();

    private CancellationTokenSource? _cts;
    private int _width;
    private int _height;

    /// <summary>Fired on the capture thread after each new frame is swapped in.</summary>
    public event Action? OnNewFrame;

    /// <summary>Incremented on every new frame. Consumers compare to detect new frames.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>Target FPS for the capture loop. 0 = unlimited.</summary>
    public int TargetFps { get; set; } = 30;

    // Performance stats
    public float CaptureFps { get; private set; }
    public float CaptureMs { get; private set; }

    public int ScreenWidth => _width;
    public int ScreenHeight => _height;
    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

    public void Initialize(int width, int height)
    {
        if (_width == width && _height == height && _captureBitmap is not null) return;

        Stop();

        _width = width;
        _height = height;

        _captureBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_captureBitmap);

        _frontColor = new Mat(height, width, MatType.CV_8UC4);
        _frontGray = new Mat(height, width, MatType.CV_8UC1);
        _backColor = new Mat(height, width, MatType.CV_8UC4);
        _backGray = new Mat(height, width, MatType.CV_8UC1);
    }

    public void Start()
    {
        if (IsRunning) return;
        if (_captureBitmap is null) throw new InvalidOperationException("Call Initialize() first.");

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var frameSw = new Stopwatch();
        var fpsSw = Stopwatch.StartNew();
        var frameCount = 0;

        while (!ct.IsCancellationRequested)
        {
            var targetFrameTimeMs = TargetFps > 0 ? 1000.0 / TargetFps : 0;

            frameSw.Restart();

            try
            {
                _graphics!.CopyFromScreen(0, 0, 0, 0, new Size(_width, _height));
                _captureBitmap!.ToMat(_backColor!);
                Cv2.CvtColor(_backColor!, _backGray!, ColorConversionCodes.BGRA2GRAY);

                lock (_swapLock)
                {
                    (_frontColor, _backColor) = (_backColor, _frontColor);
                    (_frontGray, _backGray) = (_backGray, _frontGray);
                    FrameNumber++;
                }

                OnNewFrame?.Invoke();
            }
            catch (Exception)
            {
                // GDI can throw if desktop is locked etc., just skip frame
            }

            frameSw.Stop();
            CaptureMs = (float)frameSw.Elapsed.TotalMilliseconds;

            // FPS counter
            frameCount++;
            if (fpsSw.ElapsedMilliseconds >= 1000)
            {
                CaptureFps = frameCount * 1000f / fpsSw.ElapsedMilliseconds;
                frameCount = 0;
                fpsSw.Restart();
            }

            // FPS limiter — sleep remaining budget
            var elapsed = frameSw.Elapsed.TotalMilliseconds;
            if (targetFrameTimeMs > 0 && elapsed < targetFrameTimeMs)
            {
                var sleepMs = (int)(targetFrameTimeMs - elapsed);
                if (sleepMs > 0) Thread.Sleep(sleepMs);
            }
        }
    }

    /// <summary>
    /// Get the latest captured frame. Consumers should track FrameNumber to detect new frames.
    /// </summary>
    public (Mat Color, Mat Gray)? GetLatestFrame()
    {
        lock (_swapLock)
        {
            if (_frontColor is null || _frontGray is null) return null;
            return (_frontColor, _frontGray);
        }
    }

    /// <summary>
    /// Capture a single frame immediately (for one-shot preview when loop isn't running).
    /// </summary>
    public (Mat Color, Mat Gray)? CaptureOneFrame()
    {
        if (_captureBitmap is null || _graphics is null || _backColor is null || _backGray is null)
            return null;

        if (IsRunning) return GetLatestFrame();

        var sw = Stopwatch.StartNew();

        _graphics.CopyFromScreen(0, 0, 0, 0, new Size(_width, _height));
        _captureBitmap.ToMat(_backColor);
        Cv2.CvtColor(_backColor, _backGray, ColorConversionCodes.BGRA2GRAY);

        lock (_swapLock)
        {
            (_frontColor, _backColor) = (_backColor, _frontColor);
            (_frontGray, _backGray) = (_backGray, _frontGray);
            FrameNumber++;
        }

        sw.Stop();
        CaptureMs = (float)sw.Elapsed.TotalMilliseconds;

        return GetLatestFrame();
    }

    public void Dispose()
    {
        Stop();
        _frontColor?.Dispose();
        _frontGray?.Dispose();
        _backColor?.Dispose();
        _backGray?.Dispose();
        _graphics?.Dispose();
        _captureBitmap?.Dispose();
    }
}

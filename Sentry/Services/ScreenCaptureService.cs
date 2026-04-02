using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
    private int _sourceX;
    private int _sourceY;

    /// <summary>Fired on the capture thread after each new frame is swapped in.</summary>
    public event Action? OnNewFrame;

    /// <summary>Incremented on every new frame. Consumers compare to detect new frames.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>Target FPS for the capture loop. 0 = unlimited.</summary>
    public int TargetFps { get; set; } = 30;

    // Performance stats
    public float CaptureFps { get; private set; }
    public float CaptureMs { get; private set; }

    public int SourceX => _sourceX;
    public int SourceY => _sourceY;
    public int ScreenWidth => _width;
    public int ScreenHeight => _height;
    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

    /// <summary>
    /// Returns info about all connected monitors using physical pixel coordinates.
    /// Uses EnumDisplayMonitors for position and EnumDisplaySettings for true resolution
    /// to avoid DPI scaling issues with CopyFromScreen.
    /// </summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MonitorInfoEx();
            info.cbSize = Marshal.SizeOf<MonitorInfoEx>();
            if (GetMonitorInfo(hMonitor, ref info))
            {
                // Get physical pixel resolution via EnumDisplaySettings
                var devMode = new DevMode();
                devMode.dmSize = (short)Marshal.SizeOf<DevMode>();

                if (EnumDisplaySettings(info.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, ref devMode))
                {
                    monitors.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice,
                        X = devMode.dmPositionX,
                        Y = devMode.dmPositionY,
                        Width = devMode.dmPelsWidth,
                        Height = devMode.dmPelsHeight,
                        IsPrimary = (info.dwFlags & 1) != 0 // MONITORINFOF_PRIMARY
                    });
                }
            }
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    #region Win32 Interop

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    #endregion

    public void Initialize(int width, int height) => Initialize(0, 0, width, height);

    public void Initialize(Rectangle monitorBounds)
    {
        Initialize(monitorBounds.X, monitorBounds.Y, monitorBounds.Width, monitorBounds.Height);
    }

    public void Initialize(int sourceX, int sourceY, int width, int height)
    {
        if (_sourceX == sourceX && _sourceY == sourceY
            && _width == width && _height == height && _captureBitmap is not null) return;

        Stop();

        _sourceX = sourceX;
        _sourceY = sourceY;
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
                _graphics!.CopyFromScreen(_sourceX, _sourceY, 0, 0, new Size(_width, _height));
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

        _graphics.CopyFromScreen(_sourceX, _sourceY, 0, 0, new Size(_width, _height));
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

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }

    public string DisplayName => IsPrimary
        ? $"{DeviceName} (Primary) — {Width}x{Height}"
        : $"{DeviceName} — {Width}x{Height}";

    public Rectangle Bounds => new(X, Y, Width, Height);
}

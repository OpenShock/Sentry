using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Size = System.Drawing.Size;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Pre-allocated GDI + OpenCV resources for capturing a specific screen region.
/// </summary>
[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public sealed class RegionCapture : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly Mat _colorMat;
    private readonly int _screenX;
    private readonly int _screenY;
    private readonly Size _size;

    public Mat GrayMat { get; }

    public RegionCapture(int screenX, int screenY, int width, int height)
    {
        _screenX = screenX;
        _screenY = screenY;
        _size = new Size(width, height);

        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _colorMat = new Mat(height, width, MatType.CV_8UC4);
        GrayMat = new Mat(height, width, MatType.CV_8UC1);
    }

    public void CaptureFromScreen()
    {
        _graphics.CopyFromScreen(_screenX, _screenY, 0, 0, _size);
        _bitmap.ToMat(_colorMat);
        Cv2.CvtColor(_colorMat, GrayMat, ColorConversionCodes.BGRA2GRAY);
    }

    public void Dispose()
    {
        GrayMat.Dispose();
        _colorMat.Dispose();
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}

using OpenCvSharp;

namespace OpenShock.Sentry.Models;

/// <summary>
/// Screen region defined in normalized coordinates (0.0–1.0) for resolution independence.
/// </summary>
public sealed class NormalizedRegion
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public Rect ToPixelRect(int screenWidth, int screenHeight)
    {
        return new Rect(
            (int)(X * screenWidth),
            (int)(Y * screenHeight),
            (int)(Width * screenWidth),
            (int)(Height * screenHeight)
        );
    }

    public static NormalizedRegion FullScreen => new() { X = 0, Y = 0, Width = 1, Height = 1 };
}

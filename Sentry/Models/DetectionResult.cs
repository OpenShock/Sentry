using OpenCvSharp;

namespace OpenShock.Sentry.Models;

public sealed class DetectionResult
{
    public required bool Detected { get; init; }
    public float Confidence { get; init; }
    public Rect? BoundingBox { get; init; }

    public static DetectionResult NoMatch => new() { Detected = false, Confidence = 0f };
}

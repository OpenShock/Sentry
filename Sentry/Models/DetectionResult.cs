using OpenCvSharp;

namespace OpenShock.Sentry.Models;

public sealed class DetectionResult
{
    public required bool Triggered { get; init; }
    public float Confidence { get; init; }
    public Rect? BoundingBox { get; init; }

    /// <summary>
    /// Optional normalized value (0.0–1.0) for continuous measurements
    /// such as health bar fill level, cooldown progress, etc.
    /// </summary>
    public float? Value { get; init; }

    /// <summary>
    /// Optional text extracted by the detector (e.g. OCR result, recognized label).
    /// </summary>
    public string? Text { get; init; }

    public static DetectionResult NoMatch => new() { Triggered = false, Confidence = 0f };
}

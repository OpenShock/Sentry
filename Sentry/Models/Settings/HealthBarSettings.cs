namespace OpenShock.Sentry.Models;

public enum HealthBarOrientation
{
    Horizontal,
    Vertical
}

public sealed class HealthBarSettings
{
    /// <summary>Bar orientation. Horizontal = fill measured left→right.</summary>
    public HealthBarOrientation Orientation { get; set; } = HealthBarOrientation.Horizontal;

    /// <summary>Grayscale brightness (0–255) above which a pixel counts as "filled".</summary>
    public int BrightnessThreshold { get; set; } = 80;

    /// <summary>
    /// Fraction of pixels along the perpendicular axis that must exceed the brightness
    /// threshold for a slice to count as filled. 0–1, default 0.33.
    /// </summary>
    public float SliceFillRatio { get; set; } = 0.33f;

    /// <summary>
    /// Minimum drop in fill (0–1) below the rolling baseline required to trigger.
    /// e.g. 0.10 = trigger when fill drops 10% or more.
    /// </summary>
    public float MinDropDelta { get; set; } = 0.10f;

    /// <summary>
    /// How quickly the baseline decays toward the current fill per frame (0–1).
    /// 0 = baseline is sticky (only resets on trigger / refill), 1 = no memory.
    /// Small values (e.g. 0.005) let the baseline forget very slow drains.
    /// </summary>
    public float BaselineDecay { get; set; } = 0.0f;
}

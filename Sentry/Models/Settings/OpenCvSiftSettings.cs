namespace OpenShock.Sentry.Models;

public sealed class OpenCvSiftSettings
{
    public string TemplatePath { get; set; } = "";
    public float RatioThreshold { get; set; } = 0.5f;
    public int MinGoodMatches { get; set; } = 8;
}
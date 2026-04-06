namespace OpenShock.Sentry.Models;

public sealed class OcrSettings
{
    public string Pattern { get; set; } = "";
    public string? Language { get; set; }
    public string? EngineMode { get; set; }
}
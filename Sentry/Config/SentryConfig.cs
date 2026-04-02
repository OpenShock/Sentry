namespace OpenShock.Sentry.Config;

public sealed class SentryConfig
{
    public string? ActiveProfileName { get; set; }
    public string? MonitorDeviceName { get; set; }
    public int ScreenWidth { get; set; } = 2560;
    public int ScreenHeight { get; set; } = 1440;
    public int TargetCaptureFps { get; set; } = 30;
}

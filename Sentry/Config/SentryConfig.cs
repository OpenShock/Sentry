namespace OpenShock.Sentry.Config;

public sealed class SentryConfig
{
    public string? ActiveProfileName { get; set; }
    public string? MonitorDeviceName { get; set; }
    public int ScreenWidth { get; set; } = 2560;
    public int ScreenHeight { get; set; } = 1440;
    public int TargetCaptureFps { get; set; } = 30;

    // Global clamps applied to every action mapping at trigger time.
    public byte MinIntensity { get; set; } = 1;
    public byte MaxIntensity { get; set; } = 100;
    public ushort MinDuration { get; set; } = 300;
    public ushort MaxDuration { get; set; } = 30000;
}

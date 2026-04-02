namespace OpenShock.Sentry.Models;

public sealed class GameProfile
{
    public required string Name { get; set; }
    public string? ProcessName { get; set; }
    public bool PerRegionCapture { get; set; } = true;
    public List<DetectorConfig> Detectors { get; set; } = [];
    public List<ActionMapping> Actions { get; set; } = [];
}

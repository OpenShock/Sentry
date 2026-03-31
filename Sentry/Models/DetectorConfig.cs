using System.Text.Json;

namespace OpenShock.Sentry.Models;

public sealed class DetectorConfig
{
    public required string Name { get; set; }
    public DetectorBackendType Backend { get; set; }
    public NormalizedRegion Region { get; set; } = NormalizedRegion.FullScreen;
    public GameEventType EventType { get; set; }
    public bool InvertMatch { get; set; }
    public Dictionary<string, JsonElement> Settings { get; set; } = new();
}

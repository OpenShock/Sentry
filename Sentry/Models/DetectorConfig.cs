using System.Text.Json;

namespace OpenShock.Sentry.Models;

public sealed class DetectorConfig
{
    public required string Name { get; set; }
    public DetectorBackendType Backend { get; set; }
    public NormalizedRegion Region { get; set; } = NormalizedRegion.FullScreen;
    public string EventType { get; set; } = "";
    public bool InvertMatch { get; set; }
    public bool RequireClear { get; set; }

    /// <summary>
    /// Backend-specific settings as a raw JSON object. Each detector backend
    /// deserializes this into its own typed POCO at <c>Initialize</c> time.
    /// </summary>
    public JsonElement Settings { get; set; }
}

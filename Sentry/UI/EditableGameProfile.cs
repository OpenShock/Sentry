using System.Text.Json;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.UI;

/// <summary>
/// UI-friendly version of GameProfile where detector settings use string dictionary
/// instead of JsonElement, making two-way binding straightforward.
/// </summary>
public sealed class EditableGameProfile
{
    public string Name { get; set; } = "";
    public string? ProcessName { get; set; }
    public bool PerRegionCapture { get; set; } = true;
    public bool ParallelDetection { get; set; }
    public List<EditableDetectorConfig> Detectors { get; set; } = [];
    public List<ActionMapping> Actions { get; set; } = [];

    public static EditableGameProfile From(GameProfile profile) => new()
    {
        Name = profile.Name,
        ProcessName = profile.ProcessName,
        PerRegionCapture = profile.PerRegionCapture,
        ParallelDetection = profile.ParallelDetection,
        Detectors = profile.Detectors.Select(EditableDetectorConfig.From).ToList(),
        Actions = profile.Actions.ToList()
    };

    public GameProfile ToGameProfile() => new()
    {
        Name = Name,
        ProcessName = ProcessName,
        PerRegionCapture = PerRegionCapture,
        ParallelDetection = ParallelDetection,
        Detectors = Detectors.Select(d => d.ToDetectorConfig()).ToList(),
        Actions = Actions.ToList()
    };
}

public sealed class EditableDetectorConfig
{
    public string Name { get; set; } = "";
    public DetectorBackendType Backend { get; set; }
    public NormalizedRegion Region { get; set; } = NormalizedRegion.FullScreen;
    public string EventType { get; set; } = "";
    public bool InvertMatch { get; set; }
    public Dictionary<string, string> RawSettings { get; set; } = new();

    // UI state (not serialized)
    internal bool _expanded;

    public static EditableDetectorConfig From(DetectorConfig config) => new()
    {
        Name = config.Name,
        Backend = config.Backend,
        Region = config.Region,
        EventType = config.EventType,
        InvertMatch = config.InvertMatch,
        RawSettings = config.Settings.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToString())
    };

    public DetectorConfig ToDetectorConfig() => new()
    {
        Name = Name,
        Backend = Backend,
        Region = Region,
        EventType = EventType,
        InvertMatch = InvertMatch,
        Settings = RawSettings.ToDictionary(
            kv => kv.Key,
            kv => JsonSerializer.SerializeToElement(ParseValue(kv.Value)))
    };

    private static object ParseValue(string value)
    {
        if (float.TryParse(value, out var f)) return f;
        if (int.TryParse(value, out var i)) return i;
        if (bool.TryParse(value, out var b)) return b;
        return value;
    }
}

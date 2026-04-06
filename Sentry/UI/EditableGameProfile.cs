using System.Text.Json;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.UI;

/// <summary>
/// UI-friendly version of GameProfile. Detector settings are deserialized from
/// the stored <see cref="JsonElement"/> into typed POCOs so the UI can two-way bind
/// to them, then serialized back when saving.
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
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string Name { get; set; } = "";
    public NormalizedRegion Region { get; set; } = NormalizedRegion.FullScreen;
    public string EventType { get; set; } = "";
    public bool InvertMatch { get; set; }
    public bool RequireClear { get; set; }

    /// <summary>
    /// Typed settings POCO for UI binding. Type is determined by <see cref="Backend"/>.
    /// </summary>
    public object Settings { get; private set; } = new OpenCvTemplateSettings();

    private DetectorBackendType _backend;
    public DetectorBackendType Backend
    {
        get => _backend;
        set
        {
            if (_backend == value && Settings.GetType() == GetSettingsType(value)) return;
            _backend = value;
            Settings = CreateDefaultSettings(value);
        }
    }

    // UI state (not serialized)
    internal bool _expanded;

    public static EditableDetectorConfig From(DetectorConfig config)
    {
        var edit = new EditableDetectorConfig
        {
            Name = config.Name,
            _backend = config.Backend,
            Region = config.Region,
            EventType = config.EventType,
            InvertMatch = config.InvertMatch,
            RequireClear = config.RequireClear
        };
        edit.Settings = config.Settings.ValueKind == JsonValueKind.Object
            ? config.Settings.Deserialize(GetSettingsType(config.Backend), JsonOptions)
              ?? CreateDefaultSettings(config.Backend)
            : CreateDefaultSettings(config.Backend);
        return edit;
    }

    public DetectorConfig ToDetectorConfig() => new()
    {
        Name = Name,
        Backend = Backend,
        Region = Region,
        EventType = EventType,
        InvertMatch = InvertMatch,
        RequireClear = RequireClear,
        Settings = JsonSerializer.SerializeToElement(Settings, Settings.GetType(), JsonOptions)
    };

    private static object CreateDefaultSettings(DetectorBackendType backend) => backend switch
    {
        DetectorBackendType.OpenCvTemplate => new OpenCvTemplateSettings(),
        DetectorBackendType.OpenCvSift => new OpenCvSiftSettings(),
        DetectorBackendType.Ocr => new OcrSettings(),
        DetectorBackendType.Onnx => new OnnxSettings(),
        _ => new OpenCvTemplateSettings()
    };

    private static Type GetSettingsType(DetectorBackendType backend) => backend switch
    {
        DetectorBackendType.OpenCvTemplate => typeof(OpenCvTemplateSettings),
        DetectorBackendType.OpenCvSift => typeof(OpenCvSiftSettings),
        DetectorBackendType.Ocr => typeof(OcrSettings),
        DetectorBackendType.Onnx => typeof(OnnxSettings),
        _ => typeof(OpenCvTemplateSettings)
    };
}

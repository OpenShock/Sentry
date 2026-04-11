using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection.Backends;

/// <summary>
/// Measures the fill ratio of a health/resource bar in grayscale and triggers
/// when the fill drops by at least <see cref="HealthBarSettings.MinDropDelta"/>
/// below a rolling baseline of recent maximum fill.
/// </summary>
public sealed class HealthBarDetector : IDetector
{
    private readonly ILogger<HealthBarDetector> _logger;
    private HealthBarSettings _settings = new();
    private bool _invertMatch;

    private float _baseline = -1f; // -1 = uninitialized
    private int _framesSinceTrigger;

    public string Name { get; private set; } = nameof(HealthBarDetector);

    public HealthBarDetector(ILogger<HealthBarDetector> logger)
    {
        _logger = logger;
    }

    public Task Initialize(DetectorConfig config, string profileBaseDir)
    {
        Name = config.Name;
        _settings = config.Settings.Deserialize<HealthBarSettings>(DetectorJsonOptions.Default)
                    ?? new HealthBarSettings();
        _invertMatch = config.InvertMatch;
        _baseline = -1f;

        _logger.LogInformation(
            "Initialized health bar detector '{Name}' ({Orientation}, threshold {Brightness}, drop {Drop:P0})",
            Name, _settings.Orientation, _settings.BrightnessThreshold, _settings.MinDropDelta);

        return Task.CompletedTask;
    }

    public DetectionResult Detect(Mat regionFrame)
    {
        if (regionFrame.Empty() || regionFrame.Width == 0 || regionFrame.Height == 0)
            return DetectionResult.NoMatch;

        var fill = MeasureFill(regionFrame);

        // Initialize / grow baseline.
        if (_baseline < 0f) _baseline = fill;
        else if (fill > _baseline) _baseline = fill;
        else if (_settings.BaselineDecay > 0f)
            _baseline -= (_baseline - fill) * _settings.BaselineDecay;

        var drop = _baseline - fill;
        var matched = drop >= _settings.MinDropDelta;
        var triggered = _invertMatch ? !matched : matched;

        if (matched)
        {
            // Reset baseline so we don't keep triggering until the bar refills.
            _baseline = fill;
            _framesSinceTrigger = 0;
        }
        else
        {
            _framesSinceTrigger++;
        }

        return new DetectionResult
        {
            Triggered = triggered,
            Confidence = Math.Clamp(drop / Math.Max(_settings.MinDropDelta, 0.0001f), 0f, 1f),
            Value = fill
        };
    }

    private float MeasureFill(Mat gray)
    {
        var threshold = (byte)Math.Clamp(_settings.BrightnessThreshold, 0, 255);
        var sliceFillRatio = Math.Clamp(_settings.SliceFillRatio, 0f, 1f);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, threshold, 255, ThresholdTypes.Binary);

        // Reduce along the perpendicular axis -> 1D vector of "filled pixels per slice".
        // Horizontal bar: reduce rows (axis 0) -> one value per column.
        // Vertical bar: reduce cols (axis 1) -> one value per row.
        var reduceDim = _settings.Orientation == HealthBarOrientation.Horizontal
            ? ReduceDimension.Row
            : ReduceDimension.Column;

        using var sums = new Mat();
        Cv2.Reduce(binary, sums, reduceDim, ReduceTypes.Sum, MatType.CV_32S);

        var slices = _settings.Orientation == HealthBarOrientation.Horizontal ? gray.Width : gray.Height;
        var perpendicular = _settings.Orientation == HealthBarOrientation.Horizontal ? gray.Height : gray.Width;
        if (slices <= 0 || perpendicular <= 0) return 0f;

        var requiredPerSlice = sliceFillRatio * perpendicular * 255f;

        var indexer = sums.GetGenericIndexer<int>();
        var filled = 0;
        for (var i = 0; i < slices; i++)
        {
            var v = _settings.Orientation == HealthBarOrientation.Horizontal
                ? indexer[0, i]
                : indexer[i, 0];
            if (v >= requiredPerSlice) filled++;
        }

        return filled / (float)slices;
    }

    public void Dispose()
    {
    }
}

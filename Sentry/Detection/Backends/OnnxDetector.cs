using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection.Backends;

/// <summary>
/// ONNX Runtime-based detector stub. Will run ML classification/object detection models
/// to detect complex game events that are hard to match with templates.
/// </summary>
public sealed class OnnxDetector : IDetector
{
    private readonly ILogger<OnnxDetector> _logger;

    public string Name { get; private set; } = nameof(OnnxDetector);

    public OnnxDetector(ILogger<OnnxDetector> logger)
    {
        _logger = logger;
    }

    public Task Initialize(DetectorConfig config, string profileBaseDir)
    {
        Name = config.Name;
        _logger.LogWarning("ONNX detector '{Name}' initialized as stub — not yet implemented", Name);
        return Task.CompletedTask;
    }

    public DetectionResult Detect(Mat regionFrame)
    {
        // TODO: Integrate Microsoft.ML.OnnxRuntime
        // 1. Preprocess regionFrame to model input tensor
        // 2. Run inference session
        // 3. Parse output for config.Settings["label"] with config.Settings["threshold"]
        return DetectionResult.NoMatch;
    }

    public void Dispose() { }
}

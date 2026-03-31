using Microsoft.Extensions.Logging;
using OpenShock.Sentry.Detection.Backends;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection;

public sealed class DetectorFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DetectorFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IDetector Create(DetectorConfig config, string profileBaseDir)
    {
        IDetector detector = config.Backend switch
        {
            DetectorBackendType.OpenCvTemplate => new OpenCvTemplateDetector(_loggerFactory.CreateLogger<OpenCvTemplateDetector>()),
            DetectorBackendType.OpenCvSift => new OpenCvSiftDetector(_loggerFactory.CreateLogger<OpenCvSiftDetector>()),
            DetectorBackendType.Ocr => new OcrDetector(_loggerFactory.CreateLogger<OcrDetector>()),
            DetectorBackendType.Onnx => new OnnxDetector(_loggerFactory.CreateLogger<OnnxDetector>()),
            _ => throw new ArgumentOutOfRangeException(nameof(config.Backend), config.Backend, "Unknown detector backend")
        };

        detector.Initialize(config, profileBaseDir);
        return detector;
    }
}

using Microsoft.Extensions.Logging;
using OpenShock.Sentry.Detection.Backends;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection;

public sealed class DetectorFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _moduleDataDir;

    public DetectorFactory(ILoggerFactory loggerFactory, string moduleDataDir)
    {
        _loggerFactory = loggerFactory;
        _moduleDataDir = moduleDataDir;
    }

    public async Task<IDetector> Create(DetectorConfig config, string profileBaseDir)
    {
        IDetector detector = config.Backend switch
        {
            DetectorBackendType.OpenCvTemplate => new OpenCvTemplateDetector(_loggerFactory.CreateLogger<OpenCvTemplateDetector>()),
            DetectorBackendType.OpenCvSift => new OpenCvSiftDetector(_loggerFactory.CreateLogger<OpenCvSiftDetector>()),
            DetectorBackendType.Ocr => new OcrDetector(_loggerFactory.CreateLogger<OcrDetector>(), Path.Combine(_moduleDataDir, "tessdata")),
            DetectorBackendType.Onnx => new OnnxDetector(_loggerFactory.CreateLogger<OnnxDetector>()),
            _ => throw new ArgumentOutOfRangeException(nameof(config.Backend), config.Backend, "Unknown detector backend")
        };

        await detector.Initialize(config, profileBaseDir);
        return detector;
    }
}

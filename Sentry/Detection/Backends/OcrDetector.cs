using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection.Backends;

/// <summary>
/// OCR-based detector stub. Will use text recognition + regex pattern matching
/// to detect text-based game events (e.g. "YOU DIED", "MISSION FAILED").
/// </summary>
public sealed class OcrDetector : IDetector
{
    private readonly ILogger<OcrDetector> _logger;

    public string Name { get; private set; } = nameof(OcrDetector);

    public OcrDetector(ILogger<OcrDetector> logger)
    {
        _logger = logger;
    }

    public void Initialize(DetectorConfig config, string profileBaseDir)
    {
        Name = config.Name;
        _logger.LogWarning("OCR detector '{Name}' initialized as stub — not yet implemented", Name);
    }

    public DetectionResult Detect(Mat regionFrame)
    {
        // TODO: Integrate Tesseract or PaddleOCR
        // 1. Run OCR on regionFrame
        // 2. Match extracted text against config.Settings["pattern"] regex
        // 3. Return detected if pattern matches
        return DetectionResult.NoMatch;
    }

    public void Dispose() { }
}

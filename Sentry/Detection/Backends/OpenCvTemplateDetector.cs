using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection.Backends;

/// <summary>
/// Fast template matching using Cv2.MatchTemplate.
/// Good for static HUD elements like health bars, icons, text overlays.
/// The template is resized to match the region's aspect ratio at detection time,
/// making this approach resolution-independent.
/// </summary>
public sealed class OpenCvTemplateDetector : IDetector
{
    private readonly ILogger<OpenCvTemplateDetector> _logger;
    private Mat? _template;
    private float _threshold = 0.8f;
    private bool _invertMatch;

    public string Name { get; private set; } = nameof(OpenCvTemplateDetector);

    public OpenCvTemplateDetector(ILogger<OpenCvTemplateDetector> logger)
    {
        _logger = logger;
    }

    public Task Initialize(DetectorConfig config, string profileBaseDir)
    {
        Name = config.Name;

        var settings = config.Settings.Deserialize<OpenCvTemplateSettings>(DetectorJsonOptions.Default)
            ?? throw new InvalidOperationException($"Detector '{Name}' missing OpenCvTemplate settings");

        if (string.IsNullOrEmpty(settings.TemplatePath))
            throw new InvalidOperationException($"Detector '{Name}' missing required setting 'templatePath'");

        var templatePath = settings.TemplatePath;
        if (!Path.IsPathRooted(templatePath))
            templatePath = Path.Combine(profileBaseDir, templatePath);

        _template = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
        if (_template.Empty())
            throw new FileNotFoundException($"Template image not found or empty: {templatePath}");

        _invertMatch = config.InvertMatch;
        _threshold = settings.Threshold;

        _logger.LogInformation("Initialized template detector '{Name}' with template {Path}, threshold {Threshold}",
            Name, templatePath, _threshold);

        return Task.CompletedTask;
    }

    public DetectionResult Detect(Mat regionFrame)
    {
        if (_template is null) return DetectionResult.NoMatch;

        // Resize template to fit within the region if it's larger
        var template = _template;
        if (template.Width > regionFrame.Width || template.Height > regionFrame.Height)
        {
            var scale = Math.Min(
                (float)regionFrame.Width / template.Width,
                (float)regionFrame.Height / template.Height);
            template = new Mat();
            Cv2.Resize(_template, template, new Size(
                (int)(_template.Width * scale),
                (int)(_template.Height * scale)));
        }

        if (template.Width > regionFrame.Width || template.Height > regionFrame.Height)
            return DetectionResult.NoMatch;

        using var result = new Mat();
        Cv2.MatchTemplate(regionFrame, template, result, TemplateMatchModes.CCoeffNormed);

        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);

        var confidence = (float)maxVal;
        var matched = confidence >= _threshold;
        var triggered = _invertMatch ? !matched : matched;

        if (template != _template)
            template.Dispose();

        return new DetectionResult
        {
            Triggered = triggered,
            Confidence = confidence,
            BoundingBox = matched
                ? new Rect(maxLoc.X, maxLoc.Y, _template.Width, _template.Height)
                : null
        };
    }

    public void Dispose()
    {
        _template?.Dispose();
    }
}

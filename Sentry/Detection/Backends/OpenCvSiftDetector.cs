using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection.Backends;

/// <summary>
/// SIFT feature-based matching. Scale and rotation invariant.
/// Best for detecting complex visual patterns that may appear at varying sizes.
/// </summary>
public sealed class OpenCvSiftDetector : IDetector
{
    private readonly ILogger<OpenCvSiftDetector> _logger;
    private SIFT? _sift;
    private Mat? _descTemplate;
    private KeyPoint[]? _kpTemplate;
    private Size _templateSize;
    private float _ratioThreshold = 0.5f;
    private int _minGoodMatches = 8;
    private bool _invertMatch;

    public string Name { get; private set; } = nameof(OpenCvSiftDetector);

    public OpenCvSiftDetector(ILogger<OpenCvSiftDetector> logger)
    {
        _logger = logger;
    }

    public Task Initialize(DetectorConfig config, string profileBaseDir)
    {
        Name = config.Name;

        var settings = config.Settings.Deserialize<OpenCvSiftSettings>(DetectorJsonOptions.Default)
            ?? throw new InvalidOperationException($"Detector '{Name}' missing OpenCvSift settings");

        if (string.IsNullOrEmpty(settings.TemplatePath))
            throw new InvalidOperationException($"Detector '{Name}' missing required setting 'templatePath'");

        var templatePath = settings.TemplatePath;
        if (!Path.IsPathRooted(templatePath))
            templatePath = Path.Combine(profileBaseDir, templatePath);

        using var templateMat = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
        if (templateMat.Empty())
            throw new FileNotFoundException($"Template image not found or empty: {templatePath}");

        _templateSize = templateMat.Size();

        _invertMatch = config.InvertMatch;
        _ratioThreshold = settings.RatioThreshold;
        _minGoodMatches = settings.MinGoodMatches;

        _sift = SIFT.Create();
        _descTemplate = new Mat();
        _sift.DetectAndCompute(templateMat, null, out _kpTemplate, _descTemplate);

        _logger.LogInformation(
            "Initialized SIFT detector '{Name}' with {KeypointCount} template keypoints",
            Name, _kpTemplate.Length);

        return Task.CompletedTask;
    }

    public DetectionResult Detect(Mat regionFrame)
    {
        if (_sift is null || _descTemplate is null || _kpTemplate is null)
            return DetectionResult.NoMatch;

        var descScene = new Mat();
        _sift.DetectAndCompute(regionFrame, null, out var kpScene, descScene);
        if (descScene.Empty())
        {
            descScene.Dispose();
            return DetectionResult.NoMatch;
        }

        using var indexParams = new KDTreeIndexParams(5);
        using var searchParams = new SearchParams(50);
        using var matcher = new FlannBasedMatcher(indexParams, searchParams);
        var knnMatches = matcher.KnnMatch(_descTemplate, descScene, k: 2);

        var good = knnMatches
            .Where(m => m.Length == 2 && m[0].Distance < _ratioThreshold * m[1].Distance)
            .Select(m => m[0])
            .ToArray();

        descScene.Dispose();

        if (good.Length < _minGoodMatches)
            return DetectionResult.NoMatch;

        // Compute homography to find bounding box
        var srcPts = good.Select(m => _kpTemplate[m.QueryIdx].Pt).ToArray();
        var dstPts = good.Select(m => kpScene[m.TrainIdx].Pt).ToArray();

        using var h = Cv2.FindHomography(
            InputArray.Create(srcPts),
            InputArray.Create(dstPts),
            HomographyMethods.Ransac);

        if (h.Empty())
            return DetectionResult.NoMatch;

        var corners = new[]
        {
            new Point2f(0, 0),
            new Point2f(_templateSize.Width, 0),
            new Point2f(_templateSize.Width, _templateSize.Height),
            new Point2f(0, _templateSize.Height)
        };
        var sceneCorners = Cv2.PerspectiveTransform(corners, h);
        var boundingRect = Cv2.BoundingRect(sceneCorners);

        var confidence = (float)good.Length / _kpTemplate.Length;

        return new DetectionResult
        {
            Triggered = !_invertMatch,
            Confidence = Math.Min(confidence, 1f),
            BoundingBox = boundingRect
        };
    }

    public void Dispose()
    {
        _sift?.Dispose();
        _descTemplate?.Dispose();
    }
}

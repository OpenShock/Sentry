using OpenCvSharp;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Detection;

public interface IDetector : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Initialize the detector with its configuration settings.
    /// Called once before detection starts.
    /// </summary>
    Task Initialize(DetectorConfig config, string profileBaseDir);

    /// <summary>
    /// Run detection on a cropped region of the screen frame.
    /// </summary>
    /// <param name="regionFrame">Cropped grayscale Mat of the detector's region</param>
    /// <returns>Detection result with confidence and optional bounding box</returns>
    DetectionResult Detect(Mat regionFrame);
}

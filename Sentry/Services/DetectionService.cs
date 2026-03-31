using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Detection;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Main detection loop. Captures the screen, runs all active detectors,
/// triggers shock actions, and publishes results for the UI.
/// </summary>
public sealed class DetectionService : IDisposable
{
    private readonly ILogger<DetectionService> _logger;
    private readonly ScreenCaptureService _screenCapture;
    private readonly DetectorFactory _detectorFactory;
    private readonly ShockTriggerService _shockTrigger;
    private readonly GameProfileManager _profileManager;
    private readonly PreviewService _previewService;

    private bool _running;
    private readonly List<(IDetector Detector, DetectorConfig Config)> _activeDetectors = [];
    private GameProfile? _activeProfile;
    private readonly ManualResetEventSlim _frameSignal = new(false);
    private Thread? _detectionThread;

    // Detection performance tracking
    private readonly Stopwatch _detectionSw = new();
    private readonly Stopwatch _detectionFpsTimer = new();
    private int _detectionFrameCount;

    // Observable state for UI
    public bool IsRunning => _running;
    public string? ActiveProfileName { get; private set; }
    public GameProfile? ActiveProfile => _activeProfile;
    public float DetectionFps { get; private set; }
    public float DetectionMs { get; private set; }
    public IReadOnlyList<(IDetector Detector, DetectorConfig Config)> ActiveDetectors => _activeDetectors;

    /// <summary>Recent detection events for the UI log (newest first, capped at 100)</summary>
    public ConcurrentQueue<DetectionLogEntry> DetectionLog { get; } = new();
    private const int MaxLogEntries = 100;

    /// <summary>Fired when detection state changes (new frame processed, event triggered, etc.)</summary>
    public event Action? OnStateChanged;

    public DetectionService(
        ILogger<DetectionService> logger,
        ScreenCaptureService screenCapture,
        DetectorFactory detectorFactory,
        ShockTriggerService shockTrigger,
        GameProfileManager profileManager,
        PreviewService previewService)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _detectorFactory = detectorFactory;
        _shockTrigger = shockTrigger;
        _profileManager = profileManager;
        _previewService = previewService;
    }

    public void LoadProfile(string profileName)
    {
        UnloadProfile();

        var profile = _profileManager.Load(profileName);
        if (profile is null)
            throw new InvalidOperationException($"Failed to load profile '{profileName}'");

        var baseDir = _profileManager.GetProfileBaseDir(profileName);

        foreach (var detectorConfig in profile.Detectors)
        {
            try
            {
                var detector = _detectorFactory.Create(detectorConfig, baseDir);
                _activeDetectors.Add((detector, detectorConfig));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create detector '{DetectorName}'", detectorConfig.Name);
            }
        }

        _activeProfile = profile;
        ActiveProfileName = profileName;

        // Set region overlays for preview
        UpdatePreviewOverlays();

        _logger.LogInformation("Loaded profile '{ProfileName}' with {DetectorCount} detectors",
            profileName, _activeDetectors.Count);
        OnStateChanged?.Invoke();
    }

    public void UnloadProfile()
    {
        foreach (var (detector, _) in _activeDetectors)
            detector.Dispose();
        _activeDetectors.Clear();
        _activeProfile = null;
        ActiveProfileName = null;
        _shockTrigger.ResetCooldowns();
        _previewService.ClearOverlays();
        OnStateChanged?.Invoke();
    }

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    public Task StartAsync(int screenWidth = 2560, int screenHeight = 1440)
    {
        if (IsRunning) return Task.CompletedTask;

        if (_activeProfile is null || _activeDetectors.Count == 0)
        {
            _logger.LogWarning("No profile loaded or no detectors configured. Cannot start detection.");
            return Task.CompletedTask;
        }

        _screenCapture.Initialize(screenWidth, screenHeight);
        _screenCapture.Start();
        _running = true;
        _detectionFpsTimer.Restart();
        _detectionFrameCount = 0;

        _screenCapture.OnNewFrame += SignalNewFrame;
        _detectionThread = new Thread(DetectionLoop) { IsBackground = true, Name = "DetectionLoop" };
        _detectionThread.Start();

        _logger.LogInformation("Starting detection for profile '{ProfileName}' at {Width}x{Height}",
            ActiveProfileName, screenWidth, screenHeight);

        OnStateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _screenCapture.OnNewFrame -= SignalNewFrame;
        _running = false;
        _frameSignal.Set(); // Unblock the thread so it can exit
        _detectionThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _detectionThread = null;
        _screenCapture.Stop();
        _logger.LogInformation("Detection stopped");
        OnStateChanged?.Invoke();
    }

    private void SignalNewFrame() => _frameSignal.Set();

    private void DetectionLoop()
    {
        while (_running)
        {
            _frameSignal.Wait();
            _frameSignal.Reset();

            if (!_running) break;

            try
            {
                var frame = _screenCapture.GetLatestFrame();
                if (frame is null) continue;
                var (_, grayFrame) = frame.Value;

                _detectionSw.Restart();

                var detectionOverlays = new List<DetectionOverlay>();
                var regionResults = new List<(bool Triggered, float Confidence)>();

                foreach (var (detector, config) in _activeDetectors)
                {
                    var pixelRect = config.Region.ToPixelRect(
                        _screenCapture.ScreenWidth,
                        _screenCapture.ScreenHeight);

                    pixelRect = ClampRect(pixelRect, grayFrame.Width, grayFrame.Height);
                    if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
                    {
                        regionResults.Add((false, 0f));
                        continue;
                    }

                    using var regionMat = new Mat(grayFrame, pixelRect);
                    var result = detector.Detect(regionMat);

                    var triggered = config.InvertMatch ? !result.Detected : result.Detected;
                    regionResults.Add((triggered, result.Confidence));

                    if (result.BoundingBox.HasValue)
                    {
                        var box = result.BoundingBox.Value;
                        detectionOverlays.Add(new DetectionOverlay
                        {
                            BoundingBox = new Rect(
                                pixelRect.X + box.X,
                                pixelRect.Y + box.Y,
                                box.Width, box.Height),
                            Triggered = triggered
                        });
                    }

                    if (triggered)
                    {
                        AddLogEntry(detector.Name, config.EventType, result.Confidence, config.InvertMatch);
                        _ = _shockTrigger.HandleDetection(config.EventType, _activeProfile!.Actions);
                    }
                }

                _previewService.SetDetectionOverlays(detectionOverlays);
                UpdatePreviewOverlays(regionResults);

                _detectionSw.Stop();
                DetectionMs = (float)_detectionSw.Elapsed.TotalMilliseconds;

                // FPS calculation
                _detectionFrameCount++;
                if (_detectionFpsTimer.ElapsedMilliseconds >= 1000)
                {
                    DetectionFps = _detectionFrameCount * 1000f / _detectionFpsTimer.ElapsedMilliseconds;
                    _detectionFrameCount = 0;
                    _detectionFpsTimer.Restart();
                    OnStateChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in detection");
            }
        }
    }

    private static readonly Scalar[] RegionColors =
    [
        new(255, 200, 0),   // Cyan
        new(0, 255, 128),   // Green
        new(128, 0, 255),   // Purple
        new(0, 128, 255),   // Orange
        new(255, 0, 128),   // Pink
    ];

    private void UpdatePreviewOverlays(IReadOnlyList<(bool Triggered, float Confidence)>? results = null)
    {
        var overlays = _activeDetectors.Select((d, i) =>
        {
            var overlay = new RegionOverlay
            {
                Region = d.Config.Region,
                Label = d.Config.Name,
                Color = RegionColors[i % RegionColors.Length]
            };

            if (results is not null && i < results.Count)
            {
                overlay.Triggered = results[i].Triggered;
                overlay.Confidence = results[i].Confidence;
            }

            return overlay;
        });

        _previewService.SetRegionOverlays(overlays);
    }

    private void AddLogEntry(string detectorName, GameEventType eventType, float confidence, bool inverted)
    {
        DetectionLog.Enqueue(new DetectionLogEntry
        {
            Timestamp = DateTime.Now,
            DetectorName = detectorName,
            EventType = eventType,
            Confidence = confidence,
            Inverted = inverted
        });

        // Trim old entries
        while (DetectionLog.Count > MaxLogEntries)
            DetectionLog.TryDequeue(out _);
    }

    private static Rect ClampRect(Rect rect, int maxWidth, int maxHeight)
    {
        var x = Math.Max(0, Math.Min(rect.X, maxWidth - 1));
        var y = Math.Max(0, Math.Min(rect.Y, maxHeight - 1));
        var w = Math.Min(rect.Width, maxWidth - x);
        var h = Math.Min(rect.Height, maxHeight - y);
        return new Rect(x, y, w, h);
    }

    public void Dispose()
    {
        Stop();
        UnloadProfile();
        _screenCapture.Dispose();
    }
}

public sealed class DetectionLogEntry
{
    public DateTime Timestamp { get; init; }
    public required string DetectorName { get; init; }
    public GameEventType EventType { get; init; }
    public float Confidence { get; init; }
    public bool Inverted { get; init; }
}

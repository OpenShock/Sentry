using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Config;
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

    private CancellationTokenSource? _detectionCts;
    private readonly List<(IDetector Detector, DetectorConfig Config)> _activeDetectors = [];
    private GameProfile? _activeProfile;
    private readonly SemaphoreSlim _frameSignal = new(0, 1);

    // Per-region capture resources (used when PerRegionCapture is true)
    private readonly List<RegionCapture?> _regionCaptures = [];

    // Pre-allocated overlay buffers to avoid per-frame allocations
    private readonly List<RegionOverlay> _regionOverlayBuffer = [];

    // Detection performance tracking
    private long _detectionFrameStart;
    private long _detectionFpsStart;
    private int _detectionFrameCount;

    // Reusable per-frame buffers (pre-allocated, indexed by detector — no contention in parallel)
    private readonly List<DetectionOverlay> _detectionOverlays = [];
    private DetectorTiming[] _detectorTimingsBuffer = [];
    private DetectorResult[] _detectorResults = [];

    /// <summary>
    /// When true, captures only each detector's region independently instead of reading from the full-screen buffer.
    /// </summary>
    public bool PerRegionCapture { get; set; } = true;

    /// <summary>Target FPS for the detection loop when using per-region capture.</summary>
    public int TargetFps { get; set; } = 30;

    // Observable state for UI
    public bool IsRunning => _detectionCts is not null && !_detectionCts.IsCancellationRequested;
    public string? ActiveProfileName { get; private set; }
    public GameProfile? ActiveProfile => _activeProfile;
    public float DetectionFps { get; private set; }
    public float DetectionMs { get; private set; }
    public IReadOnlyList<(IDetector Detector, DetectorConfig Config)> ActiveDetectors => _activeDetectors;

    /// <summary>Per-detector timing from the last frame (only populated in per-region mode).</summary>
    public DetectorTiming[] DetectorTimings => _detectorTimingsBuffer;

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

    public async Task LoadProfile(string profileName)
    {
        if (IsRunning) Stop();
        UnloadProfile();

        var profile = _profileManager.Load(profileName);
        if (profile is null)
            throw new InvalidOperationException($"Failed to load profile '{profileName}'");

        var baseDir = _profileManager.GetProfileBaseDir(profileName);

        foreach (var detectorConfig in profile.Detectors)
        {
            try
            {
                var detector = await _detectorFactory.Create(detectorConfig, baseDir);
                _activeDetectors.Add((detector, detectorConfig));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create detector '{DetectorName}'", detectorConfig.Name);
            }
        }

        _activeProfile = profile;
        ActiveProfileName = profileName;

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
    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        if (_activeProfile is null || _activeDetectors.Count == 0)
        {
            _logger.LogWarning("No profile loaded or no detectors configured. Cannot start detection.");
            return Task.CompletedTask;
        }

        _detectionFpsStart = Stopwatch.GetTimestamp();
        _detectionFrameCount = 0;
        _detectionCts = new CancellationTokenSource();
        var ct = _detectionCts.Token;

        // Pre-allocate per-detector buffers
        var count = _activeDetectors.Count;
        _detectorTimingsBuffer = new DetectorTiming[count];
        _detectorResults = new DetectorResult[count];
        for (var i = 0; i < count; i++)
            _detectorTimingsBuffer[i].Name = _activeDetectors[i].Config.Name;

        if (PerRegionCapture)
        {
            AllocateRegionCaptures();
            _ = Task.Run(() => DetectionLoopPerRegion(ct), ct);
        }
        else
        {
            _screenCapture.OnNewFrame += SignalNewFrame;
            _ = Task.Run(() => DetectionLoopFullScreen(ct), ct);
        }

        _logger.LogInformation(
            "Starting detection for profile '{ProfileName}' ({Mode} mode, {Count} detectors)",
            ActiveProfileName, PerRegionCapture ? "per-region" : "full-screen", count);

        OnStateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _screenCapture.OnNewFrame -= SignalNewFrame;
        _detectionCts?.Cancel();
        _detectionCts?.Dispose();
        _detectionCts = null;
        FreeRegionCaptures();
        _logger.LogInformation("Detection stopped");
        OnStateChanged?.Invoke();
    }

    private void SignalNewFrame()
    {
        if (_frameSignal.CurrentCount == 0)
            _frameSignal.Release();
    }

    // ── Region capture allocation ──────────────────────────────────────

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private void AllocateRegionCaptures()
    {
        FreeRegionCaptures();
        var srcX = _screenCapture.SourceX;
        var srcY = _screenCapture.SourceY;
        var w = _screenCapture.ScreenWidth;
        var h = _screenCapture.ScreenHeight;

        foreach (var (_, config) in _activeDetectors)
        {
            var pixelRect = config.Region.ToPixelRect(w, h);
            if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
            {
                _regionCaptures.Add(null);
                continue;
            }
            _regionCaptures.Add(new RegionCapture(
                srcX + pixelRect.X, srcY + pixelRect.Y,
                pixelRect.Width, pixelRect.Height));
        }
    }

    private void FreeRegionCaptures()
    {
        foreach (var rc in _regionCaptures)
            rc?.Dispose();
        _regionCaptures.Clear();
    }

    // ── Per-detector operations (safe for Parallel.For — each writes to its own index) ──

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private void CaptureAndDetectRegion(int i)
    {
        var capture = i < _regionCaptures.Count ? _regionCaptures[i] : null;
        if (capture is null)
        {
            _detectorResults[i] = default;
            return;
        }

        var (detector, config) = _activeDetectors[i];

        var start = Stopwatch.GetTimestamp();
        capture.CaptureFromScreen();
        _detectorTimingsBuffer[i].CaptureMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        start = Stopwatch.GetTimestamp();
        var result = detector.Detect(capture.GrayMat);
        _detectorTimingsBuffer[i].DetectMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _detectorResults[i] = new DetectorResult
        {
            Triggered = result.Triggered,
            Confidence = result.Confidence,
            BoundingBox = result.BoundingBox,
            Value = result.Value,
            Text = result.Text
        };
    }

    private void DetectFromFullFrame(int i, Mat grayFrame)
    {
        var (detector, config) = _activeDetectors[i];
        var pixelRect = config.Region.ToPixelRect(_screenCapture.ScreenWidth, _screenCapture.ScreenHeight);
        pixelRect = ClampRect(pixelRect, grayFrame.Width, grayFrame.Height);

        if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
        {
            _detectorResults[i] = default;
            return;
        }

        using var regionMat = new Mat(grayFrame, pixelRect);

        var start = Stopwatch.GetTimestamp();
        var result = detector.Detect(regionMat);
        _detectorTimingsBuffer[i].DetectMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _detectorResults[i] = new DetectorResult
        {
            Triggered = result.Triggered,
            Confidence = result.Confidence,
            BoundingBox = result.BoundingBox,
            Value = result.Value,
            Text = result.Text
        };
    }

    // ── Shared result processing (always sequential) ───────────────────

    private void ProcessDetectionResults()
    {
        _detectionOverlays.Clear();

        for (var i = 0; i < _activeDetectors.Count; i++)
        {
            var (detector, config) = _activeDetectors[i];
            ref var result = ref _detectorResults[i];

            if (result.BoundingBox.HasValue)
            {
                var pixelRect = config.Region.ToPixelRect(_screenCapture.ScreenWidth, _screenCapture.ScreenHeight);
                var box = result.BoundingBox.Value;
                _detectionOverlays.Add(new DetectionOverlay
                {
                    BoundingBox = new Rect(pixelRect.X + box.X, pixelRect.Y + box.Y, box.Width, box.Height),
                    Triggered = result.Triggered,
                });
            }

            if (result.Triggered)
            {
                AddLogEntry(detector.Name, config.EventType, result.Confidence);
                _ = _shockTrigger.HandleDetection(config.EventType, _activeProfile!.Actions);
            }
        }

        _previewService.SetDetectionOverlays(_detectionOverlays);
        UpdatePreviewOverlays();
    }

    // ── Detection loops ────────────────────────────────────────────────

    /// <summary>Full-screen mode: waits on ScreenCaptureService frames, crops regions from the full buffer.</summary>
    private async Task DetectionLoopFullScreen(CancellationToken ct)
    {
        var parallelDetect = _activeProfile!.ParallelDetection;
        var count = _activeDetectors.Count;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _frameSignal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var frame = _screenCapture.GetLatestFrame();
                if (frame is null) continue;
                var (_, grayFrame) = frame.Value;

                _detectionFrameStart = Stopwatch.GetTimestamp();

                // Phase: Detect (capture is shared via ScreenCaptureService)
                if (parallelDetect)
                    Parallel.For(0, count, i => DetectFromFullFrame(i, grayFrame));
                else
                    for (var i = 0; i < count; i++) DetectFromFullFrame(i, grayFrame);

                // Phase: Process results
                ProcessDetectionResults();
                FinishDetectionFrame();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in detection");
            }
        }
    }

    /// <summary>Per-region mode: captures only each detector's region directly via GDI. Own FPS limiter.</summary>
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private async Task DetectionLoopPerRegion(CancellationToken ct)
    {
        var parallel = _activeProfile!.ParallelDetection;
        var count = _activeDetectors.Count;
        var currentFps = TargetFps > 0 ? TargetFps : 60;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / currentFps));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            // Recreate timer if TargetFps changed
            var targetFps = TargetFps > 0 ? TargetFps : 60;
            if (targetFps != currentFps)
            {
                currentFps = targetFps;
                timer.Period = TimeSpan.FromMilliseconds(1000.0 / currentFps);
            }

            try
            {
                _detectionFrameStart = Stopwatch.GetTimestamp();

                // Capture + detect (each detector independently)
                if (parallel)
                    Parallel.For(0, count, CaptureAndDetectRegion);
                else
                    for (var i = 0; i < count; i++) CaptureAndDetectRegion(i);

                // Process results (always sequential — triggers, overlays, UI)
                ProcessDetectionResults();
                FinishDetectionFrame();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in detection");
            }
        }
    }

    // ── Frame bookkeeping ──────────────────────────────────────────────

    private void FinishDetectionFrame()
    {
        DetectionMs = (float)Stopwatch.GetElapsedTime(_detectionFrameStart).TotalMilliseconds;

        _detectionFrameCount++;
        var fpsElapsed = Stopwatch.GetElapsedTime(_detectionFpsStart);
        if (fpsElapsed.TotalMilliseconds >= 1000)
        {
            DetectionFps = _detectionFrameCount * 1000f / (float)fpsElapsed.TotalMilliseconds;
            _detectionFrameCount = 0;
            _detectionFpsStart = Stopwatch.GetTimestamp();
            OnStateChanged?.Invoke();
        }
    }

    // ── Preview overlays ───────────────────────────────────────────────

    private static readonly Scalar[] RegionColors =
    [
        new(255, 200, 0),   // Cyan
        new(0, 255, 128),   // Green
        new(128, 0, 255),   // Purple
        new(0, 128, 255),   // Orange
        new(255, 0, 128),   // Pink
    ];

    private void UpdatePreviewOverlays()
    {
        var hasResults = _detectorResults.Length == _activeDetectors.Count && IsRunning;

        // Rebuild buffer only when detector count changes
        if (_regionOverlayBuffer.Count != _activeDetectors.Count)
        {
            _regionOverlayBuffer.Clear();
            for (var i = 0; i < _activeDetectors.Count; i++)
            {
                var d = _activeDetectors[i];
                _regionOverlayBuffer.Add(new RegionOverlay
                {
                    Region = d.Config.Region,
                    Label = d.Config.Name,
                    Color = RegionColors[i % RegionColors.Length]
                });
            }
        }

        // Update mutable state in-place
        for (var i = 0; i < _regionOverlayBuffer.Count; i++)
        {
            var overlay = _regionOverlayBuffer[i];
            if (hasResults)
            {
                overlay.Triggered = _detectorResults[i].Triggered;
                overlay.Confidence = _detectorResults[i].Confidence;
                overlay.Text = _detectorResults[i].Text;
            }
            else
            {
                overlay.Triggered = null;
                overlay.Confidence = 0;
                overlay.Text = null;
            }
        }

        _previewService.SetRegionOverlays(_regionOverlayBuffer);
    }

    // ── Logging ────────────────────────────────────────────────────────

    private void AddLogEntry(string detectorName, string eventType, float confidence)
    {
        DetectionLog.Enqueue(new DetectionLogEntry
        {
            Timestamp = DateTime.Now,
            DetectorName = detectorName,
            EventType = eventType,
            Confidence = confidence
        });

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
    }
}

public sealed class DetectionLogEntry
{
    public DateTime Timestamp { get; init; }
    public required string DetectorName { get; init; }
    public required string EventType { get; init; }
    public float Confidence { get; init; }
}

public struct DetectorTiming
{
    public string Name;
    public float CaptureMs;
    public float DetectMs;
    public readonly float TotalMs => CaptureMs + DetectMs;
}

public struct DetectorResult
{
    public bool Triggered;
    public float Confidence;
    public Rect? BoundingBox;
    public float? Value;
    public string? Text;
}

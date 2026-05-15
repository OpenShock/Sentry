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
public sealed class DetectionService : IAsyncDisposable
{
    private readonly ILogger<DetectionService> _logger;
    private readonly ScreenCaptureService _screenCapture;
    private readonly DetectorFactory _detectorFactory;
    private readonly ShockTriggerService _shockTrigger;
    private readonly GameProfileManager _profileManager;
    private readonly PreviewService _previewService;

    private CancellationTokenSource? _detectionCts;
    private Task? _detectionLoop;
    private readonly List<ActiveDetector> _detectors = [];
    private GameProfile? _activeProfile;
    private readonly SemaphoreSlim _frameSignal = new(0, 1);

    // Pre-allocated overlay buffers to avoid per-frame allocations
    private readonly List<RegionOverlay> _regionOverlayBuffer = [];
    private readonly List<DetectionOverlay> _detectionOverlays = [];

    // Detection performance tracking
    private long _detectionFrameStart;
    private long _detectionFpsStart;
    private int _detectionFrameCount;

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
    public IReadOnlyList<ActiveDetector> Detectors => _detectors;

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
        _profileManager.ProfileRenamed += OnProfileRenamed;
    }

    private void OnProfileRenamed(string oldName, string newName)
    {
        if (ActiveProfileName == oldName)
        {
            ActiveProfileName = newName;
            OnStateChanged?.Invoke();
        }
    }

    public async Task LoadProfile(string profileName)
    {
        if (IsRunning) await StopAsync();
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
                _detectors.Add(new ActiveDetector(detector, detectorConfig));
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
            profileName, _detectors.Count);
        OnStateChanged?.Invoke();
    }

    public void UnloadProfile()
    {
        foreach (var d in _detectors)
            d.Dispose();
        _detectors.Clear();
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

        if (_activeProfile is null || _detectors.Count == 0)
        {
            _logger.LogWarning("No profile loaded or no detectors configured. Cannot start detection.");
            return Task.CompletedTask;
        }

        _detectionFpsStart = Stopwatch.GetTimestamp();
        _detectionFrameCount = 0;
        _detectionCts = new CancellationTokenSource();
        var ct = _detectionCts.Token;

        var count = _detectors.Count;

        if (PerRegionCapture)
        {
            AllocateRegionCaptures();
            _detectionLoop = Task.Run(() => DetectionLoopPerRegion(ct), ct);
        }
        else
        {
            _screenCapture.OnNewFrame += SignalNewFrame;
            _detectionLoop = Task.Run(() => DetectionLoopFullScreen(ct), ct);
        }

        _logger.LogInformation(
            "Starting detection for profile '{ProfileName}' ({Mode} mode, {Count} detectors)",
            ActiveProfileName, PerRegionCapture ? "per-region" : "full-screen", count);

        OnStateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _screenCapture.OnNewFrame -= SignalNewFrame;
        _detectionCts?.Cancel();

        // Wait for the detection loop to finish before disposing resources it uses
        if (_detectionLoop is not null)
        {
            try { await _detectionLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Detection loop exited with error"); }
            _detectionLoop = null;
        }

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

    // ── Region capture allocation ─────��────────────────────────────────

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private void AllocateRegionCaptures()
    {
        FreeRegionCaptures();
        var srcX = _screenCapture.SourceX;
        var srcY = _screenCapture.SourceY;
        var w = _screenCapture.ScreenWidth;
        var h = _screenCapture.ScreenHeight;

        foreach (var d in _detectors)
        {
            var pixelRect = d.Config.Region.ToPixelRect(w, h);
            if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
            {
                d.RegionCapture = null;
                continue;
            }
            d.RegionCapture = new RegionCapture(
                srcX + pixelRect.X, srcY + pixelRect.Y,
                pixelRect.Width, pixelRect.Height);
        }
    }

    private void FreeRegionCaptures()
    {
        foreach (var d in _detectors)
        {
            d.RegionCapture?.Dispose();
            d.RegionCapture = null;
        }
    }

    // ── Per-detector operations (safe for Parallel.For — each writes to its own slot) ──

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private void CaptureAndDetectRegion(int i)
    {
        var d = _detectors[i];
        if (d.RegionCapture is null)
        {
            d.ClearResult();
            return;
        }

        var start = Stopwatch.GetTimestamp();
        d.RegionCapture.CaptureFromScreen();
        d.CaptureMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        start = Stopwatch.GetTimestamp();
        var result = d.Detector.Detect(d.RegionCapture.GrayMat);
        d.DetectMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        d.SetResult(result);
    }

    private void DetectFromFullFrame(int i, Mat grayFrame)
    {
        var d = _detectors[i];
        var pixelRect = d.Config.Region.ToPixelRect(_screenCapture.ScreenWidth, _screenCapture.ScreenHeight);
        pixelRect = ClampRect(pixelRect, grayFrame.Width, grayFrame.Height);

        if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
        {
            d.ClearResult();
            return;
        }

        using var regionMat = new Mat(grayFrame, pixelRect);

        var start = Stopwatch.GetTimestamp();
        var result = d.Detector.Detect(regionMat);
        d.DetectMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        d.SetResult(result);
    }

    // ── Shared result processing (always sequential) ───────────────────

    private void ProcessDetectionResults()
    {
        _detectionOverlays.Clear();
        var anyEdgeChange = false;

        foreach (var d in _detectors)
        {
            if (d.BoundingBox.HasValue)
            {
                var pixelRect = d.Config.Region.ToPixelRect(_screenCapture.ScreenWidth, _screenCapture.ScreenHeight);
                var box = d.BoundingBox.Value;
                _detectionOverlays.Add(new DetectionOverlay
                {
                    BoundingBox = new Rect(pixelRect.X + box.X, pixelRect.Y + box.Y, box.Width, box.Height),
                    Triggered = d.Triggered,
                });
            }

            var rising = d.Triggered && !d.PreviouslyTriggered;
            var falling = !d.Triggered && d.PreviouslyTriggered;
            if (rising || falling) anyEdgeChange = true;

            if (rising)
            {
                // Open a new log span on rising edge.
                d.CurrentLogEntry = new DetectionLogEntry
                {
                    StartedAt = DateTime.Now,
                    DetectorName = d.Detector.Name,
                    EventType = d.Config.EventType,
                    PeakConfidence = d.Confidence
                };
                DetectionLog.Enqueue(d.CurrentLogEntry);
                while (DetectionLog.Count > MaxLogEntries) DetectionLog.TryDequeue(out _);
            }
            else if (d.Triggered && d.CurrentLogEntry is not null)
            {
                if (d.Confidence > d.CurrentLogEntry.PeakConfidence)
                    d.CurrentLogEntry.PeakConfidence = d.Confidence;
            }
            else if (falling && d.CurrentLogEntry is not null)
            {
                d.CurrentLogEntry.EndedAt = DateTime.Now;
                d.CurrentLogEntry = null;
            }

            if (d.Triggered)
            {
                // If RequireClear is set, only fire on rising edge (must clear before re-triggering)
                var shouldFire = !d.Config.RequireClear || rising;
                if (shouldFire)
                    _ = _shockTrigger.HandleDetection(d.Config.EventType, _activeProfile!.Actions);
            }
            d.PreviouslyTriggered = d.Triggered;
        }

        _previewService.SetDetectionOverlays(_detectionOverlays);
        UpdatePreviewOverlays();

        if (anyEdgeChange)
            OnStateChanged?.Invoke();
    }

    // ── Detection loops ────────────────────────────────────────────────

    /// <summary>Full-screen mode: waits on ScreenCaptureService frames, crops regions from the full buffer.</summary>
    private async Task DetectionLoopFullScreen(CancellationToken ct)
    {
        var parallelDetect = _activeProfile!.ParallelDetection;
        var count = _detectors.Count;

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
        var count = _detectors.Count;
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

    // ── Frame bookkeeping ────────────────────��─────────────────────────

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
        }
    }

    // ── Preview overlays ���─────────────────────────────���────────────────

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
        var hasResults = IsRunning;

        // Rebuild buffer only when detector count changes
        if (_regionOverlayBuffer.Count != _detectors.Count)
        {
            _regionOverlayBuffer.Clear();
            for (var i = 0; i < _detectors.Count; i++)
            {
                var d = _detectors[i];
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
                overlay.Triggered = _detectors[i].Triggered;
                overlay.Confidence = _detectors[i].Confidence;
                overlay.Text = _detectors[i].Text;
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

    // ── Logging ──────��────────��────────────────────────────────────────

    private static Rect ClampRect(Rect rect, int maxWidth, int maxHeight)
    {
        var x = Math.Max(0, Math.Min(rect.X, maxWidth - 1));
        var y = Math.Max(0, Math.Min(rect.Y, maxHeight - 1));
        var w = Math.Min(rect.Width, maxWidth - x);
        var h = Math.Min(rect.Height, maxHeight - y);
        return new Rect(x, y, w, h);
    }

    public async ValueTask DisposeAsync()
    {
        _profileManager.ProfileRenamed -= OnProfileRenamed;
        await StopAsync();
        UnloadProfile();
    }
}

public sealed class DetectionLogEntry
{
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; set; }
    public required string DetectorName { get; init; }
    public required string EventType { get; init; }
    public float PeakConfidence { get; set; }

    public TimeSpan Duration => (EndedAt ?? DateTime.Now) - StartedAt;
    public bool IsActive => EndedAt is null;
}

/// <summary>
/// Holds everything related to a single active detector: the detector instance,
/// its config, per-region capture, per-frame results, and runtime state.
/// </summary>
public sealed class ActiveDetector : IDisposable
{
    public IDetector Detector { get; }
    public DetectorConfig Config { get; }

    // Per-region capture (owned, nullable — only used in per-region mode)
    public RegionCapture? RegionCapture { get; set; }

    // Per-frame timing
    public float CaptureMs { get; set; }
    public float DetectMs { get; set; }
    public float TotalMs => CaptureMs + DetectMs;

    // Per-frame result
    public bool Triggered { get; private set; }
    public float Confidence { get; private set; }
    public Rect? BoundingBox { get; private set; }
    public float? Value { get; private set; }
    public string? Text { get; private set; }

    // Runtime state
    public bool PreviouslyTriggered { get; set; }
    public DetectionLogEntry? CurrentLogEntry { get; set; }

    public ActiveDetector(IDetector detector, DetectorConfig config)
    {
        Detector = detector;
        Config = config;
    }

    public void SetResult(DetectionResult result)
    {
        Triggered = result.Triggered;
        Confidence = result.Confidence;
        BoundingBox = result.BoundingBox;
        Value = result.Value;
        Text = result.Text;
    }

    public void ClearResult()
    {
        Triggered = false;
        Confidence = 0;
        BoundingBox = null;
        Value = null;
        Text = null;
    }

    public void Dispose()
    {
        Detector.Dispose();
        RegionCapture?.Dispose();
    }
}

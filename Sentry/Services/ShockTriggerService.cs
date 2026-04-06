using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenShock.Desktop.ModuleBase.Api;
using OpenShock.Desktop.ModuleBase.Config;
using OpenShock.Desktop.ModuleBase.Models;
using OpenShock.Sentry.Config;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Maps detection events to OpenShock control commands, enforcing cooldowns.
/// </summary>
public sealed class ShockTriggerService
{
    private readonly ILogger<ShockTriggerService> _logger;
    private readonly IOpenShockService _openShock;
    private readonly IModuleConfig<SentryConfig> _moduleConfig;
    private readonly ConcurrentDictionary<string, DateTime> _lastTriggered = new();

    public ShockTriggerService(ILogger<ShockTriggerService> logger, IOpenShockService openShock, IModuleConfig<SentryConfig> moduleConfig)
    {
        _logger = logger;
        _openShock = openShock;
        _moduleConfig = moduleConfig;
    }

    public async Task HandleDetection(string eventType, IReadOnlyList<ActionMapping> actions)
    {
        var mapping = actions.FirstOrDefault(a => a.EventType == eventType);
        if (mapping is null) return;

        // Enforce cooldown
        var now = DateTime.UtcNow;
        if (_lastTriggered.TryGetValue(eventType, out var lastTime)
            && (now - lastTime).TotalMilliseconds < mapping.CooldownMs)
        {
            return;
        }

        _lastTriggered[eventType] = now;

        var cfg = _moduleConfig.Config;
        var intensity = (byte)Math.Clamp(mapping.Intensity, cfg.MinIntensity, cfg.MaxIntensity);
        var duration = (ushort)Math.Clamp(mapping.Duration, cfg.MinDuration, cfg.MaxDuration);

        _logger.LogInformation(
            "Triggering {ControlType} for event {EventType}: intensity={Intensity}, duration={Duration}ms",
            mapping.ControlType, eventType, intensity, duration);

        try
        {
            _openShock.Control.ControlAllShockers(intensity, mapping.ControlType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send control command for event {EventType}", eventType);
        }
    }

    public void ResetCooldowns() => _lastTriggered.Clear();
}

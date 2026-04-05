using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenShock.Desktop.ModuleBase.Api;
using OpenShock.Desktop.ModuleBase.Models;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Maps detection events to OpenShock control commands, enforcing cooldowns.
/// </summary>
public sealed class ShockTriggerService
{
    private readonly ILogger<ShockTriggerService> _logger;
    private readonly IOpenShockService _openShock;
    private readonly ConcurrentDictionary<string, DateTime> _lastTriggered = new();

    public ShockTriggerService(ILogger<ShockTriggerService> logger, IOpenShockService openShock)
    {
        _logger = logger;
        _openShock = openShock;
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

        _logger.LogInformation(
            "Triggering {ControlType} for event {EventType}: intensity={Intensity}, duration={Duration}ms",
            mapping.ControlType, eventType, mapping.Intensity, mapping.Duration);

        try
        {
            _openShock.Control.ControlAllShockers(mapping.Intensity, mapping.ControlType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send control command for event {EventType}", eventType);
        }
    }

    public void ResetCooldowns() => _lastTriggered.Clear();
}

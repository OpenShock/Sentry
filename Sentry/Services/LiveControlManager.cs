﻿using Microsoft.Extensions.Logging;
using OpenShock.SDK.CSharp.Hub;
using OpenShock.SDK.CSharp.Hub.Models;
using OpenShock.SDK.CSharp.Live;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Sentry.Backend;
using OpenShock.Sentry.Config;

namespace OpenShock.Sentry.Services;

public sealed class LiveControlManager
{
    private readonly ILogger<LiveControlManager> _logger;
    private readonly OpenShockApi _api;
    private readonly ConfigManager _configManager;
    private readonly ILogger<OpenShockLiveControlClient> _liveControlLogger;
    private readonly OpenShockHubClient _hubClient;
    private readonly OpenShockApi _apiClient;
    private readonly SemaphoreSlim _refreshLock = new(1, maxCount: 1);

    public LiveControlManager(
        ILogger<LiveControlManager> logger,
        OpenShockApi api, 
        ConfigManager configManager,
        ILogger<OpenShockLiveControlClient> liveControlLogger,
        OpenShockHubClient hubClient,
        OpenShockApi apiClient)
    {
        _logger = logger;
        _api = api;
        _configManager = configManager;
        _liveControlLogger = liveControlLogger;
        _hubClient = hubClient;
        _apiClient = apiClient;

        _hubClient.OnDeviceStatus += HubClientOnDeviceStatus;
        _hubClient.OnDeviceUpdate += HubClientOnDeviceUpdate;
    }
    
    public event Func<Task>? OnStateUpdated;

    private async Task HubClientOnDeviceUpdate(Guid device, DeviceUpdateType type)
    {
        _logger.LogDebug("Device update received, updating shockers and refreshing connections");
        
        await _apiClient.RefreshShockers();
        await RefreshConnections();
    }

    private async Task HubClientOnDeviceStatus(IEnumerable<DeviceOnlineState> deviceStatus)
    {
        _logger.LogDebug("Device status received, refreshing connections");
        await RefreshConnections();
    }

    public Dictionary<Guid, OpenShockLiveControlClient> LiveControlClients { get; } = new();

    public async Task RefreshConnections()
    {
        await _refreshLock.WaitAsync();
        try
        {
            await RefreshInternal();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshInternal()
    {
        _logger.LogDebug("Refreshing live control connections");

        // Remove devices that dont exist anymore
        foreach (var liveControlClient in LiveControlClients)
        {
            if (_api.Devices.Any(x => x.Id == liveControlClient.Key)) continue;
            if (!LiveControlClients.Remove(liveControlClient.Key, out var removedClient))
                await removedClient!.DisposeAsync();
        }

        foreach (var device in _api.Devices)
        {
            if (LiveControlClients.ContainsKey(device.Id)) continue;

            _logger.LogTrace("Creating live control client for device [{DeviceId}]", device.Id);

            _logger.LogTrace("Getting device gateway for device [{DeviceId}]", device.Id);
            var deviceGateway = await _apiClient.GetDeviceGateway(device.Id);

            deviceGateway.Switch(success =>
                {
                    var gateway = success.Value;
                    _logger.LogTrace("Got device gateway for device [{DeviceId}] [{Gateway}]", device.Id,
                        gateway.Gateway);

                    var client = new OpenShockLiveControlClient(gateway.Gateway, device.Id,
                        _configManager.Config.OpenShock.Token, _liveControlLogger);
                    LiveControlClients.Add(device.Id, client);

                    client.State.OnValueChanged += async state =>
                    {
                        _logger.LogTrace("Live control client for device [{DeviceId}] status updated {Status}",
                            device.Id, state);
                        await OnStateUpdated.Raise();
                    };
                    
                    client.OnDeviceNotConnected += async () =>
                    {
                        _logger.LogInformation("Live control client for device [{DeviceId}] ending, device disconnected", device.Id);
                        // Dispose the client, so it gets removed from the list and co
                        await client.DisposeAsync();
                    };

                    // When the client shuts down, remove it from the list
                    client.OnDispose += async () =>
                    {
                        _logger.LogTrace("Live control client for device [{DeviceId}] disposed, removing from list",
                            device.Id);
                        if (!LiveControlClients.Remove(device.Id, out var removedClient)) return;
                        await removedClient.DisposeAsync(); // Dispose incase it was not disposed

                        await OnStateUpdated.Raise();
                    };

                    client.InitializeAsync();
                },
                found =>
                {
                    _logger.LogError(
                        "Failed to get device gateway for device [{DeviceId}], not found or no permission",
                        device.Id);
                },
                offline =>
                {
                    _logger.LogInformation("Failed to get device gateway for device [{DeviceId}], device offline",
                        device.Id);
                },
                gateway =>
                {
                    _logger.LogError(
                        "Failed to get device gateway for device [{DeviceId}], " +
                        "the device is online but its not connected to a gateway, this means the device is probably" +
                        " outdated and does not support live control. Please upgrade your device",
                        device.Id);
                }, unauthenticated =>
                {
                    _logger.LogError(
                        "Failed to get device gateway for device [{DeviceId}], we are not authenticated",
                        device.Id);
                    // TODO: Handle unauthenticated globally
                });
        }

        await OnStateUpdated.Raise();
    }
}
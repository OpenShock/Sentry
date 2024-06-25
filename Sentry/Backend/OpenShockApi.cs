﻿using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;
using OpenShock.SDK.CSharp;
using OpenShock.SDK.CSharp.Models;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Sentry.Config;

namespace OpenShock.Sentry.Backend;

public sealed class OpenShockApi
{
    private readonly ILogger<OpenShockApi> _logger;
    private readonly ConfigManager _configManager;
    private OpenShockApiClient? _client = null;

    /// <summary>
    /// DI constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="configManager"></param>
    public OpenShockApi(ILogger<OpenShockApi> logger, ConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
        SetupApiClient();
    }

    public void SetupApiClient()
    {
        _client = new OpenShockApiClient(new ApiClientOptions
        {
            Server = _configManager.Config.OpenShock.Backend,
            Token = _configManager.Config.OpenShock.Token
        });
    }
    
    public event Func<IReadOnlyCollection<ShockerResponse>, Task>? OnShockersUpdated; 

    public IReadOnlyCollection<ResponseDeviceWithShockers> Devices = Array.Empty<ResponseDeviceWithShockers>(); 
    public IReadOnlyCollection<ShockerResponse> Shockers = Array.Empty<ShockerResponse>();

    public async Task RefreshShockers()
    {
        if (_client == null)
        {
            _logger.LogError("Client is not initialized!");
            throw new Exception("Client is not initialized!");
        }
        var response = await _client.GetOwnShockers();
        
        response.Switch(success =>
            {
                Devices = success.Value;
                Shockers = success.Value.SelectMany(x => x.Shockers).ToArray();
                
                // re-populate config with previous data if present, this also deletes any shockers that are no longer present
                var shockerList = new Dictionary<Guid, OpenShockConf.ShockerConf>();
                foreach (var shocker in Shockers)
                {
                    var enabled = true;
                
                    if (_configManager.Config.OpenShock.Shockers.TryGetValue(shocker.Id, out var confShocker))
                    {
                        enabled = confShocker.Enabled;
                    }

                    shockerList.Add(shocker.Id, new OpenShockConf.ShockerConf
                    {
                        Enabled = enabled
                    });
                }
                _configManager.Config.OpenShock.Shockers = shockerList;
                _configManager.Save();
                OnShockersUpdated.Raise(Shockers);
            },
        error =>
        {
            _logger.LogError("We are not authenticated with the OpenShock API!");
            // TODO: handle unauthenticated error
        });
    }

    public void Logout()
    {
        Devices = Array.Empty<ResponseDeviceWithShockers>();
        Shockers = Array.Empty<ShockerResponse>();
        
        OnShockersUpdated.Raise(Shockers);
    }

    public
        Task<OneOf<Success<LcgResponse>, NotFound, DeviceOffline, DeviceNotConnectedToGateway, UnauthenticatedError>>
        GetDeviceGateway(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (_client == null)
        {
            _logger.LogError("Client is not initialized!");
            throw new Exception("Client is not initialized!");
        }
        
        return _client.GetDeviceGateway(deviceId, cancellationToken);
    }
        
}
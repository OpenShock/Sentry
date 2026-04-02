using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenShock.Sentry.Models;

namespace OpenShock.Sentry.Services;

/// <summary>
/// Manages loading, saving, and listing game profiles from the profiles directory.
/// </summary>
public sealed class GameProfileManager : IDisposable
{
    private readonly ILogger<GameProfileManager> _logger;
    private readonly string _profilesDir;
    private readonly Timer _saveTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private string? _pendingSaveName;
    private GameProfile? _pendingSaveProfile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public GameProfileManager(ILogger<GameProfileManager> logger, string profilesDir)
    {
        _logger = logger;
        _profilesDir = profilesDir;
        Directory.CreateDirectory(_profilesDir);
        _saveTimer = new Timer(_ => _ = SaveNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<string> ListProfiles()
    {
        return Directory.GetFiles(_profilesDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();
    }

    public GameProfile? Load(string profileName)
    {
        var path = Path.Combine(_profilesDir, $"{profileName}.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("Profile '{ProfileName}' not found at {Path}", profileName, path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GameProfile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load profile '{ProfileName}'", profileName);
            return null;
        }
    }

    public void Save(string profileName, GameProfile profile)
    {
        var path = Path.Combine(_profilesDir, $"{profileName}.json");
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
        _logger.LogInformation("Saved profile '{ProfileName}' to {Path}", profileName, path);
    }

    /// <summary>
    /// Queues a save with a 1-second debounce. Repeated calls reset the timer.
    /// </summary>
    public void SaveDebounced(string profileName, GameProfile profile)
    {
        lock (_saveTimer)
        {
            _pendingSaveName = profileName;
            _pendingSaveProfile = profile;
            _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SaveNowAsync()
    {
        string? name;
        GameProfile? profile;

        lock (_saveTimer)
        {
            name = _pendingSaveName;
            profile = _pendingSaveProfile;
            _pendingSaveName = null;
            _pendingSaveProfile = null;
        }

        if (name is null || profile is null) return;

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Save(name, profile);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Delete(string profileName)
    {
        var path = Path.Combine(_profilesDir, $"{profileName}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted profile '{ProfileName}'", profileName);
        }
    }

    public string GetProfileBaseDir(string profileName)
    {
        var dir = Path.Combine(_profilesDir, profileName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        _saveLock.Dispose();
    }
}

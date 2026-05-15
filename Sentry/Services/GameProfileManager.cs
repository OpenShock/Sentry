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

    private string ProfilePath(string profileName) =>
        Path.Combine(_profilesDir, profileName, "profile.json");

    public IReadOnlyList<string> ListProfiles()
    {
        return Directory.GetDirectories(_profilesDir)
            .Where(d => File.Exists(Path.Combine(d, "profile.json")))
            .Select(d => Path.GetFileName(d)!)
            .ToList();
    }

    public GameProfile? Load(string profileName)
    {
        var path = ProfilePath(profileName);
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
        var path = ProfilePath(profileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
        _logger.LogInformation("Saved profile '{ProfileName}' to {Path}", profileName, path);
        CleanupProfileAssets(profileName, profile);
    }

    /// <summary>
    /// Removes any file in the profile's base directory that isn't referenced by
    /// a detector setting. Referenced = some setting string value matches the file
    /// name or its full path (case-insensitive).
    /// </summary>
    private void CleanupProfileAssets(string profileName, GameProfile profile)
    {
        var baseDir = Path.Combine(_profilesDir, profileName, "assets");
        if (!Directory.Exists(baseDir)) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var det in profile.Detectors)
        {
            foreach (var asset in GetReferencedAssets(det))
            {
                if (string.IsNullOrEmpty(asset)) continue;
                var resolved = Path.IsPathRooted(asset) ? asset : Path.Combine(baseDir, asset);
                try { referenced.Add(Path.GetFullPath(resolved)); }
                catch { /* ignore malformed paths */ }
            }
        }

        foreach (var file in Directory.EnumerateFiles(baseDir))
        {
            var full = Path.GetFullPath(file);
            if (referenced.Contains(full)) continue;
            try
            {
                File.Delete(file);
                _logger.LogInformation("Removed unreferenced asset '{File}' from profile '{ProfileName}'", Path.GetFileName(file), profileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete unreferenced asset '{File}'", file);
            }
        }
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
        var dir = Path.Combine(_profilesDir, profileName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted profile '{ProfileName}'", profileName);
        }
    }

    /// <summary>
    /// Raised after a successful rename so other services (e.g. DetectionService)
    /// can update any state keyed by the old name.
    /// </summary>
    public event Action<string, string>? ProfileRenamed;

    public enum RenameResult
    {
        Success,
        NoChange,
        InvalidName,
        SourceMissing,
        TargetExists
    }

    /// <summary>
    /// Renames a profile by moving its directory. Any pending debounced save
    /// targeting the old name is redirected to the new name.
    /// </summary>
    public RenameResult Rename(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return RenameResult.NoChange;
        if (!IsValidProfileName(newName)) return RenameResult.InvalidName;

        var srcDir = Path.Combine(_profilesDir, oldName);
        var dstDir = Path.Combine(_profilesDir, newName);

        if (!Directory.Exists(srcDir)) return RenameResult.SourceMissing;
        if (Directory.Exists(dstDir)) return RenameResult.TargetExists;

        lock (_saveTimer)
        {
            if (_pendingSaveName == oldName) _pendingSaveName = newName;
        }

        _saveLock.Wait();
        try
        {
            Directory.Move(srcDir, dstDir);
        }
        finally
        {
            _saveLock.Release();
        }

        _logger.LogInformation("Renamed profile '{OldName}' → '{NewName}'", oldName, newName);
        ProfileRenamed?.Invoke(oldName, newName);
        return RenameResult.Success;
    }

    public static bool IsValidProfileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    private static IEnumerable<string> GetReferencedAssets(DetectorConfig det)
    {
        if (det.Settings.ValueKind != JsonValueKind.Object) return [];
        return det.Backend switch
        {
            DetectorBackendType.OpenCvTemplate =>
                [det.Settings.Deserialize<OpenCvTemplateSettings>(JsonOptions)?.TemplatePath ?? ""],
            DetectorBackendType.OpenCvSift =>
                [det.Settings.Deserialize<OpenCvSiftSettings>(JsonOptions)?.TemplatePath ?? ""],
            DetectorBackendType.Onnx =>
                [det.Settings.Deserialize<OnnxSettings>(JsonOptions)?.ModelPath ?? ""],
            _ => []
        };
    }

    public string GetProfileBaseDir(string profileName)
    {
        var dir = Path.Combine(_profilesDir, profileName, "assets");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        _saveLock.Dispose();
    }
}

﻿using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenShock.SDK.CSharp.Updatables;
using OpenShock.Sentry.Config;
using OpenShock.Sentry.Utils;
using Semver;

namespace OpenShock.Sentry.Services;

public sealed class Updater
{
    private const string GithubReleasesUrl = "https://api.github.com/repos/OpenShock/Sentry/releases";
    private const string GithubLatest = $"{GithubReleasesUrl}/latest";
    private const string SetupFileName = "Sentry_Setup.exe";

    private static readonly HttpClient HttpClient = new();

    private readonly string _setupFilePath = Path.Combine(Path.GetTempPath(), SetupFileName);

    private static readonly SemVersion CurrentVersion = SemVersion.Parse(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
        SemVersionStyles.Strict);

    private Uri? ReleaseDownloadUrl { get; set; }

    private readonly ILogger<Updater> _logger;
    private readonly ConfigManager _configManager;

    public UpdatableVariable<bool> CheckingForUpdate { get; } = new(false);
    public UpdatableVariable<bool> UpdateAvailable { get; } = new(false);
    public bool IsPostponed { get; private set; }
    public SemVersion? LatestVersion { get; private set; }
    public UpdatableVariable<double> DownloadProgress { get; } = new(0);


    public Updater(ILogger<Updater> logger, ConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"OpenShock.Sentry/{CurrentVersion}");
    }

    private static bool TryDeleteFile(string fileName)
    {
        if (!File.Exists(fileName)) return false;
        try
        {
            File.Delete(fileName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<(SemVersion, GithubReleaseResponse.Asset)?> GetRelease()
    {
        var updateChannel = _configManager.Config.App.UpdateChannel;
        _logger.LogInformation("Checking GitHub for updates on channel {UpdateChannel}", updateChannel);

        try
        {
            var release = updateChannel switch
            {
                UpdateChannel.Release => await GetLatestRelease(),
                UpdateChannel.PreRelease => await GetPreRelease(),
                _ => null
            };

            if (release == null)
            {
                _logger.LogError("Failed to get latest version information from GitHub");
                return null;
            }

            var asset = release.Value.Item1.Assets.FirstOrDefault(x =>
                x.Name.Equals(SetupFileName, StringComparison.InvariantCultureIgnoreCase));
            if (asset == null)
            {
                _logger.LogWarning("Could not find asset with {@SetupName}. Assets found: {@Assets}", SetupFileName,
                    release.Value.Item1.Assets);
                return null;
            }

            return (release.Value.Item2, asset);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to get latest version information from GitHub");
            return null;
        }
    }

    private async Task<(GithubReleaseResponse, SemVersion)?> GetPreRelease()
    {
        using var res = await HttpClient.GetAsync(GithubReleasesUrl);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get latest version information from GitHub. {StatusCode}",
                res.StatusCode);
            return null;
        }

        var json =
            await JsonSerializer.DeserializeAsync<IEnumerable<GithubReleaseResponse>>(
                await res.Content.ReadAsStreamAsync());
        if (json == null)
        {
            _logger.LogWarning("Could not deserialize json");
            return null;
        }

        var listOfValid = new List<(GithubReleaseResponse, SemVersion)>();
        foreach (var release in json.Where(x => x.Prerelease))
        {
            var tagName = release.TagName;
            if (!SemVersion.TryParse(tagName, SemVersionStyles.AllowV, out var version))
            {
                _logger.LogDebug("Failed to parse version. Value: {Version}", tagName);
                continue;
            }

            listOfValid.Add((release, version));
        }

        (GithubReleaseResponse, SemVersion)? newestPreRelease = listOfValid.OrderByDescending(x => x.Item2).FirstOrDefault();
        var latestRelease = await GetLatestRelease();
        if (newestPreRelease == null && latestRelease == null)
        {
            _logger.LogWarning("Could not find any valid pre-releases or releases");
            return null;
        }

        if (newestPreRelease == null)
        {
            _logger.LogWarning("Could not find any valid pre-releases, using latest release");
            return latestRelease;
        }

        if (latestRelease == null)
        {
            _logger.LogWarning("Could not find any valid releases, using latest pre-release without comparing to latest release");
            return newestPreRelease;
        }
        
        
        if (latestRelease.Value.Item2.ComparePrecedenceTo(newestPreRelease.Value.Item2) >= 0)
        {
            _logger.LogInformation("Latest release is newer than or the same as latest pre-release. Using latest release");
            return latestRelease;
        }
        
        return newestPreRelease;
    }

    private async Task<(GithubReleaseResponse, SemVersion)?> GetLatestRelease()
    {
        using var res = await HttpClient.GetAsync(GithubLatest);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get latest version information from GitHub. {StatusCode}",
                res.StatusCode);
            return null;
        }

        var json =
            await JsonSerializer.DeserializeAsync<GithubReleaseResponse>(await res.Content.ReadAsStreamAsync());
        if (json == null)
        {
            _logger.LogWarning("Could not deserialize json");
            return null;
        }
        
        if (!SemVersion.TryParse(json.TagName, SemVersionStyles.AllowV, out var version))
        {
            _logger.LogWarning("Failed to parse version. Value: {Version}", json.TagName);
            return null;
        }

        return (json, version);
    }

    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public async Task CheckUpdate()
    {
        await _updateLock.WaitAsync();

        try
        {
            CheckingForUpdate.Value = true;
            await CheckUpdateInternal();
        }
        finally
        {
            _updateLock.Release();
            CheckingForUpdate.Value = false;
        }
    }

    private async Task CheckUpdateInternal()
    {
        IsPostponed = false;
        UpdateAvailable.Value = false;

        var latestVersion = await GetRelease();
        if (latestVersion == null)
        {
            UpdateAvailable.Value = false;
            return;
        }

        var comparison = CurrentVersion.ComparePrecedenceTo(latestVersion.Value.Item1);
        if (comparison >= 0)
        {
            _logger.LogInformation("Sentry is up to date ([{Version}] >= [{LatestVersion}]) ({Comp})", CurrentVersion,
                latestVersion.Value.Item1, comparison);
            UpdateAvailable.Value = false;
            return;
        }

        UpdateAvailable.Value = true;
        LatestVersion = latestVersion.Value.Item1;
        ReleaseDownloadUrl = latestVersion.Value.Item2.BrowserDownloadUrl;
        if (_configManager.Config.App.LastIgnoredVersion != null &&
            _configManager.Config.App.LastIgnoredVersion.ComparePrecedenceTo(latestVersion.Value.Item1) >= 0)
        {
            _logger.LogInformation(
                "Sentry is not up to date. Skipping update due to previous postpone");
            IsPostponed = true;
            return;
        }

        _logger.LogWarning(
            "Sentry is not up to date. Newest version is [{NewVersion}] but you are on [{CurrentVersion}]!",
            latestVersion.Value.Item1, CurrentVersion);
    }

    public async Task DoUpdate()
    {
        _logger.LogInformation("Starting update...");

        DownloadProgress.Value = 0;
        if (LatestVersion == null || ReleaseDownloadUrl == null)
        {
            _logger.LogError("LatestVersion or LatestDownloadUrl is null. Cannot update");
            return;
        }

        TryDeleteFile(_setupFilePath);

        _logger.LogDebug("Downloading new release...");
        var sp = Stopwatch.StartNew();
        var download = await HttpClient.GetAsync(ReleaseDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        var totalBytes = download.Content.Headers.ContentLength ?? 1;

        await using (var stream = await download.Content.ReadAsStreamAsync())
        {
            await using var fStream = new FileStream(_setupFilePath, FileMode.OpenOrCreate);
            var relativeProgress = new Progress<long>(downloadedBytes =>
                DownloadProgress.Value = ((double)downloadedBytes / totalBytes) * 100);
            
            // Use extension method to report progress while downloading
            await stream.CopyToAsync(fStream, 81920, relativeProgress);
        }

        DownloadProgress.Value = 100;
        _logger.LogDebug("Downloaded file within {TimeTook}ms", sp.ElapsedMilliseconds);
        _logger.LogInformation("Download complete, now restarting to newer application in one second");
        await Task.Delay(1000);
        var startInfo = new ProcessStartInfo
        {
            FileName = _setupFilePath,
            UseShellExecute = true
        };
        Process.Start(startInfo);
        Environment.Exit(0);
    }
}

public class GithubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public required string TagName { get; set; }
    
    [JsonPropertyName("id")]
    public required ulong Id { get; set; }
    
    [JsonPropertyName("draft")]
    public required bool Draft { get; set; }
    
    [JsonPropertyName("prerelease")]
    public required bool Prerelease { get; set; }
    
    [JsonPropertyName("assets")]
    public required ICollection<Asset> Assets { get; set; }

    public class Asset
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }
        [JsonPropertyName("browser_download_url")]
        public required Uri BrowserDownloadUrl { get; set; }
    }
}
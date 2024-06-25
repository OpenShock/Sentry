using Semver;

namespace OpenShock.Sentry.Config;

public sealed class AppConfig
{
    public bool CloseToTray { get; set; } = true;

    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Release;
    public SemVersion? LastIgnoredVersion { get; set; } = null;
}

public enum UpdateChannel
{
    Release,
    PreRelease
}
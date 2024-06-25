namespace OpenShock.Sentry.Config;

public sealed class SentryConfig
{
    public OpenShockConf OpenShock { get; set; } = new();
    public AppConfig App { get; set; } = new();
}
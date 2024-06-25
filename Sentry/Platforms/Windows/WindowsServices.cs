using OpenShock.Sentry.Services;

namespace OpenShock.Sentry.Platforms.Windows;

public static class WindowsServices
{
    public static void AddWindowsServices(this IServiceCollection services)
    {
        services.AddSingleton<ITrayService, WindowsTrayService>();
    }
}
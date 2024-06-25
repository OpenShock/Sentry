using Microsoft.Extensions.Hosting;
using OpenShock.Sentry.Platforms.Windows;

namespace OpenShock.Sentry;

public static class HeadlessProgram
{
    public static IHost SetupHeadlessHost()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices(services =>
        {
            services.AddSentryServices();
            
            services.AddWindowsServices();
        });
        
        var app = builder.Build();
        app.Services.StartSentryServices(true);
        
        return app;
    }
}
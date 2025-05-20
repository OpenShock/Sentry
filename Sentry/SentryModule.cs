using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using OpenShock.Desktop.ModuleBase;
using OpenShock.Desktop.ModuleBase.Api;
using OpenShock.Desktop.ModuleBase.Navigation;
using OpenShock.Sentry;
using OpenShock.Sentry.Services;
using OpenShock.Sentry.UI;

[assembly: DesktopModule(typeof(SentryModule), "openshock.sentry", "Sentry")]
namespace OpenShock.Sentry;

public class SentryModule : DesktopModuleBase
{
    public override string IconPath => "OpenShock/Sentry/Resources/sentry-icon.png";

    public override IReadOnlyCollection<NavigationItem> NavigationComponents { get; } =
    [
        new()
        {
            Name = "Settings",
            ComponentType = typeof(SettingsTab),
            Icon = IconOneOf.FromSvg(Icons.Material.Filled.Settings)
        }
    ];

    public override async Task Setup()
    {
        ModuleServiceProvider = BuildServices();
    }
    
    private IServiceProvider BuildServices()
    {
        var loggerFactory = ModuleInstanceManager.AppServiceProvider.GetRequiredService<ILoggerFactory>();
        
        var services = new ServiceCollection();

        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddSingleton(ModuleInstanceManager.OpenShock);

        services.AddSingleton<OpenCVService>();
        
        
        return services.BuildServiceProvider();
    }        


    public override async Task Start()
    {
        var openCvService = ModuleServiceProvider.GetRequiredService<OpenCVService>();
        await openCvService.Start();
    }
}
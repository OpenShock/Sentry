using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using OpenCvSharp.Internal;
using OpenShock.Desktop.ModuleBase;
using OpenShock.Desktop.ModuleBase.Config;
using OpenShock.Desktop.ModuleBase.Navigation;
using OpenShock.Sentry;
using OpenShock.Sentry.Config;
using OpenShock.Sentry.Detection;
using OpenShock.Sentry.Services;
using OpenShock.Sentry.UI;

[assembly: DesktopModule(typeof(SentryModule), "openshock.sentry", "Sentry")]
namespace OpenShock.Sentry;

public class SentryModule : DesktopModuleBase
{
    private const string ModuleId = "openshock.sentry";
    
    public override IconOneOf? Icon { get; set; } = IconOneOf.FromPath("OpenShock/Sentry/Resources/sentry-icon.png");

    public override IReadOnlyCollection<NavigationItem> NavigationComponents { get; } =
    [
        new()
        {
            Name = "Dashboard",
            ComponentType = typeof(DashboardTab),
            Icon = IconOneOf.FromSvg(Icons.Material.Filled.Dashboard)
        },
        new()
        {
            Name = "Profiles",
            ComponentType = typeof(ProfileEditorTab),
            Icon = IconOneOf.FromSvg(Icons.Material.Filled.Tune)
        },
        new()
        {
            Name = "Settings",
            ComponentType = typeof(SettingsTab),
            Icon = IconOneOf.FromSvg(Icons.Material.Filled.Settings)
        }
    ];

    public override async Task Setup()
    {
        InitializeOpenCvNativeLibs();
        var moduleConfig = await ModuleInstanceManager.GetModuleConfig<SentryConfig>();
        ModuleServiceProvider = BuildServices(moduleConfig);
    }

    private static void InitializeOpenCvNativeLibs()
    {
        var moduleDir = Path.GetDirectoryName(typeof(SentryModule).Assembly.Location);
        if (moduleDir is null) return;

        var nativeLibDir = Path.Combine(moduleDir, "libs", "win-x64");
        if (Directory.Exists(nativeLibDir))
            WindowsLibraryLoader.Instance.AdditionalPaths.Add(nativeLibDir);
    }

    private IServiceProvider BuildServices(IModuleConfig<SentryConfig> moduleConfig)
    {
        var loggerFactory = ModuleInstanceManager.AppServiceProvider.GetRequiredService<ILoggerFactory>();

        // Data directory: %APPDATA%/OpenShock/Desktop/moduleData/openshock.sentry/
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenShock", "Desktop", "moduleData", ModuleId);
        Directory.CreateDirectory(dataDir);

        var profilesDir = Path.Combine(dataDir, "profiles");

        var services = new ServiceCollection();

        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddSingleton(moduleConfig);
        services.AddSingleton(ModuleInstanceManager.OpenShock);

        services.AddSingleton<ScreenCaptureService>();
        services.AddSingleton<PreviewService>();
        services.AddSingleton(sp => new DetectorFactory(
            sp.GetRequiredService<ILoggerFactory>(),
            dataDir));
        services.AddSingleton<ShockTriggerService>();
        services.AddSingleton(sp => new GameProfileManager(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GameProfileManager>(),
            profilesDir));
        services.AddSingleton<DetectionService>();

        return services.BuildServiceProvider();
    }

    public override async Task Start()
    {
        var logger = ModuleServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SentryModule>();
        var config = ModuleServiceProvider.GetRequiredService<IModuleConfig<SentryConfig>>();

        if (!string.IsNullOrEmpty(config.Config.ActiveProfileName))
        {
            var detectionService = ModuleServiceProvider.GetRequiredService<DetectionService>();
            logger.LogInformation("Loading profile '{ProfileName}'", config.Config.ActiveProfileName);
            await detectionService.LoadProfile(config.Config.ActiveProfileName);
        }
        else
        {
            logger.LogInformation("No active profile configured. Use the Dashboard to select one.");
        }
    }
}

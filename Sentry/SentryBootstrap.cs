using MudBlazor.Services;
using OpenShock.SDK.CSharp.Hub;
using OpenShock.Sentry.Backend;
using OpenShock.Sentry.Config;
using OpenShock.Sentry.Logging;
using OpenShock.Sentry.Services;
using OpenShock.Sentry.Services.Pipes;
using OpenShock.Sentry.Utils;
using Serilog;

namespace OpenShock.Sentry;

public static class SentryBootstrap
{
    public static void AddSentryServices(this IServiceCollection services)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Filter.ByExcluding(ev =>
                ev.Exception is InvalidDataException a && a.Message.StartsWith("Invocation provides")).Filter
            .ByExcluding(x => x.MessageTemplate.Text.StartsWith("Failed to find handler for"))
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
            .WriteTo.UiLogSink()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

        // ReSharper disable once RedundantAssignment
        var isDebug = Environment.GetCommandLineArgs()
            .Any(x => x.Equals("--debug", StringComparison.InvariantCultureIgnoreCase));

#if DEBUG
        isDebug = true;
#endif
        if (isDebug)
        {
            Console.WriteLine("Debug mode enabled");
            loggerConfiguration.MinimumLevel.Verbose();
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        services.AddSerilog(Log.Logger);

        services.AddMemoryCache();
        

        services.AddSingleton<PipeServerService>();

        services.AddSingleton<ConfigManager>();

        services.AddSingleton<Updater>();

        services.AddSingleton<OpenShockApi>();
        services.AddSingleton<OpenShockHubClient>();
        services.AddSingleton<BackendHubManager>();

        services.AddSingleton<LiveControlManager>();
        services.AddSingleton<StatusHandler>();
        

        services.AddSingleton<AuthService>();
    }

    public static void AddCommonBlazorServices(this IServiceCollection services)
    {
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        services.AddMudServices();
    }

    public static void StartSentryServices(this IServiceProvider services, bool headless)
    {
        #region SystemTray

        if (headless)
        {
            var applicationThread = new Thread(() =>
            {
                services.GetService<ITrayService>()?.Initialize();
                System.Windows.Forms.Application.Run();
            });
            applicationThread.Start();
        }
        else services.GetService<ITrayService>()?.Initialize();

        #endregion


        // <---- Warmup ---->
        services.GetRequiredService<PipeServerService>().StartServer();

        var updater = services.GetRequiredService<Updater>();
        OsTask.Run(updater.CheckUpdate);
    }
}
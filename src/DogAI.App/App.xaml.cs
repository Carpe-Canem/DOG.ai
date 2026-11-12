using System.IO;
using System.Windows;
using DogAI.Core;
using DogAI.Core.Appearance;
using DogAI.Core.Net;
using DogAI.Core.Settings;
using DogAI.Data;
using DogAI.Hub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DogAI.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "dogai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception");
            MessageBox.Show(args.Exception.Message, "DOG AI hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            _services = BuildServices(paths);
            var log = _services.GetRequiredService<ILogger<App>>();
            log.LogInformation("DOG AI starting. Data folder: {Root}", paths.Root);

            var audit = _services.GetRequiredService<NetworkAudit>();
            audit.Recorded += entry =>
            {
                if (!entry.Allowed)
                    log.LogWarning("Blocked {Source} request to {Target} ({Reason})", entry.Source, entry.Target, entry.Reason);
            };

            await _services.MigrateDogDatabaseAsync();
            await EnsureFirstRunDefaultsAsync(_services, paths);
            await _services.GetRequiredService<AppearanceState>().LoadAsync();

            new MainWindow(_services).Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(ex.Message, "DOG AI could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static ServiceProvider BuildServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton(paths);
        services.AddSingleton<OnlineGate>();
        services.AddSingleton<NetworkAudit>();
        services.AddSingleton<HubHttp>();
        services.AddSingleton<ISystemTheme, WindowsSystemTheme>();
        services.AddSingleton<AppearanceState>();
        services.AddDogData();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        return services.BuildServiceProvider();
    }

    private static async Task EnsureFirstRunDefaultsAsync(IServiceProvider services, AppPaths paths)
    {
        var store = services.GetRequiredService<ISettingsStore>();
        var settings = await store.LoadAsync();
        if (settings.ModelsDirectory is null)
            await store.SaveAsync(settings with { ModelsDirectory = paths.SuggestModelsDirectory() });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

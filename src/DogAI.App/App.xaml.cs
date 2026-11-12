using System.IO;
using System.Windows;
using DogAI.Core;
using DogAI.Core.Appearance;
using DogAI.Core.Chat;
using DogAI.Core.Models;
using DogAI.Core.Net;
using DogAI.Engine;
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

            var engine = _services.GetRequiredService<EngineManager>();
            engine.Output += line => log.LogDebug("engine: {Line}", line);
            engine.Changed += () =>
            {
                if (engine.State == EngineState.Failed) log.LogWarning("Engine failed for {Model}: {Error}", engine.ModelPath, engine.Error);
                else log.LogInformation("Engine {State} {Model}", engine.State, engine.ModelPath);
            };

            await _services.MigrateDogDatabaseAsync();
            await EnsureFirstRunDefaultsAsync(_services, paths);
            await StartModelServicesAsync(_services, log);
            await _services.GetRequiredService<AppearanceState>().LoadAsync();
            await _services.GetRequiredService<PresetCatalog>().LoadAsync();

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
        services.AddSingleton<OnlineSession>();
        services.AddSingleton<HfToken>();
        services.AddSingleton(sp => new HuggingFaceClient(sp.GetRequiredService<HubHttp>().Client, sp.GetRequiredService<HfToken>()));
        services.AddSingleton<DownloadManager>();
        services.AddSingleton(sp => new EngineUpdater(sp.GetRequiredService<HubHttp>().Client, sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<HardwareProbe>();
        services.AddSingleton<ModelLauncher>();
        services.AddSingleton<ModelBenchmark>();
        services.AddSingleton<DogAI.UI.ModelsContext>();
        services.AddSingleton<ISystemTheme, WindowsSystemTheme>();
        services.AddSingleton<AppearanceState>();
        services.AddSingleton<IDesktopShell, WpfDesktopShell>();
        services.AddSingleton<LoopbackHttp>();
        services.AddSingleton<LlamaServerLocator>();
        services.AddSingleton<EngineManager>();
        services.AddSingleton<IChatEngine>(sp => sp.GetRequiredService<EngineManager>());
        services.AddSingleton<PresetCatalog>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<DogAI.Core.Characters.CardBuilder>();
        services.AddSingleton<DogAI.Core.Stories.StoryBuilder>();
        services.AddDogData();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        return services.BuildServiceProvider();
    }

    private static async Task StartModelServicesAsync(IServiceProvider services, Microsoft.Extensions.Logging.ILogger log)
    {
        var settings = await services.GetRequiredService<ISettingsStore>().LoadAsync();
        services.GetRequiredService<HfToken>().Load(settings.HfTokenProtected);
        var engine = services.GetRequiredService<EngineManager>();
        engine.IdleUnloadAfter = settings.IdleUnloadMinutes > 0 ? TimeSpan.FromMinutes(settings.IdleUnloadMinutes) : null;
        engine.UnloadedForIdle += () => log.LogInformation("Model unloaded after {Minutes} idle minutes", settings.IdleUnloadMinutes);

        var downloads = services.GetRequiredService<DownloadManager>();
        var records = services.GetRequiredService<IModelRecordStore>();
        downloads.Completed += job =>
        {
            log.LogInformation("Downloaded {Repo} {Title} to {Path}", job.Repo, job.Title, job.MainPath);
            _ = records.SaveAsync(new ModelRecord { Path = job.MainPath, Repo = job.Repo, Quant = job.Quant, UseCase = job.UseCase });
        };
        await downloads.LoadAsync();
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

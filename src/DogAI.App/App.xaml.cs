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

            var search = _services.GetRequiredService<EmbeddingServer>();
            search.Output += line => log.LogDebug("search engine: {Line}", line);
            search.Changed += () =>
            {
                if (search.Error is { } error) log.LogWarning("Document search model problem: {Error}", error);
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
        services.AddSingleton<EmbeddingServer>();
        services.AddSingleton<DogAI.Core.Documents.IEmbedder>(sp => sp.GetRequiredService<EmbeddingServer>());
        services.AddSingleton<DogAI.Core.Documents.DocumentLibrary>();
        services.AddSingleton<PresetCatalog>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<DogAI.Core.Characters.CardBuilder>();
        services.AddSingleton<DogAI.Core.Stories.StoryBuilder>();
        AddVoice(services);
        services.AddDogData();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        return services.BuildServiceProvider();
    }

    /// <summary>Test runs can replace the microphone with a WAV file and the speakers with a folder of WAV files.</summary>
    public const string TestMicVariable = "DOGAI_TEST_MIC";
    public const string TestSpeakersVariable = "DOGAI_TEST_SPEAKERS";

    private static void AddVoice(IServiceCollection services)
    {
        services.AddSingleton<DogAI.Engine.Voice.AudioDevices>();
        services.AddSingleton<DogAI.Core.Voice.IAudioDevices>(sp => sp.GetRequiredService<DogAI.Engine.Voice.AudioDevices>());
        services.AddSingleton<DogAI.Core.Voice.ISpeechSynthesizer, DogAI.Engine.Voice.VoiceBox>();
        services.AddSingleton<DogAI.Core.Voice.ISpeechRecognizer, DogAI.Engine.Voice.WhisperRecognizer>();
        services.AddSingleton<DogAI.Core.Voice.IMicrophone>(sp =>
            Environment.GetEnvironmentVariable(TestMicVariable) is { Length: > 0 } wav
                ? new DogAI.Engine.Voice.RecordingMicrophone(wav)
                : new DogAI.Engine.Voice.Microphone(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<DogAI.Engine.Voice.AudioDevices>()));
        services.AddSingleton<DogAI.Core.Voice.IAudioOutput>(sp =>
            Environment.GetEnvironmentVariable(TestSpeakersVariable) is { Length: > 0 } folder
                ? new DogAI.Engine.Voice.RecordingSpeakers(folder)
                : new DogAI.Engine.Voice.Speakers(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<DogAI.Engine.Voice.AudioDevices>()));
        services.AddSingleton<DogAI.Core.Voice.ReadAloud>();
        services.AddSingleton<DogAI.Core.Voice.ReplyVoice>();
        services.AddSingleton<DogAI.Core.Voice.Dictation>();
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
        var voices = services.GetRequiredService<DogAI.Core.Voice.ISpeechSynthesizer>();
        downloads.Completed += job =>
        {
            log.LogInformation("Downloaded {Repo} {Title} to {Path}", job.Repo, job.Title, job.MainPath);
            // Voice models are not chat models: they get no library record.
            if (job.UseCase == DogAI.UI.Components.Models.VoiceTab.VoiceUse) voices.Refresh();
            else _ = records.SaveAsync(new ModelRecord { Path = job.MainPath, Repo = job.Repo, Quant = job.Quant, UseCase = job.UseCase });
        };
        await downloads.LoadAsync();

        var reader = services.GetRequiredService<DogAI.Core.Voice.ReadAloud>();
        reader.Changed += () =>
        {
            if (reader.Problem is { } problem) log.LogWarning("Reading aloud stopped: {Problem}", problem);
        };
        await services.GetRequiredService<DogAI.Core.Voice.ReplyVoice>().LoadAsync();

        // Documents added while no search model was set up, or before the model changed, are indexed now.
        // Nothing starts if every document is already indexed.
        services.GetRequiredService<DogAI.Core.Documents.DocumentLibrary>().StartIndexing();
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

using System.IO;
using System.Windows;
using DogAI.Core;
using DogAI.Core.Appearance;
using DogAI.Core.Chat;
using DogAI.Core.Models;
using DogAI.Core.Net;
using DogAI.Core.Privacy;
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

    /// <summary>Given to a restarted DOG.ai: wait for this process (the old one) to exit before touching any files.</summary>
    public const string AfterArgument = "--after";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A restarted DOG.ai waits for the old process first, so renames below never meet files it still holds.
        WaitForPreviousInstance(e.Args);
        // May move the earlier-named per-user folder (DOG-AI) to DOG.ai; see AppPaths.AdoptLegacyFolder.
        var paths = AppPaths.ForProgram();
        paths.EnsureCreated();
        var renamedDatabase = paths.RenameLegacyDatabase();
        // Portable: the engines and libraries inherit this, so their scratch files stay in the folder too.
        paths.UseOwnTempFolder();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "DOG.ai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception");
            MessageBox.Show(args.Exception.Message, "DOG.ai hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            if (renamedDatabase) Log.Information("Renamed the database from {Old} to {New}.", AppPaths.LegacyDatabaseName, AppPaths.DatabaseName);
            // The order matters (locked data, M8):
            // 1. A restore waiting from last time is swapped in. This only moves files, so it needs no key, and it has to
            //    come first because a restored backup can bring its own lock.
            if (DatabaseBackup.ApplyPending(paths) is { } restored) Log.Information(restored);
            // 2. A portable folder that was moved or copied is noticed (the saved paths are inside the database, so they
            //    can only be rewritten once it is open).
            if (PortableMove.MovedFrom(paths) is { } movedFrom) Log.Information("This portable folder used to be at {Old}.", movedFrom);
            // 3. Unlock. While the lock screen is up nothing else exists: no services, no tray icon, no global key, no model.
            //    The lock screen is not the main window, so closing it must not end the app by itself.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (Unlock(paths, e.Args.Contains(HiddenArgument)) is not { } key)
            {
                Shutdown(0);
                return;
            }
            // 4. Only now may anything open the database.
            DatabaseBackup.FinishRestore(paths, key.Bytes);
            if (PortableMove.Apply(paths, key.Bytes) is { } moved) Log.Information(moved);

            _services = BuildServices(paths, key);
            var log = _services.GetRequiredService<ILogger<App>>();
            log.LogInformation("DOG.ai starting. Data folder: {Root}{Portable}", paths.Root, paths.IsPortable ? " (portable)" : "");

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
            await _services.GetRequiredService<DogAI.Core.Stories.StoryOptions>().LoadAsync();
            await _services.GetRequiredService<PresetCatalog>().LoadAsync();
            await _services.GetRequiredService<DogAI.Core.Library.LibraryCatalog>().LoadAsync();

            var window = new MainWindow(_services);
            // WPF takes the first window it ever shows as the main one; after a lock screen that would be a closed window.
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            window.Show();
            await _services.GetRequiredService<WindowsIntegration>().ApplyAsync();
            await _services.GetRequiredService<AppLock>().StartAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(ex.Message, "DOG.ai could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Given to a DOG.ai restarted by a lock while its window was hidden in the tray: the lock screen waits there too.</summary>
    public const string HiddenArgument = "--hidden";

    /// <summary>
    /// Finds out whether the data is locked and, if so, shows the lock screen until the owner opens it. Returns the key to
    /// run with (<see cref="DatabaseKey.None"/> when there is no lock), or null when DOG.ai should close instead: the owner
    /// closed the lock screen or chose Quit, or the lock file is damaged or missing.
    /// </summary>
    private static DatabaseKey? Unlock(AppPaths paths, bool hidden)
    {
        DataLockState state;
        DogAI.Core.Privacy.LockFile? file;
        try
        {
            state = DataLock.Look(paths, out file);
        }
        catch (DogAI.Core.Privacy.LockFileException ex)
        {
            Log.Fatal("The data lock cannot be read: {Problem}", ex.Message);
            MessageBox.Show(ex.Message, "DOG.ai cannot open your data", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        if (state == DataLockState.Off || file is null) return DatabaseKey.None;

        Log.Information("The data is locked ({State}); waiting at the lock screen.", state);
        var screen = new LockWindow(paths, file, state, new WindowsHello());
        // While locked, the tray icon (when it is on) offers nothing but Quit; clicking it shows the lock screen.
        using var tray = file.Tray ? LockedTray(screen) : null;
        if (tray is not null) Log.Information("Locked: tray icon shown with Quit only.");
        var frame = new System.Windows.Threading.DispatcherFrame();
        screen.Closed += (_, _) => frame.Continue = false;
        // Hidden only when there is a tray icon to bring it back with.
        if (!(hidden && tray is not null)) screen.Show();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (screen.Result is null)
        {
            Log.Information("The lock screen was closed without unlocking.");
            return null;
        }
        Log.Information("Unlocked.");
        return screen.Result;
    }

    private static System.Windows.Forms.NotifyIcon LockedTray(LockWindow screen)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Quit", null, (_, _) => screen.Close());
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "DOG.ai (locked)",
            Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            screen.Show();
            if (screen.WindowState == WindowState.Minimized) screen.WindowState = WindowState.Normal;
            screen.Activate();
        };
        return tray;
    }

    private static ServiceProvider BuildServices(AppPaths paths, DatabaseKey key)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton(paths);
        // The key the database opens with during this run (no key when the lock is off). AddDogData reads it.
        services.AddSingleton(key);
        services.AddSingleton<DogAI.Core.Privacy.IHelloSigner, WindowsHello>();
        services.AddSingleton(sp => new DogAI.Core.Privacy.PrivacyLock(paths, key, sp.GetRequiredService<DogAI.Core.Privacy.IHelloSigner>()));
        services.AddSingleton<AppLock>();
        services.AddSingleton<DogAI.Core.Privacy.IAppLock>(sp => sp.GetRequiredService<AppLock>());
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
        services.AddSingleton<WindowsIntegration>();
        services.AddSingleton<IWindowsIntegration>(sp => sp.GetRequiredService<WindowsIntegration>());
        services.AddSingleton<PresetCatalog>();
        services.AddSingleton(sp => new DogAI.Core.Library.LibraryCatalog(sp.GetRequiredService<IDocumentStore>()));
        services.AddSingleton<ChatService>();
        services.AddSingleton<DogAI.Core.Characters.CardBuilder>();
        services.AddSingleton<DogAI.Core.Stories.StoryBuilder>();
        services.AddSingleton<DogAI.Core.Stories.StoryOptions>();
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

    /// <summary>After a restart for a restore, the old window may still be closing; wait for it (at most 20 seconds).</summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        var at = Array.IndexOf(args, AfterArgument);
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var pid)) return;
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static async Task EnsureFirstRunDefaultsAsync(IServiceProvider services, AppPaths paths)
    {
        var store = services.GetRequiredService<ISettingsStore>();
        var settings = await store.LoadAsync();
        // Portable: the models folder is always the one inside the program's folder.
        if (paths.IsPortable)
        {
            var own = paths.SuggestModelsDirectory();
            if (!string.Equals(settings.ModelsDirectory, own, StringComparison.OrdinalIgnoreCase))
                await store.SaveAsync(settings with { ModelsDirectory = own });
        }
        // A restored backup can name a drive this PC does not have; fall back to a folder that exists here.
        else if (settings.ModelsDirectory is null || !DriveExists(settings.ModelsDirectory))
            await store.SaveAsync(settings with { ModelsDirectory = paths.SuggestModelsDirectory() });
    }

    private static bool DriveExists(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

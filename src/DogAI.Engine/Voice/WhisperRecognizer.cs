using DogAI.Core.Settings;
using DogAI.Core.Voice;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace DogAI.Engine.Voice;

/// <summary>
/// Speech recognition with Whisper (whisper.cpp through Whisper.net), on the graphics card through Vulkan when it
/// can, else on the processor. Measured here with large-v3 turbo: a 7-second sentence takes 0.33 s on the RTX 4090
/// and 13 s on the processor. The very first run on a graphics card also prepares its shaders (about 15 s, once;
/// the driver keeps them), so the model is loaded and tried as soon as the microphone turns on.
/// The model is let go after ten idle minutes.
/// </summary>
public sealed class WhisperRecognizer : ISpeechRecognizer, IDisposable
{
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(10);
    private static readonly object RuntimeLock = new();
    private static bool _runtimeChosen;

    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private WhisperFactory? _factory;
    private string? _loadedPath;
    private DateTime _lastUse = DateTime.UtcNow;

    public WhisperRecognizer(ISettingsStore settings)
    {
        _settings = settings;
        _idleTimer = new Timer(_ => UnloadIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>"graphics card" or "processor" once a model has been loaded; null before.</summary>
    public string? RunsOn { get; private set; }

    public async Task<string?> ModelPathAsync(CancellationToken ct = default)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var folder = settings.VoiceFolder();
        var models = VoiceLibrary.SpeechModels(folder);
        return models.FirstOrDefault(m => m.Name.Equals(settings.SpeechModel, StringComparison.OrdinalIgnoreCase))?.Path
               ?? models.FirstOrDefault()?.Path;
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (await ModelPathAsync(ct).ConfigureAwait(false) is null) return;
        try
        {
            await RecognizeAsync(new float[VoiceActivity.SampleRate], "en", ct).ConfigureAwait(false);
        }
        catch (VoiceUnavailableException)
        {
            // Reported when the user actually speaks.
        }
    }

    public async Task<Recognized> RecognizeAsync(float[] samples, string language, CancellationToken ct = default)
    {
        var path = await ModelPathAsync(ct).ConfigureAwait(false)
                   ?? throw new VoiceUnavailableException("No speech recognition model is downloaded. Get one in Models → Voice.");
        if (VoiceCatalog.IsEnglishOnly(Path.GetFileName(path))) language = "en";
        if (string.IsNullOrWhiteSpace(language)) language = "auto";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var factory = Load(path);
            _lastUse = DateTime.UtcNow;
            await using var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 12))
                .WithNoSpeechThreshold(0.6f)
                .Build();
            var pieces = new List<string>();
            double? noSpeech = null;
            string? detected = null;
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                pieces.Add(segment.Text);
                noSpeech = noSpeech is null ? segment.NoSpeechProbability : Math.Min(noSpeech.Value, segment.NoSpeechProbability);
                detected ??= segment.Language;
            }
            _lastUse = DateTime.UtcNow;
            return new Recognized(string.Join(" ", pieces), noSpeech, detected);
        }
        catch (Exception e) when (e is WhisperModelLoadException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Unload();
            throw new VoiceUnavailableException("The speech recognition model could not be started: " + e.Message, e);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called under the gate.</summary>
    private WhisperFactory Load(string path)
    {
        if (_factory is not null && _loadedPath == path) return _factory;
        Unload();
        lock (RuntimeLock)
        {
            // The native library is chosen once per process: the graphics card (Vulkan) first, the processor if that fails.
            if (!_runtimeChosen)
            {
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
                _runtimeChosen = true;
            }
        }
        _factory = WhisperFactory.FromPath(path);
        _loadedPath = path;
        RunsOn = RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => "graphics card",
            null => null,
            _ => "processor",
        };
        return _factory;
    }

    private void Unload()
    {
        _factory?.Dispose();
        _factory = null;
        _loadedPath = null;
    }

    private void UnloadIdle()
    {
        if (_factory is null || DateTime.UtcNow - _lastUse < IdleUnload || !_gate.Wait(0)) return;
        try { Unload(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _gate.Wait();
        Unload();
    }
}

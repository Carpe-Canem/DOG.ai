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
/// <para>
/// While a video is being made it never comes onto the card (M21): it asks at the <see cref="DogAI.Core.Images.CardDoor"/>
/// before every use, and with the door closed it loads and runs on the processor, slower but at once. The first
/// thing said after the video loads it on the card again.
/// </para>
/// </summary>
public sealed class WhisperRecognizer : ISpeechRecognizer, DogAI.Core.Images.ICardGuest, IDisposable
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
    private readonly DogAI.Core.Images.CardDoor? _door;
    private bool _onProcessor;
    private bool _cardSeen;

    /// <param name="door">Asked before the model uses the graphics card; null where nothing shares the card (tests).</param>
    public WhisperRecognizer(ISettingsStore settings, DogAI.Core.Images.CardDoor? door = null)
    {
        _settings = settings;
        _door = door;
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
                   ?? throw new VoiceUnavailableException("No speech recognition model is downloaded. Get one in " + DogAI.Core.Models.ModelsPlaces.GetVoiceName + ".");
        if (VoiceCatalog.IsEnglishOnly(Path.GetFileName(path))) language = "en";
        if (string.IsNullOrWhiteSpace(language)) language = "auto";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // While a video is being made nothing new comes onto the card: the door answers null, and the model is
            // loaded (or loaded again) for the processor. Otherwise the entry is kept for the whole use: every use
            // takes working memory on the card, and a video asked for meanwhile waits for it.
            using var entry = _door?.Enter();
            var factory = Load(path, processor: _door is not null && entry is null);
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
    /// <param name="processor">Keep off the graphics card whatever library was loaded (a video is being made).</param>
    private WhisperFactory Load(string path, bool processor)
    {
        if (_factory is not null && _loadedPath == path && _onProcessor == processor) return _factory;
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
        _factory = processor ? WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false }) : WhisperFactory.FromPath(path);
        _loadedPath = path;
        _onProcessor = processor;
        var onCard = RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12;
        if (onCard) _cardSeen = true;
        RunsOn = RuntimeOptions.LoadedLibrary is null ? null : onCard && !processor ? "graphics card" : "processor";
        return _factory;
    }

    private void Unload()
    {
        _factory?.Dispose();
        _factory = null;
        _loadedPath = null;
        _onProcessor = false;
    }

    /// <summary>
    /// Words for the owner while speech is written down more slowly than usual: a video is being made, so the model
    /// runs on the processor. Null otherwise, and on a PC where it runs on the processor anyway.
    /// </summary>
    public string? Slower =>
        _door is { IsClosed: true } && (_cardSeen || RuntimeOptions.LoadedLibrary is null) ? DogAI.Core.Images.CardGuestWords.SpeechOnProcessor : null;

    string DogAI.Core.Images.ICardGuest.Name => "the speech recognition model";

    /// <summary>True while a model is loaded on the graphics card (one that runs on the processor holds none of its memory).</summary>
    public bool HoldsMemory => _factory is not null && RunsOn == "graphics card";

    /// <summary>True while speech is being recognised (or a model is being loaded for it).</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>
    /// Lets the model go because the graphics card needs the room, or the owner unloads everything. False while speech
    /// is being recognised. The next thing said loads it again.
    /// </summary>
    public Task<bool> LetGoAsync()
    {
        if (!_gate.Wait(0)) return Task.FromResult(false);
        try { Unload(); }
        finally { _gate.Release(); }
        return Task.FromResult(true);
    }

    /// <summary>
    /// Leaves the card for a video: what is being recognised right now is waited for (never cut off), then the model
    /// is let go. The next thing said finds the door closed and is written down on the processor.
    /// </summary>
    public async Task<bool> StepOffAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { Unload(); }
        finally { _gate.Release(); }
        return true;
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

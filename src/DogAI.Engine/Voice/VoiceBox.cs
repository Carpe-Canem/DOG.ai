using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using DogAI.Core.Settings;
using DogAI.Core.Voice;
using SherpaOnnx;

namespace DogAI.Engine.Voice;

/// <summary>
/// Speaks with Kokoro and Piper voices (sherpa-onnx, on the processor) and with the voices that come with Windows.
/// A downloaded voice is loaded the first time it speaks and let go after ten idle minutes. One piece of text is
/// turned into sound at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VoiceBox : ISpeechSynthesizer, IDisposable
{
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(10);
    private const int WindowsSampleRate = 22_050;

    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (OfflineTts Tts, DateTime LastUse)> _loaded = [];
    private readonly Timer _idleTimer;
    private IReadOnlyList<VoiceOption>? _windowsVoices;
    private (string? Folder, DateTime At, IReadOnlyList<VoicePack> Packs)? _packs;

    public VoiceBox(ISettingsStore settings)
    {
        _settings = settings;
        _idleTimer = new Timer(_ => UnloadIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<IReadOnlyList<VoiceOption>> VoicesAsync(CancellationToken ct = default)
    {
        var packs = await PacksAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => packs.SelectMany(VoiceLibrary.Options).Concat(WindowsVoices()).ToList(), ct).ConfigureAwait(false);
    }

    /// <summary>Forget the remembered list of downloaded voices (after a download or delete).</summary>
    public void Refresh() => _packs = null;

    /// <summary>The downloaded voice packs, looked up again at most every 20 seconds.</summary>
    public async Task<IReadOnlyList<VoicePack>> PacksAsync(CancellationToken ct = default)
    {
        var folder = (await _settings.LoadAsync(ct).ConfigureAwait(false)).VoiceFolder();
        if (_packs is { } p && p.Folder == folder && DateTime.UtcNow - p.At < TimeSpan.FromSeconds(20)) return p.Packs;
        var packs = await Task.Run(() => VoiceLibrary.Packs(folder), ct).ConfigureAwait(false);
        _packs = (folder, DateTime.UtcNow, packs);
        return packs;
    }

    public async Task<VoiceId?> ResolveAsync(VoiceId? wanted, CancellationToken ct = default)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var voices = await VoicesAsync(ct).ConfigureAwait(false);
        bool Has(VoiceId? id) => id is not null && voices.Any(v => v.Id == id);
        if (Has(wanted)) return wanted;
        var chosen = VoiceId.Parse(settings.Voice);
        if (Has(chosen)) return chosen;
        // Without a choice: Kokoro's preferred voice, then any downloaded voice, then Windows.
        return voices.FirstOrDefault(v => v.Id is { Kind: VoiceKind.Kokoro, Speaker: KokoroVoices.Preferred })?.Id
               ?? voices.FirstOrDefault(v => v.Id.Kind != VoiceKind.Windows)?.Id
               ?? voices.FirstOrDefault()?.Id;
    }

    public async Task<AudioClip> SynthesizeAsync(string text, VoiceId voice, float speed, CancellationToken ct = default)
    {
        speed = Math.Clamp(speed, 0.5f, 2f);
        if (voice.Kind == VoiceKind.Windows) return await Task.Run(() => SpeakWithWindows(text, voice.Model, speed), ct).ConfigureAwait(false);

        var pack = (await PacksAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Name.Equals(voice.Model, StringComparison.OrdinalIgnoreCase))
                   ?? throw new VoiceUnavailableException($"The voice \"{VoiceCatalog.Title(voice.Model)}\" is not downloaded any more.");
        var speaker = pack.Kind == VoiceKind.Kokoro ? pack.Speakers.ToList().IndexOf(voice.Speaker ?? "") : 0;
        if (speaker < 0) throw new VoiceUnavailableException($"The voice pack has no voice called {voice.Speaker}.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var tts = Load(pack, voice.Speaker);
                var audio = tts.Generate(text, speed, speaker);
                if (audio.NumSamples <= 0 || audio.SampleRate <= 0)
                    throw new VoiceUnavailableException("The voice could not say that text.");
                return new AudioClip(audio.Samples, audio.SampleRate);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets a loaded voice, loading it if needed. Called under the gate.</summary>
    private OfflineTts Load(VoicePack pack, string? speaker)
    {
        var language = pack.Kind == VoiceKind.Kokoro ? KokoroVoices.Language(speaker ?? "") ?? "en-us" : "";
        var key = pack.Folder + "|" + language;
        if (_loaded.TryGetValue(key, out var entry))
        {
            _loaded[key] = (entry.Tts, DateTime.UtcNow);
            return entry.Tts;
        }
        // Keep at most two voices loaded; each Kokoro pack holds a few hundred MB.
        while (_loaded.Count >= 2)
        {
            var oldest = _loaded.MinBy(kv => kv.Value.LastUse);
            oldest.Value.Tts.Dispose();
            _loaded.Remove(oldest.Key);
        }
        var config = pack.Kind == VoiceKind.Kokoro ? KokoroConfig(pack, language) : PiperConfig(pack);
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        OfflineTts tts;
        try
        {
            tts = new OfflineTts(config);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new VoiceUnavailableException($"The voice \"{VoiceCatalog.Title(pack.Name)}\" could not be loaded: {e.Message}", e);
        }
        if (tts.SampleRate <= 0)
        {
            tts.Dispose();
            throw new VoiceUnavailableException($"The voice \"{VoiceCatalog.Title(pack.Name)}\" could not be loaded. Its files may be incomplete.");
        }
        _loaded[key] = (tts, DateTime.UtcNow);
        return tts;
    }

    private static OfflineTtsConfig KokoroConfig(VoicePack pack, string language)
    {
        string In(string name) => Path.Combine(pack.Folder, name);
        // English voices read words from the pack's pronunciation lists (American or British), Chinese ones from the
        // Chinese list; everything else, and words not in a list, goes through espeak-ng.
        var lexicons = language switch
        {
            "en-us" => new[] { "lexicon-us-en.txt", "lexicon-zh.txt" },
            "en-gb" => ["lexicon-gb-en.txt", "lexicon-zh.txt"],
            "zh" => ["lexicon-zh.txt", "lexicon-us-en.txt"],
            _ => [],
        };
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = pack.ModelFile;
        config.Model.Kokoro.Voices = In("voices.bin");
        config.Model.Kokoro.Tokens = In("tokens.txt");
        config.Model.Kokoro.DataDir = In("espeak-ng-data");
        if (Directory.Exists(In("dict"))) config.Model.Kokoro.DictDir = In("dict");
        config.Model.Kokoro.Lexicon = string.Join(',', lexicons.Select(In).Where(File.Exists));
        config.Model.Kokoro.Lang = language;
        if (language == "zh")
            config.RuleFsts = string.Join(',', new[] { "phone-zh.fst", "date-zh.fst", "number-zh.fst" }.Select(In).Where(File.Exists));
        return config;
    }

    private static OfflineTtsConfig PiperConfig(VoicePack pack)
    {
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = pack.ModelFile;
        config.Model.Vits.Tokens = Path.Combine(pack.Folder, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(pack.Folder, "espeak-ng-data");
        return config;
    }

    private IReadOnlyList<VoiceOption> WindowsVoices()
    {
        if (_windowsVoices is not null) return _windowsVoices;
        try
        {
            using var synth = new SpeechSynthesizer();
            _windowsVoices = synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => new VoiceOption(new VoiceId(VoiceKind.Windows, v.VoiceInfo.Name),
                    $"{v.VoiceInfo.Name.Replace("Microsoft ", "")} ({v.VoiceInfo.Culture.DisplayName}, {v.VoiceInfo.Gender.ToString().ToLowerInvariant()})",
                    "Windows voices", v.VoiceInfo.Culture.Name))
                .ToList();
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _windowsVoices = [];
        }
        return _windowsVoices;
    }

    private static AudioClip SpeakWithWindows(string text, string name, float speed)
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(name);
            // Windows' rate runs from -10 to 10; each step is roughly 1.1 times faster or slower.
            synth.Rate = Math.Clamp((int)Math.Round(Math.Log(speed) / Math.Log(1.1)), -10, 10);
            using var stream = new MemoryStream();
            synth.SetOutputToAudioStream(stream, new SpeechAudioFormatInfo(WindowsSampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            synth.SetOutputToNull();
            var bytes = stream.ToArray();
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            return new AudioClip(samples, WindowsSampleRate);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            throw new VoiceUnavailableException($"The Windows voice \"{name}\" could not speak: {e.Message}", e);
        }
    }

    private void UnloadIdle()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            foreach (var key in _loaded.Where(kv => DateTime.UtcNow - kv.Value.LastUse > IdleUnload).Select(kv => kv.Key).ToList())
            {
                _loaded[key].Tts.Dispose();
                _loaded.Remove(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _gate.Wait();
        foreach (var entry in _loaded.Values) entry.Tts.Dispose();
        _loaded.Clear();
    }
}

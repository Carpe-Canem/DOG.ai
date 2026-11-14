namespace DogAI.Core.Voice;

/// <summary>Sound as mono samples between -1 and 1.</summary>
public sealed record AudioClip(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

/// <summary>Thrown when a voice or the speech recognition model cannot be used; the message is for the user.</summary>
public sealed class VoiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns text into speech. Implemented in DogAI.Engine (Kokoro and Piper through sherpa-onnx, and Windows voices).</summary>
public interface ISpeechSynthesizer
{
    /// <summary>Every voice that can be picked now: Windows voices and those in downloaded voice packs.</summary>
    Task<IReadOnlyList<VoiceOption>> VoicesAsync(CancellationToken ct = default);

    /// <summary>
    /// The voice that will actually speak for <paramref name="wanted"/>: itself if it is there, else the default
    /// voice from settings, else the first voice found. Null only when this PC has no voice at all.
    /// </summary>
    Task<VoiceId?> ResolveAsync(VoiceId? wanted, CancellationToken ct = default);

    /// <summary>Forget the remembered list of downloaded voices, after one is downloaded or deleted.</summary>
    void Refresh();

    /// <param name="speed">1 is normal; 0.5 to 2.</param>
    Task<AudioClip> SynthesizeAsync(string text, VoiceId voice, float speed, CancellationToken ct = default);
}

/// <summary>Plays sound on the speakers chosen in settings.</summary>
public interface IAudioOutput
{
    /// <summary>Plays the clip and returns when it has finished. Cancelling stops it at once.</summary>
    Task PlayAsync(AudioClip clip, CancellationToken ct = default);
}

/// <summary>What the speech recognition model heard.</summary>
/// <param name="NoSpeech">The model's own estimate (0–1) that there was no speech, when it gives one.</param>
public sealed record Recognized(string Text, double? NoSpeech, string? Language);

/// <summary>Turns speech into text (Whisper). Implemented in DogAI.Engine.</summary>
public interface ISpeechRecognizer
{
    /// <summary>The model file used, or null when none is downloaded.</summary>
    Task<string?> ModelPathAsync(CancellationToken ct = default);

    /// <summary>"graphics card" or "processor" once the model has been loaded; null before.</summary>
    string? RunsOn { get; }

    /// <summary>Loads the model ahead of use, so the first thing said is written down quickly.</summary>
    Task WarmUpAsync(CancellationToken ct = default);

    /// <param name="samples">16 kHz mono.</param>
    /// <param name="language">A language code such as "en", or "auto" to detect it.</param>
    Task<Recognized> RecognizeAsync(float[] samples, string language, CancellationToken ct = default);
}

/// <summary>The microphone. Implemented in DogAI.Engine; tests and walkthroughs can feed a recording instead.</summary>
public interface IMicrophone
{
    /// <summary>16 kHz mono samples as they arrive, on a background thread.</summary>
    event Action<float[]>? Samples;

    /// <summary>Raised on a background thread when recording stops because of a problem (device unplugged).</summary>
    event Action<string>? Failed;

    bool IsRecording { get; }

    /// <summary>Starts recording from the device named in settings (or the Windows default). Throws <see cref="VoiceUnavailableException"/>.</summary>
    Task StartAsync(CancellationToken ct = default);

    void Stop();
}

/// <summary>Sound devices by name, for the settings page.</summary>
public interface IAudioDevices
{
    IReadOnlyList<string> Microphones();
    IReadOnlyList<string> Speakers();
}

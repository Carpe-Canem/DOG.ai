using DogAI.Core.Settings;

namespace DogAI.Core.Voice;

public enum DictationState
{
    Off,

    /// <summary>The microphone is on and waiting (hands-free) or recording (push to talk).</summary>
    Listening,

    /// <summary>Hands-free: someone is talking.</summary>
    Hearing,

    /// <summary>The speech recognition model is writing down what was said.</summary>
    Writing,
}

/// <summary>
/// Talking instead of typing. <b>Push to talk</b>: <see cref="StartAsync"/> records until <see cref="FinishAsync"/>,
/// then the words go into the message box. <b>Hands-free</b>: <see cref="StartHandsFreeAsync"/> keeps the microphone
/// on; each time the user stops talking, what they said is sent. Hands-free does not listen while
/// <see cref="Paused"/> says so (a reply is being written or read aloud), so DOG AI never hears itself.
/// </summary>
public sealed class Dictation : IDisposable
{
    private static readonly TimeSpan MinClip = TimeSpan.FromMilliseconds(300);

    private readonly IMicrophone _mic;
    private readonly ISpeechRecognizer _recognizer;
    private readonly ISettingsStore _settings;
    private readonly object _lock = new();
    private readonly List<float> _recording = [];
    private VoiceActivity? _activity;
    private CancellationTokenSource? _cts;
    private bool _wasPaused;
    private int _writing;

    public Dictation(IMicrophone mic, ISpeechRecognizer recognizer, ISettingsStore settings)
    {
        _mic = mic;
        _recognizer = recognizer;
        _settings = settings;
        _mic.Samples += OnSamples;
        _mic.Failed += OnMicFailed;
    }

    public DictationState State { get; private set; }

    public bool HandsFree { get; private set; }

    public bool IsOn => State != DictationState.Off;

    /// <summary>Microphone loudness, 0 to 1, for a level meter.</summary>
    public float Level { get; private set; }

    /// <summary>The last problem in plain words (no microphone, no speech model…). Cleared when listening starts.</summary>
    public string? Problem { get; private set; }

    /// <summary>Hands-free stops listening while this returns true. Set by the screen that owns the message box.</summary>
    public Func<bool>? Paused { get; set; }

    /// <summary>Raised when the state, level or problem changes. Often from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Raised with what was said. <c>send</c> is true in hands-free mode. From a background thread.</summary>
    public event Action<string, bool>? Heard;

    /// <summary>Raised instead of <see cref="Heard"/> when the talking was started with <see cref="StartForOtherProgramsAsync"/>.</summary>
    public event Action<string>? HeardForOtherPrograms;

    /// <summary>True while the current push to talk is meant for another program (started with the key that works anywhere in Windows).</summary>
    public bool ForOtherPrograms { get; private set; }

    /// <summary>Push to talk: starts recording.</summary>
    public Task StartAsync() => BeginAsync(handsFree: false);

    /// <summary>
    /// Push to talk whose text goes to <see cref="HeardForOtherPrograms"/> (typed into whatever program has focus), not to the message box.
    /// </summary>
    public async Task StartForOtherProgramsAsync()
    {
        ForOtherPrograms = true;
        await BeginAsync(handsFree: false).ConfigureAwait(false);
        if (!IsOn) ForOtherPrograms = false;
    }

    /// <summary>Hands-free: listens until <see cref="Stop"/>, sending each thing said.</summary>
    public Task StartHandsFreeAsync() => BeginAsync(handsFree: true);

    private async Task BeginAsync(bool handsFree)
    {
        if (IsOn) Stop();
        Problem = null;
        var settings = await _settings.LoadAsync().ConfigureAwait(false);
        if (await _recognizer.ModelPathAsync().ConfigureAwait(false) is null)
        {
            Fail("No speech recognition model is downloaded. Get one in Models → Voice.");
            return;
        }
        _ = _recognizer.WarmUpAsync();
        lock (_lock)
        {
            _recording.Clear();
            _activity = handsFree ? new VoiceActivity(settings.HandsFreeSensitivity) : null;
            _cts = new CancellationTokenSource();
            _wasPaused = false;
            HandsFree = handsFree;
            State = DictationState.Listening;
        }
        try
        {
            await _mic.StartAsync().ConfigureAwait(false);
        }
        catch (VoiceUnavailableException e)
        {
            Stop();
            Fail(e.Message);
            return;
        }
        Changed?.Invoke();
    }

    /// <summary>Push to talk: stops recording and writes down what was said.</summary>
    public async Task FinishAsync()
    {
        float[] clip;
        CancellationToken ct;
        lock (_lock)
        {
            if (State != DictationState.Listening || HandsFree) return;
            clip = _recording.ToArray();
            _recording.Clear();
            ct = _cts?.Token ?? CancellationToken.None;
        }
        _mic.Stop();
        lock (_lock) State = DictationState.Writing;
        Changed?.Invoke();
        await WriteDownAsync(clip, send: false, ct).ConfigureAwait(false);
        lock (_lock) Off();
        Changed?.Invoke();
    }

    /// <summary>Stops listening at once; anything being written down is dropped.</summary>
    public void Stop()
    {
        _mic.Stop();
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            Off();
        }
        Changed?.Invoke();
    }

    private void Off()
    {
        _recording.Clear();
        _activity = null;
        HandsFree = false;
        ForOtherPrograms = false;
        State = DictationState.Off;
        Level = 0;
    }

    private void OnSamples(float[] samples)
    {
        IReadOnlyList<float[]> finished = [];
        CancellationToken ct;
        var changed = false;
        lock (_lock)
        {
            if (State == DictationState.Off) return;
            ct = _cts?.Token ?? CancellationToken.None;
            if (_activity is null)
            {
                _recording.AddRange(samples);
                Level = Loudness(samples);
                changed = true;
            }
            else if (Paused?.Invoke() == true || _writing > 0)
            {
                // Not listening while a reply is written or read, or while the last thing said is written down.
                if (!_wasPaused) _activity.Reset();
                _wasPaused = true;
                Level = 0;
            }
            else
            {
                _wasPaused = false;
                finished = _activity.Feed(samples, out _);
                Level = _activity.Level;
                State = _activity.Speaking ? DictationState.Hearing : DictationState.Listening;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        foreach (var utterance in finished)
            _ = WriteDownAsync(utterance, send: true, ct);
    }

    private async Task WriteDownAsync(float[] clip, bool send, CancellationToken ct)
    {
        if (clip.Length < MinClip.TotalSeconds * VoiceActivity.SampleRate) return;
        Interlocked.Increment(ref _writing);
        SetState(DictationState.Writing);
        try
        {
            var language = (await _settings.LoadAsync(ct).ConfigureAwait(false)).SpeechLanguage;
            var heard = await _recognizer.RecognizeAsync(clip, language, ct).ConfigureAwait(false);
            var text = Transcript.Clean([heard.Text]);
            var length = TimeSpan.FromSeconds(clip.Length / (double)VoiceActivity.SampleRate);
            if (!ct.IsCancellationRequested && !Transcript.IsLikelyInvented(text, length, heard.NoSpeech))
            {
                if (ForOtherPrograms) HeardForOtherPrograms?.Invoke(text);
                else Heard?.Invoke(text, send);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (VoiceUnavailableException e)
        {
            Fail(e.Message);
            Stop();
        }
        finally
        {
            Interlocked.Decrement(ref _writing);
            lock (_lock)
            {
                if (State == DictationState.Writing) State = HandsFree ? DictationState.Listening : DictationState.Off;
            }
            Changed?.Invoke();
        }
    }

    private void SetState(DictationState state)
    {
        lock (_lock)
        {
            if (State == DictationState.Off) return; // stopped meanwhile
            State = state;
        }
        Changed?.Invoke();
    }

    private void Fail(string message)
    {
        Problem = message;
        Changed?.Invoke();
    }

    private void OnMicFailed(string message)
    {
        Stop();
        Fail(message);
    }

    private static float Loudness(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Clamp(Math.Sqrt(sum / samples.Length) * 8, 0, 1);
    }

    public void Dispose()
    {
        _mic.Samples -= OnSamples;
        _mic.Failed -= OnMicFailed;
        Stop();
    }
}

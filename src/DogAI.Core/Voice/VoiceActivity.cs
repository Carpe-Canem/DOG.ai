namespace DogAI.Core.Voice;

/// <summary>
/// Finds where someone starts and stops talking in a stream of 16 kHz microphone samples, for hands-free mode.
/// It compares the loudness of each 20 ms frame with the room's background level (which it keeps learning while
/// nobody talks): speech starts after a run of loud frames and ends after a stretch of quiet. A little audio from
/// before the start is kept so the first word is not clipped.
/// </summary>
public sealed class VoiceActivity
{
    public const int SampleRate = 16_000;
    public const int FrameSamples = SampleRate / 50; // 20 ms

    private const int StartFrames = 6;        // 120 ms of loud frames starts an utterance
    private const int PreRollFrames = 20;     // 400 ms kept from before the start
    private const int MinSpeechFrames = 15;   // utterances shorter than 300 ms are dropped (clicks, coughs)
    private const int MaxSpeechFrames = 50 * 30; // 30 seconds, then it is cut and transcribed anyway
    private const float MinLevel = 0.004f;    // quietest level ever treated as speech (RMS, full scale = 1)

    private readonly float _ratio;
    private readonly int _endFrames;
    private readonly List<float> _frame = new(FrameSamples);
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _speech = [];
    private float _noise = 0.002f;
    private int _loudRun;
    private int _quietRun;
    private bool _speaking;
    private int _speechFrames;

    /// <param name="sensitivity">0 = needs clear, loud speech (noisy rooms); 1 = normal; 2 = picks up quiet voices.</param>
    /// <param name="endSilence">How long a pause ends what was said.</param>
    public VoiceActivity(int sensitivity = 1, TimeSpan? endSilence = null)
    {
        _ratio = sensitivity switch { <= 0 => 4.5f, 1 => 3f, _ => 2f };
        _endFrames = Math.Max(5, (int)((endSilence ?? TimeSpan.FromMilliseconds(900)).TotalMilliseconds / 20));
    }

    /// <summary>True while it thinks someone is talking.</summary>
    public bool Speaking => _speaking;

    /// <summary>The loudness of the latest frame, 0 to 1, for a level meter.</summary>
    public float Level { get; private set; }

    /// <summary>
    /// Takes the next samples. Returns each finished utterance (16 kHz mono samples), usually none.
    /// <paramref name="started"/> is set when speech began during these samples.
    /// </summary>
    public IReadOnlyList<float[]> Feed(ReadOnlySpan<float> samples, out bool started)
    {
        started = false;
        List<float[]>? done = null;
        foreach (var s in samples)
        {
            _frame.Add(s);
            if (_frame.Count < FrameSamples) continue;
            var frame = _frame.ToArray();
            _frame.Clear();
            if (Frame(frame, ref started) is { } utterance) (done ??= []).Add(utterance);
        }
        return (IReadOnlyList<float[]>?)done ?? [];
    }

    /// <summary>Forgets everything heard so far (used while DOG AI itself is speaking).</summary>
    public void Reset()
    {
        _frame.Clear();
        _preRoll.Clear();
        _speech.Clear();
        _loudRun = _quietRun = _speechFrames = 0;
        _speaking = false;
        Level = 0;
    }

    private float[]? Frame(float[] frame, ref bool started)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        var rms = (float)Math.Sqrt(sum / frame.Length);
        Level = Math.Clamp(rms * 8, 0, 1);
        var threshold = Math.Max(MinLevel, _noise * _ratio);
        var loud = rms > threshold;

        if (!_speaking)
        {
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
            _loudRun = loud ? _loudRun + 1 : 0;
            // Learn the background level from quiet frames only, quickly downwards and slowly upwards.
            if (!loud) _noise = rms < _noise ? _noise * 0.9f + rms * 0.1f : _noise * 0.995f + rms * 0.005f;
            if (_loudRun < StartFrames) return null;
            _speaking = true;
            started = true;
            _speech.Clear();
            foreach (var f in _preRoll) _speech.AddRange(f);
            _preRoll.Clear();
            _speechFrames = _loudRun;
            _quietRun = 0;
            return null;
        }

        _speech.AddRange(frame);
        _speechFrames++;
        _quietRun = loud ? 0 : _quietRun + 1;
        if (_quietRun < _endFrames && _speechFrames < MaxSpeechFrames) return null;

        _speaking = false;
        _loudRun = 0;
        var heard = _speechFrames - _quietRun;
        var utterance = _speech.ToArray();
        _speech.Clear();
        _speechFrames = 0;
        _quietRun = 0;
        return heard >= MinSpeechFrames ? utterance : null;
    }
}

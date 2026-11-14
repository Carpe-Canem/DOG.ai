using DogAI.Core.Settings;
using DogAI.Core.Voice;
using System.Runtime.Versioning;
using NAudio.Wave;

namespace DogAI.Engine.Voice;

/// <summary>Sound devices as Windows names them (the old Windows sound API cuts names at 31 characters).</summary>
[SupportedOSPlatform("windows")]
public sealed class AudioDevices : IAudioDevices
{
    public IReadOnlyList<string> Microphones()
    {
        try
        {
            return Enumerable.Range(0, WaveIn.DeviceCount).Select(i => WaveIn.GetCapabilities(i).ProductName).ToList();
        }
        catch (Exception e) when (e is NAudio.MmException or DllNotFoundException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> Speakers()
    {
        try
        {
            return Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();
        }
        catch (Exception e) when (e is NAudio.MmException or DllNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The device number for a saved name; -1 (the Windows default) when none is saved or it is not plugged in.</summary>
    internal static int Find(IReadOnlyList<string> names, string? wanted)
    {
        if (string.IsNullOrEmpty(wanted)) return -1;
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>The microphone, recorded as 16 kHz mono (what Whisper wants) in 50 ms pieces.</summary>
[SupportedOSPlatform("windows")]
public sealed class Microphone(ISettingsStore settings, AudioDevices devices) : IMicrophone, IDisposable
{
    private readonly object _lock = new();
    private WaveIn? _device;

    public event Action<float[]>? Samples;
    public event Action<string>? Failed;

    public bool IsRecording { get { lock (_lock) return _device is not null; } }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var wanted = (await settings.LoadAsync(ct).ConfigureAwait(false)).Microphone;
        lock (_lock)
        {
            if (_device is not null) return;
            var names = devices.Microphones();
            if (names.Count == 0) throw new VoiceUnavailableException("No microphone was found. Plug one in, or check Windows sound settings.");
            var device = new WaveIn
            {
                DeviceNumber = AudioDevices.Find(names, wanted),
                WaveFormat = new WaveFormat(VoiceActivity.SampleRate, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 4,
            };
            device.DataAvailable += OnData;
            device.RecordingStopped += OnStopped;
            try
            {
                device.StartRecording();
            }
            catch (NAudio.MmException e)
            {
                device.Dispose();
                throw new VoiceUnavailableException("The microphone could not be opened: " + e.Message +
                                                    ". Windows may be blocking it: check Settings → Privacy & security → Microphone.", e);
            }
            _device = device;
        }
    }

    public void Stop()
    {
        WaveIn? device;
        lock (_lock)
        {
            device = _device;
            _device = null;
        }
        if (device is null) return;
        device.DataAvailable -= OnData;
        device.RecordingStopped -= OnStopped;
        try { device.StopRecording(); }
        catch (NAudio.MmException) { }
        device.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        Samples?.Invoke(samples);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        Stop();
        Failed?.Invoke("The microphone stopped: " + e.Exception.Message);
    }

    public void Dispose() => Stop();
}

/// <summary>Plays clips on the speakers chosen in settings.</summary>
[SupportedOSPlatform("windows")]
public sealed class Speakers(ISettingsStore settings, AudioDevices devices) : IAudioOutput
{
    public async Task PlayAsync(AudioClip clip, CancellationToken ct = default)
    {
        if (clip.Samples.Length == 0) return;
        var wanted = (await settings.LoadAsync(ct).ConfigureAwait(false)).Speakers;
        var pcm = new byte[clip.Samples.Length * 2];
        for (var i = 0; i < clip.Samples.Length; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(clip.Samples[i], -1f, 1f) * short.MaxValue));

        using var source = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(clip.SampleRate, 16, 1));
        using var player = new WaveOut { DeviceNumber = AudioDevices.Find(devices.Speakers(), wanted), BufferMilliseconds = 75, NumberOfBuffers = 2 };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) finished.TrySetException(new VoiceUnavailableException("The speakers stopped: " + e.Exception.Message, e.Exception));
            else finished.TrySetResult();
        };
        try
        {
            player.Init(source);
            player.Play();
        }
        catch (NAudio.MmException e)
        {
            throw new VoiceUnavailableException("The speakers could not be opened: " + e.Message, e);
        }
        await using (ct.Register(() => player.Stop()))
        {
            await finished.Task.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// For tests and walkthroughs (DOGAI_TEST_MIC): a "microphone" that plays a WAV file in real time, then silence.
/// </summary>
public sealed class RecordingMicrophone(string wavPath) : IMicrophone
{
    private CancellationTokenSource? _cts;

    public event Action<float[]>? Samples;
    public event Action<string>? Failed;

    public bool IsRecording => _cts is not null;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        AudioClip clip;
        try
        {
            clip = Wav.Read(File.ReadAllBytes(wavPath));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new VoiceUnavailableException("The test recording could not be read: " + e.Message, e);
        }
        var samples = Wav.Resample(clip.Samples, clip.SampleRate, VoiceActivity.SampleRate);
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            const int piece = VoiceActivity.SampleRate / 20; // 50 ms
            var silence = new float[piece];
            try
            {
                // Half a second of quiet first so hands-free mode learns the "room".
                for (var i = 0; i < 10; i++) { Samples?.Invoke(silence); await Task.Delay(50, cts.Token); }
                for (var at = 0; at < samples.Length; at += piece)
                {
                    Samples?.Invoke(samples[at..Math.Min(samples.Length, at + piece)]);
                    await Task.Delay(50, cts.Token);
                }
                while (true) { Samples?.Invoke(silence); await Task.Delay(50, cts.Token); }
            }
            catch (OperationCanceledException)
            {
            }
        });
        _ = Failed; // never raised: a file cannot be unplugged
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }
}

/// <summary>For tests and walkthroughs (DOGAI_TEST_SPEAKERS): writes each clip to a numbered WAV file instead of playing it.</summary>
public sealed class RecordingSpeakers(string folder) : IAudioOutput
{
    private int _count;

    public async Task PlayAsync(AudioClip clip, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var n = Interlocked.Increment(ref _count);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"clip-{n:000}.wav"), Wav.Write(clip), ct).ConfigureAwait(false);
        // Take as long as playing would, so "speaking" lasts as long in the app.
        await Task.Delay(clip.Duration, ct).ConfigureAwait(false);
    }
}

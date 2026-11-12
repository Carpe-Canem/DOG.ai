using System.Threading.Channels;
using DogAI.Core.Settings;

namespace DogAI.Core.Voice;

/// <summary>
/// Reads text aloud, one piece at a time: the next piece is turned into sound while the current one plays, so there
/// are no gaps. Text can be given whole (<see cref="Speak"/>) or as it streams in (<see cref="Begin"/>,
/// <see cref="Feed"/>, <see cref="End"/>). Starting something new stops what was being read, unless it is queued
/// behind it (group members answering one after another).
/// </summary>
public sealed class ReadAloud(ISpeechSynthesizer synthesizer, IAudioOutput output, ISettingsStore settings) : IDisposable
{
    private readonly object _lock = new();
    private readonly List<Session> _sessions = [];

    /// <summary>What is being read now, as given by the caller ("msg:12"), or null when quiet.</summary>
    public string? Key { get { lock (_lock) return _sessions.FirstOrDefault()?.Key; } }

    public bool IsSpeaking => Key is not null;

    /// <summary>True while this key is being read or waits its turn.</summary>
    public bool Has(string key)
    {
        lock (_lock) return _sessions.Any(s => s.Key == key);
    }

    /// <summary>Why reading last stopped early, in plain words. Cleared when something new starts.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised when reading starts or stops, or a problem appears. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Reads a whole text (markdown) aloud.</summary>
    /// <param name="voice">Null means the default voice.</param>
    public void Speak(string key, string markdown, VoiceId? voice)
    {
        var session = Start(key, Task.FromResult(voice), queue: false);
        session.End(markdown);
    }

    /// <summary>Starts reading text that is still being written. The voice may still be being looked up.</summary>
    /// <param name="queue">Wait for what is being read to finish instead of stopping it.</param>
    public void Begin(string key, Task<VoiceId?> voice, bool queue = false) => Start(key, voice, queue);

    /// <summary>The whole text so far of what <paramref name="key"/> is reading.</summary>
    public void Feed(string key, string textSoFar) => Find(key)?.Feed(textSoFar);

    /// <summary>The text is complete: read what is left, then stop.</summary>
    public void End(string key, string finalText) => Find(key)?.End(finalText);

    /// <summary>Stops reading at once, including anything queued.</summary>
    public void Stop()
    {
        List<Session> old;
        lock (_lock)
        {
            old = _sessions.ToList();
            _sessions.Clear();
        }
        if (old.Count == 0) return;
        foreach (var s in old) s.Cancel();
        Changed?.Invoke();
    }

    /// <summary>Waits until nothing is being read. For tests.</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task done;
            lock (_lock)
            {
                if (_sessions.Count == 0) return;
                done = _sessions[^1].Done;
            }
            await done.ConfigureAwait(false);
        }
    }

    private Session? Find(string key)
    {
        lock (_lock) return _sessions.LastOrDefault(s => s.Key == key);
    }

    private Session Start(string key, Task<VoiceId?> voice, bool queue)
    {
        var session = new Session(key);
        List<Session> old = [];
        lock (_lock)
        {
            if (!queue)
            {
                old = _sessions.ToList();
                _sessions.Clear();
            }
            session.After = _sessions.Count > 0 ? _sessions[^1].Done : Task.CompletedTask;
            _sessions.Add(session);
            // RunAsync yields before doing anything, so starting it under the lock is safe and Done is set before
            // anything else can queue behind this session.
            session.Done = RunAsync(session, voice);
        }
        foreach (var s in old) s.Cancel();
        Problem = null;
        Changed?.Invoke();
        return session;
    }

    private async Task RunAsync(Session session, Task<VoiceId?> wanted)
    {
        await Task.Yield(); // let the caller finish starting the session first
        var ct = session.Token;
        var clips = Channel.CreateBounded<AudioClip>(2);
        try
        {
            var current = await settings.LoadAsync(ct).ConfigureAwait(false);
            var speed = (float)Math.Clamp(current.VoiceSpeed, 0.5, 2);
            var voice = await synthesizer.ResolveAsync(await wanted.ConfigureAwait(false), ct).ConfigureAwait(false)
                        ?? throw new VoiceUnavailableException("No voice is available on this PC.");

            var play = Task.Run(async () =>
            {
                try { await session.After.ConfigureAwait(false); }
                catch { /* the one before failed or stopped; this one still plays */ }
                await foreach (var clip in clips.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await output.PlayAsync(clip, ct).ConfigureAwait(false);
            }, ct);

            try
            {
                await foreach (var piece in session.Pieces.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var words = SpeechText.ForSpeech(piece);
                    if (words.Length == 0) continue;
                    var clip = await synthesizer.SynthesizeAsync(words, voice, speed, ct).ConfigureAwait(false);
                    await clips.Writer.WriteAsync(clip, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                clips.Writer.TryComplete();
            }
            await play.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is VoiceUnavailableException or IOException or InvalidOperationException)
        {
            Problem = e.Message;
        }
        finally
        {
            bool removed;
            lock (_lock) removed = _sessions.Remove(session);
            session.Dispose();
            if (removed || Problem is not null) Changed?.Invoke();
        }
    }

    public void Dispose() => Stop();

    private sealed class Session(string key) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SentenceSplitter _splitter = new();
        private readonly object _lock = new();
        private bool _ended;

        public string Key { get; } = key;
        public Channel<string> Pieces { get; } = Channel.CreateUnbounded<string>();
        public Task Done { get; set; } = Task.CompletedTask;
        public Task After { get; set; } = Task.CompletedTask;
        public CancellationToken Token => _cts.Token;

        public void Feed(string textSoFar)
        {
            lock (_lock)
            {
                if (_ended) return;
                foreach (var piece in _splitter.Feed(textSoFar)) Pieces.Writer.TryWrite(piece);
            }
        }

        public void End(string finalText)
        {
            lock (_lock)
            {
                if (_ended) return;
                _ended = true;
                foreach (var piece in _splitter.Finish(finalText)) Pieces.Writer.TryWrite(piece);
                Pieces.Writer.TryComplete();
            }
        }

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
            Pieces.Writer.TryComplete();
        }

        public void Dispose() => _cts.Dispose();
    }
}

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
    /// <param name="cast">Stories: characters whose spoken lines are read in their own voices.</param>
    public void Speak(string key, string markdown, VoiceId? voice, VoiceCast? cast = null)
    {
        var session = Start(key, Task.FromResult(voice), queue: false, Task.FromResult(cast));
        session.End(markdown);
    }

    /// <summary>Starts reading text that is still being written. The voice may still be being looked up.</summary>
    /// <param name="queue">Wait for what is being read to finish instead of stopping it.</param>
    /// <param name="cast">Stories: characters whose spoken lines are read in their own voices; may still be being looked up.</param>
    public void Begin(string key, Task<VoiceId?> voice, bool queue = false, Task<VoiceCast?>? cast = null) => Start(key, voice, queue, cast);

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

    private Session Start(string key, Task<VoiceId?> voice, bool queue, Task<VoiceCast?>? cast = null)
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
            session.Done = RunAsync(session, voice, cast ?? Task.FromResult<VoiceCast?>(null));
        }
        foreach (var s in old) s.Cancel();
        Problem = null;
        Changed?.Invoke();
        return session;
    }

    private async Task RunAsync(Session session, Task<VoiceId?> wanted, Task<VoiceCast?> wantedCast)
    {
        await Task.Yield(); // let the caller finish starting the session first
        var ct = session.Token;
        var clips = Channel.CreateBounded<AudioClip>(2);
        try
        {
            var current = await settings.LoadAsync(ct).ConfigureAwait(false);
            var speed = (float)Math.Clamp(current.VoiceSpeed, 0.5, 2);
            var narrator = await synthesizer.ResolveAsync(await wanted.ConfigureAwait(false), ct).ConfigureAwait(false)
                           ?? throw new VoiceUnavailableException("No voice is available on this PC.");
            var cast = await wantedCast.ConfigureAwait(false);
            ISpokenSplitter splitter = cast is { IsEmpty: false } ? new VoicedPieces(cast) : new SentencePieces();
            var voices = new Dictionary<int, VoiceId>();

            // A character's voice that is not on this PC any more reads in the narrator's voice, not the default one.
            async Task<VoiceId> VoiceOf(int? speaker)
            {
                if (speaker is not { } s || cast is null || s >= cast.Voices.Count || cast.Voices[s] is not { } own) return narrator;
                if (!voices.TryGetValue(s, out var found))
                {
                    var resolved = await synthesizer.ResolveAsync(own, ct).ConfigureAwait(false);
                    voices[s] = found = resolved is not null && resolved.Equals(own) ? own : narrator;
                }
                return found;
            }

            var play = Task.Run(async () =>
            {
                try { await session.After.ConfigureAwait(false); }
                catch { /* the one before failed or stopped; this one still plays */ }
                await foreach (var clip in clips.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await output.PlayAsync(clip, ct).ConfigureAwait(false);
            }, ct);

            async Task SayAsync(SpokenPiece piece)
            {
                var words = piece.Plain ? piece.Text : SpeechText.ForSpeech(piece.Text);
                if (words.Length == 0) return;
                var voice = await VoiceOf(piece.Speaker).ConfigureAwait(false);
                AudioClip clip;
                try
                {
                    clip = await synthesizer.SynthesizeAsync(words, voice, speed, ct).ConfigureAwait(false);
                }
                catch (VoiceUnavailableException) when (!voice.Equals(narrator))
                {
                    voices[piece.Speaker!.Value] = narrator;
                    clip = await synthesizer.SynthesizeAsync(words, narrator, speed, ct).ConfigureAwait(false);
                }
                await clips.Writer.WriteAsync(clip, ct).ConfigureAwait(false);
            }

            try
            {
                var text = "";
                while (await session.Texts.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    // Only the newest text matters: it holds everything the older ones did.
                    while (session.Texts.Reader.TryRead(out var newer)) text = newer;
                    foreach (var piece in splitter.Feed(text)) await SayAsync(piece).ConfigureAwait(false);
                }
                foreach (var piece in splitter.Finish(text)) await SayAsync(piece).ConfigureAwait(false);
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
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A voice library failing in a way it does not describe: still say so rather than go quiet.
            Problem = "Reading aloud stopped because of an unexpected problem: " + e.Message;
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
        private readonly object _lock = new();
        private bool _ended;

        public string Key { get; } = key;

        /// <summary>The whole text so far, each time it grows. How it is cut into pieces is decided once the voices are known.</summary>
        public Channel<string> Texts { get; } = Channel.CreateUnbounded<string>();
        public Task Done { get; set; } = Task.CompletedTask;
        public Task After { get; set; } = Task.CompletedTask;
        public CancellationToken Token => _cts.Token;

        public void Feed(string textSoFar)
        {
            lock (_lock)
            {
                if (_ended) return;
                Texts.Writer.TryWrite(textSoFar);
            }
        }

        public void End(string finalText)
        {
            lock (_lock)
            {
                if (_ended) return;
                _ended = true;
                Texts.Writer.TryWrite(finalText);
                Texts.Writer.TryComplete();
            }
        }

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
            Texts.Writer.TryComplete();
        }

        public void Dispose() => _cts.Dispose();
    }
}

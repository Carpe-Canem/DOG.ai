using DogAI.Core.Video;

namespace DogAI.Core.Images;

/// <summary>The main graphics card's memory, measured now, in bytes.</summary>
/// <param name="PictureBytes">What the picture engine holds (0 when it is not running or cannot be measured).</param>
/// <param name="ChatBytes">What the chat model holds (0 when none is loaded or it cannot be measured).</param>
/// <param name="FreeRamBytes">The PC's own free memory (RAM), or null when it was not measured. Only a video made by a plan asks for it.</param>
public sealed record CardMemory(long TotalBytes, long FreeBytes, long PictureBytes = 0, long ChatBytes = 0, long? FreeRamBytes = null);

/// <summary>Measures the graphics card. Null when it cannot be measured (no NVIDIA card or driver tool).</summary>
public interface ICardMeter
{
    Task<CardMemory?> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Asked for before a chat model is loaded into graphics memory, and kept until the load has ended. It waits for a
/// picture being drawn and makes room for the model. Dispose it when the load is over.
/// </summary>
/// <param name="waking">True when the model that was set aside for a picture is coming back; false for any other load.</param>
public delegate Task<IDisposable> CardTurn(bool waking, CancellationToken ct);

/// <summary>The chat model as the graphics-card coordinator sees it. Implemented by the engine manager; faked in tests.</summary>
public interface IChatModelSlot
{
    /// <summary>A chat model is in graphics memory now.</summary>
    bool IsLoaded { get; }

    /// <summary>The chat model was set aside for a picture; it loads again the next time it is asked for.</summary>
    bool IsResting { get; }

    /// <summary>A reply is being written.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Takes the loaded model out of graphics memory but remembers it, so the next request brings it back. This is not
    /// the owner unloading it. False when nothing was set aside: no model is loaded, or a reply is being written.
    /// </summary>
    Task<bool> RestAsync();

    /// <summary>
    /// When a chat model is being loaded only because a page opened (nobody has asked it anything yet), stops that load
    /// and remembers the model as set aside, so the next request loads it. False, with nothing changed, for any other load
    /// (the owner's, or one bringing the model back for a request) or when nothing is loading. Returns once the load has stopped.
    /// </summary>
    Task<bool> SetAsideLoadAsync();

    /// <summary>Set by the coordinator. Every load of a chat model takes a turn first (see <see cref="CardTurn"/>).</summary>
    CardTurn? CardTurn { get; set; }
}

/// <summary>
/// Something besides the chat model and the picture model that can hold graphics memory: the document search model, a
/// speech model. It is let go when the card needs the room and it is not being used, and by "Unload everything". It
/// comes back by itself the next time it is needed.
/// </summary>
public interface ICardGuest
{
    /// <summary>What to call it in a sentence, e.g. "the document search model".</summary>
    string Name { get; }

    /// <summary>It is in graphics memory now (or on its way in).</summary>
    bool HoldsMemory { get; }

    /// <summary>It is being used right now, so it must not be let go.</summary>
    bool IsBusy { get; }

    /// <summary>Gives its graphics memory back. False, with nothing changed, when it is being used.</summary>
    Task<bool> LetGoAsync();

    /// <summary>
    /// Leaves the card for a video (M21): a video takes the whole card, and a guest working beside it slowed the clip
    /// by half (the document search model indexing on the card: 3 min 40 s where the estimate said 2 min 26 s). So a
    /// guest that is being used is not cut off and does not stay either: it finishes what it is doing right now (one
    /// batch of passages, one thing said), gives its graphics memory back, and carries on on the processor, because
    /// the door is closed by then (<see cref="CardDoor"/>). True when it is off the card. A guest that knows no
    /// better is let go only when idle, as for a picture.
    /// </summary>
    Task<bool> StepOffAsync(CancellationToken ct = default) => LetGoAsync();
}

/// <summary>
/// Whether a guest may come onto the graphics card now (M21). A video takes the whole card, and a guest loads without a
/// card turn, so one could come onto the card while a clip was at its peak. The coordinator closes the door from the
/// moment a clip is asked for until it has ended; a guest asks at the door before it loads into graphics memory, and
/// with the door closed it runs on the processor meanwhile. Nothing else closes it: beside pictures and chat models a
/// guest comes and goes as it did before.
/// </summary>
public sealed class CardDoor
{
    private readonly object _lock = new();
    private int _closed;
    private int _entering;
    private TaskCompletionSource? _empty;

    /// <summary>True while a video is being made: nothing new comes onto the card.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_lock) return _closed > 0;
        }
    }

    /// <summary>Raised when the door closes or opens. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Asked by a guest before it loads into graphics memory, or uses the card. Null while the door is closed: the
    /// guest stays off the card. Otherwise something to dispose when the load (or the use) has ended; a video that is
    /// asked for meanwhile waits for it before it measures the card.
    /// </summary>
    public IDisposable? Enter()
    {
        lock (_lock)
        {
            if (_closed > 0) return null;
            _entering++;
            return new Entry(this);
        }
    }

    /// <summary>Closes the door for a video. Every call is answered by one <see cref="Open"/>.</summary>
    public void Close()
    {
        lock (_lock) _closed++;
        Changed?.Invoke();
    }

    public void Open()
    {
        lock (_lock) _closed = Math.Max(0, _closed - 1);
        Changed?.Invoke();
    }

    /// <summary>Returns once no guest is on its way onto the card any more. Call it after <see cref="Close"/>.</summary>
    public async Task EmptyAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_entering == 0) return;
                _empty ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _empty.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Leave()
    {
        TaskCompletionSource? done = null;
        lock (_lock)
        {
            if (--_entering > 0) return;
            _entering = 0;
            done = _empty;
            _empty = null;
        }
        done?.TrySetResult();
    }

    private sealed class Entry(CardDoor door) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) door.Leave();
        }
    }
}

/// <summary>
/// What the owner is told while a guest runs on the processor because a video is being made (M21). Measured in the
/// app while a Quick clip was made: the document search model indexed 6 to 9 passages a second on the processor
/// (100 to 108 on the card) and answered a question in under a tenth of a second; the speech model wrote down 8.5
/// seconds of talk in 10 seconds (0.6 on the card); neither took any of the card, and the clip took 2 min 18 s to
/// 2 min 31 s against an estimate of 2 min 26 s.
/// </summary>
public static class CardGuestWords
{
    /// <summary>The document search model's state on the Documents screen while it runs on the processor and a video is being made.</summary>
    public const string SearchOnProcessor = "On the processor while a video is made";

    /// <summary>The same once the video is done: it moves by itself, at its next use.</summary>
    public const string SearchStillOnProcessor = "On the processor until it is next used";

    /// <summary>The line under that state.</summary>
    public const string SearchAbout = "A video is being made and has the whole graphics card, so documents are indexed and searched on the processor meanwhile. That is slower: about 8 passages a second in place of 100. The model moves back to the graphics card by itself the first time it is used after the video.";

    /// <summary>Added to the chat box's dictation line, and said on the Video page while something is dictated.</summary>
    public const string SpeechOnProcessor = "A video is being made, so what you say is written down on the processor meanwhile: that takes about as long as you spoke.";

    /// <summary>On the Video page while a clip is made and documents are indexed meanwhile.</summary>
    public const string IndexingMeanwhile = "Documents are being indexed on the processor while the clip is made, which is slower. Indexing goes back to the graphics card by itself when the clip is done.";

    /// <summary>On the Video page while a clip is made and the document search model was needed meanwhile.</summary>
    public const string SearchMeanwhile = "The document search model runs on the processor while the clip is made.";

    /// <summary>
    /// The line on the Video page, the screen the owner is on while a clip is made, when a guest works on the
    /// processor meanwhile; null when none does. Leaving that page stops the clip, so no other screen shows it for long.
    /// </summary>
    /// <param name="searchOnProcessor">The document search model runs on the processor now.</param>
    /// <param name="indexing">Documents are being indexed.</param>
    /// <param name="dictating">Something is being dictated, and the speech model keeps off the card.</param>
    public static string? Meanwhile(bool searchOnProcessor, bool indexing, bool dictating)
    {
        var parts = new List<string>();
        if (searchOnProcessor) parts.Add(indexing ? IndexingMeanwhile : SearchMeanwhile);
        if (dictating) parts.Add(SpeechOnProcessor);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}

/// <summary>
/// What the owner is told about a chat model that was set aside, by what it made room for: pictures, or a video
/// (M21 step 6: the status bar said "set aside for pictures" after a video too). One place, so the status bar, the
/// model button and the helpers that wait for the model agree. <see cref="CardCoordinator.SetAsideForVideo"/> says which.
/// </summary>
public static class SetAsideWords
{
    private static string For(bool video) => video ? "a video" : "pictures";

    /// <summary>The status bar: "Huihui · set aside for a video, back with your next message".</summary>
    public static string Status(string? model, bool video) => $"{model} · set aside for {For(video)}, back with your next message";

    /// <summary>The model button's notice, after the model's name.</summary>
    public static string Notice(bool video) => $"was set aside to make room for {For(video)}. It loads again by itself with your next message.";

    /// <summary>The start of a helper's line while the model comes back: "The chat model was set aside for a video."</summary>
    public static string Away(bool video) => $"The chat model was set aside for {For(video)}.";

    /// <summary>The same with "to make room".</summary>
    public static string MadeRoom(bool video) => $"The chat model was set aside to make room for {For(video)}.";
}

/// <summary>What "Unload everything" did.</summary>
/// <param name="Gone">What was taken out of graphics memory, as names for a sentence.</param>
/// <param name="Stayed">What was left because it is being used.</param>
/// <param name="Refused">Why nothing was done (a reply or a picture is being made), or null.</param>
public sealed record CardCleared(IReadOnlyList<string> Gone, IReadOnlyList<string> Stayed, string? Refused = null)
{
    /// <summary>One or two sentences for the owner.</summary>
    public string Text
    {
        get
        {
            if (Refused is not null) return Refused;
            var stayed = Stayed.Count == 0 ? "" : $" {Sentence(Stayed)} {(Stayed.Count == 1 ? "is" : "are")} being used, so {(Stayed.Count == 1 ? "it stays" : "they stay")}.";
            return (Gone.Count == 0 ? (Stayed.Count == 0 ? "Nothing was loaded." : "Nothing else was loaded.") : $"Unloaded {List(Gone)}.") + stayed;
        }
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static string Sentence(IReadOnlyList<string> names)
    {
        var list = List(names);
        return list.Length == 0 ? list : char.ToUpperInvariant(list[0]) + list[1..];
    }
}

/// <summary>What the coordinator did about the chat model for the latest picture.</summary>
public enum CardChoice
{
    /// <summary>No chat model was in graphics memory, so there was nothing to decide.</summary>
    NothingLoaded,

    /// <summary>Both fit: the chat model stayed where it was.</summary>
    KeptBoth,

    /// <summary>They did not fit (or the card could not be measured): the chat model was set aside.</summary>
    SetChatAside,
}

/// <param name="NeedBytes">What the picture was expected to need at its peak.</param>
/// <param name="FreeBytes">Free graphics memory when it was decided; -1 when it could not be measured.</param>
public sealed record CardDecision(CardChoice Choice, long NeedBytes, long FreeBytes);

/// <summary>How much graphics memory a picture needs. The numbers come from the M9 trial on SDXL checkpoints.</summary>
public static class PictureMemory
{
    private const long Gb = 1L << 30;

    /// <summary>Used when the checkpoint's size is unknown: a usual SDXL file.</summary>
    public const long TypicalCheckpointBytes = 7 * Gb;

    /// <summary>
    /// Kept free on top of what a picture or a model is expected to need: Windows and other programs take and give back
    /// a few hundred megabytes all the time, and running out does not fail on Windows, it makes everything crawl.
    /// </summary>
    public const long Margin = Gb;

    /// <summary>
    /// The peak for one picture: the model itself (the file plus a quarter: a 6.5 GB checkpoint held 8 GB), the LoRAs,
    /// and working memory that grows with the picture's size (about 4 GB per million pixels of the largest pass:
    /// 832×1216 peaked at 11.5 GB, the same with hires fix ×1.5 at 17 GB). It errs a little on the high side.
    /// </summary>
    /// <remarks>
    /// A picture large enough to be decoded in tiles (<see cref="ImageRequest.DecodesInTiles"/>) needs far less working
    /// memory: about half a gigabyte per million pixels. Measured in the M16 step 5 trial above an idle model of 6.9 GB
    /// (a 6.5 GB checkpoint): 1664×2432 peaked at 8.7 GB, 2112×3072 at 10.0 GB, 2496×3648 at 11.4 GB.
    /// </remarks>
    public static long Need(long checkpointBytes, long loraBytes, ImageRequest request)
    {
        var file = checkpointBytes > 0 ? checkpointBytes : TypicalCheckpointBytes;
        var (width, height) = request.LargestPass;
        var megapixels = width * (double)height / 1_000_000.0;
        var perMegapixel = request.DecodesInTiles ? Gb / 2 : 4 * Gb;
        return file + file / 4 + Math.Max(0, loraBytes) + (long)(megapixels * perMegapixel);
    }

    /// <summary>
    /// What enlarging a picture with an upscaler file needs: it works through the picture in small pieces, so the
    /// size of the picture does not matter. The trial measured half a gigabyte; this is twice that.
    /// </summary>
    public const long EnlargeNeed = Gb;

    /// <summary>
    /// True when a picture needing <paramref name="needBytes"/> fits beside everything that is on the card now. What the
    /// picture engine already holds counts towards the need. An unmeasured card never fits: better one swap too many.
    /// </summary>
    public static bool FitsBeside(CardMemory? now, long needBytes) =>
        now is not null && now.FreeBytes >= Math.Max(0, needBytes - now.PictureBytes) + Margin;
}

/// <summary>
/// Shares the graphics card between the chat model and the picture model. It is the picture engine the rest of DOG.ai
/// uses (<see cref="IImageEngine"/>), wrapped around the real one, so no picture can be drawn without it.
/// <para>
/// Per picture: when both fit (free memory is measured, and the picture's need is estimated from its size), the chat
/// model stays. Otherwise the chat model is set aside, the picture is drawn, and the chat model is loaded again only
/// when it is next asked for, so "Try again" and a run of pictures cost one swap, not one each. Setting aside is not
/// the owner unloading: a model the owner unloaded stays unloaded, and one that was never loaded is never loaded here.
/// </para>
/// <para>
/// One thing at a time changes what is on the card. A picture waits for a reply being written (it is never cut off)
/// and for a chat model that is loading. Anything that wants the chat model while a picture is being drawn waits for
/// the picture: a picture takes seconds, and the message that was sent is then answered instead of failing. Before the
/// chat model comes back, the idle picture model is let go unless both fit.
/// </para>
/// <para>
/// The other things on the card (<see cref="ICardGuest"/>: the document search model, a speech model) make way first:
/// when a picture or a returning chat model would not fit, the idle ones are let go before anything larger is moved.
/// "Unload everything" (<see cref="ClearAsync"/>) is the owner emptying the card.
/// </para>
/// <para>
/// A video (M20) takes the whole card (<see cref="MakeAsync"/>): under one turn the chat model is set aside and the
/// picture model and every idle guest are let go, the clip is made, and the video program is gone again before the
/// turn is given back. The chat model comes back as after a picture: when it is next asked for.
/// </para>
/// </summary>
public sealed class CardCoordinator : IImageEngine, IVideoEngine
{
    /// <summary>Progress stage while a picture waits for another picture, a loading chat model, or a reply being written.</summary>
    public const string Waiting = "waiting";

    /// <summary>Progress stage while the chat model is taken out of graphics memory.</summary>
    public const string MakingRoom = "making_room";

    /// <summary>Progress stage while an upscaler file enlarges a picture (the engine reports no steps for it).</summary>
    public const string Enlarging = "enlarging";

    /// <summary>The names used on the screen and in <see cref="CardCleared"/>.</summary>
    public const string ChatName = "the chat model";
    public const string PictureName = "the picture model";
    public const string VideoName = "the video model";

    private const string NoModel = "No picture model is loaded. Choose a checkpoint first.";

    private readonly IImageEngine _images;
    private readonly IChatModelSlot _chat;
    private readonly ICardMeter _meter;
    private readonly Func<string, long> _fileSize;
    private readonly IReadOnlyList<ICardGuest> _guests;
    private readonly IVideoEngine? _video;
    private readonly CardDoor _door;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private int _pictures;
    private int _clips;
    private long _restedBytes;

    /// <param name="fileSize">The size of a file in bytes, 0 when it is missing (tests pass their own).</param>
    /// <param name="guests">What else can hold graphics memory (see <see cref="ICardGuest"/>).</param>
    /// <param name="video">The video engine itself, or null where no video is made (most tests).</param>
    /// <param name="door">The door the guests ask at before they come onto the card (the app gives them the same one).</param>
    public CardCoordinator(IImageEngine images, IChatModelSlot chat, ICardMeter meter, Func<string, long>? fileSize = null,
        IEnumerable<ICardGuest>? guests = null, IVideoEngine? video = null, CardDoor? door = null)
    {
        _door = door ?? new CardDoor();
        _images = images;
        _chat = chat;
        _meter = meter;
        _fileSize = fileSize ?? SizeOf;
        _guests = guests?.ToList() ?? [];
        _video = video;
        _images.Changed += () => Changed?.Invoke();
        if (_video is not null) _video.Changed += () => Changed?.Invoke();
        _chat.CardTurn = TakeChatTurnAsync;
    }

    /// <summary>How often a waiting picture looks again whether the reply being written has ended.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public ImageModel? Model => _images.Model;

    /// <summary>
    /// True from the moment a picture or a video is asked for until it is finished or has failed: waiting, making room
    /// and drawing. The idle lock counts it as busy.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _pictures) > 0 || _images.IsBusy;

    /// <summary>True from the moment a video is asked for until it is finished, cancelled or has failed.</summary>
    public bool IsMakingVideo => Volatile.Read(ref _clips) > 0;

    /// <summary>
    /// True when the chat model that is set aside now made room for a video, false when it was for a picture. Only
    /// for the words the owner reads while it is away (<see cref="SetAsideWords"/>).
    /// </summary>
    public bool SetAsideForVideo => Volatile.Read(ref _restedForVideo) == 1;

    private int _restedForVideo;

    /// <summary>
    /// True while a video is made by a plan: on a card that another program holds part of, or one under 24 GB.
    /// The engine then keeps the model itself in RAM and is asked for no previews.
    /// </summary>
    public bool IsVideoCrowded
    {
        get => _videoCrowded;
        private set => _videoCrowded = value;
    }

    // Written by the clip's thread, read by the page's.
    private volatile bool _videoCrowded;

    bool IVideoEngine.IsBusy => IsMakingVideo;

    bool IVideoEngine.IsRunning => _video is { IsRunning: true };

    Task IVideoEngine.StopAsync() => _video?.StopAsync() ?? Task.CompletedTask;

    public bool IsRunning => _images.IsRunning;

    public string? Error => _images.Error;

    /// <summary>What was decided for the latest picture, or null before the first.</summary>
    public CardDecision? LastDecision { get; private set; }

    public event Action? Changed;

    /// <summary>What the coordinator did, in plain words, for the app log. Never contains a prompt.</summary>
    public event Action<string>? Noted;

    /// <summary>What is in graphics memory now, as names for a sentence ("the chat model", "the picture model", …).</summary>
    public IReadOnlyList<string> InMemory
    {
        get
        {
            var names = new List<string>();
            if (_chat.IsLoaded) names.Add(ChatName);
            if (_images.IsRunning) names.Add(PictureName);
            if (_video is { IsRunning: true }) names.Add(VideoName);
            names.AddRange(_guests.Where(g => g.HoldsMemory).Select(g => g.Name));
            return names;
        }
    }

    /// <summary>Why "Unload everything" must wait, in words for the owner, or null when it may run.</summary>
    public string? BusyReason =>
        _chat.IsBusy ? "A reply is being written. Unload when it has finished."
        : IsMakingVideo ? "A video is being made. Unload when it has finished."
        : IsBusy ? "A picture is being drawn. Unload when it has finished."
        : null;

    /// <summary>
    /// The owner's "Unload everything": the chat model, the picture model (its checkpoint stays chosen) and every guest
    /// leave graphics memory. Refused, with the reason, while a reply or a picture is being made. A guest that is being
    /// used (documents being indexed) stays and is named.
    /// </summary>
    /// <param name="unloadChat">
    /// Unloads the chat model as the owner's own unload, so nothing brings it back until a model is asked for; true when
    /// there was one (loaded, loading or set aside). It runs before the turn is taken: a model that is still loading
    /// holds the turn until its load ends, and unloading is what ends it.
    /// </param>
    public async Task<CardCleared> ClearAsync(Func<Task<bool>> unloadChat, CancellationToken ct = default)
    {
        if (BusyReason is { } why) return new CardCleared([], [], why);
        var gone = new List<string>();
        var stayed = new List<string>();
        if (await unloadChat().ConfigureAwait(false)) gone.Add(ChatName);
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Nothing is set aside any more, so nothing is measured against it when a model is next loaded.
            Interlocked.Exchange(ref _restedBytes, 0);
            if (_images.IsRunning)
            {
                await _images.ReleaseAsync(ct).ConfigureAwait(false);
                gone.Add(PictureName);
            }
            foreach (var guest in _guests)
            {
                if (!guest.HoldsMemory) continue;
                (await guest.LetGoAsync().ConfigureAwait(false) ? gone : stayed).Add(guest.Name);
            }
        }
        finally
        {
            _turn.Release();
        }
        var cleared = new CardCleared(gone, stayed);
        Noted?.Invoke("the owner asked to unload everything. " + cleared.Text);
        Changed?.Invoke();
        return cleared;
    }

    /// <summary>
    /// Lets the guests that are not being used go, because the card needs the room. Call it only while holding a turn
    /// (a chat model load does, see <see cref="CardTurn"/>). True when something was let go, so the card is worth
    /// measuring again.
    /// </summary>
    /// <param name="because">The reason for the log, e.g. "a chat model is being loaded and would not fit beside it".</param>
    public async Task<bool> MakeWayAsync(string because)
    {
        var any = false;
        foreach (var guest in _guests)
        {
            if (!guest.HoldsMemory || guest.IsBusy || !await guest.LetGoAsync().ConfigureAwait(false)) continue;
            any = true;
            Noted?.Invoke($"{guest.Name} was let go because {because}. It loads again when it is next needed.");
        }
        return any;
    }

    /// <summary>Choosing another checkpoint waits for a picture being drawn rather than cutting it off.</summary>
    public async Task LoadAsync(ImageModel model, CancellationToken ct = default)
    {
        await SetAsidePageLoadAsync().ConfigureAwait(false);
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _images.LoadAsync(model, ct).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Cancels a picture being drawn and gives the memory back (it does not wait for the picture).</summary>
    public Task UnloadAsync(CancellationToken ct = default) => _images.UnloadAsync(ct);

    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _images.ReleaseAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    public async Task<ImageResult> DrawAsync(ImageRequest request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
    {
        // Checked before anything is moved: a request that cannot be drawn must not cost the chat model its place.
        if (request.Problems() is { Count: > 0 } problems) throw new ImageEngineException(string.Join(" ", problems));
        if (_images.Model is null) throw new ImageEngineException(NoModel);

        Interlocked.Increment(ref _pictures);
        Changed?.Invoke();
        try
        {
            await SetAsidePageLoadAsync().ConfigureAwait(false);
            if (_turn.CurrentCount == 0) progress?.Report(new ImageProgress(Waiting, null, null));
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var model = _images.Model ?? throw new ImageEngineException(NoModel);
                var need = NeedOf(model, request);
                await MakeRoomAsync(need, "the picture", progress, ct).ConfigureAwait(false);
                return await _images.DrawAsync(request, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pictures);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Makes a video. A clip takes the whole card, so under one turn everything else leaves it first: the chat model
    /// is set aside (a reply being written is waited for, never cut off), the picture model is let go, and
    /// every guest leaves (one that is being used finishes what it is doing first, then carries on on the processor:
    /// <see cref="ICardGuest.StepOffAsync"/>). When the cleared card still has less than the clip needs it is refused in words and
    /// nothing is made. The video program runs only while the clip is made (the engine ends it), so when the turn is
    /// given back the card is free again, and the chat model comes back the next time it is asked for.
    /// </summary>
    public async Task<VideoResult> MakeAsync(VideoModel model, VideoRequest request, IProgress<VideoProgress>? progress = null, CancellationToken ct = default)
    {
        var video = _video ?? throw new VideoEngineException("This DOG.ai has no video engine.");
        // Checked before anything is moved: a clip that cannot be made must not cost the chat model its place.
        if (request.Problems() is { Count: > 0 } problems) throw new VideoEngineException(string.Join(" ", problems));
        if (model.Problem() is { } missing) throw new VideoEngineException(missing);

        Interlocked.Increment(ref _pictures);
        Interlocked.Increment(ref _clips);
        // From here until the clip has ended no guest comes onto the card (M21): one that is needed meanwhile runs
        // on the processor. A guest loads without a card turn, so only the door keeps it off. (Closed inside the "try"
        // and opened in its "finally": the door tells its listeners, and one that failed must not leave it closed.)
        try
        {
            _door.Close();
            Changed?.Invoke();
            await SetAsidePageLoadAsync(forVideo: true).ConfigureAwait(false);
            if (_turn.CurrentCount == 0) progress?.Report(new VideoProgress(VideoStages.Waiting));
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // A guest that was on its way onto the card when the clip was asked for is let in first (seconds),
                // so the card is measured with it there and not a moment before it arrives.
                await _door.EmptyAsync(ct).ConfigureAwait(false);
                var free = await ClearForVideoAsync(request, progress, ct).ConfigureAwait(false);
                // The engine is told what is free: it does not see what other programs hold (VideoMemory.EnginePlan).
                IsVideoCrowded = VideoMemory.Crowded(free);
                if (IsVideoCrowded) Changed?.Invoke();
                // On a crowded card the full-size previews are what does not fit: each one took most of a minute
                // (a clip of 2½ minutes took 4½ with them and 2½ without, beside a program holding 6.8 GB).
                var asked = IsVideoCrowded ? request with { Preview = false } : request;
                return await video.MakeAsync(model, asked, free, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                IsVideoCrowded = false;
                // The engine ends its program with the clip; this only makes sure of it before anything else gets the card.
                await EndVideoAsync().ConfigureAwait(false);
                _turn.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _clips);
            Interlocked.Decrement(ref _pictures);
            _door.Open();
            Changed?.Invoke();
        }
    }

    /// <summary>Ends the video program if one is still there. Call it only while holding a turn.</summary>
    private async Task EndVideoAsync()
    {
        if (_video is not { IsRunning: true } video) return;
        await video.StopAsync().ConfigureAwait(false);
        Noted?.Invoke("the video program was still running and was ended.");
    }

    /// <summary>
    /// Takes everything off the card for a clip, then checks that the card has the room. Called while holding a turn.
    /// Returns what is free on the cleared card, or null when the card could not be measured.
    /// </summary>
    private async Task<long?> ClearForVideoAsync(VideoRequest request, IProgress<VideoProgress>? progress, CancellationToken ct)
    {
        // What the clip takes when the card has it, and the least it is made with at all (a Quick clip by a plan, M21).
        var need = VideoMemory.Need(request);
        var least = VideoMemory.LeastNeed(request);
        var stopped = Interlocked.Exchange(ref _stoppedLoad, 0) == 1;
        var before = await _meter.ReadAsync(ct).ConfigureAwait(false);
        // A card that could never hold a clip is said so before anything is moved.
        // (The same rule the Video page uses to offer a quality: VideoMemory.CardCanMake.)
        if (before is not null && !VideoMemory.CardCanMake(before.TotalBytes, request))
            throw new VideoEngineException($"A video needs about {Gb(least)} of graphics memory, and this PC’s graphics card has {Gb(before.TotalBytes)}.");
        // A card under 24 GB never has the whole model's room: there a partly used card is no sign of another program,
        // and what is waited for after clearing it is what such a card can have.
        var planned = VideoMemory.AlwaysPlanned(before?.TotalBytes);
        var hoped = before is null ? need : Math.Max(least, Math.Min(need, before.TotalBytes - VideoMemory.WindowsHolds));

        var gone = new List<string>();
        var stayed = new List<string>();
        if (_chat.IsLoaded || _images.IsRunning || _guests.Any(g => g.HoldsMemory)) progress?.Report(new VideoProgress(VideoStages.MakingRoom));
        var waited = false;
        while (_chat.IsLoaded)
        {
            ct.ThrowIfCancellationRequested();
            if (await _chat.RestAsync().ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _restedBytes, before?.ChatBytes ?? 0);
                Interlocked.Exchange(ref _restedForVideo, 1);
                gone.Add($"{ChatName} ({Gb(before?.ChatBytes ?? -1)}) was set aside");
                break;
            }
            // A reply is being written. It is never cut off; the video waits for it.
            if (!waited) progress?.Report(new VideoProgress(VideoStages.Waiting));
            waited = true;
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        if (waited) progress?.Report(new VideoProgress(VideoStages.MakingRoom));
        if (_images.IsRunning)
        {
            await _images.ReleaseAsync(ct).ConfigureAwait(false);
            gone.Add($"{PictureName} was let go");
        }
        foreach (var guest in _guests)
        {
            if (!guest.HoldsMemory) continue;
            // An idle guest goes at once. One that is being used finishes what it is doing right now, then leaves
            // and carries on on the processor (the door is closed): beside a working guest the clip was half slower.
            var working = guest.IsBusy;
            if (await guest.StepOffAsync(ct).ConfigureAwait(false))
                gone.Add(working ? $"{guest.Name} left the card and carries on on the processor" : $"{guest.Name} was let go");
            else stayed.Add(guest.Name);
        }

        // What a program held is given back a moment after it ends: look again a few times before believing a full card.
        var now = gone.Count == 0 ? before : await _meter.ReadAsync(ct).ConfigureAwait(false);
        for (var look = 0; look < 10 && gone.Count > 0 && now is not null && !VideoMemory.RoomEnough(now.FreeBytes, hoped); look++)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            now = await _meter.ReadAsync(ct).ConfigureAwait(false);
        }

        // A card that still looks partly used may only be slow to give back what a program held that ended a moment
        // ago (the video program of the clip before, with nothing of DOG.ai's to move this time): look a little
        // longer before refusing the clip or planning it for a crowded card.
        for (var look = 0; look < 4 && !planned && now is not null && VideoMemory.Crowded(now.FreeBytes); look++)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            now = await _meter.ReadAsync(ct).ConfigureAwait(false);
        }

        var setAside = _chat.IsResting && (stopped || gone.Count > 0 && gone[0].StartsWith(ChatName, StringComparison.Ordinal));
        LastDecision = new CardDecision(setAside ? CardChoice.SetChatAside : CardChoice.NothingLoaded, need, now?.FreeBytes ?? -1);
        var moved = gone.Count == 0 ? "nothing else was on the card" : CardCleared.List(gone);
        if (now is not null && !VideoMemory.RoomFor(now.FreeBytes, request))
        {
            Noted?.Invoke($"a video needs about {Gb(least)}; {moved}, and only {Gb(now.FreeBytes)} is free, so the video was not made.");
            var why = stayed.Count > 0
                ? $" {char.ToUpperInvariant(stayed[0][0])}{CardCleared.List(stayed)[1..]} {(stayed.Count == 1 ? "is" : "are")} being used and {(stayed.Count == 1 ? "stays" : "stay")} in graphics memory: try again when {(stayed.Count == 1 ? "it has" : "they have")} finished."
                : " Close other programs that use the graphics card, then try again.";
            throw new VideoEngineException($"A video needs about {Gb(least)} of graphics memory and only {Gb(now.FreeBytes)} is free after DOG.ai made room.{why}");
        }
        // A clip made by a plan keeps the model itself in RAM: without the room there the PC would page and crawl.
        if (now is not null && !VideoMemory.RamEnough(now.FreeRamBytes, now.FreeBytes))
        {
            Noted?.Invoke($"a video made with part of the card needs about {Gb(VideoMemory.PlannedRamNeed)} of free RAM; {moved}, and only {Gb(now.FreeRamBytes ?? 0)} of RAM is free, so the video was not made.");
            throw new VideoEngineException(VideoMemory.TooLittleRam(now.FreeRamBytes ?? 0, planned));
        }
        Noted?.Invoke(now is null
            ? $"a video needs the whole card (about {Gb(need)}); {moved}. The card could not be measured."
            : VideoMemory.Crowded(now.FreeBytes)
                ? $"a video needs the whole card (about {Gb(need)}); {moved}. Only {Gb(now.FreeBytes)} is free ({(planned ? "the card is smaller than that" : "something else holds part of the card")}), so the video engine was told to plan with {VideoMemory.EnginePlan(now.FreeBytes, request.Decoder)} GB and to send no previews."
                : $"a video needs the whole card (about {Gb(need)}); {moved}. {Gb(now.FreeBytes)} is free.");
        return now?.FreeBytes;
    }

    /// <summary>
    /// Asked before choosing a picture's size (M18): would <paramref name="request"/>, drawn with <paramref name="model"/>,
    /// fit beside the chat model now, by the same rule <see cref="DrawAsync"/> uses? Null when no chat model is in graphics
    /// memory (one set aside counts as none), so there is nothing a size could keep. The card is read as it is: idle
    /// guests are counted as staying, so it may answer no where drawing would have let them go first.
    /// </summary>
    public async Task<bool?> FitsBesideChatAsync(ImageModel model, ImageRequest request, CancellationToken ct = default)
    {
        if (!_chat.IsLoaded) return null;
        var now = await _meter.ReadAsync(ct).ConfigureAwait(false);
        return PictureMemory.FitsBeside(now, NeedOf(model, request));
    }

    /// <summary>What a picture is expected to need at its peak with a model: the one estimate the coordinator decides by.</summary>
    public long NeedOf(ImageModel model, ImageRequest request) =>
        PictureMemory.Need(_fileSize(model.CheckpointPath), LoraBytes(model, request), request);

    /// <summary>
    /// Enlarges a picture with an upscaler file. It takes its turn on the card like a picture, but needs so little
    /// (<see cref="PictureMemory.EnlargeNeed"/>) that the chat model nearly always stays where it is.
    /// </summary>
    public async Task<ImageEnlarged> EnlargeAsync(ImageEnlarge request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
    {
        if (request.Problems() is { Count: > 0 } problems) throw new ImageEngineException(string.Join(" ", problems));
        if (_images.Model is null) throw new ImageEngineException(NoModel);

        Interlocked.Increment(ref _pictures);
        Changed?.Invoke();
        try
        {
            await SetAsidePageLoadAsync().ConfigureAwait(false);
            if (_turn.CurrentCount == 0) progress?.Report(new ImageProgress(Waiting, null, null));
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_images.Model is null) throw new ImageEngineException(NoModel);
                await MakeRoomAsync(PictureMemory.EnlargeNeed, "enlarging the picture", progress, ct).ConfigureAwait(false);
                return await _images.EnlargeAsync(request, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pictures);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// A chat model that is loading only because a page opened holds the card for half a minute, and the picture would set
    /// it aside as soon as it is in. So it is set aside now instead, before it finishes (the next request loads it).
    /// A load the owner started, or one bringing the model back for a request, is waited for as before.
    /// </summary>
    private async Task SetAsidePageLoadAsync(bool forVideo = false)
    {
        if (_turn.CurrentCount > 0 || !await _chat.SetAsideLoadAsync().ConfigureAwait(false)) return;
        Interlocked.Exchange(ref _stoppedLoad, 1);
        Interlocked.Exchange(ref _restedForVideo, forVideo ? 1 : 0);
        // Its size on the card was never measured: when it comes back, the picture model is let go to be safe.
        Interlocked.Exchange(ref _restedBytes, 0);
        Noted?.Invoke($"a chat model was loading only because a page opened, so the load was stopped and the model set aside for the {(forVideo ? "video" : "picture")}. It loads when it is next asked for.");
    }

    private int _stoppedLoad;

    /// <param name="need">What the job is expected to need at its peak, in bytes.</param>
    /// <param name="what">The job, for the log: "the picture", "enlarging the picture".</param>
    private async Task MakeRoomAsync(long need, string what, IProgress<ImageProgress>? progress, CancellationToken ct)
    {
        var stopped = Interlocked.Exchange(ref _stoppedLoad, 0) == 1;
        var guests = _guests.Any(g => g.HoldsMemory);
        if (!_chat.IsLoaded && !guests)
        {
            LastDecision = new CardDecision(stopped && _chat.IsResting ? CardChoice.SetChatAside : CardChoice.NothingLoaded, need, -1);
            return;
        }

        var now = await _meter.ReadAsync(ct).ConfigureAwait(false);
        // The small things make way first: an idle document search model takes seconds to bring back, a chat model half a minute.
        if (guests && !PictureMemory.FitsBeside(now, need) && await MakeWayAsync("a picture needs the room").ConfigureAwait(false))
            now = await _meter.ReadAsync(ct).ConfigureAwait(false);
        if (!_chat.IsLoaded)
        {
            LastDecision = new CardDecision(stopped && _chat.IsResting ? CardChoice.SetChatAside : CardChoice.NothingLoaded, need, -1);
            return;
        }

        var free = now?.FreeBytes ?? -1;
        if (PictureMemory.FitsBeside(now, need))
        {
            LastDecision = new CardDecision(CardChoice.KeptBoth, need, free);
            Noted?.Invoke($"{what} needs about {Gb(need)} and {Gb(free)} is free ({Gb(now!.PictureBytes)} already held by the picture model), so the chat model stays loaded.");
            return;
        }

        progress?.Report(new ImageProgress(MakingRoom, null, null));
        var waited = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Unloaded meanwhile, by the owner or for sitting unused: nothing to set aside any more.
            if (!_chat.IsLoaded) break;
            if (await _chat.RestAsync().ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _restedBytes, now?.ChatBytes ?? 0);
                Interlocked.Exchange(ref _restedForVideo, 0);
                break;
            }
            // A reply is being written. It is never cut off; the picture waits for it.
            if (!waited) progress?.Report(new ImageProgress(Waiting, null, null));
            waited = true;
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        LastDecision = new CardDecision(CardChoice.SetChatAside, need, free);
        Noted?.Invoke(now is null
            ? $"the graphics card could not be measured, so the chat model was set aside for the picture (it needs about {Gb(need)})."
            : $"{what} needs about {Gb(need)} and only {Gb(free)} is free, so the chat model ({Gb(now.ChatBytes)}) was set aside. It loads again when it is next asked for.");
    }

    /// <summary>
    /// A chat model is about to be loaded. Waits for a picture being drawn, then lets the idle picture model go unless
    /// the model that is coming back is known to fit beside it. The caller keeps the turn until its load has ended, so
    /// no picture starts measuring the card while a model is half loaded.
    /// </summary>
    private async Task<IDisposable> TakeChatTurnAsync(bool waking, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var need = waking ? Interlocked.Read(ref _restedBytes) : 0;
            // The model that was set aside comes back with the settings it had, so it must find the room it had: the
            // idle guests go too unless it is known to fit beside them. (Any other load is measured by the model
            // launcher, which asks for the room itself: see MakeWayAsync.)
            if (waking && _guests.Any(g => g.HoldsMemory && !g.IsBusy))
            {
                var beside = need > 0 ? await _meter.ReadAsync(ct).ConfigureAwait(false) : null;
                // What the picture model holds counts as free here: it is let go below when the model does not fit beside it.
                if (beside is null || beside.FreeBytes + beside.PictureBytes < need + PictureMemory.Margin)
                    await MakeWayAsync("the chat model is coming back and needs the room").ConfigureAwait(false);
            }
            if (_images.IsRunning)
            {
                // Another model than the one set aside is measured by the model launcher itself, against a card that
                // is free of the picture model; its size is not known here.
                var now = need > 0 ? await _meter.ReadAsync(ct).ConfigureAwait(false) : null;
                if (now is null || now.FreeBytes < need + PictureMemory.Margin)
                {
                    await _images.ReleaseAsync(ct).ConfigureAwait(false);
                    Noted?.Invoke(waking
                        ? "the chat model is coming back, so the picture model was let go. The next picture loads it again."
                        : "a chat model is being loaded, so the picture model was let go. The next picture loads it again.");
                }
                else
                {
                    Noted?.Invoke($"the chat model is coming back ({Gb(need)}) and {Gb(now.FreeBytes)} is free, so the picture model stays loaded too.");
                }
            }
            return new Turn(_turn);
        }
        catch
        {
            _turn.Release();
            throw;
        }
    }

    private long LoraBytes(ImageModel model, ImageRequest request)
    {
        if (model.LoraFolder is not { } folder || request.Loras is not { Count: > 0 } loras) return 0;
        long total = 0;
        foreach (var lora in loras)
            if (ImageRequest.IsSafeRelativePath(lora.Path)) total += _fileSize(Path.Combine(folder, lora.Path));
        return total;
    }

    private static long SizeOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    private static string Gb(long bytes) =>
        bytes < 0 ? "an unknown amount" : (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";

    private sealed class Turn(SemaphoreSlim turn) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) turn.Release();
        }
    }
}

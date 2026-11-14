namespace DogAI.Core.Images;

/// <summary>The main graphics card's memory, measured now, in bytes.</summary>
/// <param name="PictureBytes">What the picture engine holds (0 when it is not running or cannot be measured).</param>
/// <param name="ChatBytes">What the chat model holds (0 when none is loaded or it cannot be measured).</param>
public sealed record CardMemory(long TotalBytes, long FreeBytes, long PictureBytes = 0, long ChatBytes = 0);

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

    /// <summary>Set by the coordinator. Every load of a chat model takes a turn first (see <see cref="CardTurn"/>).</summary>
    CardTurn? CardTurn { get; set; }
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
    public static long Need(long checkpointBytes, long loraBytes, ImageRequest request)
    {
        var file = checkpointBytes > 0 ? checkpointBytes : TypicalCheckpointBytes;
        var megapixels = LargestSide(request.Width, request.Hires) * (double)LargestSide(request.Height, request.Hires) / 1_000_000.0;
        return file + file / 4 + Math.Max(0, loraBytes) + (long)(megapixels * 4 * Gb);
    }

    /// <summary>A side of the largest pass. The engine rounds a hires fix size up to a multiple of 64 (832×1216 at ×1.5 came out 1280×1856).</summary>
    private static int LargestSide(int side, HiresFix? hires) =>
        hires is null ? side : (int)Math.Ceiling(side * hires.Scale / 64) * 64;

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
/// </summary>
public sealed class CardCoordinator : IImageEngine
{
    /// <summary>Progress stage while a picture waits for another picture, a loading chat model, or a reply being written.</summary>
    public const string Waiting = "waiting";

    /// <summary>Progress stage while the chat model is taken out of graphics memory.</summary>
    public const string MakingRoom = "making_room";

    private const string NoModel = "No picture model is loaded. Choose a checkpoint first.";

    private readonly IImageEngine _images;
    private readonly IChatModelSlot _chat;
    private readonly ICardMeter _meter;
    private readonly Func<string, long> _fileSize;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private int _pictures;
    private long _restedBytes;

    /// <param name="fileSize">The size of a file in bytes, 0 when it is missing (tests pass their own).</param>
    public CardCoordinator(IImageEngine images, IChatModelSlot chat, ICardMeter meter, Func<string, long>? fileSize = null)
    {
        _images = images;
        _chat = chat;
        _meter = meter;
        _fileSize = fileSize ?? SizeOf;
        _images.Changed += () => Changed?.Invoke();
        _chat.CardTurn = TakeChatTurnAsync;
    }

    /// <summary>How often a waiting picture looks again whether the reply being written has ended.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public ImageModel? Model => _images.Model;

    /// <summary>
    /// True from the moment a picture is asked for until it is finished or has failed: waiting, making room and drawing.
    /// The idle lock counts it as busy.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _pictures) > 0 || _images.IsBusy;

    public bool IsRunning => _images.IsRunning;

    public string? Error => _images.Error;

    /// <summary>What was decided for the latest picture, or null before the first.</summary>
    public CardDecision? LastDecision { get; private set; }

    public event Action? Changed;

    /// <summary>What the coordinator did, in plain words, for the app log. Never contains a prompt.</summary>
    public event Action<string>? Noted;

    /// <summary>Choosing another checkpoint waits for a picture being drawn rather than cutting it off.</summary>
    public async Task LoadAsync(ImageModel model, CancellationToken ct = default)
    {
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
            if (_turn.CurrentCount == 0) progress?.Report(new ImageProgress(Waiting, null, null));
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var model = _images.Model ?? throw new ImageEngineException(NoModel);
                await MakeRoomAsync(model, request, progress, ct).ConfigureAwait(false);
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

    private async Task MakeRoomAsync(ImageModel model, ImageRequest request, IProgress<ImageProgress>? progress, CancellationToken ct)
    {
        var need = PictureMemory.Need(_fileSize(model.CheckpointPath), LoraBytes(model, request), request);
        if (!_chat.IsLoaded)
        {
            LastDecision = new CardDecision(CardChoice.NothingLoaded, need, -1);
            return;
        }

        var now = await _meter.ReadAsync(ct).ConfigureAwait(false);
        var free = now?.FreeBytes ?? -1;
        if (PictureMemory.FitsBeside(now, need))
        {
            LastDecision = new CardDecision(CardChoice.KeptBoth, need, free);
            Noted?.Invoke($"the picture needs about {Gb(need)} and {Gb(free)} is free ({Gb(now!.PictureBytes)} already held by the picture model), so the chat model stays loaded.");
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
            : $"the picture needs about {Gb(need)} and only {Gb(free)} is free, so the chat model ({Gb(now.ChatBytes)}) was set aside. It loads again when it is next asked for.");
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
            if (_images.IsRunning)
            {
                // Another model than the one set aside is measured by the model launcher itself, against a card that
                // is free of the picture model; its size is not known here.
                var need = waking ? Interlocked.Read(ref _restedBytes) : 0;
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

using System.Globalization;
using DogAI.Core.Chat;
using DogAI.Core.Images;

namespace DogAI.Core.Video;

/// <summary>
/// One clip made on the Video section's Make tab, held in memory only. It keeps the request exactly as it was made (the
/// seed the engine used and the first frame included), so Keep never reads the controls as they are now.
/// </summary>
/// <param name="Number">Counts up from 1 while DOG.ai is open, so tries can be told apart.</param>
/// <param name="Request">The request sent, with the seed the clip was made from.</param>
/// <param name="Expected">What the estimate said before it started, in seconds.</param>
/// <param name="KeptId">The <see cref="Clip"/> row it was kept as, once kept.</param>
/// <param name="Still">A small WebP of its first frame, made in the page (<see cref="PicturePreview"/>'s rules); null until made, or when it could not be.</param>
/// <param name="Continues">
/// The clip whose last frame this one started from (Continue the clip), when it was made that way in this run of
/// DOG.ai; null otherwise. It is what "Join with the clip before" joins it with (M22). For a joined try: what its
/// first part continued, so three clips made one after another join in two presses.
/// </param>
/// <param name="Parts">
/// How many clips <see cref="Result"/> holds: 1 as made, 2 or 3 once joined (<see cref="VideoSession.Join"/>). A
/// joined try's <see cref="Request"/> is its last part's; its result has all the frames (241 or 361).
/// </param>
public sealed record VideoTry(
    int Number,
    VideoRequest Request,
    VideoResult Result,
    TimeSpan Took,
    double Expected,
    long? KeptId = null,
    byte[]? Still = null,
    VideoBefore? Continues = null,
    int Parts = 1)
{
    /// <summary>The row to keep: this clip as it was made, with its still when there is one. A joined try keeps its last part's request and says how many clips its video holds.</summary>
    public Clip ToKeep() => Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, Request, Result, Still, Parts);

    /// <summary>True for a clip joined from two or three.</summary>
    public bool Joined => Parts > 1;

    /// <summary>How long it plays, in seconds (all of its parts).</summary>
    public double Seconds => Result.Fps > 0 ? Result.Frames / (double)Result.Fps : 0;

    public bool FromPicture => Request.StartFrame is not null;

    // Kept beside the still's bytes, not in the record: a record's fields count when two tries are compared.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> Addresses = new();

    /// <summary>The still as a data: address (from memory; never a file), made once; null without a still.</summary>
    public string? StillSrc => Still is { } still ? Addresses.GetValue(still, static s => "data:image/webp;base64," + Convert.ToBase64String(s)) : null;
}

/// <summary>
/// The clip a last frame ended (M22): a try on the Make tab by its number, or a kept clip by its id. A clip made from
/// that frame continues that clip, and only such a pair is ever joined. Memory only, like the frame that carries it:
/// a kept row does not remember what it continued.
/// </summary>
public sealed record VideoBefore(int? Try = null, long? Kept = null);

/// <summary>
/// The picture a clip will start from, held in memory only until it is let go: the JPEG the page cut from a file (or,
/// from step 5, a picture DOG.ai holds), already checked as a first frame for both qualities of its shape.
/// </summary>
/// <param name="From">Where it came from, in words for the screen ("a file").</param>
/// <param name="Continues">
/// True for the last frame of a clip (Continue the clip, M21): the next clip goes on where that one ended, and the
/// page says what that costs (<see cref="VideoPanels.Continuation"/>).
/// </param>
/// <param name="Ended">The clip this last frame ended, when DOG.ai knows it (M22); only with <paramref name="Continues"/>.</param>
public sealed record VideoFrame(byte[] Bytes, int Width, int Height, PictureShape Shape, string From, bool Continues = false, VideoBefore? Ended = null)
{
    /// <summary>
    /// The frame for these bytes, or why there is none: a WebP DOG.ai drew or a JPEG the page made (its colour profile
    /// taken out first), of one of the three shapes, acceptable to both Quick's and Better's size of that shape.
    /// </summary>
    public static (VideoFrame? Frame, string? Problem) Of(byte[]? bytes, string from, bool continues = false, VideoBefore? ended = null)
    {
        if (bytes is null || bytes.Length == 0) return (null, "It is not a picture DOG.ai can open (" + ComposerPictures.Kinds + ").");
        var plain = JpegInfo.Read(bytes) is not null ? AttachedPictures.Plain(bytes) : bytes;
        if (VideoStartFrame.SizeOf(plain) is not { } size) return (null, "It is not a picture DOG.ai can open (" + ComposerPictures.Kinds + ").");
        var shape = VideoFrames.Nearest(size.Width, size.Height);
        foreach (var quality in Enum.GetValues<VideoQuality>())
        {
            var (w, h) = VideoRequest.SizeOf(quality, shape);
            if (VideoStartFrame.Problem(plain, w, h) is { } problem) return (null, problem);
        }
        return (new VideoFrame(plain, size.Width, size.Height, shape, from, continues, continues ? ended : null), null);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> Addresses = new();

    /// <summary>The frame as a data: address for its thumbnail, made once.</summary>
    public string Src => Addresses.GetValue(Bytes, static b =>
        (WebpInfo.Read(b) is not null ? "data:image/webp;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(b));
}

/// <summary>How a picture of any shape becomes a first frame: the nearest of the three shapes, cut from its middle.</summary>
public static class VideoFrames
{
    /// <summary>The shape whose clip is nearest this picture's (by how much it would have to be cut).</summary>
    public static PictureShape Nearest(int width, int height)
    {
        if (width <= 0 || height <= 0) return PictureShape.Square;
        var ratio = Math.Log((double)width / height);
        return Enum.GetValues<PictureShape>()
            .OrderBy(s => Math.Abs(ratio - Math.Log(Ratio(s))))
            .First();
    }

    /// <summary>The shape's width over its height, at Better's size (the larger of the two).</summary>
    public static double Ratio(PictureShape shape)
    {
        var (w, h) = VideoRequest.SizeOf(VideoQuality.Better, shape);
        return (double)w / h;
    }

    /// <summary>
    /// What the page cuts from a picture of this size: the nearest shape, the middle part of that shape (as large as
    /// the picture allows), and the size it is written at, which is Better's size of that shape. Better's, not Quick's:
    /// the original file is not kept, so a frame cut at Quick's size would be enlarged for a Better clip; Better's size
    /// passes as a Quick frame too (the shapes of one kind differ by under 3%, which the engine takes off two edges).
    /// </summary>
    public static FrameCut Cut(int width, int height)
    {
        var shape = Nearest(width, height);
        var (outW, outH) = VideoRequest.SizeOf(VideoQuality.Better, shape);
        double want = (double)outW / outH, have = (double)width / height;
        int cw = width, ch = height;
        if (have > want) cw = Math.Max(1, Math.Min(width, (int)Math.Round(height * want)));
        else if (have < want) ch = Math.Max(1, Math.Min(height, (int)Math.Round(width / want)));
        return new FrameCut(shape, (width - cw) / 2, (height - ch) / 2, cw, ch, outW, outH);
    }
}

/// <summary>A cut from the middle of a picture (<see cref="VideoFrames.Cut"/>), and the size it is written at.</summary>
public sealed record FrameCut(PictureShape Shape, int X, int Y, int Width, int Height, int OutWidth, int OutHeight);

/// <summary>
/// The four parts of a clip the bar is made of, in order. Only drawing counts steps; the rest run on the clock against
/// <see cref="VideoEstimate"/>.
/// </summary>
public enum VideoPart { None, Start, Drawing, Decoding, Encoding }

/// <summary>The progress bar's rule: where a clip is, as a share of the whole estimate.</summary>
public static class VideoBar
{
    /// <summary>How far a part that runs on the clock may get before its end is reported.</summary>
    public const double ClockCap = 0.95;

    /// <summary>The highest share shown before the clip is finished.</summary>
    public const double Highest = 0.99;

    /// <summary>The part a stage belongs to; waiting and making room are none (they are not in the estimate).</summary>
    public static VideoPart PartOf(string? stage) => stage switch
    {
        VideoStages.Starting or VideoStages.Preparing or VideoStages.ReadingFrame or VideoStages.ReadingSentence => VideoPart.Start,
        VideoStages.Drawing => VideoPart.Drawing,
        VideoStages.Decoding => VideoPart.Decoding,
        VideoStages.Encoding => VideoPart.Encoding,
        _ => VideoPart.None,
    };

    /// <summary>
    /// The share of the clip done, from 0 to <see cref="Highest"/>: every part before this one is done, drawing counts
    /// its steps, and a part without a count runs on <paramref name="secondsInPart"/> against its estimate, up to
    /// <see cref="ClockCap"/> of it. A part that is never reported (decoding can go by unseen) is simply passed.
    /// </summary>
    public static double Share(VideoEstimate estimate, VideoPart part, int? step, int? steps, double secondsInPart)
    {
        if (part == VideoPart.None || estimate.Total <= 0) return 0;
        double Clock(double length) => length <= 0 ? 0 : length * Math.Clamp(secondsInPart / length, 0, ClockCap);
        var done = part switch
        {
            VideoPart.Start => Clock(estimate.Start),
            VideoPart.Drawing => estimate.Start + (steps is > 0 && step is { } s ? estimate.Drawing * Math.Clamp((double)s / steps.Value, 0, 1) : 0),
            VideoPart.Decoding => estimate.Start + estimate.Drawing + Clock(estimate.Decoding),
            _ => estimate.Start + estimate.Drawing + estimate.Decoding + Clock(estimate.Encoding),
        };
        return Math.Clamp(done / estimate.Total, 0, Highest);
    }
}

/// <summary>
/// The Video section's Make tab while DOG.ai is open: the sentence, the first frame, the strip of tries and a clip
/// being made. All of it is in memory only (a singleton with no store): nothing here is written anywhere, and it is
/// gone after a restart or a lock. A clip reaches the disk only when the owner presses Keep
/// (<see cref="VideoTry.ToKeep"/>, then <see cref="IClipStore.AddAsync"/>).
/// </summary>
public sealed class VideoSession
{
    /// <summary>The most tries held at once. A clip is megabytes (3 to 20 MB); the oldest goes first, never the one shown.</summary>
    public const int Max = 4;

    // A clip is added from the thread that made it while the page may be drawing the strip. So the list is changed
    // only under _gate, and readers get a copy that never changes (Tries), as in ImagesSession.
    private readonly object _gate = new();
    private readonly List<VideoTry> _tries = [];
    private volatile IReadOnlyList<VideoTry> _shown = [];
    private volatile VideoTry? _selected;
    private int _number;
    private CancellationTokenSource? _cts;
    private int _run;

    /// <summary>Raised when the sentence, the frame, the tries or a clip in progress change. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>What is typed in the sentence box. Never saved.</summary>
    public string Sentence { get; private set; } = "";

    /// <summary>The picture the next clip starts from, or null. Memory only.</summary>
    public VideoFrame? Frame { get; private set; }

    public void Type(string sentence)
    {
        if (Sentence == (sentence ?? "")) return;
        Sentence = sentence ?? "";
        Changed?.Invoke();
    }

    // ── "Write it for me…" (M21 step 4): the helper's words, its choice and its last sentence, memory only ──

    /// <summary>What the owner typed in the helper's box (the words the sentence is written from). Never saved.</summary>
    public string HelperWords { get; private set; } = "";

    public void HelperSay(string words) => HelperWords = words ?? "";

    private bool? _helperFrame;

    /// <summary>
    /// True when the helper writes from the first frame: as the owner chose, else whenever a first frame is held.
    /// </summary>
    public bool HelperFromFrame => _helperFrame ?? Frame is not null;

    public void HelperFrom(bool frame)
    {
        if (_helperFrame == frame) return;
        _helperFrame = frame;
        Changed?.Invoke();
    }

    /// <summary>The helper's last sentence, until another is written or it is used. Memory only.</summary>
    public VideoDraft? HelperDraft { get; private set; }

    /// <summary>True when <see cref="HelperDraft"/> was written from the frame held: another frame lets it go.</summary>
    public bool HelperDraftFromFrame { get; private set; }

    public void HelperWrote(VideoDraft? draft, bool fromFrame = false)
    {
        HelperDraft = draft;
        HelperDraftFromFrame = draft is not null && fromFrame;
        Changed?.Invoke();
    }

    /// <summary>
    /// Holds the picture the next clip starts from (null lets it go). The "Animate…" buttons (M20 step 5) and Make one
    /// like this hand their picture over through here. A picture just held is shown large in place of the clip shown
    /// (<see cref="Shown"/>) until a clip is picked or made.
    /// </summary>
    public void Hold(VideoFrame? frame)
    {
        // A sentence the helper wrote from the frame held is not about another one.
        if (!ReferenceEquals(frame, Frame) && HelperDraftFromFrame) (HelperDraft, HelperDraftFromFrame) = (null, false);
        Frame = frame;
        FrameShown = frame is not null;
        Changed?.Invoke();
    }

    /// <summary>True while the picture just held is shown large in place of a clip; picking a try or a new clip ends it.</summary>
    public bool FrameShown { get; private set; }

    /// <summary>The try shown large on the stage: none while the first frame just held is shown, else <see cref="Selected"/>.</summary>
    public VideoTry? Shown => FrameShown && Frame is not null ? null : Selected;

    /// <summary>The tries, oldest first, as they are now: a list of its own that later changes do not touch.</summary>
    public IReadOnlyList<VideoTry> Tries => _shown;

    /// <summary>The try shown large: the one picked, else the newest.</summary>
    public VideoTry? Selected
    {
        get
        {
            var (all, picked) = (_shown, _selected);
            return picked is not null && all.Contains(picked) ? picked : all.LastOrDefault();
        }
    }

    /// <summary>True when the last clip added had to let an old try go.</summary>
    public bool Dropped { get; private set; }

    public bool IsMaking => _cts is not null;

    /// <summary>The engine's last report for the clip being made, or null before the first.</summary>
    public VideoProgress? Progress { get; private set; }

    /// <summary>The newest full-size preview (PNG) of the clip being made, or null.</summary>
    public byte[]? Preview { get; private set; }

    /// <summary>What the clip being made is expected to take, part by part; null when nothing is made.</summary>
    public VideoEstimate? Estimate { get; private set; }

    /// <summary>The request of the clip being made; null when nothing is made.</summary>
    public VideoRequest? Making { get; private set; }

    /// <summary>When the clip being made was asked for (UTC).</summary>
    public DateTime StartedAt { get; private set; }

    private VideoPart _part;
    private DateTime _partSince;
    private double _bar;

    /// <summary>
    /// The share of the clip being made that is done, from 0 to 0.99 (<see cref="VideoBar.Share"/>), never going back.
    /// The page asks again every second: decoding and encoding report nothing while they run.
    /// </summary>
    public double BarAt(DateTime now)
    {
        if (!IsMaking || Estimate is not { } estimate) return 0;
        var p = Progress;
        var share = VideoBar.Share(estimate, _part, p?.Step, p?.TotalSteps, (now - _partSince).TotalSeconds);
        if (share > _bar) _bar = share;
        return _bar;
    }

    public void Select(VideoTry shown)
    {
        _selected = shown;
        FrameShown = false;
        Changed?.Invoke();
    }

    /// <summary>Marks a try as kept, so Keep is not offered twice for it.</summary>
    public VideoTry Kept(VideoTry kept, long id) => Change(kept, t => t with { KeptId = id });

    /// <summary>Gives a try the still the page made from its first frame (the first one made stays).</summary>
    public VideoTry WithStill(VideoTry made, byte[] still) => Change(made, t => t.Still is null ? t with { Still = still } : t);

    /// <summary>A kept clip was removed: any try kept as that row is marked not kept again. True when a try was marked.</summary>
    public bool Unkept(long id)
    {
        var any = false;
        lock (_gate)
        {
            for (var i = 0; i < _tries.Count; i++)
            {
                if (_tries[i].KeptId != id) continue;
                var was = _tries[i];
                _tries[i] = was with { KeptId = null };
                if (ReferenceEquals(_selected, was)) _selected = _tries[i];
                any = true;
            }
            if (any) _shown = [.. _tries];
        }
        if (any) Changed?.Invoke();
        return any;
    }

    /// <summary>Lets every try go (nothing kept is touched).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tries.Clear();
            _selected = null;
            _shown = [];
        }
        Changed?.Invoke();
    }

    /// <summary>Stops the clip being made.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Makes the clip <paramref name="request"/> asks for through the engine (the graphics-card coordinator in the app),
    /// with previews, and puts it into the strip as soon as it is done. Returns the new try. Cancel throws
    /// <see cref="OperationCanceledException"/>; a refusal or failure <see cref="VideoEngineException"/>.
    /// </summary>
    /// <param name="estimate">What the page said before it started (the bar runs on it).</param>
    /// <exception cref="InvalidOperationException">Another clip is being made, or the request has a problem.</exception>
    public async Task<VideoTry> MakeAsync(IVideoEngine engine, VideoModel model, VideoRequest request, VideoEstimate estimate, CancellationToken ct = default)
    {
        if (request.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        // A clip that starts from the last frame held continues the clip that frame ended.
        var continues = Frame is { Ended: { } ended } held && request.StartFrame is { } start
            && (ReferenceEquals(start, held.Bytes) || start.AsSpan().SequenceEqual(held.Bytes)) ? ended : null;
        var cts = Take(ct);
        var run = ++_run;
        try
        {
            Estimate = estimate;
            Making = request;
            StartedAt = DateTime.UtcNow;
            (_part, _partSince, _bar, Progress, Preview) = (VideoPart.None, StartedAt, 0, null, null);
            Changed?.Invoke();
            var reporter = new Reporter(p =>
            {
                // A late report, after the clip finished or another began, is about something else.
                if (run != _run || _cts is null) return;
                var part = VideoBar.PartOf(p.Stage);
                if (part != _part)
                {
                    (_part, _partSince) = (part, DateTime.UtcNow);
                    // Waiting and making room come before the estimate starts: the bar starts again from nothing.
                    if (part == VideoPart.None) _bar = 0;
                }
                Progress = p;
                if (p.Preview is { } preview) Preview = preview;
                Changed?.Invoke();
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await engine.MakeAsync(model, request with { Preview = true }, reporter, cts.Token).ConfigureAwait(false);
            var made = new VideoTry(Interlocked.Increment(ref _number), request with { Seed = result.Seed, Preview = false }, result, clock.Elapsed, estimate.Total,
                Continues: continues);
            Add(made);
            return made;
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
            (Progress, Preview, Estimate, Making, _part) = (null, null, null, null, VideoPart.None);
            Changed?.Invoke();
        }
    }

    // ── "Join with the clip before" (M22): two clips in one file, a new try beside them ──

    /// <summary>
    /// Why <paramref name="made"/> cannot be joined with the clip it continues, in words, or null when it can: it
    /// continues nothing DOG.ai knows of, a clip is being made, the clip before is gone (let go from the strip, or
    /// the kept video removed), the two are not the same size or length, or together they would hold more than
    /// <see cref="VideoClips.MaxParts"/> clips.
    /// </summary>
    /// <param name="kept">
    /// For a clip that continues a kept video (<see cref="VideoBefore.Kept"/>): that row as the store has it now
    /// (its facts are enough here), or null when it has been removed. Not needed for one that continues a try.
    /// </param>
    public string? JoinProblem(VideoTry made, Clip? kept = null) => Before(made, kept, needVideo: false).Problem;

    /// <summary>
    /// Joins <paramref name="made"/> with the clip it continues into one file (<see cref="WebmJoin"/>: nothing is
    /// decoded, nothing is sent anywhere) and puts the result into the strip as <b>a new try beside the two</b>, which
    /// stay as they are. The new try is shown; its request is <paramref name="made"/>'s (the last part's), it holds
    /// both clips' parts, and it remembers what the clip before continued. The joined file is checked as a kept one
    /// is before it gets there.
    /// </summary>
    /// <param name="kept">For a clip that continues a kept video: that row, read with its video.</param>
    /// <exception cref="InvalidOperationException">It cannot be joined; the message says why, in words.</exception>
    public VideoTry Join(VideoTry made, Clip? kept = null)
    {
        var (problem, first, before) = Before(made, kept, needVideo: true);
        if (problem is not null || first is null) throw new InvalidOperationException(problem ?? VideoPanels.JoinKeptGone);
        var joined = WebmJoin.Join(first, made.Result.Webm, out var why) ?? throw new InvalidOperationException(why);
        var parts = before.Parts + made.Parts;
        // What a kept clip must be, asked before it is shown.
        if (VideoClips.Problem(joined, made.Result.Width, made.Result.Height, made.Request.Frames, parts) is { } wrong)
            throw new InvalidOperationException(wrong);
        var info = WebmInfo.Read(joined)!.Value;
        // Draw may have been pressed meanwhile: nothing is put into the strip under a clip being made.
        if (IsMaking) throw new InvalidOperationException(VideoPanels.JoinWhileMaking);
        var next = new VideoTry(Interlocked.Increment(ref _number), made.Request,
            new VideoResult(joined, info.Width, info.Height, info.Frames, made.Result.Fps, made.Result.Seed),
            TimeSpan.Zero, 0, KeptId: null, Still: before.Still, Continues: before.Continues, Parts: parts);
        Add(next);
        return next;
    }

    /// <summary>The clip <paramref name="made"/> continues, as joining needs it, or why there is none to join with.</summary>
    private (string? Problem, byte[]? Video, (int Parts, byte[]? Still, VideoBefore? Continues) Before) Before(VideoTry made, Clip? kept, bool needVideo)
    {
        if (made.Continues is not { } before) return (VideoPanels.JoinNothing, null, default);
        if (IsMaking) return (VideoPanels.JoinWhileMaking, null, default);
        byte[]? video;
        int width, height, frames, fps, parts;
        byte[]? still;
        VideoBefore? earlier;
        if (before.Try is { } number)
        {
            if (_shown.FirstOrDefault(t => t.Number == number) is not { } held) return (VideoPanels.JoinBeforeGone, null, default);
            (video, width, height, frames, fps, parts, still, earlier) =
                (held.Result.Webm, held.Result.Width, held.Result.Height, held.Request.Frames, held.Result.Fps, held.Parts, held.Still, held.Continues);
        }
        else
        {
            if (kept is null || kept.Id != before.Kept || (needVideo && kept.Video is null)) return (VideoPanels.JoinKeptGone, null, default);
            // A kept row does not remember what it continued.
            (video, width, height, frames, fps, parts, still, earlier) = (kept.Video, kept.Width, kept.Height, kept.Frames, kept.Fps, kept.Parts, kept.Still, null);
        }
        if (width != made.Result.Width || height != made.Result.Height)
            return (VideoPanels.JoinOtherSize(width, height, made.Result.Width, made.Result.Height), null, default);
        if (frames != made.Request.Frames || fps != made.Result.Fps) return (VideoPanels.JoinOtherLength, null, default);
        if (parts + made.Parts > VideoClips.MaxParts) return (VideoPanels.JoinTooMany(parts + made.Parts), null, default);
        return (null, video, (parts, still, earlier));
    }

    private CancellationTokenSource Take(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (Interlocked.CompareExchange(ref _cts, cts, null) is null) return cts;
        cts.Dispose();
        throw new InvalidOperationException("A video is already being made.");
    }

    /// <summary>
    /// Changes the try as it is now and returns it. Found by its number, not by reference: the caller may hold the
    /// record from before another change (its still arrived while Keep was pressed), and a change made to that old
    /// record would be lost, leaving the clip in the strip unmarked. A try that was let go meanwhile is changed and
    /// handed back without being put back.
    /// </summary>
    private VideoTry Change(VideoTry was, Func<VideoTry, VideoTry> change)
    {
        VideoTry now;
        lock (_gate)
        {
            var index = _tries.FindIndex(t => t.Number == was.Number);
            if (index < 0) return change(was);
            var current = _tries[index];
            now = change(current);
            if (ReferenceEquals(now, current)) return now;
            _tries[index] = now;
            if (_selected?.Number == now.Number) _selected = now;
            _shown = [.. _tries];
        }
        Changed?.Invoke();
        return now;
    }

    private void Add(VideoTry next)
    {
        lock (_gate)
        {
            Dropped = false;
            _tries.Add(next);
            while (_tries.Count > Max && _tries.FindIndex(t => !ReferenceEquals(t, next) && !ReferenceEquals(t, _selected)) is var i and >= 0)
            {
                _tries.RemoveAt(i);
                Dropped = true;
            }
            _selected = next;
            _shown = [.. _tries];
            FrameShown = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Reports straight through (no captured context): the page switches to its renderer itself.</summary>
    private sealed class Reporter(Action<VideoProgress> report) : IProgress<VideoProgress>
    {
        public void Report(VideoProgress value) => report(value);
    }
}

/// <summary>The controls the Video section remembers: never the sentence or the first frame.</summary>
/// <param name="Seed">Null: a new seed for every clip.</param>
/// <param name="Faster">The choice "Faster" (<see cref="VideoRequest.Faster"/>): off unless the owner chose it.</param>
public sealed record VideoControls(VideoQuality Quality = VideoQuality.Quick, PictureShape Shape = PictureShape.Wide, long? Seed = null, bool Faster = false);

/// <summary>
/// The Video section's remembered controls (document <see cref="Key"/>; a singleton, loaded at start), as
/// <see cref="ImagesMemory"/> remembers the Images section's. Never the sentence, the frame or a clip.
/// </summary>
public sealed class VideoChoices(Models.IDocumentStore? documents = null)
{
    public const string Key = "video";

    private VideoControls _controls = new();

    public event Action? Changed;

    public VideoControls Controls => _controls;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        _controls = Clean(await documents.LoadAsync<VideoDocument>(Key, ct));
        Changed?.Invoke();
    }

    public async Task SaveAsync(VideoControls controls, CancellationToken ct = default)
    {
        var clean = Clean(new VideoDocument { Quality = controls.Quality, Shape = controls.Shape, Seed = controls.Seed, Faster = controls.Faster });
        if (clean == _controls) return;
        _controls = clean;
        if (documents is not null)
            await documents.SaveAsync(Key, new VideoDocument { Quality = clean.Quality, Shape = clean.Shape, Seed = clean.Seed, Faster = clean.Faster }, ct);
        Changed?.Invoke();
    }

    /// <summary>What was read, with anything this class could not have written left out.</summary>
    private static VideoControls Clean(VideoDocument? doc) => doc is null ? new() : new(
        Enum.IsDefined(doc.Quality) ? doc.Quality : VideoQuality.Quick,
        Enum.IsDefined(doc.Shape) ? doc.Shape : PictureShape.Wide,
        doc.Seed is >= 0 and <= SeedChoice.Highest ? doc.Seed : null,
        doc.Faster);

    /// <summary>What is saved: four controls, no sentence and no picture. A document from before M21 has no Faster and reads off.</summary>
    public sealed class VideoDocument
    {
        public VideoQuality Quality { get; set; }
        public PictureShape Shape { get; set; } = PictureShape.Wide;
        public long? Seed { get; set; }
        public bool Faster { get; set; }
    }
}

/// <summary>The Video section's places, fold ids and wording, so the screens stay consistent and the rules can be tested.</summary>
public static class VideoPanels
{
    /// <summary>The Make tab's address.</summary>
    public const string Address = "video";

    /// <summary>The Kept videos tab's address (a second route of the same page, so a clip being made goes on while it is open).</summary>
    public const string KeptAddress = "video/kept";

    /// <summary>The two tabs in the header, as (address, title).</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> Tabs = [(Address, "Make"), (KeptAddress, "Kept videos")];

    /// <summary>The short note in the side panel of the Kept videos tab (in place of the controls).</summary>
    public const string KeptAbout = "Kept inside DOG.ai, and locked with the rest when the lock is on. A video leaves only through Save as a file.";

    // Where a first frame came from, as the Make tab says it ("From a kept picture · Wide · 1216 × 832").
    public const string FromFile = "a file", FromTry = "a picture in Images", FromKeptPicture = "a kept picture",
        FromPortrait = "the portrait", FromStory = "a story picture", FromChat = "a chat picture", FromKeptVideo = "the kept video";

    // Continue the clip (M21): the last frame of a clip, as the first frame of the next ("From the end of the clip before · Wide · 960 × 640").
    public const string FromTryEnd = "the end of the clip before", FromKeptVideoEnd = "the end of the kept video";

    /// <summary>Where a picture that had to be cut came from: "a kept picture, cut to wide".</summary>
    public static string CutFrom(string from, PictureShape shape) => $"{from}, cut to {shape.ToString().ToLowerInvariant()}";

    /// <summary>The note on the Make tab after a picture was handed over to start a clip.</summary>
    public static string Arrived(VideoFrame frame) => frame.Continues
        ? $"The next clip will start from {frame.From}. Change the sentence below to say what happens next, then press Draw."
        : $"The clip will start from {frame.From}. Say below what moves, then press Draw.";

    /// <summary>
    /// Said while the last frame of a clip is held as the next clip's first frame. The M21 trial, three continuations
    /// in a row: fine detail fell by a tenth after one, a quarter after two and a third after three, by which time
    /// the character had become another animal in the same jacket; and the same sentence made the same motion again
    /// (a head went on turning until it hung).
    /// </summary>
    public const string Continuation =
        "Each continued clip is a little softer than the one before and drifts further from the first picture: after two it shows. " +
        "Change the sentence, or the clip repeats the same motion.";

    /// <summary>The line under the first frame held: what a continuation costs, else that a clip follows its first frame loosely.</summary>
    public static string AboutFrame(VideoFrame frame) => frame.Continues ? Continuation : FollowsLoosely;

    /// <summary>
    /// The line under the grid: "3 kept videos", "30 kept videos · showing 24", or what it says when there is nothing.
    /// </summary>
    public static string KeptCount(int total, int shown)
    {
        if (total <= 0) return "No kept videos yet. Clips you keep on the Make tab show here.";
        var count = total.ToString("N0", CultureInfo.InvariantCulture);
        var head = total == 1 ? "1 kept video" : $"{count} kept videos";
        return shown < total ? $"{head} · showing {shown.ToString("N0", CultureInfo.InvariantCulture)}" : head;
    }

    /// <summary>
    /// The facts of a kept clip in its large view: "960 × 640 · 5 s · Quick · seed 123 · from a picture · 2.4 MB · kept
    /// 17 Nov 2026, 14:03". <paramref name="keptLocal"/> is when it was kept, in local time.
    /// </summary>
    public static string KeptMeta(Clip c, DateTime keptLocal)
    {
        var request = c.ToRequest();
        var parts = new List<string>
        {
            $"{c.Width} × {c.Height}",
            Length(c.Seconds),
        };
        if (c.Joined) parts.Add(ClipsJoined(c.Parts));
        if (request.Level is { } level) parts.Add(level.Quality.ToString());
        if (c.Faster) parts.Add(FasterName);
        parts.Add("seed " + c.Seed.ToString(CultureInfo.InvariantCulture));
        // A joined clip's first frame in the row is its last part's: the end of the clip before, not a picture it began with.
        if (c.HasStartFrame && !c.Joined) parts.Add("from a picture");
        parts.Add((c.VideoBytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
        parts.Add("kept " + keptLocal.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>The note on the Make tab after Make one like this.</summary>
    public static string PutBack(VideoLike like, bool betterOffered)
    {
        var seed = like.Controls.Seed?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var what = like.Frame is null ? "sentence, quality, shape and seed" : "sentence, first frame, quality, shape and seed";
        var faster = like.Controls.Faster ? ", with Faster" : "";
        var quick = like.Controls.Quality == VideoQuality.Better && !betterOffered;
        // A joined clip keeps only its last part's request (the owner's decision, 2026-11-18): that is what comes back.
        if (like.Parts > 1)
            return $"The kept video is {ClipsJoined(like.Parts)}, and only its last clip can be made again. That clip’s {what} ({seed}) are back{faster}: " +
                (quick ? "it was made at Better, which this card cannot make, so Draw makes the last five seconds again at Quick, not the whole video."
                    : "Draw makes the last five seconds again, not the whole video.") +
                " Press New seed under Seed for new ones.";
        var line = $"The kept video’s {what} ({seed}) are back{faster}: Draw makes the same clip. Press New seed under Seed for new ones.";
        return quick
            ? line.Replace("Draw makes the same clip", "it was made at Better, which this card cannot make, so Draw makes it at Quick", StringComparison.Ordinal)
            : line;
    }

    // ── Joined clips (M22) ──

    /// <summary>"10 s": how long a clip plays, as its facts say it.</summary>
    public static string Length(double seconds) => seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";

    /// <summary>"2 clips joined", as a joined clip's facts say it.</summary>
    public static string ClipsJoined(int parts) => $"{parts} clips joined";

    /// <summary>The button on a clip that continues another.</summary>
    public const string JoinName = "Join with the clip before";

    /// <summary>
    /// Said where clips are joined. The M21 trial: at the join the picture softens in one step and jumps a little (the
    /// first frame after a given frame moves about twice as much as the others). Nothing smooths it: that would take
    /// an encoder, and the frames are the clips' own.
    /// </summary>
    public const string JoinLine = "At each join the picture softens in one step and jumps a little; the clips’ own frames are kept as they are.";

    /// <summary>The note on the Make tab after a join.</summary>
    public static string JoinedNote(VideoTry joined) =>
        $"Joined with the clip before: {ClipsJoined(joined.Parts)} in one file, {Length(joined.Seconds)}, as a new clip beside them. {JoinLine}";

    /// <summary>In a joined kept clip's large view: what the row's sentence and first frame are.</summary>
    public static string JoinedAbout(int parts) =>
        $"This video is {ClipsJoined(parts)}. The sentence, the seed and the first frame shown are its last clip’s; the earlier clips’ are not kept.";

    public const string JoinNothing = "This clip does not continue another, so there is nothing to join it with.";

    public const string JoinWhileMaking = "A clip is being made. Join when it is done.";

    /// <summary>The clip before was a try and the strip let it go (it holds <see cref="VideoSession.Max"/>).</summary>
    public static readonly string JoinBeforeGone =
        $"The clip before is not held any more (only the last {VideoSession.Max} clips are), so there is nothing to join this one with.";

    public const string JoinKeptGone = "The kept video this clip continues has been removed, so there is nothing to join it with.";

    public const string JoinOtherLength = "The clip before is not the same length as this one, so the two cannot be joined.";

    public static string JoinOtherSize(int beforeWidth, int beforeHeight, int width, int height) =>
        $"The clip before is {beforeWidth} × {beforeHeight} and this one {width} × {height}. Only clips of the same size can be joined.";

    /// <summary>A fourth clip is refused: three in one file, 15 seconds, is the most.</summary>
    public static string JoinTooMany(int parts) =>
        $"A video holds at most {VideoClips.MaxParts} clips (15 seconds), and these together would be {parts}. The clip can still be kept and continued by itself.";

    /// <summary>Added to the note after Continue the clip on a video that is full: the next clip cannot be joined to it.</summary>
    public static readonly string ContinuedFull =
        $" It holds {VideoClips.MaxParts} clips, which is the most one video holds: the next clip will stay a clip of its own.";

    /// <summary>The choice's name, as a clip's facts say it ("… · Quick · Faster · seed 123 · …").</summary>
    public const string FasterName = "Faster";

    /// <summary>Said where the first frame is chosen and shown (the M20 trial: a closed smile became a grin by the middle).</summary>
    public const string FollowsLoosely = "A clip follows its first frame only loosely: a face in particular can change as it moves.";

    public const string Quality = "video-quality", Shape = "video-shape", Faster = "video-faster", Seed = "video-seed";

    /// <summary>Every fold of the side panel in order, and whether it starts open.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, bool Open)> Folds =
        [(Quality, "Quality", true), (Shape, "Shape", true), (Faster, FasterName, true), (Seed, "Seed", true)];

    /// <summary>The Faster fold's summary.</summary>
    public static string FasterSummary(bool on) => on ? "On" : "Off";

    /// <summary>
    /// The line under the Faster tick. The M21 trial: a whole Quick clip 32 to 39% faster, a Better one 23 to 26%
    /// (decoding cannot be made faster); side by side the same clip, the finest hair lines a little softer at Quick.
    /// </summary>
    public const string FasterAbout =
        "About a third faster at Quick and a quarter at Better; the same clip to the eye, fine lines a little softer. A clip made with it records it, so Make one like this makes it again.";

    /// <summary>
    /// The note on the Make tab after Continue the clip: where the next clip starts, and, for a kept clip made at
    /// Better on a card that cannot make Better, that it is made at Quick.
    /// </summary>
    public static string Continued(VideoContinue next, bool betterOffered) =>
        Arrived(next.Frame) + (next.Controls.Quality == VideoQuality.Better && !betterOffered
            ? " It was made at Better, which this card cannot make, so the next clip is made at Quick."
            : "") + (next.Parts >= VideoClips.MaxParts ? ContinuedFull : "");

    public static bool OpensByDefault(string id) => Folds.FirstOrDefault(f => f.Id == id) is { Id: not null } f ? f.Open : true;

    public static string Title(string id) => Folds.First(f => f.Id == id).Title;

    /// <summary>"Quick · 960 × 640".</summary>
    public static string QualitySummary(VideoQuality quality, PictureShape shape)
    {
        var (w, h) = VideoRequest.SizeOf(quality, shape);
        return $"{quality} · {w} × {h}";
    }

    /// <summary>The line under a quality's button.</summary>
    public static string Blurb(VideoQuality quality, PictureShape shape, bool faster = false)
    {
        var words = VideoEstimate.For(VideoRequest.For("-", quality, shape, faster: faster)).Words();
        return quality == VideoQuality.Quick
            ? $"Smaller, with the fast decoder: {words}."
            : $"Larger and sharper, with the model’s own decoder: {words}.";
    }

    public static string SeedSummary(long? seed) => seed is { } n ? n.ToString(CultureInfo.InvariantCulture) : "New each time";

    /// <summary>"Takes about 2 minutes" beside Draw.</summary>
    public static string EstimateLine(VideoEstimate estimate) => "Takes " + estimate.Words();

    /// <summary>The status line beside Draw while nothing is made: "Takes about 1½ minutes · Quick · 960 × 640 · Faster".</summary>
    public static string Ready(VideoEstimate estimate, VideoQuality quality, PictureShape shape, bool faster) =>
        $"{EstimateLine(estimate)} · {QualitySummary(quality, shape)}" + (faster ? " · " + FasterName : "");

    /// <summary>How long something took: "48 s", "2 min 29 s".</summary>
    public static string Duration(TimeSpan took)
    {
        var seconds = (int)Math.Round(took.TotalSeconds);
        return seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min {seconds % 60} s";
    }

    /// <summary>The facts under the clip shown: "Try 2 · 960 × 640 · 5 s · Quick · seed 123 · from a picture · made in 2 min 29 s · kept".</summary>
    public static string Meta(VideoTry t)
    {
        var parts = new List<string>
        {
            $"Try {t.Number}",
            $"{t.Result.Width} × {t.Result.Height}",
            Length(t.Seconds),
        };
        if (t.Joined) parts.Add(ClipsJoined(t.Parts));
        if (t.Request.Level is { } level) parts.Add(level.Quality.ToString());
        if (t.Request.Faster) parts.Add(FasterName);
        parts.Add("seed " + t.Result.Seed.ToString(CultureInfo.InvariantCulture));
        // A joined clip was not made in one go, and its first frame is its last part's: neither is said of it.
        if (t.FromPicture && !t.Joined) parts.Add("from a picture");
        if (!t.Joined) parts.Add("made in " + Duration(t.Took));
        if (t.KeptId is not null) parts.Add("kept");
        return string.Join(" · ", parts);
    }

    /// <summary>What a clip being made says: the stage, with the step while drawing.</summary>
    public static string Doing(VideoProgress? p) => p is null ? "Starting…" : p.Stage switch
    {
        VideoStages.Waiting => "Waiting for the graphics card…",
        VideoStages.MakingRoom => "Making room on the graphics card…",
        VideoStages.Starting => "Starting the video engine…",
        VideoStages.Preparing => "Getting ready…",
        VideoStages.ReadingFrame => "Reading the first frame…",
        VideoStages.ReadingSentence => "Reading the model and the sentence…",
        VideoStages.Drawing => p.Step is { } s && p.TotalSteps is { } t ? $"Drawing, step {s} of {t}" : "Drawing…",
        VideoStages.Decoding => "Turning it into frames…",
        VideoStages.Encoding => "Writing the video…",
        _ => "Working…",
    };

    /// <summary>
    /// The same, with one more line while the clip is made on a card that something else holds part of
    /// (<see cref="CardCoordinator.IsVideoCrowded"/>): the estimate was for the whole card.
    /// </summary>
    /// <param name="alwaysPlanned">The card is one where every clip is made so (<see cref="VideoMemory.AlwaysPlanned"/>): nothing else need be on it.</param>
    public static string Doing(VideoProgress? p, bool crowded, bool alwaysPlanned = false) =>
        crowded ? $"{Doing(p)} {(alwaysPlanned ? SmallCard : Crowded)}" : Doing(p);

    public const string Crowded = "Part of the graphics card is in use, so nothing of the clip shows until it is done, and it may take longer than was said.";

    /// <summary>The same line on a card under 24 GB, where it is the card's size and not another program.</summary>
    public const string SmallCard = "On a graphics card of this size nothing of the clip shows until it is done.";

    /// <summary>
    /// What the stage says while a clip is made and no picture of it has come yet. With the fast decoder the engine
    /// sends a frame every four steps; with the model's own decoder (Better) its previews failed at every step in
    /// M20 step 4 ("preview decode failed"), so nothing is promised there.
    /// </summary>
    public static string WhileMaking(VideoDecoder decoder, bool crowded = false) =>
        crowded ? "Nothing of it shows until it is done; the bar below says how far it is."
        : decoder == VideoDecoder.Fast ? "Its frames show here every few steps while it is drawn."
        : "At Better nothing of it shows until it is done; the bar below says how far it is.";

    /// <summary>The note after a clip: how long it took against what was said before.</summary>
    public static string Done(VideoTry t) =>
        $"Made in {Duration(t.Took)} (the estimate said {Duration(TimeSpan.FromSeconds(t.Expected))}).";
}

/// <summary>
/// Kept videos › "Make one like this": what goes back onto the Make tab from a kept clip, so that Draw makes the same
/// clip again: its sentence, its quality and shape (told from its size), whether it was Faster, its seed and its
/// first frame.
/// </summary>
/// <param name="Frame">The first frame to hold, or null for a clip made from the sentence alone (any frame held is let go).</param>
/// <param name="Parts">
/// How many clips the kept video holds. For a joined one (2 or 3) what goes back is its <b>last part</b> (all the
/// row keeps): Draw makes the last five seconds again, not the whole video, and the note says so
/// (<see cref="VideoPanels.PutBack"/>). The frame put back is an ordinary first frame: the clip made from it is not
/// known to continue anything.
/// </param>
public sealed record VideoLike(string Sentence, VideoControls Controls, VideoFrame? Frame, int Parts = 1)
{
    /// <summary>
    /// What a kept clip puts back, or why it cannot. The clip must have been read with its first frame
    /// (<see cref="IClipStore.GetAsync"/>) when it had one.
    /// </summary>
    public static (VideoLike? Like, string? Problem) Of(Clip clip)
    {
        var request = clip.ToRequest();
        var (quality, shape) = request.Level ?? (request.Decoder == VideoDecoder.Own ? VideoQuality.Better : VideoQuality.Quick, VideoFrames.Nearest(clip.Width, clip.Height));
        VideoFrame? frame = null;
        if (clip.HasStartFrame)
        {
            if (clip.StartFrame is not { } bytes) return (null, "Its first frame could not be read.");
            var (held, problem) = VideoFrame.Of(bytes, VideoPanels.FromKeptVideo);
            if (held is null) return (null, "Its first frame cannot start a clip: " + problem);
            frame = held;
            shape = held.Shape;
        }
        var seed = clip.Seed is >= 0 and <= SeedChoice.Highest ? clip.Seed : (long?)null;
        return (new VideoLike(clip.Prompt, new VideoControls(quality, shape, seed, clip.Faster), frame, Math.Max(1, clip.Parts)), null);
    }
}

/// <summary>
/// "Continue the clip" (M21): what goes onto the Make tab so that the next clip starts where a clip ended: the clip's
/// last frame as the first frame, its sentence (still in the box, for the owner to change), its quality and shape,
/// whether it was Faster, and a new seed. The next clip is a clip of its own beside the first; the two are joined only
/// when the owner presses Join on the new one (M22), which is why the frame says which clip it ended
/// (<see cref="VideoFrame.Ended"/>). A joined clip is continued like any other: its last frame is the file's end and
/// its sentence and settings are its last part's.
/// </summary>
/// <param name="Frame">The clip's last frame, held as the next clip's first (<see cref="VideoFrame.Continues"/>).</param>
/// <param name="Parts">How many clips the continued video holds (at <see cref="VideoClips.MaxParts"/> the next clip cannot be joined to it).</param>
public sealed record VideoContinue(string Sentence, VideoControls Controls, VideoFrame Frame, int Parts = 1)
{
    /// <summary>From a try on the Make tab.</summary>
    public static (VideoContinue? Next, string? Problem) Of(VideoTry made, byte[]? lastFrame) =>
        Of(made.Request, lastFrame, VideoPanels.FromTryEnd, new VideoBefore(Try: made.Number), made.Parts);

    /// <summary>From a kept clip (read with or without its video and first frame: neither is needed).</summary>
    public static (VideoContinue? Next, string? Problem) Of(Clip kept, byte[]? lastFrame) =>
        Of(kept.ToRequest() with { StartFrame = null }, lastFrame, VideoPanels.FromKeptVideoEnd, kept.Id > 0 ? new VideoBefore(Kept: kept.Id) : null, kept.Parts);

    /// <summary>
    /// What continues the clip <paramref name="made"/> asked for, or why it cannot be continued.
    /// <paramref name="lastFrame"/> is the JPEG the page made of the clip's last frame, at the clip's own size (the
    /// player's time set to the clip's length and drawn; quality 0.9, as chat pictures): it is checked as every first
    /// frame is, and must be the clip's size, since anything else is not that clip's frame.
    /// </summary>
    /// <param name="ended">The clip the frame ended, when it is one DOG.ai can find again (a try, a kept clip).</param>
    /// <param name="parts">How many clips that video holds.</param>
    public static (VideoContinue? Next, string? Problem) Of(VideoRequest made, byte[]? lastFrame, string from, VideoBefore? ended = null, int parts = 1)
    {
        if (lastFrame is null || lastFrame.Length == 0) return (null, "The clip’s last frame could not be read.");
        var (frame, problem) = VideoFrame.Of(lastFrame, from, continues: true, ended);
        if (frame is null) return (null, "The clip’s last frame cannot start a clip: " + problem);
        if (frame.Width != made.Width || frame.Height != made.Height)
            return (null, $"The last frame read is {frame.Width} × {frame.Height}, not the clip’s {made.Width} × {made.Height}.");
        var quality = made.Level?.Quality ?? (made.Decoder == VideoDecoder.Own ? VideoQuality.Better : VideoQuality.Quick);
        // A new seed: the clip's own seed would start the same motion over from where it ended.
        return (new VideoContinue(made.Prompt, new VideoControls(quality, frame.Shape, Seed: null, made.Faster), frame, Math.Max(1, parts)), null);
    }
}

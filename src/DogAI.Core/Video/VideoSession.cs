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
public sealed record VideoTry(
    int Number,
    VideoRequest Request,
    VideoResult Result,
    TimeSpan Took,
    double Expected,
    long? KeptId = null,
    byte[]? Still = null)
{
    /// <summary>The row to keep: this clip as it was made, with its still when there is one.</summary>
    public Clip ToKeep() => Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, Request, Result, Still);

    public bool FromPicture => Request.StartFrame is not null;

    // Kept beside the still's bytes, not in the record: a record's fields count when two tries are compared.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> Addresses = new();

    /// <summary>The still as a data: address (from memory; never a file), made once; null without a still.</summary>
    public string? StillSrc => Still is { } still ? Addresses.GetValue(still, static s => "data:image/webp;base64," + Convert.ToBase64String(s)) : null;
}

/// <summary>
/// The picture a clip will start from, held in memory only until it is let go: the JPEG the page cut from a file (or,
/// from step 5, a picture DOG.ai holds), already checked as a first frame for both qualities of its shape.
/// </summary>
/// <param name="From">Where it came from, in words for the screen ("a file").</param>
public sealed record VideoFrame(byte[] Bytes, int Width, int Height, PictureShape Shape, string From)
{
    /// <summary>
    /// The frame for these bytes, or why there is none: a WebP DOG.ai drew or a JPEG the page made (its colour profile
    /// taken out first), of one of the three shapes, acceptable to both Quick's and Better's size of that shape.
    /// </summary>
    public static (VideoFrame? Frame, string? Problem) Of(byte[]? bytes, string from)
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
        return (new VideoFrame(plain, size.Width, size.Height, shape, from), null);
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

    /// <summary>
    /// Holds the picture the next clip starts from (null lets it go). Step 5's "Animate…" buttons hand their picture
    /// over through here.
    /// </summary>
    public void Hold(VideoFrame? frame)
    {
        Frame = frame;
        Changed?.Invoke();
    }

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
        Changed?.Invoke();
    }

    /// <summary>Marks a try as kept, so Keep is not offered twice for it.</summary>
    public VideoTry Kept(VideoTry kept, long id) => Replace(kept, kept with { KeptId = id });

    /// <summary>Gives a try the still the page made from its first frame.</summary>
    public VideoTry WithStill(VideoTry made, byte[] still) => Replace(made, made with { Still = still });

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
            var made = new VideoTry(Interlocked.Increment(ref _number), request with { Seed = result.Seed, Preview = false }, result, clock.Elapsed, estimate.Total);
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

    private CancellationTokenSource Take(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (Interlocked.CompareExchange(ref _cts, cts, null) is null) return cts;
        cts.Dispose();
        throw new InvalidOperationException("A video is already being made.");
    }

    private VideoTry Replace(VideoTry was, VideoTry now)
    {
        var replaced = false;
        lock (_gate)
        {
            // Compared by reference: a try's fields could match another's.
            var index = _tries.FindIndex(t => ReferenceEquals(t, was));
            if (index >= 0)
            {
                _tries[index] = now;
                replaced = true;
            }
            if (ReferenceEquals(_selected, was)) _selected = now;
            _shown = [.. _tries];
        }
        if (replaced) Changed?.Invoke();
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
public sealed record VideoControls(VideoQuality Quality = VideoQuality.Quick, PictureShape Shape = PictureShape.Wide, long? Seed = null);

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
        var clean = Clean(new VideoDocument { Quality = controls.Quality, Shape = controls.Shape, Seed = controls.Seed });
        if (clean == _controls) return;
        _controls = clean;
        if (documents is not null)
            await documents.SaveAsync(Key, new VideoDocument { Quality = clean.Quality, Shape = clean.Shape, Seed = clean.Seed }, ct);
        Changed?.Invoke();
    }

    /// <summary>What was read, with anything this class could not have written left out.</summary>
    private static VideoControls Clean(VideoDocument? doc) => doc is null ? new() : new(
        Enum.IsDefined(doc.Quality) ? doc.Quality : VideoQuality.Quick,
        Enum.IsDefined(doc.Shape) ? doc.Shape : PictureShape.Wide,
        doc.Seed is >= 0 and <= SeedChoice.Highest ? doc.Seed : null);

    /// <summary>What is saved: three controls, no sentence and no picture.</summary>
    public sealed class VideoDocument
    {
        public VideoQuality Quality { get; set; }
        public PictureShape Shape { get; set; } = PictureShape.Wide;
        public long? Seed { get; set; }
    }
}

/// <summary>The Video section's places, fold ids and wording, so the screens stay consistent and the rules can be tested.</summary>
public static class VideoPanels
{
    /// <summary>The Make tab's address.</summary>
    public const string Address = "video";

    /// <summary>Said where the first frame is chosen and shown (the M20 trial: a closed smile became a grin by the middle).</summary>
    public const string FollowsLoosely = "A clip follows its first frame only loosely: a face in particular can change as it moves.";

    public const string Quality = "video-quality", Shape = "video-shape", Seed = "video-seed";

    /// <summary>Every fold of the side panel in order, and whether it starts open.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, bool Open)> Folds =
        [(Quality, "Quality", true), (Shape, "Shape", true), (Seed, "Seed", true)];

    public static bool OpensByDefault(string id) => Folds.FirstOrDefault(f => f.Id == id) is { Id: not null } f ? f.Open : true;

    public static string Title(string id) => Folds.First(f => f.Id == id).Title;

    /// <summary>"Quick · 960 × 640".</summary>
    public static string QualitySummary(VideoQuality quality, PictureShape shape)
    {
        var (w, h) = VideoRequest.SizeOf(quality, shape);
        return $"{quality} · {w} × {h}";
    }

    /// <summary>The line under a quality's button.</summary>
    public static string Blurb(VideoQuality quality, PictureShape shape)
    {
        var words = VideoEstimate.For(VideoRequest.For("-", quality, shape)).Words();
        return quality == VideoQuality.Quick
            ? $"Smaller, with the fast decoder: {words}."
            : $"Larger and sharper, with the model’s own decoder: {words}.";
    }

    public static string SeedSummary(long? seed) => seed is { } n ? n.ToString(CultureInfo.InvariantCulture) : "New each time";

    /// <summary>"Takes about 2 minutes" beside Draw.</summary>
    public static string EstimateLine(VideoEstimate estimate) => "Takes " + estimate.Words();

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
            (t.Result.Fps > 0 ? (t.Result.Frames / (double)t.Result.Fps).ToString("0.#", CultureInfo.InvariantCulture) : "0") + " s",
        };
        if (t.Request.Level is { } level) parts.Add(level.Quality.ToString());
        parts.Add("seed " + t.Result.Seed.ToString(CultureInfo.InvariantCulture));
        if (t.FromPicture) parts.Add("from a picture");
        parts.Add("made in " + Duration(t.Took));
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
    /// What the stage says while a clip is made and no picture of it has come yet. With the fast decoder the engine
    /// sends a frame every four steps; with the model's own decoder (Better) its previews failed at every step in
    /// M20 step 4 ("preview decode failed"), so nothing is promised there.
    /// </summary>
    public static string WhileMaking(VideoDecoder decoder) => decoder == VideoDecoder.Fast
        ? "Its frames show here every few steps while it is drawn."
        : "At Better nothing of it shows until it is done; the bar below says how far it is.";

    /// <summary>The note after a clip: how long it took against what was said before.</summary>
    public static string Done(VideoTry t) =>
        $"Made in {Duration(t.Took)} (the estimate said {Duration(TimeSpan.FromSeconds(t.Expected))}).";
}

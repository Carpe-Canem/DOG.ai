using DogAI.Core.Images;
using DogAI.Core.Models;

namespace DogAI.Core.Video;

/// <summary>
/// How long a clip is expected to take, in seconds, part by part. The numbers are the M20 trial's (docs/PLAN.md,
/// "Trial results"), and the M21 trial's for the choice "Faster": an RTX 4090 with the card cleared. Only drawing counts steps; the page runs its bar on
/// <see cref="Decoding"/> and <see cref="Encoding"/> for the two parts the engine reports no count for.
/// </summary>
/// <param name="Start">Starting the program and reading the model's files and the sentence.</param>
/// <param name="Drawing">Every step together.</param>
public sealed record VideoEstimate(double Start, double Drawing, double Decoding, double Encoding)
{
    public double Total => Start + Drawing + Decoding + Encoding;

    /// <summary>Megapixels against the seconds one step took for a clip of 121 frames.</summary>
    private static readonly (double Megapixels, double Seconds)[] PerStep = [(0.40, 2.98), (0.61, 4.95), (0.90, 8.4), (1.01, 9.95)];

    /// <summary>The model's own decoder. From 0.9 megapixels it decodes in pieces after two refused attempts, hence the jump.</summary>
    private static readonly (double Megapixels, double Seconds)[] OwnDecoder = [(0.40, 26), (0.61, 40), (0.90, 178), (1.01, 192)];

    private static readonly (double Megapixels, double Seconds)[] Encoder = [(0.40, 4), (0.61, 8.5), (0.90, 11), (1.01, 12.5)];

    /// <summary>
    /// What is left of the drawing time with the choice "Faster" (<see cref="VideoRequest.Faster"/>), by megapixels.
    /// The M21 trial, whole clips of 20 steps: 0.58 to 0.64 of the plain drawing at Quick's size (960 × 640) and
    /// 0.51 to 0.54 at Better's (1216 × 832); smaller sizes were not tried and are taken a little worse than Quick. It is a
    /// share of the plain table and not a table of its own, so the two can never drift apart: 3.1 s a step at Quick
    /// and 5.3 at Better. Start, decoding and encoding are untouched by it.
    /// </summary>
    private static readonly (double Megapixels, double Share)[] FasterDrawing = [(0.40, 0.65), (0.6144, 0.62), (1.0117, 0.53)];

    /// <summary>The share of the plain drawing time a Faster clip of this size takes: between the sizes tried on a straight line, beyond them the nearest.</summary>
    public static double FasterShare(double megapixels) =>
        Math.Clamp(At(FasterDrawing, megapixels), FasterDrawing[^1].Share, FasterDrawing[0].Share);

    /// <summary>The fast decoder took one to two seconds at every size tried.</summary>
    private const double FastDecoder = 2;

    /// <summary>
    /// The program was just started for this clip and Windows still has the model's files in its cache (a clip was
    /// made a moment ago): 6 seconds in the trial.
    /// </summary>
    public const double WarmStart = 6;

    /// <summary>
    /// After anything else large was read (a chat model, a picture checkpoint) the files are read from the disk again:
    /// the text encoder took 12 seconds and the model 20.
    /// </summary>
    public const double ColdStart = 35;

    /// <summary>A start frame is read in half a second.</summary>
    private const double StartFrame = 0.5;

    /// <summary>How long after a clip Windows is taken to still hold the model's files.</summary>
    public static readonly TimeSpan WarmFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the next clip starts warm: one was finished within <see cref="WarmFor"/> and nothing large (a chat model,
    /// the picture model) is in memory now, which would have pushed the files out of Windows' cache. Errs cold.
    /// </summary>
    public static bool IsWarm(DateTime? lastMadeAtUtc, DateTime nowUtc, bool somethingLargeInMemory) =>
        !somethingLargeInMemory && lastMadeAtUtc is { } last && nowUtc - last >= TimeSpan.Zero && nowUtc - last <= WarmFor;

    /// <param name="warm">True when a clip was made a moment ago and nothing large was loaded since.</param>
    public static VideoEstimate For(VideoRequest request, bool warm = false)
    {
        var megapixels = request.Width * (double)request.Height / 1_000_000.0;
        // The model works on one picture for every four frames, and a step grows faster than their number:
        // 33 frames took 0.21 of a 121-frame step (twice measured), 193 frames 1.9 times one.
        var pictures = (request.Frames - 1) / 4 + 1;
        var length = Math.Pow(pictures / 31.0, 1.25);
        var frames = request.Frames / (double)VideoRequest.FiveSeconds;
        return new VideoEstimate(
            (warm ? WarmStart : ColdStart) + (request.StartFrame is null ? 0 : StartFrame),
            // With Faster the engine still reports every step (it skips 5 to 7 of 20 inside), so the bar counts as ever.
            request.Steps * At(PerStep, megapixels) * length * (request.Faster ? FasterShare(megapixels) : 1),
            request.Decoder == VideoDecoder.Fast ? FastDecoder : At(OwnDecoder, megapixels) * frames,
            At(Encoder, megapixels) * frames);
    }

    /// <summary>"about 2 minutes", "about 3½ minutes", "about 40 seconds": the whole clip, for the line beside Draw.</summary>
    public string Words()
    {
        var seconds = Total;
        if (seconds < 50) return $"about {Math.Max(10, Math.Round(seconds / 10) * 10):0} seconds";
        var halves = (int)Math.Round(seconds / 30);
        var minutes = halves / 2;
        if (halves % 2 == 1) return minutes == 0 ? "about half a minute" : $"about {minutes}½ minutes";
        return minutes == 1 ? "about a minute" : $"about {minutes} minutes";
    }

    /// <summary>The table's value at a size: on a straight line between the two nearest sizes measured, and beyond its ends on the nearest line.</summary>
    private static double At((double X, double Y)[] table, double x)
    {
        var i = 1;
        while (i < table.Length - 1 && x > table[i].X) i++;
        var (a, b) = (table[i - 1], table[i]);
        return Math.Max(0, a.Y + (x - a.X) * (b.Y - a.Y) / (b.X - a.X));
    }
}

/// <summary>
/// How much graphics memory a clip needs. A clip takes the whole card: the chat model is set aside and everything else
/// let go before it starts, so this is not weighed against them, only against what the cleared card has.
/// </summary>
public static class VideoMemory
{
    private const long Gb = 1L << 30;

    /// <summary>Above this many pixels a clip counts as Better's size (Quick is 0.61 million, Better 1.01 million).</summary>
    public const int LargeAbovePixels = 800_000;

    /// <summary>
    /// The peak for one clip with the margin in it: 15 GB at Quick's size, 16 GB at Better's (measured 13.8 and
    /// 15.0 GB with the program started with <c>--max-vram -3</c>; with more room the engine uses it and gives way by
    /// itself). A cleared 24 GB card has about 22.5 GB.
    /// </summary>
    public static long Need(VideoRequest request) =>
        (long)request.Width * request.Height > LargeAbovePixels ? 16 * Gb : 15 * Gb;

    /// <summary>
    /// The least free graphics memory a Quick clip is made with (M21 step 5). On a card that has less than the whole
    /// model needs, the engine is given a plan (<see cref="EnginePlan"/>), keeps the model itself in RAM
    /// (<see cref="PlannedRamNeed"/>) and brings it to the card piece by piece. Measured with another program holding
    /// the rest of a 24 GB card. The engine alone: with 14.9, 11.1, 9.1, 7.2 and 6.8 GB free a Quick clip took 142 to
    /// 146 seconds (139 on the whole card), with Faster 107 and 106 at 7.2 and 6.8 GB; the engine took the plan and
    /// 2.7 to 2.8 GB, and the other program's memory was never touched. With 6.2 GB free (a plan of 3.2) every step
    /// ran short of its working memory and the clip took 171 seconds; with Faster at 6.1 GB, and plain at 5.1 GB,
    /// the engine made nothing ("returned no results"). In the app: 2 min 26 s, 2 min 26 s, 2 min 28 s and 2 min 28 s
    /// with 14.9, 11.1, 9.1 and 7.0 GB free (the estimate said 2 min 26 s), Faster 1 min 49 s at 7.0 GB (1 min 48 s).
    /// Better is not made this way: its decoder needs the model kept on the card (<see cref="PlanCapOwnDecoder"/>).
    /// </summary>
    public const long QuickPlannedNeed = 7 * Gb;

    /// <summary>
    /// What must be free on the cleared card for the clip to be made at all: Better's whole need, and for Quick the
    /// least a planned clip was made with. <see cref="Need(VideoRequest)"/> stays what the clip takes when it can.
    /// </summary>
    public static long LeastNeed(VideoRequest request) => IsLarge(request) ? Need(request) : QuickPlannedNeed;

    public static long LeastNeed(VideoQuality quality) => LeastNeed(VideoRequest.For("-", quality, PictureShape.Wide));

    private static bool IsLarge(VideoRequest request) => (long)request.Width * request.Height > LargeAbovePixels;

    /// <summary>
    /// The smallest card a video is made on: 8 GB, which has about 7 GB once Windows has its part
    /// (<see cref="QuickPlannedNeed"/>). Not tried on a real card of that size: see <see cref="SmallCardNote"/>.
    /// </summary>
    public const long SmallestCard = 8 * Gb;

    /// <summary>
    /// True for a card that makes clips, but only Quick ones and always by a plan: 8 GB up to and including 16 GB.
    /// None of that size was tried for real (<see cref="SmallCardNote"/>).
    /// </summary>
    public static bool IsSmallCard(long? cardTotal) =>
        cardTotal is { } total && CardCanMake(total, VideoQuality.Quick) && !CardCanMake(total, VideoQuality.Better);

    /// <summary>
    /// True for a card that never has the whole model's room (<see cref="WholeModelFree"/>) whatever is on it, so
    /// every clip on it is made by a plan: every card under 24 GB.
    /// </summary>
    public static bool AlwaysPlanned(long? cardTotal) => cardTotal is { } total && total - WindowsHolds < WholeModelFree;

    /// <summary>What the page says on such a card, so nobody takes the numbers for more than they are.</summary>
    public const string SmallCardNote =
        "DOG.ai has not been tried on a graphics card of this size. It was measured on a 24 GB card with another program holding the rest: " +
        "a Quick clip then took about 2½ minutes. Most of the video model is kept in the PC’s own memory meanwhile, " +
        "which takes about 12 GB of free memory (RAM), and nothing of the clip shows until it is done.";

    /// <summary>
    /// The free RAM a clip made by a plan needs (M21 step 5). The engine then keeps the model itself (9.5 GB) in
    /// RAM: its own memory there was 11.0 GB in the planned clips measured (plans of 3.8 to 10 GB, plain and Faster),
    /// and free RAM fell by 11 GB while the clip was made, where a clip on the whole card takes 1.6 GB. The engine
    /// decides by the free RAM it finds, less 2 GB: with 12.5 GB free the clip took its usual 144 seconds and 1.7 GB
    /// was left; with 9.8 GB free the engine kept the text encoder in RAM and the model on a card that had no room
    /// for it, and the clip took 401 seconds. And with 17 GB or more free it keeps the text encoder (5.8 GB) in
    /// RAM as well: 16.6 GB of its own, at normal speed, with about 1 GB of RAM left (what M21 step 1 saw).
    /// A Better clip by a plan (13 GB, the model kept on the card) had the text encoder and its decoder in RAM:
    /// 9.6 GB of its own, in 7 min 16 s. One need for both, with a gigabyte to spare over the larger.
    /// </summary>
    public const long PlannedRamNeed = 12 * Gb;

    /// <summary>
    /// Whether the PC has the free RAM for this clip: only a clip that gets a plan needs it. Unknown free RAM, and a
    /// clip on the whole card (started and judged exactly as in M20), always pass.
    /// </summary>
    public static bool RamEnough(long? freeRamBytes, long? cardFreeBytes) =>
        !Crowded(cardFreeBytes) || freeRamBytes is not { } ram || ram >= PlannedRamNeed;

    /// <summary>
    /// What a graphics card reports for its size is a little under the size on the box (this PC's 24 GB card says
    /// 24,564 MiB, a 16 GB card about 16,376), and the needs above are whole gigabytes with a margin in them. So a card,
    /// and the room on it, count within this of a need.
    /// </summary>
    public const long CardSizeSlack = 512L << 20;

    /// <summary>What Windows itself keeps on the card while nothing else is there (0.9 to 1.4 GB measured here).</summary>
    public const long WindowsHolds = 1 * Gb;

    /// <summary>The need of a clip at this quality (any shape: the shapes of one quality have the same need).</summary>
    public static long Need(VideoQuality quality) => Need(VideoRequest.For("-", quality, PictureShape.Wide));

    /// <summary>
    /// Whether a card of this size can make the clip at all: once everything else has left it, what Windows keeps
    /// aside, the need must fit (within <see cref="CardSizeSlack"/>). A card that could not be measured counts as able
    /// (the coordinator clears it and goes ahead). One rule for the page (Better is not offered on a 16 GB card) and
    /// for <see cref="CardCoordinator"/>, which refuses before anything is moved.
    /// </summary>
    public static bool CardCanMake(long? cardTotal, VideoRequest request) =>
        cardTotal is not { } total || total - WindowsHolds + CardSizeSlack >= LeastNeed(request);

    public static bool CardCanMake(long? cardTotal, VideoQuality quality) =>
        CardCanMake(cardTotal, VideoRequest.For("-", quality, PictureShape.Wide));

    /// <summary>Whether this much free memory on a cleared card is room enough for a clip that needs <paramref name="need"/>.</summary>
    public static bool RoomEnough(long freeBytes, long need) => freeBytes + CardSizeSlack >= need;

    /// <summary>
    /// A planned Quick clip's need has little margin in it, so the room counts within a quarter of a gigabyte only:
    /// the least free memory a clip was made with at normal speed was 6.8 GB (a plan of 3.8), plain and Faster.
    /// </summary>
    public const long PlannedSlack = 256L << 20;

    /// <summary>
    /// Whether the cleared card has the room for this clip at all: Better's whole need (within
    /// <see cref="CardSizeSlack"/>, as in M20), or for Quick the least a planned clip is made with
    /// (<see cref="QuickPlannedNeed"/>, within <see cref="PlannedSlack"/>).
    /// </summary>
    public static bool RoomFor(long freeBytes, VideoRequest request) =>
        IsLarge(request) ? RoomEnough(freeBytes, Need(request)) : freeBytes + PlannedSlack >= QuickPlannedNeed;

    /// <summary>
    /// With this much free on the cleared card the whole model and its working memory fit: the engine then takes up
    /// to 21.7 GB by itself (measured in M20 steps 2 and 6: the card at 23.0 of 24.0 GB with Windows' 1.4 GB).
    /// </summary>
    public const long WholeModelFree = 22 * Gb;

    /// <summary>What the engine is told to leave free of what DOG.ai measured, when part of the card is in use.</summary>
    public const long PlanReserve = 3 * Gb;

    /// <summary>
    /// The most the engine is told to plan with when part of the card is in use. With a plan of 11 GB or less the
    /// engine keeps the model itself (9.5 GB) in RAM and brings it to the card piece by piece: it then takes the plan
    /// and about 2.8 GB (8.6 GB at a plan of 5.8, 13.8 GB at 11) and turns the clip into frames in a second or two.
    /// With 12.5 GB or more it keeps the model on the card instead and takes 17 GB while the clip is turned into
    /// frames, whatever it was told: beside a program holding 6.8 GB that step then took 13 seconds in one run and
    /// had not ended after 2½ minutes in another (M20 step 6). So the cap is well under the point where it changes.
    /// That is for the fast decoder (Quick), which turns the whole clip into frames at once.
    /// </summary>
    public const long PlanCap = 10 * Gb;

    /// <summary>
    /// The same for the model's own decoder (Better), and the other way round: it works in pieces and needs the
    /// model kept on the card. With a plan of 12.5 or 13.1 GB a Better clip beside a program holding 6.8 GB took the
    /// engine 13.6 GB and decoded in its usual 3¼ minutes; with a plan of 10 GB its decoding had not ended after nine
    /// minutes and free RAM fell to 1.8 GB (M20 step 6, in the app).
    /// </summary>
    public const long PlanCapOwnDecoder = 13 * Gb;

    /// <summary>
    /// True when the cleared card has too little free for the whole model (something else holds part of it: another
    /// program, or a guest of DOG.ai's that is being used). The clip is still made, with a plan for the engine
    /// (<see cref="EnginePlan"/>) and without previews: beside a program holding 6.8 GB a Quick clip then took
    /// 2 min 23 s in the app against an estimate of 2 min 26 s.
    /// </summary>
    public static bool Crowded(long? freeBytes) => freeBytes is { } free && free < WholeModelFree;

    /// <summary>
    /// The engine's <c>--max-vram</c> value for a clip, from the free memory DOG.ai measured after it cleared the card.
    /// <para>
    /// The engine cannot be left to measure for itself: it does not see what other programs hold (with 6.8 GB held by
    /// another program it reported 23.0 GB free, filled the card to 23.9 GB, borrowed 2.8 GB of Windows' shared memory,
    /// and a clip of 58 seconds took 222; M20 step 6). So: with the whole card free, or a card that could not be
    /// measured, <c>-3</c> as in every clip measured in M20 (the engine keeps 3 GB of what it sees); otherwise a plan
    /// in GiB worked out here, what is free less <see cref="PlanReserve"/> and never over the decoder's cap.
    /// </para>
    /// </summary>
    /// <param name="decoder">The clip's decoder: each has its own cap (<see cref="PlanCap"/>, <see cref="PlanCapOwnDecoder"/>).</param>
    public static string EnginePlan(long? freeBytes, VideoDecoder decoder = VideoDecoder.Fast)
    {
        if (freeBytes is not { } free || !Crowded(free)) return WholeCardPlan;
        var plan = Math.Min(free - PlanReserve, decoder == VideoDecoder.Own ? PlanCapOwnDecoder : PlanCap);
        // One decimal, rounded down, in the engine's own unit; never nothing or less (0 and below mean something else to it).
        var tenths = Math.Max(10, plan * 10 / Gb);
        return (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The value for a card with nothing else on it: the engine keeps 3 GB of what it measures itself.</summary>
    public const string WholeCardPlan = "-3";

    /// <summary>What the owner is told when a clip that would be made by a plan finds too little free RAM.</summary>
    /// <param name="alwaysPlanned">The card is one that always gets a plan; else something holds part of a larger card.</param>
    public static string TooLittleRam(long freeRamBytes, bool alwaysPlanned) =>
        (alwaysPlanned ? "On a graphics card of this size" : "With part of the graphics card in use") +
        $" the video model is kept in the PC’s own memory while a clip is made. That takes about {Sizes.Gb(PlannedRamNeed)} of free memory (RAM), and only {Sizes.Gb(freeRamBytes)} is free. Close other programs, then try again.";

    /// <summary>Why a card of this size cannot make clips at this quality, in words, or null when it can.</summary>
    public static string? CannotMake(long? cardTotal, VideoQuality quality)
    {
        if (CardCanMake(cardTotal, quality)) return null;
        var need = LeastNeed(quality);
        return $"{quality} needs about {Sizes.Gb(need)} of free graphics memory, more than this card ({Sizes.Gb(cardTotal ?? 0)}) has once Windows has its part." +
            (quality == VideoQuality.Better && CardCanMake(cardTotal, VideoQuality.Quick) ? " Quick works on it." : "");
    }
}

/// <summary>
/// The video models folder: <c>&lt;models&gt;\video</c> with the engine's own folder names beneath it, created only
/// when needed, as <c>&lt;models&gt;\image</c> is.
/// </summary>
public static class VideoFolders
{
    public const string Name = "video";
    public const string DiffusionModels = "diffusion_models";
    public const string TextEncoders = "text_encoders";
    public const string Vae = "vae";

    public static IReadOnlyList<string> All { get; } = [DiffusionModels, TextEncoders, Vae];

    /// <summary>The full paths of a set of files under the video models folder <paramref name="root"/>.</summary>
    public static VideoModel ModelFor(string root, VideoFiles files) => new(
        In(root, DiffusionModels, files.Model),
        In(root, Vae, files.Vae),
        In(root, TextEncoders, files.TextEncoder),
        In(root, Vae, files.FastDecoder));

    private static string In(string root, string folder, string file) =>
        Path.Combine(root, folder, file.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// A video model's four files, each named by its path inside its own folder under <c>&lt;models&gt;\video</c>, never by
/// a full path: a kept clip holds these, so a moved models folder or portable install needs nothing rewritten.
/// </summary>
/// <param name="Model">Inside <see cref="VideoFolders.DiffusionModels"/>.</param>
/// <param name="Vae">Inside <see cref="VideoFolders.Vae"/>.</param>
/// <param name="TextEncoder">Inside <see cref="VideoFolders.TextEncoders"/>.</param>
/// <param name="FastDecoder">Inside <see cref="VideoFolders.Vae"/> too: it is a decoder.</param>
public sealed record VideoFiles(string Model, string Vae, string TextEncoder, string FastDecoder)
{
    /// <summary>Wan 2.2 TI2V-5B as the M20 trial used it: the one family of the first version.</summary>
    public static VideoFiles Wan22 { get; } = new(
        "wan2.2_ti2v_5B_fp16.safetensors", "wan2.2_vae.safetensors", "umt5-xxl-encoder-Q8_0.gguf", "taew2_2.safetensors");

    /// <summary>One spelling per file (forward slashes), as kept pictures name theirs.</summary>
    public VideoFiles Normalised() => new(
        ImageModelCatalog.Normalise(Model), ImageModelCatalog.Normalise(Vae),
        ImageModelCatalog.Normalise(TextEncoder), ImageModelCatalog.Normalise(FastDecoder));

    /// <summary>What is wrong with the names, or nothing: each must be a path that cannot leave its folder.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var (file, name) in new[] { (Model, "video model"), (Vae, "decoder"), (TextEncoder, "text encoder"), (FastDecoder, "fast decoder") })
        {
            if (!ImageRequest.IsSafeRelativePath(file)) problems.Add($"The {name} must be named by its path inside its own folder.");
            else if (!VideoModel.IsOpenable(file)) problems.Add($"The {name} must be a .safetensors or .gguf file.");
        }
        return problems;
    }
}

/// <summary>What a clip must be before it is shown or kept.</summary>
public static class VideoClips
{
    /// <summary>The most bytes a clip may have. Five seconds at Better's size were 5 to 14 MB at the chosen setting.</summary>
    public const int MaxBytes = 96 * 1024 * 1024;

    /// <summary>
    /// What stops these bytes from being a clip, in plain words, or null: a WebM by its own structure
    /// (<see cref="WebmInfo"/>: one VP8 track and nothing a program could have written a prompt into), and, with
    /// <paramref name="width"/>, <paramref name="height"/> and <paramref name="frames"/>, of the size and length asked for.
    /// </summary>
    /// <param name="parts">
    /// How many clips the file holds (M22): 1 for a clip as the engine made it, which must have exactly
    /// <paramref name="frames"/>; 2 or 3 for clips joined by <see cref="WebmJoin"/>, which must have
    /// <see cref="FramesOf"/> and be written as DOG.ai writes a joined file, with an index and a cue list that point
    /// at its frames.
    /// </param>
    public static string? Problem(byte[]? webm, int width = 0, int height = 0, int frames = 0, int parts = 1)
    {
        if (parts is < 1 or > MaxParts) return TooManyParts;
        if (webm is null || webm.Length == 0) return "The video is empty.";
        if (webm.Length > MaxBytes) return "The video is too large a file.";
        // A single clip is read exactly as before M22; only a joined one needs its parts' places.
        WebmParts? file = null;
        if ((parts == 1 ? WebmInfo.Read(webm, out var why) : (file = WebmInfo.Parts(webm, out why))?.Info) is not { } info)
            return "The video is not a plain WebM file: " + why;
        if (width > 0 && height > 0 && (info.Width != width || info.Height != height))
            return $"The video is {info.Width} × {info.Height}, not the {width} × {height} asked for.";
        if (file is null)
            return frames > 0 && info.Frames != frames ? $"The video has {info.Frames} frames, not the {frames} asked for." : null;
        if (frames > 0 && info.Frames != FramesOf(frames, parts))
            return $"The video has {info.Frames} frames, not the {FramesOf(frames, parts)} that {parts} joined clips of {frames} frames have.";
        return (WebmJoin.ShapeProblem(file) ?? file.IndexProblem()) is { } wrong ? "The joined video is not written as DOG.ai writes one: " + wrong + "." : null;
    }

    /// <summary>The most clips one file holds (the owner's decision, 2026-11-18): a clip and two continuations, 15 seconds.</summary>
    public const int MaxParts = 3;

    public static readonly string TooManyParts = $"A video holds between 1 and {MaxParts} clips.";

    /// <summary>
    /// How many frames a file of <paramref name="parts"/> joined clips has, each of <paramref name="frames"/>: a
    /// continuation's first frame repeats the clip before's last and is left out, so 121 gives 241 and 361.
    /// </summary>
    public static int FramesOf(int frames, int parts) => parts <= 1 ? frames : parts * (frames - 1) + 1;

    /// <summary>How long such a file plays, in seconds, counted as a single clip's length is (frames over frames a second).</summary>
    public static double SecondsOf(int frames, int fps, int parts = 1) => fps > 0 ? FramesOf(frames, parts) / (double)fps : 0;

    /// <summary>
    /// When such a file's last frame shows, in seconds. The page reads a clip's last frame by setting the player's
    /// time to the file's own length; this is what it uses when the file does not say one.
    /// </summary>
    public static double LastFrameSeconds(int frames, int fps, int parts = 1) => fps > 0 ? Math.Max(0, FramesOf(frames, parts) - 1) / (double)fps : 0;

    /// <summary>The same for a request's clip.</summary>
    public static string? Problem(byte[]? webm, VideoRequest request) => Problem(webm, request.Width, request.Height, request.Frames);
}

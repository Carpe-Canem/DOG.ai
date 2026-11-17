using DogAI.Core.Images;
using DogAI.Core.Models;

namespace DogAI.Core.Video;

/// <summary>
/// How long a clip is expected to take, in seconds, part by part. The numbers are the M20 trial's (docs/PLAN.md,
/// "Trial results"): an RTX 4090 with the card cleared. Only drawing counts steps; the page runs its bar on
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
            request.Steps * At(PerStep, megapixels) * length,
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
    /// The smallest card a video is made on. Under it the engine keeps the whole model in RAM (15 GB of it), which was
    /// asked of the engine in the trial but never tried on such a card.
    /// </summary>
    public const long SmallestCard = 16 * Gb;

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
        cardTotal is not { } total || total - WindowsHolds + CardSizeSlack >= Need(request);

    public static bool CardCanMake(long? cardTotal, VideoQuality quality) =>
        CardCanMake(cardTotal, VideoRequest.For("-", quality, PictureShape.Wide));

    /// <summary>Whether this much free memory on a cleared card is room enough for a clip that needs <paramref name="need"/>.</summary>
    public static bool RoomEnough(long freeBytes, long need) => freeBytes + CardSizeSlack >= need;

    /// <summary>
    /// With this much free on the cleared card the whole model and its working memory fit: the engine then takes up
    /// to 21.7 GB by itself (measured in M20 steps 2 and 6: the card at 23.0 of 24.0 GB with Windows' 1.4 GB).
    /// </summary>
    public const long WholeModelFree = 22 * Gb;

    /// <summary>What the engine is told to leave free of what DOG.ai measured, when part of the card is in use.</summary>
    public const long PlanReserve = 3 * Gb;

    /// <summary>
    /// The most the engine is told to plan with when part of the card is in use. It is under the model's own 15.3 GB
    /// on purpose: the engine then keeps a part of the model in RAM (the text encoder, 5.8 GB) and takes 14 to 17 GB
    /// of the card (measured with another program holding 6.8 GB), where with the whole model on the card it takes
    /// 21.7 GB whatever it was told.
    /// </summary>
    public const long PlanCap = 14 * Gb;

    /// <summary>
    /// True when the cleared card has too little free for the whole model (something else holds part of it: another
    /// program, or a guest of DOG.ai's that is being used). The clip is still made, with a plan for the engine
    /// (<see cref="EnginePlan"/>) and without previews: beside a program holding 6.8 GB a Quick clip then took 150
    /// seconds against an estimate of 146 (with previews 268: each full-size preview took most of a minute).
    /// </summary>
    public static bool Crowded(long? freeBytes) => freeBytes is { } free && free < WholeModelFree;

    /// <summary>
    /// The engine's <c>--max-vram</c> value for a clip, from the free memory DOG.ai measured after it cleared the card.
    /// <para>
    /// The engine cannot be left to measure for itself: it does not see what other programs hold (with 6.8 GB held by
    /// another program it reported 23.0 GB free, filled the card to 23.9 GB, borrowed 2.8 GB of Windows' shared memory,
    /// and a clip of 58 seconds took 222; M20 step 6). So: with the whole card free, or a card that could not be
    /// measured, <c>-3</c> as in every clip measured in M20 (the engine keeps 3 GB of what it sees); otherwise a plan
    /// in GiB worked out here, what is free less <see cref="PlanReserve"/> and never over <see cref="PlanCap"/>.
    /// </para>
    /// </summary>
    public static string EnginePlan(long? freeBytes)
    {
        if (freeBytes is not { } free || !Crowded(free)) return WholeCardPlan;
        var plan = Math.Min(free - PlanReserve, PlanCap);
        // One decimal, rounded down, in the engine's own unit; never nothing or less (0 and below mean something else to it).
        var tenths = Math.Max(10, plan * 10 / Gb);
        return (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The value for a card with nothing else on it: the engine keeps 3 GB of what it measures itself.</summary>
    public const string WholeCardPlan = "-3";

    /// <summary>Why a card of this size cannot make clips at this quality, in words, or null when it can.</summary>
    public static string? CannotMake(long? cardTotal, VideoQuality quality)
    {
        if (CardCanMake(cardTotal, quality)) return null;
        var need = Need(quality);
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
    public static string? Problem(byte[]? webm, int width = 0, int height = 0, int frames = 0)
    {
        if (webm is null || webm.Length == 0) return "The video is empty.";
        if (webm.Length > MaxBytes) return "The video is too large a file.";
        if (WebmInfo.Read(webm, out var why) is not { } info) return "The video is not a plain WebM file: " + why;
        if (width > 0 && height > 0 && (info.Width != width || info.Height != height))
            return $"The video is {info.Width} × {info.Height}, not the {width} × {height} asked for.";
        if (frames > 0 && info.Frames != frames) return $"The video has {info.Frames} frames, not the {frames} asked for.";
        return null;
    }

    /// <summary>The same for a request's clip.</summary>
    public static string? Problem(byte[]? webm, VideoRequest request) => Problem(webm, request.Width, request.Height, request.Frames);
}

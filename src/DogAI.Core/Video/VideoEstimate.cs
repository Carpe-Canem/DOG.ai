using DogAI.Core.Images;

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

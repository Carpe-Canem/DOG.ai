using DogAI.Core.Images;

namespace DogAI.Core.Video;

/// <summary>The two sizes a clip is made at. Quick comes first, so a value nobody set reads Quick.</summary>
public enum VideoQuality
{
    /// <summary>DOG.ai's Small picture sizes (960 × 640) with the fast decoder: about two minutes.</summary>
    Quick,

    /// <summary>DOG.ai's Normal picture sizes (1216 × 832) with the model's own decoder: about seven minutes.</summary>
    Better,
}

/// <summary>
/// What turns the finished clip into frames. The engine decides it when it starts, not per clip, so a clip with the
/// other decoder is a start of its own. Fast comes first, so a value nobody set reads Fast.
/// </summary>
public enum VideoDecoder
{
    /// <summary>The small decoder (taew2_2): one to two seconds, a little softer in fine detail.</summary>
    Fast,

    /// <summary>The model's own decoder: 40 seconds at Quick's size, over three minutes at Better's.</summary>
    Own,
}

/// <summary>The four files a video model is, as full paths. Only .safetensors and .gguf files are ever opened.</summary>
/// <param name="DiffusionPath">The model itself (wan2.2_ti2v_5B_fp16.safetensors).</param>
/// <param name="VaePath">Its own decoder.</param>
/// <param name="TextEncoderPath">The text encoder that reads the sentence (a .gguf for the recommended set).</param>
/// <param name="FastDecoderPath">The fast decoder, which also makes the full-size previews.</param>
public sealed record VideoModel(string DiffusionPath, string VaePath, string TextEncoderPath, string FastDecoderPath)
{
    /// <summary>The four paths with what each is called in a sentence.</summary>
    public IReadOnlyList<(string Path, string Name)> Files =>
    [
        (DiffusionPath, "video model"),
        (VaePath, "video model’s decoder"),
        (TextEncoderPath, "text encoder"),
        (FastDecoderPath, "fast decoder"),
    ];

    /// <summary>
    /// What stops the model from being used, in plain words, or null: a file of another format (never opened) or one
    /// that is not there. Asked before the card is cleared, so a missing file does not cost the chat model its place.
    /// </summary>
    public string? Problem()
    {
        foreach (var (path, name) in Files)
        {
            if (!IsOpenable(path))
                return $"The {name} must be a .safetensors or .gguf file. Other formats can carry program code, so DOG.ai does not open them.";
            if (!File.Exists(path)) return $"The {name} file was not found: {path}";
        }
        return null;
    }

    /// <summary>True for a file the engine may be given: the two formats that cannot carry program code.</summary>
    public static bool IsOpenable(string path) =>
        path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Everything one clip request says, beside <see cref="ImageRequest"/> and sharing nothing with it: a video model reads
/// a sentence, not tags, and none of a picture's settings mean anything to it. The numbers are the ones the M20 trial
/// chose by looking at clips (docs/PLAN.md): 121 frames at 24 a second (five seconds, what the model was made for),
/// 20 steps, Euler, guidance 5, flow shift 5, file-size setting 70.
/// </summary>
/// <param name="Prompt">The sentence: what the clip shows and what moves.</param>
/// <param name="Seed">Same seed and settings give the same clip. Below zero: DOG.ai picks one and reports it in <see cref="VideoResult.Seed"/>.</param>
/// <param name="Decoder">Carried apart from the size, so Better's size can be made with the fast decoder.</param>
/// <param name="StartFrame">
/// The picture that becomes the first frame, as checked bytes (<see cref="VideoStartFrame.Problem"/>): a WebP DOG.ai
/// drew or the JPEG the page made. Never a path or an address. Null for a clip from the sentence alone.
/// </param>
/// <param name="Frames">Always one more than a multiple of four (the engine makes 117 of 120).</param>
/// <param name="Compression">The WebM's file-size setting, 0 to 100. At 70 a clip is half the size of 90 and looks the same.</param>
/// <param name="Preview">Send the newest frame through <see cref="VideoProgress.Preview"/> while the clip is drawn.</param>
public sealed record VideoRequest(
    string Prompt,
    int Width = 960,
    int Height = 640,
    long Seed = -1,
    VideoDecoder Decoder = VideoDecoder.Fast,
    byte[]? StartFrame = null,
    string NegativePrompt = VideoRequest.FixedNegative,
    int Frames = VideoRequest.FiveSeconds,
    int Fps = 24,
    int Steps = 20,
    string Sampler = "euler",
    double Guidance = 5,
    double FlowShift = 5,
    int Compression = 70,
    bool Preview = false,
    int PreviewEvery = 4)
{
    /// <summary>121 frames at 24 a second.</summary>
    public const int FiveSeconds = 121;

    /// <summary>The longest clip made. Eight seconds worked in the trial and looked worse.</summary>
    public const int MaxFrames = FiveSeconds;

    public const int MinSide = 256;

    /// <summary>The model's own size is 1280 × 704; nothing larger was tried.</summary>
    public const int MaxSide = 1280;

    /// <summary>50 steps looked no better than 30 in the trial.</summary>
    public const int MaxSteps = 50;

    /// <summary>
    /// Sent with every clip; the owner does not type one (the trial: none, an English one and the model's own Chinese
    /// one gave near enough the same clip). It names what a clip should not be, and nothing about style: a drawn first
    /// frame must stay drawn.
    /// </summary>
    public const string FixedNegative =
        "overexposed, static, motionless, blurred details, subtitles, text, watermark, worst quality, low quality, " +
        "jpeg artifacts, ugly, deformed, disfigured, extra fingers, fused fingers, poorly drawn hands, poorly drawn face, " +
        "malformed limbs, extra limbs, cluttered background, walking backwards";

    /// <summary>
    /// The clip the owner asks for: the size is DOG.ai's own picture size for the shape (Quick = Small, Better =
    /// Normal), so a picture drawn in DOG.ai becomes a first frame without being cut; Quick takes the fast decoder,
    /// Better the model's own.
    /// </summary>
    public static VideoRequest For(string sentence, VideoQuality quality, PictureShape shape, long seed = -1, byte[]? startFrame = null)
    {
        var (width, height) = SizeOf(quality, shape);
        return new VideoRequest(sentence.Trim(), width, height, seed, DecoderOf(quality), startFrame);
    }

    public static (int Width, int Height) SizeOf(VideoQuality quality, PictureShape shape) =>
        PictureLevels.Size(shape, quality == VideoQuality.Quick ? PictureSize.Small : PictureSize.Normal);

    public static VideoDecoder DecoderOf(VideoQuality quality) => quality == VideoQuality.Quick ? VideoDecoder.Fast : VideoDecoder.Own;

    /// <summary>The quality and shape whose size this clip has, or null for a size neither has.</summary>
    public (VideoQuality Quality, PictureShape Shape)? Level =>
        PictureLevels.Of(Width, Height) is { } found
            ? (found.Size == PictureSize.Small ? VideoQuality.Quick : VideoQuality.Better, found.Shape)
            : null;

    /// <summary>How long the clip plays, in seconds.</summary>
    public double Seconds => Fps > 0 ? Frames / (double)Fps : 0;

    /// <summary>What is wrong with the request in plain words, or nothing. Checked before anything reaches the engine.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Prompt)) problems.Add("Say in a sentence what the video should show.");
        var sized = true;
        foreach (var (side, name) in new[] { (Width, "width"), (Height, "height") })
        {
            if (side is < MinSide or > MaxSide)
            {
                problems.Add($"The {name} must be between {MinSide} and {MaxSide} pixels.");
                sized = false;
            }
            // The engine rounds any other size up, and the clip would not be the size asked for.
            else if (side % 32 != 0)
            {
                problems.Add($"The {name} must be a multiple of 32 pixels.");
                sized = false;
            }
        }
        if (Frames is < 5 or > MaxFrames) problems.Add($"A video has between 5 and {MaxFrames} frames.");
        else if (Frames % 4 != 1) problems.Add("The number of frames must be one more than a multiple of four.");
        if (Fps is < 1 or > 60) problems.Add("Frames a second must be between 1 and 60.");
        if (Steps is < 1 or > MaxSteps) problems.Add($"Steps must be between 1 and {MaxSteps}.");
        if (Guidance is < 0 or > 30 || double.IsNaN(Guidance)) problems.Add("Guidance must be between 0 and 30.");
        if (FlowShift is <= 0 or > 30 || double.IsNaN(FlowShift)) problems.Add("Flow shift must be above 0 and at most 30.");
        if (Compression is < 0 or > 100) problems.Add("The file-size setting must be between 0 and 100.");
        if (PreviewEvery < 1) problems.Add("Previews must come every step or less often.");
        if (string.IsNullOrWhiteSpace(Sampler)) problems.Add("No sampler is chosen.");
        if (StartFrame is not null && sized && VideoStartFrame.Problem(StartFrame, Width, Height) is { } frame) problems.Add(frame);
        return problems;
    }
}

/// <summary>
/// The picture a clip starts from. It travels inside the request as bytes, so it is checked here first: only a WebP
/// (what DOG.ai draws) or a JPEG (what the page makes from a file, M17's path) with nothing in it but the picture, and
/// only in the clip's own shape. The engine cuts a picture of another shape from the middle (a tall portrait put into
/// a wide clip lost its head), so that is never left to it: the page cuts first.
/// </summary>
public static class VideoStartFrame
{
    public const int MinSide = 64;
    public const int MaxSide = 4096;
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// How far the picture's shape may be from the clip's. DOG.ai's own sizes of one shape differ by up to 4.7%
    /// (960 × 1472 against 832 × 1216), which costs a sliver at two edges; a tall picture for a wide clip is far outside.
    /// </summary>
    public const double ShapeTolerance = 0.05;

    /// <summary>The picture's size by its own structure, or null when it is neither a WebP nor a JPEG.</summary>
    public static (int Width, int Height)? SizeOf(byte[] frame) =>
        WebpInfo.Read(frame) is { } webp ? (webp.Width, webp.Height)
        : JpegInfo.Read(frame) is { } jpeg ? (jpeg.Width, jpeg.Height)
        : null;

    /// <summary>True when a picture of this size has the clip's shape, near enough that nothing that matters is cut.</summary>
    public static bool SameShape(int frameWidth, int frameHeight, int width, int height)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || width <= 0 || height <= 0) return false;
        double a = (double)frameWidth * height, b = (double)frameHeight * width;
        return Math.Abs(a - b) <= ShapeTolerance * Math.Max(a, b);
    }

    /// <summary>What stops these bytes from being the first frame of a clip of this size, in plain words, or null.</summary>
    public static string? Problem(byte[]? frame, int width, int height)
    {
        if (frame is null || frame.Length == 0) return "The first frame is empty.";
        if (frame.Length > MaxBytes) return "The first frame is too large a file.";
        int w, h;
        if (WebpInfo.Read(frame) is { } webp)
        {
            // Where a program could have written a prompt; DOG.ai's own pictures never have one.
            if (webp.HasMetadata) return "The first frame carries extra information besides the picture.";
            (w, h) = (webp.Width, webp.Height);
        }
        else if (JpegInfo.Read(frame) is { } jpeg)
        {
            if (jpeg.HasExtras || jpeg.HasProfile) return "The first frame carries extra information besides the picture.";
            (w, h) = (jpeg.Width, jpeg.Height);
        }
        else
        {
            return "The first frame must be a picture DOG.ai drew or one the page made from a file.";
        }
        if (Math.Min(w, h) < MinSide || Math.Max(w, h) > MaxSide) return $"The first frame must be between {MinSide} and {MaxSide} pixels a side.";
        if (!SameShape(w, h, width, height)) return "The first frame is not the shape of the video. Cut it to the video’s shape first.";
        return null;
    }
}

/// <summary>Where a clip is: the stage, and the step within it when the engine counts steps.</summary>
/// <param name="Stage">
/// <see cref="VideoStages"/>: the engine's own names while it works, DOG.ai's while the clip waits. Only "sampling"
/// counts steps; decoding and encoding report none.
/// </param>
/// <param name="Preview">The newest frame as a PNG, when previews were asked for and a new one arrived.</param>
public sealed record VideoProgress(string Stage, int? Step = null, int? TotalSteps = null, byte[]? Preview = null);

/// <summary>The stage names a clip reports.</summary>
public static class VideoStages
{
    /// <summary>Waiting for a picture, a loading chat model, or a reply being written.</summary>
    public const string Waiting = CardCoordinator.Waiting;

    /// <summary>The chat model, the picture model and the other things on the card are being taken off it.</summary>
    public const string MakingRoom = CardCoordinator.MakingRoom;

    /// <summary>The video program is starting (about a second).</summary>
    public const string Starting = "starting";

    // The engine's own, in the order they come.
    public const string Preparing = "preparing";

    /// <summary>Reading the first frame (only with one).</summary>
    public const string ReadingFrame = "vae_encode";

    /// <summary>Reading the sentence. The model's files are read here too: seconds, or half a minute after something else large was read.</summary>
    public const string ReadingSentence = "conditioning";
    public const string Drawing = "sampling";
    public const string Decoding = "vae_decode";
    public const string Encoding = "encoding";
}

/// <summary>A finished clip: a WebM that passed <see cref="VideoClips.Problem"/>, with what its own structure says.</summary>
/// <param name="Seed">The seed it was made from (the one asked for, or the one DOG.ai picked).</param>
public sealed record VideoResult(byte[] Webm, int Width, int Height, int Frames, int Fps, long Seed);

/// <summary>A video engine problem, said in plain words.</summary>
public sealed class VideoEngineException(string message) : Exception(message);

/// <summary>
/// The video engine as the rest of DOG.ai sees it. The real one is <c>VideoServer</c> (DogAI.Engine), the picture
/// engine's program started with a video model; tests use a fake. The app hands out <see cref="CardCoordinator"/> under
/// this interface: a clip takes the whole graphics card, so nothing may make one without it.
/// </summary>
public interface IVideoEngine
{
    /// <summary>True from the moment a clip is asked for until it is finished, cancelled or has failed.</summary>
    bool IsBusy { get; }

    /// <summary>True while the video program is running. It runs only while a clip is made.</summary>
    bool IsRunning { get; }

    event Action? Changed;

    /// <summary>
    /// Makes the clip <paramref name="request"/> describes with <paramref name="model"/>. The program is started for
    /// the clip and ended when the clip has been collected, so nothing is left on the card. Cancelling
    /// <paramref name="ct"/> ends the program within a few seconds and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="VideoEngineException">A file is missing, the engine is missing or failed, the card has too little room, or the clip was not a clean WebM.</exception>
    Task<VideoResult> MakeAsync(VideoModel model, VideoRequest request, IProgress<VideoProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Ends the video program if it is running (a clip being made fails).</summary>
    Task StopAsync();
}

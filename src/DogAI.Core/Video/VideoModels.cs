using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Models;

namespace DogAI.Core.Video;

/// <summary>Which of a video model's four files a file is meant to be.</summary>
public enum VideoFileRole
{
    /// <summary>The model itself, in <see cref="VideoFolders.DiffusionModels"/>.</summary>
    Model,

    /// <summary>The model's own decoder, in <see cref="VideoFolders.Vae"/>.</summary>
    Vae,

    /// <summary>The text encoder that reads the sentence, in <see cref="VideoFolders.TextEncoders"/>.</summary>
    TextEncoder,

    /// <summary>The fast decoder, in <see cref="VideoFolders.Vae"/>.</summary>
    FastDecoder,
}

/// <summary>
/// One file of the recommended video model: where it goes, where it comes from, and the bytes it must have. A part of a
/// download is checked against <see cref="Sha256"/> before it is moved into place; a part without one would not be
/// checked at all, so every file here carries one.
/// </summary>
/// <param name="Role">Which file of the four it is.</param>
/// <param name="Folder">Its folder under <c>&lt;models&gt;\video</c>.</param>
/// <param name="FileName">Its name there (the name <see cref="VideoFiles.Wan22"/> gives).</param>
/// <param name="Repo">The Hugging Face repository, or the GitHub repository for the fast decoder.</param>
/// <param name="SourcePath">The file's path inside that repository.</param>
/// <param name="Url">For a file from GitHub, the address pinned to one commit; null for Hugging Face (its address is made from the repository and path).</param>
public sealed record VideoDownload(VideoFileRole Role, string Folder, string FileName, string Repo, string SourcePath, string? Url,
    long Bytes, string Sha256, string Licence)
{
    /// <summary>The part's name inside the video models folder, with its subfolder ("vae\taew2_2.safetensors").</summary>
    public string PartName => Path.Combine(Folder, FileName);

    public bool FromGitHub => Url is not null;

    /// <summary>What the file is, in a few words.</summary>
    public string Title => VideoDownloads.TitleOf(Role);
}

/// <summary>
/// The video model DOG.ai recommends (M20; the owner's decision, 2026-11-16: one family, Wan 2.2 TI2V-5B): four files
/// downloaded together into <c>&lt;models&gt;\video</c>. Checked on 2026-11-17: the three Hugging Face files' sizes and
/// SHA-256 are the ones their repositories publish, and match the files the M20 trial downloaded. The fast decoder comes
/// from GitHub at commit <see cref="TaehvCommit"/>, where its git object id (<c>9d0ef195…</c>) is that of the trial's
/// file; GitHub publishes no SHA-256, so the trial's is pinned.
/// </summary>
public static class VideoDownloads
{
    /// <summary>The use case video downloads carry, so they never become chat-model records.</summary>
    public const string UseCase = "video";

    public const string Title = "Wan 2.2 TI2V-5B";
    public const string Blurb = "Makes five-second clips from a sentence, or from a sentence and a picture as the first frame. Reads sentences, not tags.";

    public const string TaehvRepo = "madebyollin/taehv";
    public const string TaehvCommit = "fa579a9a726b0a55951998d73e309bfdf0abd342";

    /// <summary>The model first: the download's main file, by which the downloads list and the button find the job.</summary>
    public static IReadOnlyList<VideoDownload> Wan22 { get; } =
    [
        new(VideoFileRole.Model, VideoFolders.DiffusionModels, VideoFiles.Wan22.Model, "Comfy-Org/Wan_2.2_ComfyUI_Repackaged",
            "split_files/diffusion_models/wan2.2_ti2v_5B_fp16.safetensors", null, 9_999_658_848,
            "456f901338bd9eadbded3828b819109a9b68e8a525ca5cf8d0049a69fcfeca1e", "Apache 2.0"),
        new(VideoFileRole.TextEncoder, VideoFolders.TextEncoders, VideoFiles.Wan22.TextEncoder, "city96/umt5-xxl-encoder-gguf",
            "umt5-xxl-encoder-Q8_0.gguf", null, 6_043_068_256,
            "2521d4de0bf9e1cc6549866463ceae85e4ec3239bc6063f7488810be39033bbc", "Apache 2.0"),
        new(VideoFileRole.Vae, VideoFolders.Vae, VideoFiles.Wan22.Vae, "Comfy-Org/Wan_2.2_ComfyUI_Repackaged",
            "split_files/vae/wan2.2_vae.safetensors", null, 1_409_400_960,
            "e40321bd36b9709991dae2530eb4ac303dd168276980d3e9bc4b6e2b75fed156", "Apache 2.0"),
        new(VideoFileRole.FastDecoder, VideoFolders.Vae, VideoFiles.Wan22.FastDecoder, TaehvRepo,
            "safetensors/taew2_2.safetensors", $"https://raw.githubusercontent.com/{TaehvRepo}/{TaehvCommit}/safetensors/taew2_2.safetensors", 22_848_048,
            "b84609b2a133d48434bd9636bfcb44bf05168dc436e2d3cecf26256faa1f5325", "MIT"),
    ];

    public static long TotalBytes => Wan22.Sum(d => d.Bytes);

    public static VideoDownload For(VideoFileRole role) => Wan22.First(d => d.Role == role);

    public static string TitleOf(VideoFileRole role) => role switch
    {
        VideoFileRole.Model => "The model",
        VideoFileRole.Vae => "Its decoder",
        VideoFileRole.TextEncoder => "Its text encoder",
        _ => "The fast decoder",
    };

    /// <summary>What each file does, in one line.</summary>
    public static string AboutOf(VideoFileRole role) => role switch
    {
        VideoFileRole.Model => "Draws the clip.",
        VideoFileRole.Vae => "Turns a Better clip into frames, and reads a first frame.",
        VideoFileRole.TextEncoder => "Reads the sentence.",
        _ => "Turns a Quick clip into frames in a second or two, and shows the clip while it is made.",
    };

    /// <summary>A card is taken as 16 GB when it is within this of it (<see cref="VideoMemory.CardSizeSlack"/>).</summary>
    public const long CardSizeSlack = VideoMemory.CardSizeSlack;

    /// <summary>
    /// Whether this PC is offered the video model: a graphics card of <see cref="VideoMemory.SmallestCard"/> (16 GB) or
    /// more. Smaller cards are not offered in the first version: the engine could keep the model in RAM, but that was
    /// only asked of it, never tried on such a card (M20 trial).
    /// </summary>
    public static bool Offered(long? cardBytes) => cardBytes is { } total && total >= VideoMemory.SmallestCard - CardSizeSlack;

    /// <summary>The line beside the download that says whether it fits, in words.</summary>
    public static string FitText(long? cardBytes) => cardBytes is not { } total
        ? "Needs an NVIDIA graphics card with 16 GB or more. No graphics card was found on this PC, so it is not offered."
        : Offered(total)
            ? $"Fits this PC: a clip needs about {Sizes.Gb(NeedAt(VideoQuality.Quick))} of graphics memory ({Sizes.Gb(NeedAt(VideoQuality.Better))} for Better), and this card has {Sizes.Gb(total)}. The chat model is set aside while a clip is made."
            : $"Needs a graphics card with 16 GB or more; this one has {Sizes.Gb(total)}. DOG.ai does not offer video on smaller cards yet: it has not been tried on one.";

    private static long NeedAt(VideoQuality quality) => VideoMemory.Need(VideoRequest.For("-", quality, PictureShape.Wide));

    /// <summary>The line said where a clip would need the model and it is not all there.</summary>
    public static PlaceLine Missing(IReadOnlyCollection<VideoFileRole> missing) => new(
        missing.Count == Wan22.Count
            ? "Making clips needs the video model (four files, " + Sizes.Gb(TotalBytes) + "). Get it in"
            : $"The video model is missing {Join(missing.Select(r => TitleOf(r).ToLowerInvariant()))}. Get it in",
        ModelsPlaces.GetVideoName, ModelsPlaces.Address(ModelsPlaces.GetVideo));

    /// <summary>"a, b and c".</summary>
    public static string Join(IEnumerable<string> words)
    {
        var list = words.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }
}

/// <summary>What a file in the video models folder is, by its header.</summary>
public enum VideoFileKind
{
    /// <summary>Not a file DOG.ai knows for video.</summary>
    Unknown,

    /// <summary>The Wan 2.2 TI2V-5B model (48 latent channels, 3,072 wide).</summary>
    Wan22Model,

    /// <summary>The Wan 2.2 decoder (48 latent channels).</summary>
    Wan22Vae,

    /// <summary>The UMT5-XXL text encoder Wan reads sentences with.</summary>
    Umt5Encoder,

    /// <summary>The fast decoder for Wan 2.2 (TAEHV, 48 latent channels).</summary>
    Wan22FastDecoder,

    /// <summary>A Wan file of another version or size (Wan 2.1, the 14B models, their decoders): DOG.ai does not use it.</summary>
    OtherWan,

    /// <summary>A format that can carry program code (.ckpt, .pt, .pth, .bin): listed, never opened.</summary>
    Refused,
}

/// <summary>What a video file is and a line about it for the owner.</summary>
public sealed record VideoFileVerdict(VideoFileKind Kind, string Note)
{
    /// <summary>The kind of file each role must be.</summary>
    public static VideoFileKind KindFor(VideoFileRole role) => role switch
    {
        VideoFileRole.Model => VideoFileKind.Wan22Model,
        VideoFileRole.Vae => VideoFileKind.Wan22Vae,
        VideoFileRole.TextEncoder => VideoFileKind.Umt5Encoder,
        _ => VideoFileKind.Wan22FastDecoder,
    };
}

/// <summary>
/// Tells the four video files from anything else by their headers alone, in the way <see cref="ImageFileRecognizer"/>
/// tells checkpoints from LoRAs: tensor names and shapes for the three .safetensors files, the GGUF header's keys for the
/// text encoder. Pure: the Engine opens the files (read only) and hands over what it read.
/// </summary>
public static class VideoFileRecognizer
{
    /// <summary>Wan 2.2's latent channels (Wan 2.1 has 16).</summary>
    public const int Wan22Channels = 48;

    /// <summary>The 5B model's width (the 14B models are 5,120 wide).</summary>
    public const int Wan5BWidth = 3072;

    /// <summary>What a .safetensors header is. A diffusion model may carry ComfyUI's "model.diffusion_model." prefix.</summary>
    public static VideoFileVerdict Classify(SafetensorsHeader header)
    {
        var t = header.Tensors;
        long[]? Shape(string name) => t.TryGetValue(name, out var s) ? s : t.TryGetValue("model.diffusion_model." + name, out s) ? s : null;

        if (Shape("patch_embedding.weight") is { } patch && (Shape("head.head.weight") is not null || t.Keys.Any(k => k.Contains("blocks.0.cross_attn.", StringComparison.Ordinal))))
        {
            return patch is [Wan5BWidth, Wan22Channels, 1, 2, 2]
                ? new(VideoFileKind.Wan22Model, "Wan 2.2 TI2V-5B, the model.")
                : new(VideoFileKind.OtherWan, $"A Wan model of another version or size ({Describe(patch)}). DOG.ai uses only Wan 2.2 TI2V-5B.");
        }

        var encoderDecoderOnly = t.Count > 0 && t.Keys.All(k => k.StartsWith("encoder.", StringComparison.Ordinal) || k.StartsWith("decoder.", StringComparison.Ordinal) ||
                                                                k.StartsWith("conv1.", StringComparison.Ordinal) || k.StartsWith("conv2.", StringComparison.Ordinal));
        if (encoderDecoderOnly && t.TryGetValue("decoder.conv1.weight", out var conv) && conv.Length == 5)
        {
            return conv[1] == Wan22Channels
                ? new(VideoFileKind.Wan22Vae, "Wan 2.2's own decoder.")
                : new(VideoFileKind.OtherWan, $"A video decoder for {conv[1]} channels (Wan 2.1's, by the look of it). Wan 2.2 needs its own.");
        }

        // TAEHV: numbered layers only, 2-D convolutions; the decoder's first takes the latent channels.
        if (encoderDecoderOnly && t.TryGetValue("decoder.1.weight", out var first) && first.Length == 4 && t.Keys.Any(k => k.StartsWith("encoder.", StringComparison.Ordinal)))
        {
            return first[1] == Wan22Channels
                ? new(VideoFileKind.Wan22FastDecoder, "The fast decoder for Wan 2.2.")
                : new(VideoFileKind.OtherWan, $"A fast decoder for {first[1]} channels (another model's). Wan 2.2 needs taew2_2.");
        }

        return new(VideoFileKind.Unknown, "Not a video model file DOG.ai knows.");
    }

    /// <summary>
    /// What a GGUF file is, by its header: the UMT5-XXL encoder says architecture "t5encoder", width 4,096 and "umt5" in
    /// its name (the T5-XXL encoder of other models has the same width and another vocabulary, so the name decides).
    /// </summary>
    public static VideoFileVerdict ClassifyGguf(string? architecture, int? embeddingLength, string? name, string? baseName, string fileName)
    {
        if (!string.Equals(architecture, "t5encoder", StringComparison.OrdinalIgnoreCase))
            return new(VideoFileKind.Unknown, architecture is null ? "Not a model file DOG.ai can read." : $"A {architecture} model, not a text encoder for video.");
        var umt5 = new[] { name, baseName, fileName }.Any(s => s?.Contains("umt5", StringComparison.OrdinalIgnoreCase) == true);
        if (!umt5) return new(VideoFileKind.OtherWan, "A T5 text encoder of another model. Wan reads sentences with UMT5-XXL.");
        return embeddingLength == 4096
            ? new(VideoFileKind.Umt5Encoder, "UMT5-XXL, the text encoder.")
            : new(VideoFileKind.OtherWan, $"A UMT5 encoder of another size ({embeddingLength?.ToString() ?? "unknown"} wide). Wan needs UMT5-XXL.");
    }

    private static string Describe(long[] shape) =>
        shape.Length >= 2 ? $"{shape[0]:N0} wide, {shape[1]} channels" : "an unusual shape";
}

/// <summary>Where one of the four files stands.</summary>
public enum VideoFileStatus
{
    /// <summary>Not there.</summary>
    Missing,

    /// <summary>Being downloaded (in the download list and not finished).</summary>
    Arriving,

    /// <summary>There, and its header says it is the right file.</summary>
    Ready,

    /// <summary>A file of that name is there but is not the right one (the note says what it is).</summary>
    Wrong,
}

/// <summary>One of the four files as found on this PC.</summary>
/// <param name="Size">Its size, or 0 when it is missing.</param>
/// <param name="Note">What it is, or why it cannot be used.</param>
/// <param name="Unusual">A right file whose size is not the recommended one's: it may work; DOG.ai has not tried it.</param>
public sealed record VideoFileFound(VideoFileRole Role, string FullPath, VideoFileStatus Status, long Size, string Note, bool Unusual = false)
{
    public VideoDownload Download => VideoDownloads.For(Role);
}

/// <summary>A file in the video folders that is not one of the four (another model's, a refused format, anything else).</summary>
public sealed record VideoOtherFile(string Folder, string RelativePath, string FullPath, long Size, VideoFileVerdict Verdict);

/// <summary>What is in the video models folder, as the Models section shows it.</summary>
public sealed record VideoLook(string? Root, IReadOnlyList<VideoFileFound> Files, IReadOnlyList<VideoOtherFile> Others)
{
    public static VideoLook Nothing(string? root) => new(root, [], []);

    /// <summary>All four are there and right: clips can be made (given the picture engine).</summary>
    public bool Ready => Files.Count == VideoDownloads.Wan22.Count && Files.All(f => f.Status == VideoFileStatus.Ready);

    /// <summary>The four files' roles that are not ready (missing, still arriving, or wrong).</summary>
    public IReadOnlyList<VideoFileRole> NotReady =>
        Files.Count == 0 ? [.. VideoDownloads.Wan22.Select(d => d.Role)] : [.. Files.Where(f => f.Status != VideoFileStatus.Ready).Select(f => f.Role)];

    /// <summary>Files to download: the ones not there at all. A wrong one is deleted first (On this PC › Video).</summary>
    public IReadOnlyList<VideoFileRole> ToDownload =>
        Files.Count == 0 ? [.. VideoDownloads.Wan22.Select(d => d.Role)] : [.. Files.Where(f => f.Status == VideoFileStatus.Missing).Select(f => f.Role)];

    /// <summary>Files there and wrong: a download cannot put the right one in their place.</summary>
    public IReadOnlyList<VideoFileFound> Wrong => [.. Files.Where(f => f.Status == VideoFileStatus.Wrong)];

    /// <summary>Any of the four is on disk (ready, wrong or not).</summary>
    public bool AnyThere => Files.Any(f => f.Status is VideoFileStatus.Ready or VideoFileStatus.Wrong);

    public long BytesThere => Files.Where(f => f.Status is VideoFileStatus.Ready or VideoFileStatus.Wrong).Sum(f => f.Size);

    /// <summary>The state in one word, for the page and for checks: ready, incomplete, arriving or none.</summary>
    public string State =>
        Ready ? "ready"
        : Files.Any(f => f.Status == VideoFileStatus.Arriving) ? "arriving"
        : AnyThere ? "incomplete"
        : "none";
}

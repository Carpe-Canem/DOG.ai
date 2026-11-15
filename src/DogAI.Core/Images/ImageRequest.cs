namespace DogAI.Core.Images;

/// <summary>How a checkpoint was trained to predict noise. Almost all SDXL checkpoints are <see cref="Auto"/>.</summary>
public enum ImagePrediction
{
    /// <summary>Let the engine read it from the file (the normal "eps" kind for SDXL).</summary>
    Auto,

    /// <summary>V-prediction checkpoints (some NoobAI versions). Not tried on this PC yet.</summary>
    V,
}

/// <summary>The checkpoint the engine draws with, and what it is loaded with.</summary>
/// <param name="CheckpointPath">Full path of a .safetensors checkpoint.</param>
/// <param name="LoraFolder">The folder LoRAs are read from; <see cref="LoraUse.Path"/> is relative to it.</param>
/// <param name="VaePath">A separate VAE file, or null for the one inside the checkpoint.</param>
/// <param name="UpscalerFolder">
/// The folder upscaler files are read from (<see cref="ImageEnlarge.Upscaler"/> names one at its top level), or null.
/// The engine is pointed at it only while every file in it is one DOG.ai lets it open.
/// </param>
public sealed record ImageModel(
    string CheckpointPath,
    string? LoraFolder = null,
    string? VaePath = null,
    ImagePrediction Prediction = ImagePrediction.Auto,
    string? UpscalerFolder = null);

/// <summary>A LoRA used for one picture.</summary>
/// <param name="Path">The file, relative to <see cref="ImageModel.LoraFolder"/> (for example "style/ink.safetensors").</param>
/// <param name="Strength">How strongly it applies; 1 is the LoRA as trained.</param>
public sealed record LoraUse(string Path, double Strength = 1.0);

/// <summary>"Hires fix": draw, enlarge by <paramref name="Scale"/>, then refine with <paramref name="Steps"/> more steps.</summary>
/// <param name="Denoise">How much the second pass may change (0 to 1).</param>
/// <param name="Upscaler">How the picture is enlarged before the second pass; "Latent" needs no extra file.</param>
public sealed record HiresFix(double Scale, int Steps, double Denoise, string Upscaler = "Latent");

public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
}

/// <summary>Everything one picture request says. Defaults are the engine's plain ones; step 6 of M9 picks real numbers per checkpoint.</summary>
/// <param name="Seed">Same seed and settings give the same picture. Below zero: DOG.ai picks one and reports it in <see cref="ImageResult.Seed"/>.</param>
/// <param name="Sampler">The engine's name for it, for example "euler_a" or "dpm++2m".</param>
/// <param name="Scheduler">The engine's name for it, or null for the engine's default.</param>
/// <param name="Guidance">How closely the picture follows the prompt (CFG scale).</param>
/// <param name="ClipSkip">2 for most anime-style SDXL checkpoints; null for the engine's default.</param>
/// <param name="Quality">For JPEG and WebP, 0 to 100; null for the engine's default.</param>
/// <param name="Count">Pictures drawn one after another from <see cref="Seed"/> (the engine adds one per picture).</param>
/// <param name="Preview">Send small in-progress pictures through <see cref="ImageProgress.Preview"/>.</param>
/// <param name="PreviewEvery">A preview every this many steps.</param>
public sealed record ImageRequest(
    string Prompt,
    string NegativePrompt = "",
    int Width = 1024,
    int Height = 1024,
    long Seed = -1,
    int Steps = 20,
    string Sampler = "euler_a",
    string? Scheduler = null,
    double Guidance = 5.5,
    int? ClipSkip = null,
    IReadOnlyList<LoraUse>? Loras = null,
    HiresFix? Hires = null,
    ImageFormat Format = ImageFormat.Png,
    int? Quality = null,
    int Count = 1,
    bool Preview = false,
    int PreviewEvery = 3)
{
    public const int MinSide = 256;
    public const int MaxSide = 4096;
    public const int MaxSteps = 150;
    public const int MaxCount = 8;

    /// <summary>
    /// Above this many pixels in its largest pass a picture is decoded in tiles (see <see cref="DecodesInTiles"/>).
    /// It sits just above the largest picture drawn before M16 step 5 (Fine: 1280 × 1856), so those are drawn as they
    /// always were and a kept one still redraws byte for byte.
    /// </summary>
    public const int TiledAbovePixels = 2_400_000;

    /// <summary>
    /// The size of the largest pass: the size asked for, or with a hires fix the enlarged size, which the engine rounds
    /// up to a multiple of 64 (832 × 1216 at ×1.5 comes out 1280 × 1856).
    /// </summary>
    public (int Width, int Height) LargestPass => Hires is null ? (Width, Height) : (Enlarged(Width, Hires.Scale), Enlarged(Height, Hires.Scale));

    /// <summary>A side after a hires fix at <paramref name="scale"/>, as the engine rounds it.</summary>
    public static int Enlarged(int side, double scale) => (int)Math.Ceiling(side * scale / 64) * 64;

    /// <summary>
    /// True when the engine is told to turn the drawing into the picture (and back, for the hires fix) piece by piece.
    /// Whole, that step's memory grows with the picture: 1280 × 1856 peaked at 16.1 GB, and 1664 × 2432 filled a 24 GB
    /// card and did not finish in ten minutes. In tiles the same picture peaked at 8.7 GB (M16 step 5 trial).
    /// </summary>
    public bool DecodesInTiles => (long)LargestPass.Width * LargestPass.Height > TiledAbovePixels;

    /// <summary>What is wrong with the request in plain words, or nothing. Checked before anything reaches the engine.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Prompt)) problems.Add("The prompt is empty.");
        foreach (var (side, name) in new[] { (Width, "width"), (Height, "height") })
        {
            if (side is < MinSide or > MaxSide) problems.Add($"The {name} must be between {MinSide} and {MaxSide} pixels.");
            else if (side % 8 != 0) problems.Add($"The {name} must be a multiple of 8 pixels.");
        }
        if (Steps is < 1 or > MaxSteps) problems.Add($"Steps must be between 1 and {MaxSteps}.");
        if (Guidance is < 0 or > 30 || double.IsNaN(Guidance)) problems.Add("Guidance must be between 0 and 30.");
        if (ClipSkip is < 1 or > 12) problems.Add("Clip skip must be between 1 and 12.");
        if (Quality is < 0 or > 100) problems.Add("Quality must be between 0 and 100.");
        if (Count is < 1 or > MaxCount) problems.Add($"Between 1 and {MaxCount} pictures can be drawn at once.");
        if (PreviewEvery < 1) problems.Add("Previews must come every step or less often.");
        if (string.IsNullOrWhiteSpace(Sampler)) problems.Add("No sampler is chosen.");
        foreach (var lora in Loras ?? [])
        {
            if (!IsSafeRelativePath(lora.Path)) problems.Add($"\"{lora.Path}\" is not a file inside the LoRA folder.");
            if (lora.Strength is < -5 or > 5 || double.IsNaN(lora.Strength)) problems.Add($"The strength of \"{lora.Path}\" must be between -5 and 5.");
        }
        if (Hires is { } h)
        {
            if (h.Scale is <= 1 or > 4 || double.IsNaN(h.Scale)) problems.Add("Hires fix must enlarge by more than 1× and at most 4×.");
            if (h.Steps is < 1 or > MaxSteps) problems.Add($"Hires fix steps must be between 1 and {MaxSteps}.");
            if (h.Denoise is <= 0 or > 1 || double.IsNaN(h.Denoise)) problems.Add("Hires fix strength must be above 0 and at most 1.");
            if (Math.Max(Width, Height) * h.Scale > MaxSide) problems.Add($"Hires fix would make the picture larger than {MaxSide} pixels.");
        }
        return problems;
    }

    /// <summary>A path relative to a folder that cannot leave it: not rooted, no drive, no "..".</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\') || System.IO.Path.IsPathRooted(path)) return false;
        if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return false;
        return path.Split('/', '\\').All(part => part.Length > 0 && part != "." && part != "..");
    }
}

/// <summary>Where a picture is: the engine's stage, and the step within it when it counts steps.</summary>
/// <param name="Stage">The engine's name: "conditioning" (reading the prompt), "sampling" (drawing), "decoding" (making the picture), and so on.</param>
/// <param name="Pass">1 for the first drawing, 2 for the hires fix's second pass.</param>
/// <param name="Preview">A small PNG of the picture so far, when previews were asked for and a new one arrived.</param>
public sealed record ImageProgress(string Stage, int? Step, int? TotalSteps, int Pass = 1, byte[]? Preview = null);

/// <summary>The finished pictures, in the requested format, with the seed of the first one.</summary>
public sealed record ImageResult(IReadOnlyList<byte[]> Images, ImageFormat Format, long Seed);

/// <summary>
/// A picture to enlarge with an upscaler file (ESRGAN kind): the picture as it is, made larger. Nothing is drawn, so no
/// prompt is sent and nothing in the picture changes.
/// </summary>
/// <param name="Image">The picture, as WebP.</param>
/// <param name="Upscaler">The upscaler's file name without ".safetensors"; a file at the top of <see cref="ImageModel.UpscalerFolder"/>.</param>
/// <param name="Quality">The WebP quality of the result.</param>
public sealed record ImageEnlarge(byte[] Image, string Upscaler, int Quality = PictureFormat.Quality)
{
    /// <summary>The longest side the engine enlarges to.</summary>
    public const int MaxSide = 8192;

    /// <summary>What is wrong with the request in plain words, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (WebpInfo.Read(Image) is null) problems.Add("The picture to enlarge is not a WebP image.");
        if (!IsUpscalerName(Upscaler)) problems.Add("The upscaler must be a file at the top of the upscalers folder.");
        if (Quality is < 0 or > 100) problems.Add("Quality must be between 0 and 100.");
        return problems;
    }

    /// <summary>A plain file name without a folder: the engine looks only at the top of the upscalers folder.</summary>
    public static bool IsUpscalerName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && ImageRequest.IsSafeRelativePath(name) &&
        name.IndexOfAny(['/', '\\']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

/// <summary>An enlarged picture (WebP, nothing but the picture in it) with its size and how many times larger it is.</summary>
public sealed record ImageEnlarged(byte[] Image, int Width, int Height, int Scale);

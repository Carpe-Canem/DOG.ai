namespace DogAI.Core.Images;

/// <summary>The three named quality levels the owner chooses between, in place of raw numbers.</summary>
public enum PictureQuality
{
    /// <summary>Fewer steps: fast, for trying ideas.</summary>
    Draft,

    /// <summary>The checkpoint's own step count.</summary>
    Standard,

    /// <summary>Standard, then enlarged and refined (hires fix). For a picture already chosen: it can change small things.</summary>
    Fine,
}

/// <summary>The three picture shapes. Each comes in two sizes (<see cref="PictureSize"/>).</summary>
public enum PictureShape
{
    /// <summary>640 × 960 Small, 832 × 1216 Normal. The default for portraits.</summary>
    Tall,

    /// <summary>768 × 768 Small, 1024 × 1024 Normal.</summary>
    Square,

    /// <summary>960 × 640 Small, 1216 × 832 Normal. The default for story illustrations.</summary>
    Wide,
}

/// <summary>
/// How large a picture is first drawn (M18). Small is where every picture starts (the owner's request, 2026-11-15);
/// Make larger redraws a chosen one larger from the same seed. Small comes first so a value nobody set reads as Small.
/// </summary>
public enum PictureSize
{
    /// <summary>About 0.6 million pixels: 640 × 960. Faster and lighter on graphics memory; chosen by looking in the M18 trial.</summary>
    Small,

    /// <summary>SDXL's native sizes, about one million pixels: 832 × 1216. Every picture before M18 was this size.</summary>
    Normal,
}

/// <summary>
/// Turns a quality level and a shape into numbers. The numbers were chosen in M9 step 6 by drawing the same pictures at
/// several values with the owner's Illustrious checkpoint and looking at them; see docs/PLAN.md.
/// </summary>
public static class PictureLevels
{
    /// <summary>Draft draws this share of the checkpoint's steps: 20 of 28, which looked as good as 28 at a glance.</summary>
    public const double DraftShare = 0.7;

    /// <summary>
    /// Draft never goes below this many steps. At 12 one person's features showed up on another (a braid on the wrong
    /// head) and eyes went wrong; 16 held together.
    /// </summary>
    public const int DraftFloor = 16;

    /// <summary>Fine enlarges by this much before refining (832×1216 comes out 1280×1856).</summary>
    public const double FineScale = 1.5;

    /// <summary>Fine's second pass draws this share of the checkpoint's steps. 8, 14 and 20 looked the same; 14 costs 3 s more than 8.</summary>
    public const double FineStepShare = 0.5;

    /// <summary>
    /// How much Fine's second pass may change (0 to 1). At 0.3 faces and hands get sharper and the picture stays the
    /// one that was chosen; at 0.45 an open eye closed and a scar changed shape, at 0.6 new strands of hair crossed a face.
    /// </summary>
    public const double FineDenoise = 0.3;

    /// <summary>
    /// How Fine enlarges before the second pass. Lanczos enlarges the finished picture; the engine's default
    /// ("Latent") enlarges the unfinished one and redrew hair, scars and face shapes at every strength tried.
    /// </summary>
    public const string FineUpscaler = "Lanczos";

    /// <summary>
    /// Make larger from a Small picture: a hires fix of ×1.5 with Fine's second pass (640 × 960 comes out 960 × 1472). In
    /// the M18 trial it kept the layout and looked as good as or better than a picture drawn at Normal.
    /// </summary>
    public const double LargerScale = FineScale;

    /// <summary>
    /// Make larger from a Small picture when only this fits beside the loaded chat model: ×1.25, which is Normal's size
    /// for Tall and Wide (800 × 1200 rounds up to 832 × 1216) and 960 × 960 for Square. A little softer than ×1.5.
    /// </summary>
    public const double LargerFallbackScale = 1.25;

    /// <summary>
    /// A size's pixels. Small's sides are multiples of 64: the picture engine rounds every side up to one, so any other
    /// number would come out larger than asked.
    /// </summary>
    public static (int Width, int Height) Size(PictureShape shape, PictureSize size) => (shape, size) switch
    {
        (PictureShape.Tall, PictureSize.Small) => (640, 960),
        (PictureShape.Wide, PictureSize.Small) => (960, 640),
        (_, PictureSize.Small) => (768, 768),
        (PictureShape.Tall, _) => (832, 1216),
        (PictureShape.Wide, _) => (1216, 832),
        _ => (1024, 1024),
    };

    /// <summary>The shape and size whose pixels are <paramref name="width"/> × <paramref name="height"/>, or null for a size typed by hand.</summary>
    public static (PictureShape Shape, PictureSize Size)? Of(int width, int height)
    {
        foreach (var size in Enum.GetValues<PictureSize>())
            foreach (var shape in Enum.GetValues<PictureShape>())
                if (Size(shape, size) == (width, height)) return (shape, size);
        return null;
    }

    /// <summary>The steps Draft draws for a checkpoint whose own count is <paramref name="steps"/>.</summary>
    public static int DraftSteps(int steps) => Math.Min(steps, Math.Max(DraftFloor, (int)Math.Round(steps * DraftShare)));

    /// <summary>
    /// Fine's hires fix for a checkpoint whose own step count is <paramref name="steps"/>. Make larger is the same at
    /// <paramref name="scale"/> (<see cref="LargerScale"/> or <see cref="LargerFallbackScale"/>).
    /// </summary>
    public static HiresFix FineHires(int steps, double scale = FineScale) =>
        new(scale, Math.Max(8, (int)Math.Round(steps * FineStepShare)), FineDenoise, FineUpscaler);

    /// <summary>
    /// The request at a quality level, shape and size. <paramref name="request"/> carries the checkpoint's own step count
    /// (as <see cref="ImagePrompt.Compose"/> makes it); everything else in it is kept, so Edit and redraw can change the
    /// level alone. Fine is the hires fix at <paramref name="fineScale"/>: at Normal it is Fine as before M18 (1280 × 1856);
    /// at Small it is Make larger (<see cref="LargerScale"/>, or <see cref="LargerFallbackScale"/>).
    /// </summary>
    public static ImageRequest At(this ImageRequest request, PictureQuality quality, PictureShape shape, PictureSize size, double fineScale = FineScale)
    {
        var (width, height) = Size(shape, size);
        return quality switch
        {
            PictureQuality.Draft => request with { Width = width, Height = height, Steps = DraftSteps(request.Steps), Hires = null },
            PictureQuality.Fine => request with { Width = width, Height = height, Hires = FineHires(request.Steps, fineScale) },
            _ => request with { Width = width, Height = height, Hires = null },
        };
    }

    /// <summary>Whether a picture at this quality can be made larger (one at Fine, or made larger already, cannot).</summary>
    public static bool CanMakeLarger(PictureQuality quality) => quality != PictureQuality.Fine;

    /// <summary>
    /// The hires scale Make larger uses for a picture of <paramref name="size"/>: Normal goes to Fine (×1.5); Small goes
    /// ×1.5, or ×1.25 when only that fits beside the loaded chat model. <paramref name="fits"/> answers whether a scale
    /// fits beside it (null: no chat model is loaded, so nothing would be set aside). When neither fits, ×1.5 is drawn and
    /// the chat model is set aside, as for any picture.
    /// </summary>
    public static async Task<double> LargerScaleAsync(PictureSize size, Func<double, Task<bool?>> fits)
    {
        if (size == PictureSize.Normal) return FineScale;
        if (await fits(LargerScale).ConfigureAwait(false) is null or true) return LargerScale;
        return await fits(LargerFallbackScale).ConfigureAwait(false) == true ? LargerFallbackScale : LargerScale;
    }

    /// <summary>
    /// The size a new picture starts at. <paramref name="wanted"/> is the setting; when it is Normal and a Normal picture
    /// would set the loaded chat model aside where a Small one would not, it starts Small (<see cref="StartedSmall"/> says
    /// so). <paramref name="fits"/> answers whether a size fits beside the chat model (null: none is loaded).
    /// </summary>
    public static async Task<PictureSize> StartSizeAsync(PictureSize wanted, Func<PictureSize, Task<bool?>> fits)
    {
        if (wanted == PictureSize.Small || await fits(PictureSize.Normal).ConfigureAwait(false) is null or true) return wanted;
        return await fits(PictureSize.Small).ConfigureAwait(false) == true ? PictureSize.Small : wanted;
    }

    /// <summary>Said after a picture that <see cref="StartSizeAsync"/> started Small in place of Normal.</summary>
    public const string StartedSmall = "Drawn Small: at Normal size the chat model would have been set aside. Make larger keeps the picture and adds detail.";

    /// <summary>Said after Make larger chose <see cref="LargerFallbackScale"/>.</summary>
    public const string LargerFellBack = "Made larger ×1.25: ×1.5 would have set the chat model aside.";

    public static string Title(PictureSize size) => size.ToString();

    /// <summary>One line for the owner on what a size is for.</summary>
    public static string Blurb(PictureSize size) => size switch
    {
        PictureSize.Small => "Faster, and leaves more room for the chat model. Make larger on a picture you like.",
        _ => "SDXL’s own size, about a million pixels.",
    };

    /// <summary>
    /// A picture's level in words: "Standard", "Draft", "Fine", and for a Small picture made larger "Made larger ×1.5"
    /// (never "Fine": Fine is the hires fix at Normal's size).
    /// </summary>
    public static string Title(PictureQuality quality, PictureSize size, double fineScale) =>
        quality == PictureQuality.Fine && size == PictureSize.Small
            ? "Made larger ×" + fineScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : Title(quality);

    public static string Title(PictureQuality quality) => quality.ToString();

    /// <summary>One line for the owner on what a level is for.</summary>
    public static string Blurb(PictureQuality quality) => quality switch
    {
        PictureQuality.Draft => "Fast, for trying ideas.",
        PictureQuality.Fine => "Enlarged and refined. Slower, and it can change small details, so use it on a picture you already like.",
        _ => "The checkpoint’s normal quality.",
    };

    /// <summary>"Tall (640 × 960)": the shape with its pixels at a size.</summary>
    public static string Title(PictureShape shape, PictureSize size)
    {
        var (width, height) = Size(shape, size);
        return $"{shape} ({width} × {height})";
    }
}

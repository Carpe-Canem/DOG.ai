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

/// <summary>The three picture shapes, at SDXL's native sizes (about one million pixels each).</summary>
public enum PictureShape
{
    /// <summary>832 × 1216. The default for portraits.</summary>
    Tall,

    /// <summary>1024 × 1024.</summary>
    Square,

    /// <summary>1216 × 832. The default for story illustrations.</summary>
    Wide,
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

    public static (int Width, int Height) Size(PictureShape shape) => shape switch
    {
        PictureShape.Tall => (832, 1216),
        PictureShape.Wide => (1216, 832),
        _ => (1024, 1024),
    };

    /// <summary>The steps Draft draws for a checkpoint whose own count is <paramref name="steps"/>.</summary>
    public static int DraftSteps(int steps) => Math.Min(steps, Math.Max(DraftFloor, (int)Math.Round(steps * DraftShare)));

    /// <summary>Fine's hires fix for a checkpoint whose own step count is <paramref name="steps"/>.</summary>
    public static HiresFix FineHires(int steps) => new(FineScale, Math.Max(8, (int)Math.Round(steps * FineStepShare)), FineDenoise, FineUpscaler);

    /// <summary>
    /// The request at a quality level and shape. <paramref name="request"/> carries the checkpoint's own step count (as
    /// <see cref="ImagePrompt.Compose"/> makes it); everything else in it is kept, so Edit and redraw can change the
    /// level alone.
    /// </summary>
    public static ImageRequest At(this ImageRequest request, PictureQuality quality, PictureShape shape)
    {
        var (width, height) = Size(shape);
        return quality switch
        {
            PictureQuality.Draft => request with { Width = width, Height = height, Steps = DraftSteps(request.Steps), Hires = null },
            PictureQuality.Fine => request with { Width = width, Height = height, Hires = FineHires(request.Steps) },
            _ => request with { Width = width, Height = height, Hires = null },
        };
    }

    public static string Title(PictureQuality quality) => quality.ToString();

    /// <summary>One line for the owner on what a level is for.</summary>
    public static string Blurb(PictureQuality quality) => quality switch
    {
        PictureQuality.Draft => "Fast, for trying ideas.",
        PictureQuality.Fine => "Enlarged and refined. Slower, and it can change small details, so use it on a picture you already like.",
        _ => "The checkpoint’s normal quality.",
    };

    public static string Title(PictureShape shape) => shape switch
    {
        PictureShape.Tall => "Tall (832 × 1216)",
        PictureShape.Wide => "Wide (1216 × 832)",
        _ => "Square (1024 × 1024)",
    };
}

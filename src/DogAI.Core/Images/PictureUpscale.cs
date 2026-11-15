using System.Globalization;

namespace DogAI.Core.Images;

/// <summary>The two ways Upscale makes a picture larger (M16 step 5; both were tried on this PC first, see docs/PLAN.md).</summary>
public enum UpscaleWay
{
    /// <summary>
    /// Draw the picture again from its seed with its own checkpoint and settings, enlarge it and refine the enlarged
    /// picture (the hires fix). Needs no extra file; adds detail; small things can change.
    /// </summary>
    Redraw,

    /// <summary>
    /// Enlarge the picture as it is with an upscaler file (ESRGAN kind). Nothing is drawn, so nothing changes, and it
    /// needs neither the picture's checkpoint nor much graphics memory.
    /// </summary>
    Enlarge,
}

/// <summary>
/// A picture as Upscale needs it: the picture itself and everything it was drawn with. Made from a try on the Make tab
/// or from a kept picture, so both are upscaled by the same code.
/// </summary>
/// <param name="Request">The one-picture request that drew it, with its seed.</param>
/// <param name="Image">The picture as WebP.</param>
public sealed record UpscaleSource(string Checkpoint, string? Vae, ImagePrediction Prediction, ImageRequest Request, byte[] Image, int ImageWidth, int ImageHeight)
{
    public static UpscaleSource Of(ImagesTry made) =>
        new(made.Checkpoint, made.Vae, made.Prediction, made.Request, made.Image, made.ImageWidth, made.ImageHeight);

    /// <summary>From a kept picture read with its image (<see cref="IPictureStore.GetAsync"/>); null when the image was left out.</summary>
    public static UpscaleSource? Of(Picture kept) => kept.Image is { } image
        ? new(kept.Checkpoint, kept.Vae, kept.Prediction, kept.ToRequest(), image, kept.ImageWidth, kept.ImageHeight)
        : null;
}

/// <summary>One size "Redraw larger" offers: the scale counted from the size the picture was first drawn at, and the size it gives.</summary>
public sealed record RedrawSize(double Scale, int Width, int Height)
{
    /// <summary>"1664 × 2432 (×2)".</summary>
    public string Label => $"{Width} × {Height} (×{Scale.ToString("0.##", CultureInfo.InvariantCulture)})";
}

/// <summary>
/// The rules and wording of Upscale. An upscaled picture is a new picture beside the one it came from: a new try on the
/// Make tab, a new row when kept. Nothing is ever changed in place.
/// <para>
/// A redrawn picture is an ordinary picture with a hires fix, so its row draws it again like any other. An enlarged
/// one keeps the request of the picture it came from (that is what "Make one like this" can draw); that it was enlarged
/// is told from its size (<see cref="EnlargedBy(ImageRequest, int, int)"/>), so the Pictures table needs no new column.
/// </para>
/// </summary>
public static class PictureUpscale
{
    /// <summary>The scales Redraw larger offers, counted from the size the picture was first drawn at.</summary>
    public static readonly IReadOnlyList<double> Scales = [1.5, 2, 2.5, 3];

    public const string RedrawTitle = "Redraw larger";
    public const string EnlargeTitle = "Enlarge only";

    public const string RedrawAbout =
        "DOG.ai draws the picture again from its seed, with its own checkpoint and settings, enlarges it and refines the enlarged picture. It adds detail; small things can change.";

    public const string EnlargeAbout =
        "An upscaler file makes the picture larger as it is. Nothing is drawn again, so nothing in it changes, and it takes little graphics memory.";

    /// <summary>What the Enlarge only part says when no upscaler file can be used.</summary>
    public const string NoUpscaler =
        "No upscaler file is in the upscalers folder. Put an ESRGAN upscaler (.safetensors) at the top of that folder, then press Look again.";

    /// <summary>
    /// The request that draws <paramref name="request"/>'s picture again at <paramref name="scale"/> times its first
    /// size: the same seed and settings with a hires fix. A picture that already has one keeps its steps, strength and
    /// enlarger; any other gets the numbers Fine uses (<see cref="PictureLevels.FineHires"/>).
    /// </summary>
    public static ImageRequest Redraw(ImageRequest request, double scale) => (request with
    {
        Count = 1,
        Preview = false,
        Hires = (request.Hires ?? PictureLevels.FineHires(request.Steps)) with { Scale = scale },
    }).ForKeeping();

    /// <summary>
    /// The sizes Redraw larger can offer for a picture: every scale above the one it has that the request's own rules
    /// allow. Empty when it is already as large as a redraw goes.
    /// </summary>
    public static IReadOnlyList<RedrawSize> RedrawSizes(ImageRequest request)
    {
        var has = request.Hires?.Scale ?? 1;
        // A Small picture is also offered ×1.25 (Normal's size for Tall and Wide): Make larger's choice when ×1.5 would
        // set the chat model aside.
        IEnumerable<double> scales = IsSmall(request) ? [PictureLevels.LargerFallbackScale, .. Scales] : Scales;
        return [.. scales.Where(s => s > has + 0.01)
            .Select(s => Redraw(request, s))
            .Where(r => r.Seed >= 0 && r.Problems().Count == 0)
            .Select(r => new RedrawSize(r.Hires!.Scale, r.LargestPass.Width, r.LargestPass.Height))];
    }

    /// <summary>True when the picture was first drawn at one of the Small sizes (M18).</summary>
    public static bool IsSmall(ImageRequest request) => PictureLevels.Of(request.Width, request.Height)?.Size == PictureSize.Small;

    /// <summary>
    /// The size Redraw larger starts on: for a Small picture not made larger yet, Make larger's own choice (×1.5, or ×1.25
    /// when only that fits beside the chat model; <paramref name="fits"/> as for <see cref="PictureLevels.LargerScaleAsync"/>);
    /// for any other ×2, else the first offered.
    /// </summary>
    public static async Task<double> FirstScaleAsync(ImageRequest request, IReadOnlyList<RedrawSize> sizes, Func<double, Task<bool?>> fits)
    {
        if (sizes.Count == 0) return 0;
        if (IsSmall(request) && request.Hires is null)
        {
            var larger = await PictureLevels.LargerScaleAsync(PictureSize.Small, fits).ConfigureAwait(false);
            if (sizes.Any(s => s.Scale == larger)) return larger;
        }
        return (sizes.FirstOrDefault(s => s.Scale == 2) ?? sizes[0]).Scale;
    }

    /// <summary>Why Redraw larger has no size to offer.</summary>
    public static string NoRedrawSize(ImageRequest request) => request.Hires is { } h && h.Scale >= Scales[^1] - 0.01
        ? $"It is already drawn ×{h.Scale.ToString("0.##", CultureInfo.InvariantCulture)} its first size, as large as a redraw goes."
        : $"A larger redraw would pass {ImageRequest.MaxSide} pixels, the most a picture can be drawn at.";

    /// <summary>
    /// How many times an upscaler file enlarged a picture, or null when it was not: the picture is at least about twice
    /// the size its request draws (a hires fix included).
    /// </summary>
    public static int? EnlargedBy(ImageRequest request, int imageWidth, int imageHeight)
    {
        var (width, height) = request.LargestPass;
        if (width <= 0 || height <= 0 || imageWidth <= 0 || imageHeight <= 0) return null;
        var by = Math.Min(imageWidth / (double)width, imageHeight / (double)height);
        var whole = (int)Math.Round(by);
        return by >= 1.9 && Math.Abs(by - whole) < 0.1 ? whole : null;
    }

    public static int? EnlargedBy(Picture kept) => EnlargedBy(kept.ToRequest(), kept.ImageWidth, kept.ImageHeight);

    public static int? EnlargedBy(ImagesTry made) => EnlargedBy(made.Request, made.ImageWidth, made.ImageHeight);

    /// <summary>The size a picture has after an upscaler that enlarges <paramref name="by"/> times.</summary>
    public static (int Width, int Height) Enlarged(int width, int height, int by) => (width * by, height * by);

    /// <summary>What stops a picture of this size from being enlarged <paramref name="by"/> times, in plain words, or null.</summary>
    public static string? EnlargeProblem(int width, int height, int by)
    {
        var (w, h) = Enlarged(width, height, by);
        return Math.Max(w, h) > ImageEnlarge.MaxSide
            ? $"It would become {w} × {h}; the picture engine enlarges to {ImageEnlarge.MaxSide} pixels a side at most."
            : null;
    }

    /// <summary>How an upscaler file is named in the list: "RealESRGAN_x4plus (×4)".</summary>
    public static string UpscalerLabel(string name, int? by) => by is { } n ? $"{name} (×{n})" : name;

    /// <summary>What is said once it is done: "Upscaled to 1664 × 2432 in 25 s."</summary>
    public static string Done(ImagesTry made) =>
        $"Upscaled to {made.ImageWidth} × {made.ImageHeight} in {Seconds(made.Took)}.";

    /// <summary>The facts under an upscaled picture before it is kept: "3328 × 4864 · enlarged ×4 · 1.3 MB · 8.8 s".</summary>
    public static string ResultMeta(ImagesTry made)
    {
        var parts = new List<string> { $"{made.ImageWidth} × {made.ImageHeight}" };
        if (EnlargedBy(made) is { } by) parts.Add($"enlarged ×{by}");
        else if (made.Request.Hires is { } h) parts.Add("hires ×" + h.Scale.ToString("0.##", CultureInfo.InvariantCulture));
        parts.Add((made.Image.Length / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
        parts.Add(Seconds(made.Took));
        return string.Join(" · ", parts);
    }

    private static string Seconds(TimeSpan took) => ImagesPanels.Seconds(took);
}

namespace DogAI.Core.Images;

/// <summary>
/// One picture drawn in the picture maker, held in memory only. Nothing of it reaches the disk unless the owner picks it
/// (<see cref="ToKeep"/> → <see cref="IPictureStore.AddAsync"/>).
/// </summary>
/// <param name="Number">Counts up from 1 in one picture maker, so the owner can tell tries apart.</param>
/// <param name="Checkpoint">The checkpoint's path inside the checkpoints folder.</param>
/// <param name="Settings">The checkpoint's card as it was when drawing (its VAE and prediction are kept with the picture).</param>
/// <param name="Base">
/// The request as <see cref="ImagePrompt.Compose"/> makes it, at the checkpoint's own step count, with the seed this
/// picture was drawn from. Quality and shape are applied to it (<see cref="Request"/>), never to an already-levelled request:
/// <see cref="PictureLevels.At"/> takes the checkpoint's own step count.
/// </param>
/// <param name="Moment">The planned moment, for the owner to read; null for a redraw of a kept picture.</param>
/// <param name="KeptId">The <see cref="Picture"/> row this is, when it is already kept.</param>
public sealed record PictureTry(
    int Number,
    string Checkpoint,
    CheckpointSettings Settings,
    ImageRequest Base,
    PictureQuality Quality,
    PictureShape Shape,
    byte[] Image,
    int ImageWidth,
    int ImageHeight,
    string? Moment,
    TagNotes Notes,
    long? KeptId = null)
{
    /// <summary>What was sent to the engine for this picture (WebP, the way kept pictures are drawn).</summary>
    public ImageRequest Request => PictureTries.Levelled(Base, Quality, Shape);

    /// <summary>
    /// The row to keep for <paramref name="ownerKind"/> / <paramref name="ownerId"/>. Drawing it again from the row gives
    /// the same picture.
    /// </summary>
    public Picture ToKeep(string ownerKind, long ownerId) =>
        Picture.Of(ownerKind, ownerId, Checkpoint, Settings, Request, new ImageResult([Image], ImageFormat.Webp, Base.Seed));

    // Kept beside the picture's bytes, not in the record: a record's fields count when two tries are compared.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> Addresses = new();

    /// <summary>
    /// The picture as a data: address for an img tag (from memory; never a file). Made once per picture: the picture
    /// maker reads it for every try on every render, several times a second while a picture is being drawn.
    /// </summary>
    public string Src => Addresses.GetValue(Image, static image => "data:image/webp;base64," + Convert.ToBase64String(image));
}

/// <summary>
/// The picture maker's tries: drawing one, trying again with a new seed, redrawing with one thing changed and the seed kept,
/// and taking a kept picture back in so it can be redrawn. Held in memory; at most <see cref="Max"/>.
/// </summary>
public sealed class PictureTries(IImageEngine engine)
{
    /// <summary>The most tries held at once. Each is 100 to 200 KB; the oldest goes first (never a kept one).</summary>
    public const int Max = 12;

    private readonly List<PictureTry> _tries = [];
    private int _number;

    public IReadOnlyList<PictureTry> All => _tries;

    /// <summary>True when the last <see cref="Add"/> had to let an old try go.</summary>
    public bool Dropped { get; private set; }

    public PictureTry Add(PictureTry next)
    {
        Dropped = false;
        _tries.Add(next);
        while (_tries.Count > Max && _tries.FindIndex(t => t.KeptId is null && !ReferenceEquals(t, next)) is var i and >= 0)
        {
            _tries.RemoveAt(i);
            Dropped = true;
        }
        return next;
    }

    public void Clear() => _tries.Clear();

    /// <summary>The request sent to the engine: <paramref name="base"/> at a quality and shape, drawn as kept pictures are.</summary>
    public static ImageRequest Levelled(ImageRequest @base, PictureQuality quality, PictureShape shape) =>
        @base.At(quality, shape).ForKeeping();

    /// <summary>
    /// Draws <paramref name="base"/> at a quality and shape with the checkpoint, through the engine (the graphics-card
    /// coordinator in the app), with previews. A seed below zero gets a new one; the try keeps the seed the engine used.
    /// Call it only after the chat model's job has ended.
    /// </summary>
    public async Task<PictureTry> DrawAsync(string imageRoot, string checkpoint, CheckpointSettings settings, ImageRequest @base,
        PictureQuality quality, PictureShape shape, string? moment, TagNotes notes, IProgress<ImageProgress>? progress = null,
        CancellationToken ct = default)
    {
        var request = Levelled(@base, quality, shape) with { Preview = progress is not null, PreviewEvery = 4 };
        var model = ImageFolders.ModelFor(imageRoot, checkpoint, settings);
        if (engine.Model != model) await engine.LoadAsync(model, ct).ConfigureAwait(false);
        var result = await engine.DrawAsync(request, progress, ct).ConfigureAwait(false);
        var image = result.Images[0];
        var size = WebpInfo.Read(image);
        return Add(new PictureTry(++_number, ImageModelCatalog.Normalise(checkpoint), settings, @base with { Seed = result.Seed }, quality, shape,
            image, size?.Width ?? 0, size?.Height ?? 0, moment, notes));
    }

    /// <summary>The same plan again with a new seed.</summary>
    public Task<PictureTry> TryAgainAsync(string imageRoot, PictureTry like, IProgress<ImageProgress>? progress = null, CancellationToken ct = default) =>
        DrawAsync(imageRoot, like.Checkpoint, like.Settings, like.Base with { Seed = -1 }, like.Quality, like.Shape, like.Moment, like.Notes, progress, ct);

    /// <summary>
    /// The same picture with things changed: the prompt, the quality, the shape, the LoRAs, and the seed when the owner
    /// asks (Keep is the default). With the seed kept, a changed quality alone gives the same picture drawn faster or finer
    /// (with the Euler sampler); anything else changes more. It is always a new try: the one it came from is not touched.
    /// </summary>
    /// <param name="loras">The LoRAs and strengths to draw with; null keeps the try's own. A LoRA's trigger words stay in the
    /// prompt until the owner takes them out.</param>
    /// <param name="seed">The seed row's choice; null keeps the try's own seed.</param>
    public Task<PictureTry> RedrawAsync(string imageRoot, PictureTry from, string prompt, PictureQuality quality, PictureShape shape,
        IReadOnlyList<LoraUse>? loras = null, SeedChoice? seed = null, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
    {
        prompt = string.Join(' ', prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (prompt.Length == 0) throw new InvalidOperationException("The prompt is empty.");
        var @base = from.Base with
        {
            Prompt = prompt,
            Loras = loras is null ? from.Base.Loras : [.. loras],
            Seed = (seed ?? SeedChoice.Keep).For(from.Base.Seed),
        };
        return DrawAsync(imageRoot, from.Checkpoint, from.Settings, @base, quality, shape,
            prompt == from.Base.Prompt ? from.Moment : null, prompt == from.Base.Prompt ? from.Notes : TagNotes.None, progress, ct);
    }

    /// <summary>
    /// Takes a kept picture back in, so it can be redrawn. <paramref name="card"/> is its checkpoint's card now; only its
    /// step count is used, to tell which quality the picture was drawn at (Fine has a hires fix; Draft has fewer steps than
    /// the card). The picture's own VAE and prediction are kept.
    /// </summary>
    public PictureTry Adopt(Picture kept, CheckpointSettings card)
    {
        var (@base, quality) = BaseOf(kept, card.Steps);
        var shape = ShapeOf(kept.Width, kept.Height);
        var settings = card with { Vae = kept.Vae, Prediction = kept.Prediction };
        return Add(new PictureTry(++_number, kept.Checkpoint, settings, @base, quality, shape, kept.Image ?? [], kept.ImageWidth, kept.ImageHeight,
            null, TagNotes.None, kept.Id));
    }

    /// <summary>A kept picture's request at the checkpoint's own step count, and the quality it was drawn at.</summary>
    public static (ImageRequest Base, PictureQuality Quality) BaseOf(Picture kept, int cardSteps)
    {
        var request = kept.ToRequest() with { Hires = null };
        if (kept.Hires is not null) return (request, PictureQuality.Fine);
        if (kept.Steps != cardSteps && kept.Steps == PictureLevels.DraftSteps(cardSteps)) return (request with { Steps = cardSteps }, PictureQuality.Draft);
        // Drawn at the card's own count, or at a count the card no longer has: Standard at the picture's own count.
        return (request, PictureQuality.Standard);
    }

    public static PictureShape ShapeOf(int width, int height) =>
        Enum.GetValues<PictureShape>().FirstOrDefault(s => PictureLevels.Size(s) == (width, height),
            width > height ? PictureShape.Wide : width < height ? PictureShape.Tall : PictureShape.Square);
}

/// <summary>
/// The one kept picture of a character or persona: the portrait in use, which Edit and redraw starts from later. Called
/// after the owner is saved (a new one has no id before), so a cancelled edit leaves nothing.
/// </summary>
public static class Portraits
{
    /// <summary>
    /// Makes <paramref name="chosen"/> the owner's only kept picture, or keeps none when it is null (the picture was removed
    /// or came from a file). The new row is added before the old ones go, so a failure leaves the old picture kept.
    /// Returns the id of the row kept, or null.
    /// </summary>
    /// <exception cref="InvalidOperationException">The picture could not be kept (see <see cref="Picture.Problems"/>).</exception>
    public static async Task<long?> KeepAsync(IPictureStore store, string ownerKind, long ownerId, PictureTry? chosen, CancellationToken ct = default)
    {
        var existing = await store.ListAsync(ownerKind, ownerId, ct).ConfigureAwait(false);
        long? keep = chosen switch
        {
            null => null,
            // The kept picture chosen again: nothing new to write.
            { KeptId: { } same } when existing.Any(p => p.Id == same) => same,
            _ => (await store.AddAsync(chosen.ToKeep(ownerKind, ownerId), ct).ConfigureAwait(false)).Id,
        };
        foreach (var old in existing)
            if (old.Id != keep) await store.DeleteAsync(old.Id, ct).ConfigureAwait(false);
        return keep;
    }
}

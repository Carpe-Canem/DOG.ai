namespace DogAI.Core.Images;

/// <summary>
/// What a kept picture can belong to. The kind is plain text in the database, so later steps can add kinds without a
/// migration. For the three kinds below the database itself removes the pictures when their owner is deleted (triggers in
/// the Pictures migration), so a deleted chat, story or card never leaves pictures behind.
/// </summary>
public static class PictureOwners
{
    /// <summary>A character card (<see cref="Picture.OwnerId"/> is the character's id). For portraits.</summary>
    public const string Character = "character";

    /// <summary>A persona. For portraits.</summary>
    public const string Persona = "persona";

    /// <summary>A message: a story passage, or later a chat reply. Goes when the message or its whole chat is deleted.</summary>
    public const string Message = "message";

    /// <summary>Pictures kept from the test picture in Models › On this PC › Pictures while checking (owner id 0).</summary>
    public const string Test = "test";

    /// <summary>
    /// Pictures kept in the Images section (M16), with owner id <see cref="ImagesOwner"/>. Unlike a portrait or a passage's
    /// picture there is no one-row limit: every Keep adds a row, and only Remove takes one away. Nothing else owns them,
    /// so no trigger is needed.
    /// </summary>
    public const string Images = "images";

    /// <summary>The owner id of every picture kept in the Images section.</summary>
    public const long ImagesOwner = 0;

    public const int MaxKindLength = 20;
}

/// <summary>
/// The small version of a kept picture that the Kept pictures grid shows. It is made in memory (by the page, from the
/// picture itself) and kept in the picture's own row, so the lock, backups and a portable move cover it and removing the
/// picture removes it. It is never written to a file or a cache.
/// </summary>
public static class PicturePreview
{
    /// <summary>The longest side a preview is made with, in pixels. A picture smaller than this is not enlarged.</summary>
    public const int Side = 384;

    /// <summary>The longest side a preview may have; anything larger is a picture, not a preview.</summary>
    public const int MaxSide = 512;

    /// <summary>The most bytes a preview may have. One at <see cref="Side"/> is about 15 to 40 KB.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The size to make the preview of a picture of this size: the same shape, the longest side <see cref="Side"/>.</summary>
    public static (int Width, int Height) SizeFor(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return (0, 0);
        var scale = Math.Min(1.0, (double)Side / Math.Max(imageWidth, imageHeight));
        return (Math.Max(1, (int)Math.Round(imageWidth * scale)), Math.Max(1, (int)Math.Round(imageHeight * scale)));
    }

    /// <summary>
    /// The picture part alone, as a plain lossy WebP: the browser's encoder wraps its preview in the extended format with
    /// a colour profile (ICC, about 450 bytes) beside the picture. Anything that is not an extended lossy file with
    /// only a colour profile and the picture (transparency, animation, EXIF, XMP) comes back unchanged, for
    /// <see cref="Problem"/> to judge.
    /// </summary>
    public static byte[] Plain(byte[] webp)
    {
        if (webp.Length < 30 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            || !webp.AsSpan(12, 4).SequenceEqual("VP8X"u8)) return webp;
        byte[]? picture = null;
        var at = 12;
        while (at + 8 <= webp.Length)
        {
            var name = webp.AsSpan(at, 4);
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4, 4));
            if (size > (uint)(webp.Length - at - 8)) return webp;
            if (name.SequenceEqual("VP8 "u8)) picture = webp.AsSpan(at, 8 + (int)size).ToArray();
            else if (!name.SequenceEqual("VP8X"u8) && !name.SequenceEqual("ICCP"u8)) return webp;
            at += 8 + (int)size + (int)(size & 1);
        }
        if (picture is null) return webp;
        var plain = new byte[12 + picture.Length + (picture.Length & 1)];
        "RIFF"u8.CopyTo(plain);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), (uint)(plain.Length - 8));
        "WEBP"u8.CopyTo(plain.AsSpan(8));
        picture.CopyTo(plain, 12);
        return plain;
    }

    /// <summary>What stops these bytes from being the preview of a picture of this size, in plain words, or null.</summary>
    public static string? Problem(byte[]? preview, int imageWidth, int imageHeight)
    {
        if (preview is null || WebpInfo.Read(preview) is not { } info) return "The preview is not a WebP image.";
        if (preview.Length > MaxBytes) return "The preview is too large to be a preview.";
        if (info.Width > MaxSide || info.Height > MaxSide) return $"A preview is at most {MaxSide} pixels on its longest side.";
        // A place where a program could have written a prompt; the page's own previews never have one.
        if (info.HasMetadata) return "The preview carries extra information besides the picture.";
        if (imageWidth > 0 && imageHeight > 0)
        {
            // The same shape as its picture, give or take the rounding of a small size.
            double a = (double)info.Width * imageHeight, b = (double)info.Height * imageWidth;
            if (Math.Abs(a - b) > 0.05 * Math.Max(a, b)) return "The preview is not the shape of its picture.";
        }
        return null;
    }
}

/// <summary>Which kept pictures to list. Only <see cref="OwnerKind"/> is needed; everything else narrows the list.</summary>
/// <param name="Words">What was typed in the find box. Every word must be somewhere in the prompt (not the negative prompt).</param>
/// <param name="Checkpoint">Only pictures drawn with this checkpoint (its path inside the checkpoints folder).</param>
/// <param name="Shape">Only pictures of this shape, told from the picture's own size (<see cref="Shapes"/>).</param>
/// <param name="Skip">How many of the newest to pass over: the pictures already shown.</param>
/// <param name="Take">How many to return, at most <see cref="MaxTake"/>.</param>
public sealed record PictureQuery(
    string OwnerKind,
    long OwnerId = 0,
    string? Words = null,
    string? Checkpoint = null,
    PictureShape? Shape = null,
    int Skip = 0,
    int Take = PictureQuery.PageSize)
{
    public const int PageSize = 40;
    public const int MaxTake = 200;

    /// <summary>At most this many typed words are looked for, each at most <see cref="MaxWordLength"/> characters.</summary>
    public const int MaxWords = 8, MaxWordLength = 100;

    /// <summary>The Images section's kept pictures.</summary>
    public static PictureQuery Images(string? words = null, string? checkpoint = null, PictureShape? shape = null, int skip = 0, int take = PageSize) =>
        new(PictureOwners.Images, PictureOwners.ImagesOwner, words, checkpoint, shape, skip, take);

    /// <summary>
    /// The typed text as the words to find: split at spaces and commas (a prompt is a list of tags), without repeats,
    /// in the order typed. Capital and small letters are the same.
    /// </summary>
    public static IReadOnlyList<string> WordsOf(string? typed) =>
        [.. (typed ?? "").Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Length > MaxWordLength ? w[..MaxWordLength] : w)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxWords)];

    /// <summary>
    /// True when a prompt has every word. The store asks the database the same thing, so a page can be cut there; the two
    /// agree for English letters (the database does not fold the case of others: "É" and "é" differ for it).
    /// </summary>
    public static bool Matches(string prompt, IReadOnlyList<string> words) =>
        words.All(w => prompt.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The shape of a picture of this size: taller than wide, square, or wider than tall.</summary>
    public static PictureShape Shapes(int width, int height) =>
        height > width ? PictureShape.Tall : width > height ? PictureShape.Wide : PictureShape.Square;
}

/// <summary>
/// One page of kept pictures, newest first, each with its preview (when it has one) and without its image.
/// </summary>
/// <param name="Total">How many pictures the query finds in all, whatever the page.</param>
/// <param name="Skip">How many were passed over to reach this page.</param>
public sealed record PicturePage(IReadOnlyList<Picture> Pictures, int Total, int Skip)
{
    public static PicturePage Empty { get; } = new([], 0, 0);

    /// <summary>True when there are older pictures after this page.</summary>
    public bool HasMore => Skip + Pictures.Count < Total;

    /// <summary>The <see cref="PictureQuery.Skip"/> that gives the page after this one.</summary>
    public int Next => Skip + Pictures.Count;
}

/// <summary>How kept pictures are stored.</summary>
public static class PictureFormat
{
    /// <summary>
    /// The WebP quality kept pictures are drawn with (0 to 100). Chosen in M9 step 5 by comparing pictures from the
    /// owner's Illustrious checkpoint by eye against the lossless original, enlarged four times; see docs/PLAN.md.
    /// </summary>
    public const int Quality = 90;

    /// <summary>The same request, with the picture coming back the way it is kept: WebP at <see cref="Quality"/>.</summary>
    public static ImageRequest ForKeeping(this ImageRequest request) => request with { Format = ImageFormat.Webp, Quality = Quality };
}

/// <summary>
/// A kept picture: the image itself (WebP) and everything needed to draw it again. Lives in the database's Pictures
/// table, so the lock, backups, restore and portable mode cover it like any other row. Pictures that were tried and not
/// kept never become one of these: they exist only in memory.
/// </summary>
/// <remarks>
/// Files are named by their path inside their own folder ("illustrious.safetensors", "style/ink.safetensors"), never by a
/// full path, so a moved models folder or portable install needs nothing rewritten.
/// </remarks>
public sealed class Picture
{
    public long Id { get; set; }

    /// <summary>One of <see cref="PictureOwners"/>.</summary>
    public string OwnerKind { get; set; } = "";

    /// <summary>The owner's id within its kind.</summary>
    public long OwnerId { get; set; }

    /// <summary>The picture as WebP. Null on pictures that came from a list, which leaves the images out; ask the store for it.</summary>
    public byte[]? Image { get; set; }

    /// <summary>Size of the picture itself in pixels (larger than <see cref="Width"/> × <see cref="Height"/> after a hires fix).</summary>
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>How many bytes <see cref="Image"/> has, known without reading it.</summary>
    public int ImageBytes { get; set; }

    /// <summary>
    /// A small WebP of the picture for grids (<see cref="PicturePreview"/>), or null when none was made yet. Kept in
    /// the row, never as a file. Null on pictures from <see cref="IPictureStore.ListAsync"/>, which leaves it out.
    /// </summary>
    public byte[]? Preview { get; set; }

    // ---- Everything needed to draw it again ----

    /// <summary>The checkpoint, as its path inside the checkpoints folder.</summary>
    public string Checkpoint { get; set; } = "";

    /// <summary>A separate VAE, as its path inside the vae folder; null for the one in the checkpoint.</summary>
    public string? Vae { get; set; }

    public ImagePrediction Prediction { get; set; }

    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";

    /// <summary>The size asked for, before any hires fix.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    public long Seed { get; set; }
    public int Steps { get; set; }
    public string Sampler { get; set; } = "";
    public string? Scheduler { get; set; }
    public double Guidance { get; set; }
    public int? ClipSkip { get; set; }

    /// <summary>Each LoRA as its path inside the loras folder, with its strength.</summary>
    public List<LoraUse> Loras { get; set; } = [];

    public HiresFix? Hires { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The picture numbered <paramref name="index"/> of a finished drawing, ready to keep. <paramref name="checkpoint"/> is
    /// the checkpoint's path inside the checkpoints folder and <paramref name="settings"/> its card as it was when drawing.
    /// </summary>
    public static Picture Of(string ownerKind, long ownerId, string checkpoint, CheckpointSettings settings,
        ImageRequest request, ImageResult result, int index = 0) =>
        Of(ownerKind, ownerId, checkpoint, settings.Vae, settings.Prediction, request, result, index);

    /// <summary>
    /// The same from the pieces themselves, for a picture that was not drawn from a checkpoint's card as it stands (the
    /// Images section, where the VAE is one of the controls).
    /// </summary>
    public static Picture Of(string ownerKind, long ownerId, string checkpoint, string? vae, ImagePrediction prediction,
        ImageRequest request, ImageResult result, int index = 0)
    {
        var image = result.Images[index];
        var info = WebpInfo.Read(image);
        return new Picture
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Image = image,
            ImageWidth = info?.Width ?? 0,
            ImageHeight = info?.Height ?? 0,
            ImageBytes = image.Length,
            Checkpoint = ImageModelCatalog.Normalise(checkpoint),
            Vae = vae is { Length: > 0 } ? ImageModelCatalog.Normalise(vae) : null,
            Prediction = prediction,
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            Width = request.Width,
            Height = request.Height,
            // The engine draws several pictures of one request from seeds that go up by one.
            Seed = result.Seed + index,
            Steps = request.Steps,
            Sampler = request.Sampler,
            Scheduler = request.Scheduler,
            Guidance = request.Guidance,
            ClipSkip = request.ClipSkip,
            Loras = [.. (request.Loras ?? []).Select(l => l with { Path = ImageModelCatalog.Normalise(l.Path) })],
            Hires = request.Hires,
        };
    }

    /// <summary>The request that draws this picture again: same seed and settings, kept the same way.</summary>
    public ImageRequest ToRequest() => new ImageRequest(Prompt, NegativePrompt, Width, Height, Seed, Steps, Sampler, Scheduler, Guidance,
        ClipSkip, Loras, Hires).ForKeeping();

    /// <summary>The model to load to draw it again. <paramref name="imageRoot"/> is the image models folder as it is now.</summary>
    public ImageModel ModelIn(string imageRoot) => ImageFolders.ModelFor(imageRoot, Checkpoint, Vae, Prediction);

    /// <summary>What stops this picture from being kept, in plain words, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(OwnerKind) || OwnerKind.Length > PictureOwners.MaxKindLength)
            problems.Add("The picture does not say what it belongs to.");
        if (Image is null || WebpInfo.Read(Image) is not { } info)
            problems.Add("The picture is not a WebP image.");
        else if (info.Width != ImageWidth || info.Height != ImageHeight)
            problems.Add("The picture's size does not match the image.");
        if (Preview is not null && PicturePreview.Problem(Preview, ImageWidth, ImageHeight) is { } preview) problems.Add(preview);
        // A full path would tie the picture to one PC and one folder, and would say where the owner keeps things.
        if (!ImageRequest.IsSafeRelativePath(Checkpoint))
            problems.Add("The checkpoint must be named by its path inside the checkpoints folder.");
        if (Vae is not null && !ImageRequest.IsSafeRelativePath(Vae))
            problems.Add("The VAE must be named by its path inside the vae folder.");
        if (Seed < 0) problems.Add("The picture's seed is not known.");
        problems.AddRange(ToRequest().Problems());
        return problems;
    }
}

/// <summary>
/// Kept pictures. The real one is <c>SqlitePictureStore</c> (DogAI.Data). Nothing here draws: a picture is drawn through
/// <see cref="IImageEngine"/>, lives in memory, and reaches the disk only through <see cref="AddAsync"/>.
/// </summary>
public interface IPictureStore
{
    /// <summary>
    /// Keeps a picture and returns it with its id and time. Throws <see cref="InvalidOperationException"/> with
    /// <see cref="Picture.Problems"/> when it cannot be kept (not WebP, a full path where a file name belongs).
    /// </summary>
    Task<Picture> AddAsync(Picture picture, CancellationToken ct = default);

    /// <summary>The picture with its image, or null when it is gone.</summary>
    Task<Picture?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Only the image (WebP), or null when the picture is gone.</summary>
    Task<byte[]?> ImageAsync(long id, CancellationToken ct = default);

    /// <summary>An owner's pictures, newest first, without their images or previews (<see cref="Picture.Image"/> is null).</summary>
    Task<IReadOnlyList<Picture>> ListAsync(string ownerKind, long ownerId, CancellationToken ct = default);

    /// <summary>
    /// One page of an owner's pictures, newest first, found and filtered as the query says. Each comes with its preview
    /// and without its image, so a grid can be drawn from the page alone.
    /// </summary>
    Task<PicturePage> PageAsync(PictureQuery query, CancellationToken ct = default);

    /// <summary>The checkpoints an owner's pictures were drawn with, in alphabetical order: what a filter can offer.</summary>
    Task<IReadOnlyList<string>> CheckpointsAsync(string ownerKind, long ownerId, CancellationToken ct = default);

    /// <summary>Only the preview (a small WebP), or null when the picture is gone or has none yet.</summary>
    Task<byte[]?> PreviewAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Gives a kept picture its preview, or a new one. False when the picture is gone. Throws
    /// <see cref="InvalidOperationException"/> with <see cref="PicturePreview.Problem"/> when the bytes cannot be one.
    /// </summary>
    Task<bool> SetPreviewAsync(long id, byte[] preview, CancellationToken ct = default);

    /// <summary>Removes one picture. Removing one that is already gone does nothing.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Removes all of an owner's pictures and says how many there were.</summary>
    Task<int> DeleteForAsync(string ownerKind, long ownerId, CancellationToken ct = default);
}

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

    /// <summary>Pictures kept from the test picture on Models → Images while checking (owner id 0).</summary>
    public const string Test = "test";

    public const int MaxKindLength = 20;
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
            Vae = settings.Vae is { Length: > 0 } vae ? ImageModelCatalog.Normalise(vae) : null,
            Prediction = settings.Prediction,
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
    public ImageModel ModelIn(string imageRoot) => new(
        Path.Combine(imageRoot, ImageFolders.Checkpoints, Checkpoint.Replace('/', Path.DirectorySeparatorChar)),
        Path.Combine(imageRoot, ImageFolders.Loras),
        Vae is { } vae ? Path.Combine(imageRoot, ImageFolders.Vae, vae.Replace('/', Path.DirectorySeparatorChar)) : null,
        Prediction);

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

    /// <summary>An owner's pictures, newest first, without their images (<see cref="Picture.Image"/> is null).</summary>
    Task<IReadOnlyList<Picture>> ListAsync(string ownerKind, long ownerId, CancellationToken ct = default);

    /// <summary>Removes one picture. Removing one that is already gone does nothing.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Removes all of an owner's pictures and says how many there were.</summary>
    Task<int> DeleteForAsync(string ownerKind, long ownerId, CancellationToken ct = default);
}

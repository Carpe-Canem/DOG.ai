using DogAI.Core.Images;

namespace DogAI.Core.Video;

/// <summary>
/// What a kept clip can belong to. The kind is plain text in the database, as for kept pictures. Clips kept in the
/// Video section belong to <see cref="Video"/>; for the other three kinds the database itself removes the clips when
/// their owner is deleted (triggers in the Clips migration), so nothing that is deleted can leave a clip behind.
/// </summary>
public static class ClipOwners
{
    /// <summary>Clips kept in the Video section, with owner id <see cref="VideoOwner"/>. Every Keep adds a row; only Remove takes one away.</summary>
    public const string Video = "video";

    /// <summary>The owner id of every clip kept in the Video section.</summary>
    public const long VideoOwner = 0;

    public const string Message = PictureOwners.Message;
    public const string Character = PictureOwners.Character;
    public const string Persona = PictureOwners.Persona;

    public const int MaxKindLength = PictureOwners.MaxKindLength;
}

/// <summary>
/// A kept clip: the video itself (WebM), a small still of it for lists, the picture it started from when it had one,
/// and everything needed to make it again. Lives in the database's Clips table, so the lock, backups, restore and a
/// portable move cover it like any other row. A clip that was made and not kept never becomes one of these: it exists
/// only in memory.
/// </summary>
/// <remarks>
/// Files are named by their path inside their own folder, never by a full path. The three large values are the last
/// columns of the table, the video last of all (the Clips migration says why; ClipsFaster shows how a column is added).
/// </remarks>
public sealed class Clip
{
    public long Id { get; set; }

    /// <summary>One of <see cref="ClipOwners"/>.</summary>
    public string OwnerKind { get; set; } = "";

    public long OwnerId { get; set; }

    // ---- Everything needed to make it again ----

    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Frames { get; set; }
    public int Fps { get; set; }
    public long Seed { get; set; }
    public int Steps { get; set; }
    public string Sampler { get; set; } = "";
    public double Guidance { get; set; }
    public double FlowShift { get; set; }
    public VideoDecoder Decoder { get; set; }
    public int Compression { get; set; }

    /// <summary>
    /// Made with the choice "Faster" (<see cref="VideoRequest.Faster"/>; M21). It changes the clip's frames, so it is
    /// kept with the clip. False on every clip kept before M21.
    /// </summary>
    public bool Faster { get; set; }

    /// <summary>The model's four files, each as its path inside its own folder (<see cref="VideoFiles"/>).</summary>
    public string Model { get; set; } = "";
    public string Vae { get; set; } = "";
    public string TextEncoder { get; set; } = "";
    public string FastDecoder { get; set; } = "";

    /// <summary>How many bytes <see cref="Video"/> has, known without reading it.</summary>
    public int VideoBytes { get; set; }

    /// <summary>How many bytes <see cref="StartFrame"/> has; 0 for a clip made from the sentence alone.</summary>
    public int StartFrameBytes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// A small WebP of the first frame for lists (the rules of <see cref="PicturePreview"/>), made in the page; null
    /// when none could be made. Never a file.
    /// </summary>
    public byte[]? Still { get; set; }

    /// <summary>The picture the clip started from, as it was sent; null without one, and on clips that came from a list.</summary>
    public byte[]? StartFrame { get; set; }

    /// <summary>The clip as WebM. Null on clips that came from a list, which leaves it out; ask the store for it.</summary>
    public byte[]? Video { get; set; }

    public bool HasStartFrame => StartFrameBytes > 0;

    public VideoFiles Files => new(Model, Vae, TextEncoder, FastDecoder);

    /// <summary>A finished clip, ready to keep. <paramref name="files"/> names the model it was made with.</summary>
    public static Clip Of(string ownerKind, long ownerId, VideoFiles files, VideoRequest request, VideoResult result, byte[]? still = null)
    {
        var named = files.Normalised();
        return new Clip
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            Width = request.Width,
            Height = request.Height,
            Frames = request.Frames,
            Fps = request.Fps,
            Seed = result.Seed,
            Steps = request.Steps,
            Sampler = request.Sampler,
            Guidance = request.Guidance,
            FlowShift = request.FlowShift,
            Decoder = request.Decoder,
            Compression = request.Compression,
            Faster = request.Faster,
            Model = named.Model,
            Vae = named.Vae,
            TextEncoder = named.TextEncoder,
            FastDecoder = named.FastDecoder,
            VideoBytes = result.Webm.Length,
            StartFrameBytes = request.StartFrame?.Length ?? 0,
            Still = still,
            StartFrame = request.StartFrame,
            Video = result.Webm,
        };
    }

    /// <summary>The request that makes this clip again: same seed, settings and first frame. Needs <see cref="StartFrame"/> read when the clip had one.</summary>
    public VideoRequest ToRequest() =>
        new(Prompt, Width, Height, Seed, Decoder, StartFrame, NegativePrompt, Frames, Fps, Steps, Sampler, Guidance, FlowShift, Compression) { Faster = Faster };

    /// <summary>The model to make it again with. <paramref name="videoRoot"/> is the video models folder as it is now.</summary>
    public VideoModel ModelIn(string videoRoot) => VideoFolders.ModelFor(videoRoot, Files);

    /// <summary>What stops this clip from being kept, in plain words, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(OwnerKind) || OwnerKind.Length > ClipOwners.MaxKindLength)
            problems.Add("The video does not say what it belongs to.");
        if (VideoClips.Problem(Video, Width, Height, Frames) is { } video) problems.Add(video);
        // The same rules as a kept picture's preview: a small WebP of the clip's shape with nothing in it but the picture.
        if (Still is not null && PicturePreview.Problem(Still, Width, Height) is not null)
            problems.Add("The still is not a small WebP of the video’s first frame.");
        // A full path would tie the clip to one PC and one folder, and would say where the owner keeps things.
        problems.AddRange(Files.Problems());
        if (Seed < 0) problems.Add("The video’s seed is not known.");
        // The first frame is checked with the request it is part of.
        problems.AddRange(ToRequest().Problems());
        return problems;
    }
}

/// <summary>One page of kept clips, newest first, each with its still and without its video or first frame.</summary>
/// <param name="Total">How many clips the owner has in all, whatever the page.</param>
/// <param name="Skip">How many were passed over to reach this page.</param>
public sealed record ClipPage(IReadOnlyList<Clip> Clips, int Total, int Skip)
{
    public const int PageSize = 24;
    public const int MaxTake = 100;

    /// <summary>True when there are older clips after this page.</summary>
    public bool HasMore => Skip + Clips.Count < Total;

    /// <summary>The skip that gives the page after this one.</summary>
    public int Next => Skip + Clips.Count;
}

/// <summary>
/// Kept clips. The real one is <c>SqliteClipStore</c> (DogAI.Data). Nothing here makes a clip: one is made through
/// <see cref="IVideoEngine"/>, lives in memory, and reaches the disk only through <see cref="AddAsync"/>.
/// </summary>
public interface IClipStore
{
    /// <summary>
    /// Keeps a clip and returns it with its id and time. Throws <see cref="InvalidOperationException"/> with
    /// <see cref="Clip.Problems"/> when it cannot be kept (not a plain WebM, a full path where a file name belongs).
    /// </summary>
    Task<Clip> AddAsync(Clip clip, CancellationToken ct = default);

    /// <summary>The clip with its video, still and first frame, or null when it is gone.</summary>
    Task<Clip?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>The clip's facts and still, without its video or first frame, or null when it is gone.</summary>
    Task<Clip?> FactsAsync(long id, CancellationToken ct = default);

    /// <summary>Only the video (WebM), or null when the clip is gone.</summary>
    Task<byte[]?> VideoAsync(long id, CancellationToken ct = default);

    /// <summary>Only the picture the clip started from, or null when it had none or is gone.</summary>
    Task<byte[]?> StartFrameAsync(long id, CancellationToken ct = default);

    /// <summary>One page of an owner's clips, newest first, each with its still and without its video or first frame.</summary>
    Task<ClipPage> PageAsync(string ownerKind, long ownerId, int skip = 0, int take = ClipPage.PageSize, CancellationToken ct = default);

    /// <summary>
    /// Gives a kept clip its still, or a new one. False when the clip is gone. Throws
    /// <see cref="InvalidOperationException"/> when the bytes cannot be one.
    /// </summary>
    Task<bool> SetStillAsync(long id, byte[] still, CancellationToken ct = default);

    /// <summary>Removes one clip. Removing one that is already gone does nothing.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Removes all of an owner's clips and says how many there were.</summary>
    Task<int> DeleteForAsync(string ownerKind, long ownerId, CancellationToken ct = default);
}

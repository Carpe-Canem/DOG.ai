using DogAI.Core.Video;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

/// <summary>
/// Kept clips (M20), in the Clips table of the one database. Nothing else is written anywhere: no file beside the
/// database, no still cache. So whatever covers the database (the lock, backups, restore, a moved portable folder)
/// covers clips too. A clip reaches the disk only through <see cref="AddAsync"/>, and only as a WebM that passes
/// <see cref="VideoClips.Problem(byte[], int, int, int, int)"/>.
/// </summary>
/// <remarks>
/// The three large values are the last columns, the video last of all: SQLite keeps a large value in a chain of pages
/// after the row and walks the whole chain to reach any column behind it. Lists read every column but the first frame
/// and the video, so they never touch a clip's megabytes. A new column goes before the still (the migration says how).
/// </remarks>
public sealed class SqliteClipStore(IDbContextFactory<DogDbContext> factory, DatabaseTidy? tidy = null) : IClipStore
{
    /// <summary>Every column but the first frame and the video.</summary>
    private static IQueryable<Clip> Listed(IQueryable<Clip> rows) => rows.AsNoTracking().Select(c => new Clip
    {
        Id = c.Id,
        OwnerKind = c.OwnerKind,
        OwnerId = c.OwnerId,
        Prompt = c.Prompt,
        NegativePrompt = c.NegativePrompt,
        Width = c.Width,
        Height = c.Height,
        Frames = c.Frames,
        Fps = c.Fps,
        Seed = c.Seed,
        Steps = c.Steps,
        Sampler = c.Sampler,
        Guidance = c.Guidance,
        FlowShift = c.FlowShift,
        Decoder = c.Decoder,
        Compression = c.Compression,
        Faster = c.Faster,
        Parts = c.Parts,
        Model = c.Model,
        Vae = c.Vae,
        TextEncoder = c.TextEncoder,
        FastDecoder = c.FastDecoder,
        VideoBytes = c.VideoBytes,
        StartFrameBytes = c.StartFrameBytes,
        CreatedAt = c.CreatedAt,
        Still = c.Still,
        StartFrame = null,
        Video = null,
    });

    public async Task<Clip> AddAsync(Clip clip, CancellationToken ct = default)
    {
        if (clip.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        await using var db = await factory.CreateDbContextAsync(ct);
        clip.Id = 0;
        clip.VideoBytes = clip.Video!.Length;
        clip.StartFrameBytes = clip.StartFrame?.Length ?? 0;
        // One spelling per file, as kept pictures name theirs.
        var files = clip.Files.Normalised();
        (clip.Model, clip.Vae, clip.TextEncoder, clip.FastDecoder) = (files.Model, files.Vae, files.TextEncoder, files.FastDecoder);
        if (clip.CreatedAt == default) clip.CreatedAt = DateTime.UtcNow;
        db.Clips.Add(clip);
        await db.SaveChangesAsync(ct);
        return clip;
    }

    public async Task<Clip?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Clips.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Clip?> FactsAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Listed(db.Clips.Where(c => c.Id == id)).FirstOrDefaultAsync(ct);
    }

    public async Task<byte[]?> VideoAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Clips.AsNoTracking().Where(c => c.Id == id).Select(c => c.Video).FirstOrDefaultAsync(ct);
    }

    public async Task<byte[]?> StartFrameAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Clips.AsNoTracking().Where(c => c.Id == id).Select(c => c.StartFrame).FirstOrDefaultAsync(ct);
    }

    public async Task<ClipPage> PageAsync(string ownerKind, long ownerId, int skip = 0, int take = ClipPage.PageSize, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var found = db.Clips.Where(c => c.OwnerKind == ownerKind && c.OwnerId == ownerId);
        var total = await found.CountAsync(ct);
        skip = Math.Max(0, skip);
        if (skip >= total) return new ClipPage([], total, skip);
        var clips = await Listed(found.OrderByDescending(c => c.Id).Skip(skip).Take(Math.Clamp(take, 1, ClipPage.MaxTake))).ToListAsync(ct);
        return new ClipPage(clips, total, skip);
    }

    public async Task<bool> SetStillAsync(long id, byte[] still, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var size = await db.Clips.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.Width, c.Height, Had = c.Still != null }).FirstOrDefaultAsync(ct);
        if (size is null) return false;
        if (DogAI.Core.Images.PicturePreview.Problem(still, size.Width, size.Height) is not null)
            throw new InvalidOperationException("The still is not a small WebP of the video’s first frame.");
        var changed = await db.Clips.Where(c => c.Id == id).ExecuteUpdateAsync(set => set.SetProperty(c => c.Still, still), ct);
        // A still that was replaced is removed data like any other: the log file beside the database is emptied soon.
        if (changed > 0 && size.Had) tidy?.Deleted();
        return changed > 0;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Clips.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteForAsync(string ownerKind, long ownerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Clips.Where(c => c.OwnerKind == ownerKind && c.OwnerId == ownerId).ExecuteDeleteAsync(ct);
    }
}

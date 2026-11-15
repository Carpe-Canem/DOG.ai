using DogAI.Core.Images;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

/// <summary>
/// Kept pictures, in the Pictures table of the one database. Nothing else is written anywhere: no file beside the
/// database, no thumbnail cache. So whatever covers the database (the lock, backups, restore, a moved portable folder)
/// covers pictures too.
/// </summary>
/// <remarks>
/// The <c>PicturePreviews</c> migration put the image last in the row. SQLite keeps a large value in a chain of pages
/// after the row and has to walk the whole chain to reach any column behind it, so with the image in the middle every
/// list, count and search read each picture in full. Keep it last: a new column goes before it (the migration says how).
/// </remarks>
public sealed class SqlitePictureStore(IDbContextFactory<DogDbContext> factory, DatabaseTidy? tidy = null) : IPictureStore
{
    public async Task<PicturePage> PageAsync(PictureQuery query, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var found = db.Pictures.AsNoTracking().Where(p => p.OwnerKind == query.OwnerKind && p.OwnerId == query.OwnerId);
        foreach (var word in PictureQuery.WordsOf(query.Words))
        {
            // The typed word as it stands: its own % and _ are letters here, not wildcards.
            var pattern = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            found = found.Where(p => EF.Functions.Like(p.Prompt, pattern, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(query.Checkpoint))
        {
            var checkpoint = ImageModelCatalog.Normalise(query.Checkpoint);
            found = found.Where(p => p.Checkpoint == checkpoint);
        }
        found = query.Shape switch
        {
            PictureShape.Tall => found.Where(p => p.ImageHeight > p.ImageWidth),
            PictureShape.Wide => found.Where(p => p.ImageWidth > p.ImageHeight),
            PictureShape.Square => found.Where(p => p.ImageWidth == p.ImageHeight),
            _ => found,
        };

        var total = await found.CountAsync(ct);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, PictureQuery.MaxTake);
        if (skip >= total) return new PicturePage([], total, skip);
        // Every column but the image: the preview is what a grid shows.
        var pictures = await found.OrderByDescending(p => p.Id).Skip(skip).Take(take)
            .Select(p => new Picture
            {
                Id = p.Id,
                OwnerKind = p.OwnerKind,
                OwnerId = p.OwnerId,
                Image = null,
                ImageWidth = p.ImageWidth,
                ImageHeight = p.ImageHeight,
                ImageBytes = p.ImageBytes,
                Preview = p.Preview,
                Checkpoint = p.Checkpoint,
                Vae = p.Vae,
                Prediction = p.Prediction,
                Prompt = p.Prompt,
                NegativePrompt = p.NegativePrompt,
                Width = p.Width,
                Height = p.Height,
                Seed = p.Seed,
                Steps = p.Steps,
                Sampler = p.Sampler,
                Scheduler = p.Scheduler,
                Guidance = p.Guidance,
                ClipSkip = p.ClipSkip,
                Loras = p.Loras,
                Hires = p.Hires,
                CreatedAt = p.CreatedAt,
            })
            .ToListAsync(ct);
        return new PicturePage(pictures, total, skip);
    }

    public async Task<IReadOnlyList<string>> CheckpointsAsync(string ownerKind, long ownerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var names = await db.Pictures.AsNoTracking().Where(p => p.OwnerKind == ownerKind && p.OwnerId == ownerId)
            .Select(p => p.Checkpoint).Distinct().ToListAsync(ct);
        return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<byte[]?> PreviewAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Pictures.AsNoTracking().Where(p => p.Id == id).Select(p => p.Preview).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SetPreviewAsync(long id, byte[] preview, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var size = await db.Pictures.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new { p.ImageWidth, p.ImageHeight, Had = p.Preview != null }).FirstOrDefaultAsync(ct);
        if (size is null) return false;
        if (PicturePreview.Problem(preview, size.ImageWidth, size.ImageHeight) is { } problem) throw new InvalidOperationException(problem);
        var changed = await db.Pictures.Where(p => p.Id == id).ExecuteUpdateAsync(set => set.SetProperty(p => p.Preview, preview), ct);
        // A preview that was replaced is removed data like any other: the log file beside the database is emptied soon.
        if (changed > 0 && size.Had) tidy?.Deleted();
        return changed > 0;
    }

    public async Task<Picture> AddAsync(Picture picture, CancellationToken ct = default)
    {
        if (picture.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        await using var db = await factory.CreateDbContextAsync(ct);
        picture.Id = 0;
        picture.ImageBytes = picture.Image!.Length;
        // One spelling per file, so a filter by checkpoint finds every picture drawn with it.
        picture.Checkpoint = ImageModelCatalog.Normalise(picture.Checkpoint);
        if (picture.Vae is not null) picture.Vae = ImageModelCatalog.Normalise(picture.Vae);
        picture.Loras = [.. picture.Loras.Select(l => l with { Path = ImageModelCatalog.Normalise(l.Path) })];
        if (picture.CreatedAt == default) picture.CreatedAt = DateTime.UtcNow;
        db.Pictures.Add(picture);
        await db.SaveChangesAsync(ct);
        return picture;
    }

    public async Task<Picture?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Pictures.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<byte[]?> ImageAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Pictures.AsNoTracking().Where(p => p.Id == id).Select(p => p.Image).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Picture>> ListAsync(string ownerKind, long ownerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Every column but the image, so a list of pictures does not read megabytes.
        return await db.Pictures.AsNoTracking()
            .Where(p => p.OwnerKind == ownerKind && p.OwnerId == ownerId)
            .OrderByDescending(p => p.Id)
            .Select(p => new Picture
            {
                Id = p.Id,
                OwnerKind = p.OwnerKind,
                OwnerId = p.OwnerId,
                Image = null,
                ImageWidth = p.ImageWidth,
                ImageHeight = p.ImageHeight,
                ImageBytes = p.ImageBytes,
                Checkpoint = p.Checkpoint,
                Vae = p.Vae,
                Prediction = p.Prediction,
                Prompt = p.Prompt,
                NegativePrompt = p.NegativePrompt,
                Width = p.Width,
                Height = p.Height,
                Seed = p.Seed,
                Steps = p.Steps,
                Sampler = p.Sampler,
                Scheduler = p.Scheduler,
                Guidance = p.Guidance,
                ClipSkip = p.ClipSkip,
                Loras = p.Loras,
                Hires = p.Hires,
                CreatedAt = p.CreatedAt,
            })
            .ToListAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Pictures.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteForAsync(string ownerKind, long ownerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Pictures.Where(p => p.OwnerKind == ownerKind && p.OwnerId == ownerId).ExecuteDeleteAsync(ct);
    }
}

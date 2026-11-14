using DogAI.Core.Images;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

/// <summary>
/// Kept pictures, in the Pictures table of the one database. Nothing else is written anywhere: no file beside the
/// database, no thumbnail cache. So whatever covers the database (the lock, backups, restore, a moved portable folder)
/// covers pictures too.
/// </summary>
public sealed class SqlitePictureStore(IDbContextFactory<DogDbContext> factory) : IPictureStore
{
    public async Task<Picture> AddAsync(Picture picture, CancellationToken ct = default)
    {
        if (picture.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        await using var db = await factory.CreateDbContextAsync(ct);
        picture.Id = 0;
        picture.ImageBytes = picture.Image!.Length;
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

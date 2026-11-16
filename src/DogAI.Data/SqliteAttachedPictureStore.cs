using DogAI.Core.Chat;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

/// <summary>
/// The pictures on chat messages (M17), in their own table. A picture reaches the disk only through
/// <see cref="AddAsync"/> or <see cref="CopyAsync"/>, and only as a JPEG that passes
/// <see cref="AttachedPictures.Problem"/>. Rows go with their message (a foreign key that cascades).
/// </summary>
public sealed class SqliteAttachedPictureStore(IDbContextFactory<DogDbContext> factory) : IAttachedPictureStore
{
    /// <summary>Rows without their images: the image column is never read for a list.</summary>
    internal static IQueryable<AttachedPicture> Listed(IQueryable<AttachedPicture> rows) => rows.AsNoTracking().Select(p => new AttachedPicture
    {
        Id = p.Id,
        MessageId = p.MessageId,
        ConversationId = p.ConversationId,
        Position = p.Position,
        Width = p.Width,
        Height = p.Height,
        Bytes = p.Bytes,
        CreatedAt = p.CreatedAt,
    });

    public async Task<IReadOnlyList<AttachedPicture>> AddAsync(long messageId, IReadOnlyList<byte[]> jpegs, CancellationToken ct = default)
    {
        if (jpegs.Count == 0) return [];
        foreach (var jpeg in jpegs)
            if (AttachedPictures.Problem(jpeg) is { } problem) throw new InvalidOperationException(problem);

        await using var db = await factory.CreateDbContextAsync(ct);
        var message = await db.Messages.AsNoTracking().Where(m => m.Id == messageId).Select(m => new { m.ConversationId }).FirstOrDefaultAsync(ct)
                      ?? throw new InvalidOperationException("That message no longer exists.");
        var has = await db.AttachedPictures.Where(p => p.MessageId == messageId).Select(p => (int?)p.Position).ToListAsync(ct);
        if (has.Count + jpegs.Count > AttachedPictures.MaxPerMessage)
            throw new InvalidOperationException($"A message takes at most {AttachedPictures.MaxPerMessage} pictures.");

        var position = has.Count == 0 ? 0 : has.Max()!.Value + 1;
        var now = DateTime.UtcNow;
        var rows = new List<AttachedPicture>();
        foreach (var jpeg in jpegs)
        {
            var (width, height) = AttachedPictures.SizeOf(jpeg);
            rows.Add(new AttachedPicture
            {
                MessageId = messageId,
                ConversationId = message.ConversationId,
                Position = position++,
                Width = width,
                Height = height,
                Bytes = jpeg.Length,
                CreatedAt = now,
                Image = jpeg,
            });
        }
        db.AttachedPictures.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Select(Listing).ToList();
    }

    public async Task<IReadOnlyList<AttachedPicture>> CopyAsync(long fromMessageId, long toMessageId, IReadOnlyCollection<long>? only = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AttachedPictures.AsNoTracking().Where(p => p.MessageId == fromMessageId).OrderBy(p => p.Position).ThenBy(p => p.Id).ToListAsync(ct);
        var jpegs = rows.Where(p => p.Image is not null && (only is null || only.Contains(p.Id))).Select(p => p.Image!).ToList();
        // Through AddAsync: the same check, the same count, and the very bytes (the engine knows a picture by them).
        return await AddAsync(toMessageId, jpegs, ct);
    }

    public async Task<AttachedPicture?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AttachedPictures.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<IReadOnlyDictionary<long, byte[]>> ImagesAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<long, byte[]>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AttachedPictures.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Image }).ToListAsync(ct);
        return rows.Where(r => r.Image is not null).ToDictionary(r => r.Id, r => r.Image!);
    }

    private static AttachedPicture Listing(AttachedPicture row) => new()
    {
        Id = row.Id,
        MessageId = row.MessageId,
        ConversationId = row.ConversationId,
        Position = row.Position,
        Width = row.Width,
        Height = row.Height,
        Bytes = row.Bytes,
        CreatedAt = row.CreatedAt,
    };
}

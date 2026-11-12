using DogAI.Core.Stories;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

public sealed class SqliteStoryStore(IDbContextFactory<DogDbContext> factory) : IStoryStore
{
    public async Task<IReadOnlyList<Story>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Stories.AsNoTracking().ToListAsync(ct);
    }

    public async Task<Story?> GetAsync(long conversationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Stories.AsNoTracking().FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);
    }

    public async Task SaveAsync(Story story, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Stories.FirstOrDefaultAsync(s => s.ConversationId == story.ConversationId, ct);
        if (existing is null)
        {
            story.Id = 0;
            db.Stories.Add(story);
        }
        else
        {
            story.Id = existing.Id;
            db.Entry(existing).CurrentValues.SetValues(story);
        }
        await db.SaveChangesAsync(ct);
    }
}

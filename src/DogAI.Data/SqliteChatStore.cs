using DogAI.Core.Chat;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

public sealed class SqliteChatStore(IDbContextFactory<DogDbContext> factory) : IChatStore
{
    public async Task<IReadOnlyList<Conversation>> ListAsync(string mode, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AsNoTracking()
            .Where(c => c.Mode == mode)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<Conversation?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        conversation.CreatedAt = conversation.UpdatedAt = DateTime.UtcNow;
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task UpdateAsync(Conversation conversation, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == conversation.Id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Title, conversation.Title)
            .SetProperty(c => c.SystemPrompt, conversation.SystemPrompt)
            .SetProperty(c => c.PresetId, conversation.PresetId)
            .SetProperty(c => c.PersonaId, conversation.PersonaId)
            .SetProperty(c => c.AuthorsNote, conversation.AuthorsNote)
            .SetProperty(c => c.AuthorsNoteDepth, conversation.AuthorsNoteDepth)
            .SetProperty(c => c.MemoryEnabled, conversation.MemoryEnabled), ct);
    }

    public async Task SetSummaryAsync(long conversationId, string summary, long? throughId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Summary, summary)
            .SetProperty(c => c.SummaryThroughId, throughId), ct);
    }

    public async Task SetActiveLeafAsync(long conversationId, long? leafId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.ActiveLeafId, leafId)
            .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(long conversationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        message.CreatedAt = DateTime.UtcNow;
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task UpdateMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages.Where(m => m.Id == message.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Content, message.Content)
            .SetProperty(m => m.Reasoning, message.Reasoning)
            .SetProperty(m => m.PromptText, message.PromptText)
            .SetProperty(m => m.ModelName, message.ModelName)
            .SetProperty(m => m.PresetId, message.PresetId)
            .SetProperty(m => m.PromptTokens, message.PromptTokens)
            .SetProperty(m => m.CompletionTokens, message.CompletionTokens)
            .SetProperty(m => m.TokensPerSecond, message.TokensPerSecond)
            .SetProperty(m => m.TrimmedMessages, message.TrimmedMessages)
            .SetProperty(m => m.LoreUsed, message.LoreUsed)
            .SetProperty(m => m.MemoryUsed, message.MemoryUsed), ct);
    }

    public async Task<IReadOnlyList<Conversation>> ListForCharacterAsync(long characterId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AsNoTracking()
            .Where(c => c.CharacterId == characterId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }
}

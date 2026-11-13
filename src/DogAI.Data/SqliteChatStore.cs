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
            .OrderByDescending(c => c.Pinned)
            .ThenByDescending(c => c.UpdatedAt)
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
            .SetProperty(c => c.MemoryEnabled, conversation.MemoryEnabled)
            .SetProperty(c => c.RecallEnabled, conversation.RecallEnabled)
            .SetProperty(c => c.TurnOrder, conversation.TurnOrder), ct);
    }

    public async Task SetSummaryAsync(long conversationId, string summary, long? throughId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Summary, summary)
            .SetProperty(c => c.SummaryThroughId, throughId), ct);
    }

    public async Task SetPinnedAsync(long conversationId, bool pinned, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Pinned, pinned), ct);
    }

    public async Task SetFolderAsync(long conversationId, long? folderId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.FolderId, folderId), ct);
    }

    public async Task<IReadOnlyList<ChatFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folders = await db.ChatFolders.AsNoTracking().ToListAsync(ct);
        // Sorted here rather than in SQL so names compare the way people read them, not by byte.
        return folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Id).ToList();
    }

    public async Task<ChatFolder> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        var clean = ChatFolders.CleanName(name) ?? throw new ArgumentException("A folder needs a name.", nameof(name));
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = new ChatFolder { Name = clean, CreatedAt = DateTime.UtcNow };
        db.ChatFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task RenameFolderAsync(long folderId, string name, CancellationToken ct = default)
    {
        var clean = ChatFolders.CleanName(name) ?? throw new ArgumentException("A folder needs a name.", nameof(name));
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.ChatFolders.Where(f => f.Id == folderId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, clean), ct);
    }

    public async Task SetFolderCollapsedAsync(long folderId, bool collapsed, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.ChatFolders.Where(f => f.Id == folderId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Collapsed, collapsed), ct);
    }

    public async Task DeleteFolderAsync(long folderId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Conversations.Where(c => c.FolderId == folderId).ExecuteUpdateAsync(s => s.SetProperty(c => c.FolderId, (long?)null), ct);
        await db.ChatFolders.Where(f => f.Id == folderId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
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
        // The chat's own documents and their passages, its links to shared collections, and its message vectors.
        var own = db.Collections.Where(c => c.ConversationId == id).Select(c => c.Id);
        var docs = db.Documents.Where(d => own.Contains(d.CollectionId)).Select(d => d.Id);
        await db.Chunks.Where(c => docs.Contains(c.DocumentId)).ExecuteDeleteAsync(ct);
        await db.Documents.Where(d => own.Contains(d.CollectionId)).ExecuteDeleteAsync(ct);
        await db.ChatCollections.Where(l => l.ConversationId == id || own.Contains(l.CollectionId)).ExecuteDeleteAsync(ct);
        await db.Collections.Where(c => c.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.MessageVectors.Where(v => v.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.ChatMembers.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Stories.Where(s => s.ConversationId == id).ExecuteDeleteAsync(ct);
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
            .SetProperty(m => m.MemoryUsed, message.MemoryUsed)
            .SetProperty(m => m.ExtraJson, message.ExtraJson)
            .SetProperty(m => m.SourcesJson, message.SourcesJson), ct);
    }

    public async Task SetMessageExtraAsync(long messageId, string? extraJson, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.ExtraJson, extraJson), ct);
    }

    public async Task<IReadOnlyList<Conversation>> ListForCharacterAsync(long characterId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AsNoTracking()
            .Where(c => c.CharacterId == characterId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChatMember>> GetMembersAsync(long conversationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ChatMembers.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Position).ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task SetMembersAsync(long conversationId, IReadOnlyList<ChatMember> members, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ChatMembers.Where(m => m.ConversationId == conversationId).ExecuteDeleteAsync(ct);
        var position = 0;
        foreach (var member in members.DistinctBy(m => m.CharacterId))
        {
            member.Id = 0;
            member.ConversationId = conversationId;
            member.Position = position++;
            db.ChatMembers.Add(member);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

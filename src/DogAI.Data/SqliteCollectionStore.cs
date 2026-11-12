using DogAI.Core.Documents;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

public sealed class SqliteCollectionStore(IDbContextFactory<DogDbContext> factory) : ICollectionStore
{
    public async Task<IReadOnlyList<DocumentCollection>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Collections.AsNoTracking().Where(c => c.ConversationId == null).ToListAsync(ct);
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<DocumentCollection?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<DocumentCollection> CreateAsync(DocumentCollection collection, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        collection.Id = 0;
        collection.CreatedAt = collection.UpdatedAt = DateTime.UtcNow;
        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);
        return collection;
    }

    public async Task UpdateAsync(DocumentCollection collection, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        await db.Collections.Where(c => c.Id == collection.Id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Name, collection.Name)
            .SetProperty(c => c.Description, collection.Description)
            .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var docs = db.Documents.Where(d => d.CollectionId == id).Select(d => d.Id);
        await db.Chunks.Where(c => docs.Contains(c.DocumentId)).ExecuteDeleteAsync(ct);
        await db.Documents.Where(d => d.CollectionId == id).ExecuteDeleteAsync(ct);
        await db.ChatCollections.Where(l => l.CollectionId == id).ExecuteDeleteAsync(ct);
        await db.Collections.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<DocumentCollection?> ForConversationAsync(long conversationId, bool create, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var own = await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);
        if (own is not null || !create) return own;
        own = new DocumentCollection { Name = "This chat's files", ConversationId = conversationId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Collections.Add(own);
        await db.SaveChangesAsync(ct);
        return own;
    }

    public async Task<IReadOnlyList<long>> AttachedAsync(long conversationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ChatCollections.AsNoTracking().Where(l => l.ConversationId == conversationId)
            .OrderBy(l => l.Id).Select(l => l.CollectionId).ToListAsync(ct);
    }

    public async Task SetAttachedAsync(long conversationId, IReadOnlyList<long> collectionIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ChatCollections.Where(l => l.ConversationId == conversationId).ExecuteDeleteAsync(ct);
        foreach (var id in collectionIds.Distinct())
            db.ChatCollections.Add(new ChatCollection { ConversationId = conversationId, CollectionId = id });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentSummary>> DocumentsAsync(long collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking().Where(d => d.CollectionId == collectionId).OrderBy(d => d.Id)
            .Select(d => new DocumentSummary(d.Id, d.CollectionId, d.FileName, d.Kind, d.SizeBytes, d.PageCount, d.ChunkCount, d.Status, d.Error,
                d.VectorModel, d.SourcePath, d.AddedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, int>> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking().GroupBy(d => d.CollectionId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<SourceDocument?> GetDocumentAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
    }

    public async Task<bool> HasFileAsync(long collectionId, string sha256, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Documents.AnyAsync(d => d.CollectionId == collectionId && d.Sha256 == sha256, ct);
    }

    public async Task<SourceDocument> AddDocumentAsync(SourceDocument document, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        document.Id = 0;
        document.ChunkCount = chunks.Count;
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);
        foreach (var chunk in chunks)
        {
            chunk.Id = 0;
            chunk.DocumentId = document.Id;
            db.Chunks.Add(chunk);
        }
        await db.SaveChangesAsync(ct);
        var now = DateTime.UtcNow;
        await db.Collections.Where(c => c.Id == document.CollectionId).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now), ct);
        await tx.CommitAsync(ct);
        return document;
    }

    public async Task SetStatusAsync(long documentId, DocumentStatus status, string? error, string? vectorModel, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Documents.Where(d => d.Id == documentId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, status)
            .SetProperty(d => d.Error, error)
            .SetProperty(d => d.VectorModel, vectorModel), ct);
    }

    public async Task DeleteDocumentAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);
        await db.Documents.Where(d => d.Id == documentId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<long>> NeedingIndexAsync(string? model, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Failed
                        && db.Chunks.Any(c => c.DocumentId == d.Id && (c.VectorModel == null || c.VectorModel != model)))
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentChunk>> ChunksToIndexAsync(long documentId, string model, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == documentId && (c.VectorModel == null || c.VectorModel != model))
            .OrderBy(c => c.Ordinal).ToListAsync(ct);
    }

    public async Task SetVectorsAsync(IReadOnlyList<(long ChunkId, byte[] Vector)> vectors, string model, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var (id, vector) in vectors)
            await db.Chunks.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Vector, vector)
                .SetProperty(c => c.VectorModel, model), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<IndexedChunk>> IndexedAsync(IReadOnlyList<long> collectionIds, string model, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => c.VectorModel == model && c.Vector != null
                        && db.Documents.Any(d => d.Id == c.DocumentId && collectionIds.Contains(d.CollectionId)))
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.DocumentId, c.Text, c.Vector })
            .ToListAsync(ct);
        return rows.Select(r => new IndexedChunk(r.Id, r.DocumentId, r.Text, VectorMath.FromBytes(r.Vector!))).ToList();
    }

    public async Task<IReadOnlyList<DocumentChunk>> ChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Vectors are not needed by callers; leave them out.
        var rows = await db.Chunks.AsNoTracking().Where(c => chunkIds.Contains(c.Id))
            .Select(c => new DocumentChunk
            {
                Id = c.Id, DocumentId = c.DocumentId, Ordinal = c.Ordinal, Start = c.Start, Length = c.Length,
                Page = c.Page, Heading = c.Heading, Text = c.Text, VectorModel = c.VectorModel,
            })
            .ToListAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyList<MessageVector>> MessageVectorsAsync(long conversationId, string model, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MessageVectors.AsNoTracking().Where(v => v.ConversationId == conversationId && v.Model == model).ToListAsync(ct);
    }

    public async Task SaveMessageVectorsAsync(IReadOnlyList<MessageVector> vectors, CancellationToken ct = default)
    {
        if (vectors.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = vectors.Select(v => v.MessageId).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.MessageVectors.Where(v => ids.Contains(v.MessageId)).ExecuteDeleteAsync(ct);
        db.MessageVectors.AddRange(vectors);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

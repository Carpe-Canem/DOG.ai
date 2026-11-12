namespace DogAI.Core.Documents;

/// <summary>
/// A named set of documents that chats can search. A chat's own files live in a collection that belongs to that chat
/// (<see cref="ConversationId"/> set); those are not listed with the shared ones and go when the chat is deleted.
/// </summary>
public sealed class DocumentCollection
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The chat whose own files these are. Null for a shared collection.</summary>
    public long? ConversationId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsPrivate => ConversationId is not null;
}

public enum DocumentStatus
{
    /// <summary>Read and split, but not yet searchable (no document search model yet, or waiting its turn).</summary>
    Waiting = 0,
    Indexing = 1,
    Ready = 2,
    Failed = 3,
}

/// <summary>One file added to a collection. The extracted text is kept, so passages can be shown even if the file moves.</summary>
public sealed class SourceDocument
{
    public long Id { get; set; }
    public long CollectionId { get; set; }
    public string FileName { get; set; } = "";

    /// <summary>Where the file was when it was added. Only used to open the original.</summary>
    public string? SourcePath { get; set; }

    /// <summary>"pdf", "docx", "markdown", "code" or "text".</summary>
    public string Kind { get; set; } = "text";

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file, so the same file is not added to a collection twice.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>All the text read from the file. Chunks point into it.</summary>
    public string Text { get; set; } = "";

    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public DocumentStatus Status { get; set; }

    /// <summary>Why reading or indexing failed, in plain words.</summary>
    public string? Error { get; set; }

    /// <summary>The document search model its passages were indexed with. Null until indexed.</summary>
    public string? VectorModel { get; set; }

    public DateTime AddedAt { get; set; }
}

/// <summary>A passage of a document: the unit that is searched and quoted.</summary>
public sealed class DocumentChunk
{
    public long Id { get; set; }
    public long DocumentId { get; set; }

    /// <summary>Position within the document, from 0.</summary>
    public int Ordinal { get; set; }

    /// <summary>Where the passage sits in <see cref="SourceDocument.Text"/>.</summary>
    public int Start { get; set; }
    public int Length { get; set; }

    /// <summary>PDF page number (from 1). Null for other kinds.</summary>
    public int? Page { get; set; }

    /// <summary>The heading the passage falls under, when the document has headings.</summary>
    public string? Heading { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Normalised embedding as little-endian floats. Null until indexed.</summary>
    public byte[]? Vector { get; set; }
    public string? VectorModel { get; set; }
}

/// <summary>Which collections a chat (or story) searches.</summary>
public sealed class ChatCollection
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public long CollectionId { get; set; }
}

/// <summary>A chat message's embedding, for recalling relevant earlier messages in long chats.</summary>
public sealed class MessageVector
{
    public long MessageId { get; set; }
    public long ConversationId { get; set; }

    /// <summary>Stable hash of the text that was embedded; an edited message is embedded again.</summary>
    public long ContentHash { get; set; }

    public string Model { get; set; } = "";
    public byte[] Vector { get; set; } = [];
}

/// <summary>A document as listed (without its text).</summary>
public sealed record DocumentSummary(long Id, long CollectionId, string FileName, string Kind, long SizeBytes, int PageCount, int ChunkCount,
    DocumentStatus Status, string? Error, string? VectorModel, string? SourcePath, DateTime AddedAt);

/// <summary>A passage with its vector, held in memory for searching.</summary>
public sealed record IndexedChunk(long ChunkId, long DocumentId, string Text, float[] Vector);

public interface ICollectionStore
{
    /// <summary>Shared collections (not chats' own), by name.</summary>
    Task<IReadOnlyList<DocumentCollection>> ListAsync(CancellationToken ct = default);
    Task<DocumentCollection?> GetAsync(long id, CancellationToken ct = default);
    Task<DocumentCollection> CreateAsync(DocumentCollection collection, CancellationToken ct = default);
    Task UpdateAsync(DocumentCollection collection, CancellationToken ct = default);

    /// <summary>Deletes the collection with its documents, and detaches it from chats.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>The collection holding a chat's own files; created when <paramref name="create"/> and missing.</summary>
    Task<DocumentCollection?> ForConversationAsync(long conversationId, bool create, CancellationToken ct = default);

    /// <summary>Shared collections a chat searches (its own files are always searched as well).</summary>
    Task<IReadOnlyList<long>> AttachedAsync(long conversationId, CancellationToken ct = default);
    Task SetAttachedAsync(long conversationId, IReadOnlyList<long> collectionIds, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentSummary>> DocumentsAsync(long collectionId, CancellationToken ct = default);

    /// <summary>Document counts per collection, for lists.</summary>
    Task<IReadOnlyDictionary<long, int>> CountsAsync(CancellationToken ct = default);
    Task<SourceDocument?> GetDocumentAsync(long documentId, CancellationToken ct = default);
    Task<bool> HasFileAsync(long collectionId, string sha256, CancellationToken ct = default);

    /// <summary>Saves a new document with its passages (without vectors).</summary>
    Task<SourceDocument> AddDocumentAsync(SourceDocument document, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default);
    Task SetStatusAsync(long documentId, DocumentStatus status, string? error, string? vectorModel, CancellationToken ct = default);
    Task DeleteDocumentAsync(long documentId, CancellationToken ct = default);

    /// <summary>Documents whose passages are not all indexed with <paramref name="model"/> (or not at all), oldest first.</summary>
    Task<IReadOnlyList<long>> NeedingIndexAsync(string? model, CancellationToken ct = default);

    /// <summary>A document's passages that have no vector for <paramref name="model"/>.</summary>
    Task<IReadOnlyList<DocumentChunk>> ChunksToIndexAsync(long documentId, string model, CancellationToken ct = default);
    Task SetVectorsAsync(IReadOnlyList<(long ChunkId, byte[] Vector)> vectors, string model, CancellationToken ct = default);

    /// <summary>Indexed passages of the given collections, for <paramref name="model"/>.</summary>
    Task<IReadOnlyList<IndexedChunk>> IndexedAsync(IReadOnlyList<long> collectionIds, string model, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentChunk>> ChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken ct = default);

    Task<IReadOnlyList<MessageVector>> MessageVectorsAsync(long conversationId, string model, CancellationToken ct = default);
    Task SaveMessageVectorsAsync(IReadOnlyList<MessageVector> vectors, CancellationToken ct = default);
}

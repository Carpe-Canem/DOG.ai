using System.Security.Cryptography;
using DogAI.Core.Chat;

namespace DogAI.Core.Documents;

/// <summary>A passage found for a prompt, with what is needed to cite it.</summary>
public sealed record FoundPassage(DocumentChunk Chunk, string FileName, float Similarity);

/// <summary>Which document is being indexed and how far along it is.</summary>
public sealed record IndexProgress(long DocumentId, string FileName, int Done, int Total);

/// <summary>
/// Documents and their search: adding files (read, cut into passages, saved), indexing passages with the document
/// search model in the background, and finding passages (and, for long chats, earlier messages) that match a query.
/// Searches use an in-memory copy of each collection's vectors, rebuilt when the collection changes.
/// </summary>
public sealed class DocumentLibrary(ICollectionStore store, IEmbedder embedder)
{
    private const int BatchSize = 16;
    private const int MessageBatchSize = 32;

    /// <summary>The longest piece of a message that is embedded for recall.</summary>
    private const int MessageEmbedChars = 1500;

    private readonly object _lock = new();
    private readonly Dictionary<(long Collection, string Model), ChunkIndex> _indexes = [];
    private Task? _loop;
    private bool _again;

    public ICollectionStore Store => store;

    /// <summary>Raised when documents are added, indexed or removed, and as indexing progresses. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>The document being indexed now, if any.</summary>
    public IndexProgress? Progress { get; private set; }

    /// <summary>Why waiting documents are not being indexed (for example, no document search model), in plain words. Null when fine.</summary>
    public string? Problem { get; private set; }

    public bool IsIndexing => _loop is { IsCompleted: false };

    public Task<string?> ModelIdAsync(CancellationToken ct = default) => embedder.ModelIdAsync(ct);

    /// <summary>
    /// Reads a file, cuts it into passages and saves it in the collection, then starts indexing in the background.
    /// Returns null when the same file is already in the collection.
    /// </summary>
    /// <exception cref="DocumentReadException">The file cannot be read as a document.</exception>
    public async Task<SourceDocument?> AddFileAsync(long collectionId, string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new DocumentReadException("The file is not there any more.");
        if (info.Length > TextExtractor.MaxFileBytes) throw new DocumentReadException("It is larger than 200 MB, which is more than DOG AI reads.");
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DocumentReadException("The file could not be opened: " + e.Message);
        }
        return await AddAsync(collectionId, info.Name, bytes, path, ct);
    }

    /// <inheritdoc cref="AddFileAsync"/>
    public async Task<SourceDocument?> AddAsync(long collectionId, string fileName, byte[] bytes, string? sourcePath = null, CancellationToken ct = default)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (await store.HasFileAsync(collectionId, sha, ct)) return null;
        // A file that cannot be read is reported back to this method rather than thrown on the worker thread, so
        // the debugger does not stop on an expected, handled problem ("not handled in user code").
        var (extracted, chunks, problem) = await Task.Run(() =>
        {
            try
            {
                var text = TextExtractor.Read(fileName, bytes);
                return (text, Chunker.Split(text), (string?)null);
            }
            catch (DocumentReadException e)
            {
                return (null!, (IReadOnlyList<Chunk>)[], e.Message);
            }
        }, ct);
        if (problem is not null) throw new DocumentReadException(problem);
        var document = await store.AddDocumentAsync(new SourceDocument
        {
            CollectionId = collectionId,
            FileName = fileName,
            SourcePath = sourcePath,
            Kind = extracted.Kind,
            SizeBytes = bytes.LongLength,
            Sha256 = sha,
            Text = extracted.Text,
            PageCount = extracted.PageStarts.Count,
            Status = DocumentStatus.Waiting,
            AddedAt = DateTime.UtcNow,
        }, chunks.Select((c, i) => new DocumentChunk
        {
            Ordinal = i,
            Start = c.Start,
            Length = c.Length,
            Page = c.Page,
            Heading = c.Heading,
            Text = c.Text,
        }).ToList(), ct);
        Changed?.Invoke();
        StartIndexing();
        return document;
    }

    public async Task DeleteDocumentAsync(long documentId, CancellationToken ct = default)
    {
        var doc = await store.GetDocumentAsync(documentId, ct);
        await store.DeleteDocumentAsync(documentId, ct);
        if (doc is not null) Forget(doc.CollectionId);
        Changed?.Invoke();
    }

    public async Task DeleteCollectionAsync(long collectionId, CancellationToken ct = default)
    {
        await store.DeleteAsync(collectionId, ct);
        Forget(collectionId);
        Changed?.Invoke();
    }

    /// <summary>Marks a document that failed to index as waiting, and tries again.</summary>
    public async Task RetryAsync(long documentId, CancellationToken ct = default)
    {
        await store.SetStatusAsync(documentId, DocumentStatus.Waiting, null, null, ct);
        Changed?.Invoke();
        StartIndexing();
    }

    /// <summary>
    /// Indexes, in the background, every document not yet indexed with the current document search model (new ones, and
    /// all of them after the model changes). Does nothing if that is already running, except make it look again when done.
    /// </summary>
    public void StartIndexing()
    {
        lock (_lock)
        {
            if (_loop is { IsCompleted: false })
            {
                _again = true;
                return;
            }
            _again = false;
            _loop = Task.Run(IndexLoopAsync);
        }
    }

    /// <summary>Waits for background indexing to finish (tests, and shutting down tidily).</summary>
    public Task WaitForIndexingAsync()
    {
        lock (_lock) return _loop ?? Task.CompletedTask;
    }

    private async Task IndexLoopAsync()
    {
        while (true)
        {
            try
            {
                var model = await embedder.ModelIdAsync();
                var waiting = await store.NeedingIndexAsync(model);
                if (waiting.Count > 0 && model is null)
                {
                    Problem = "There is no document search model yet, so documents cannot be searched. Get one in Models → Recommended → Document search.";
                }
                else
                {
                    Problem = null;
                    foreach (var id in waiting)
                        if (!await IndexAsync(id, model!)) break;
                }
            }
            catch (Exception e)
            {
                Problem = "Indexing stopped: " + e.Message;
            }
            Progress = null;
            Changed?.Invoke();
            lock (_lock)
            {
                if (!_again) return;
                _again = false;
            }
        }
    }

    /// <summary>Indexes one document's passages. False when the search model cannot be used (the rest wait too).</summary>
    private async Task<bool> IndexAsync(long documentId, string model)
    {
        var doc = await store.GetDocumentAsync(documentId);
        if (doc is null) return true;
        var chunks = await store.ChunksToIndexAsync(documentId, model);
        await store.SetStatusAsync(documentId, DocumentStatus.Indexing, null, null);
        var done = 0;
        Progress = new IndexProgress(documentId, doc.FileName, 0, chunks.Count);
        Changed?.Invoke();
        try
        {
            foreach (var batch in chunks.Chunk(BatchSize))
            {
                var texts = batch.Select(c => EmbeddingFormat.Document(model, Labelled(doc.FileName, c.Heading, c.Text))).ToList();
                var vectors = await embedder.EmbedAsync(texts);
                if (vectors.Count != batch.Length) throw new InvalidOperationException("The search model returned the wrong number of results.");
                await store.SetVectorsAsync(batch.Select((c, i) => (c.Id, VectorMath.ToBytes(VectorMath.Normalize(vectors[i])))).ToList(), model);
                done += batch.Length;
                Progress = new IndexProgress(documentId, doc.FileName, done, chunks.Count);
                Changed?.Invoke();
            }
            await store.SetStatusAsync(documentId, DocumentStatus.Ready, null, model);
            Forget(doc.CollectionId);
            return true;
        }
        catch (EmbedderUnavailableException e)
        {
            await store.SetStatusAsync(documentId, DocumentStatus.Waiting, null, null);
            Problem = e.Message;
            return false;
        }
        catch (Exception e)
        {
            await store.SetStatusAsync(documentId, DocumentStatus.Failed, "It could not be indexed: " + e.Message, null);
            return true;
        }
    }

    /// <summary>What the search model reads for a passage: where it comes from, then the passage.</summary>
    private static string Labelled(string fileName, string? heading, string text) =>
        fileName + (heading is { Length: > 0 } h ? " — " + h : "") + "\n\n" + text;

    /// <summary>
    /// The passages of <paramref name="collectionIds"/> that best match <paramref name="query"/>. Empty when there is no
    /// search model or nothing is indexed yet. Documents not yet indexed with the current model are queued for indexing.
    /// </summary>
    /// <exception cref="EmbedderUnavailableException">The search model could not be started.</exception>
    public async Task<IReadOnlyList<FoundPassage>> SearchAsync(IReadOnlyList<long> collectionIds, string query, int count,
        float minSimilarity = Retriever.DefaultMinSimilarity, CancellationToken ct = default)
    {
        if (collectionIds.Count == 0 || query.Trim().Length == 0) return [];
        var model = await embedder.ModelIdAsync(ct);
        if (model is null) return [];
        var indexes = new List<ChunkIndex>();
        foreach (var id in collectionIds.Distinct()) indexes.Add(await IndexOfAsync(id, model, ct));
        if (indexes.All(x => x.Chunks.Count == 0))
        {
            if ((await store.NeedingIndexAsync(model, ct)).Count > 0) StartIndexing();
            return [];
        }
        var vector = VectorMath.Normalize((await embedder.EmbedAsync([EmbeddingFormat.Query(model, query)], ct))[0]);
        var hits = Retriever.Search(indexes, vector, query, count, minSimilarity);
        var chunks = (await store.ChunksAsync(hits.Select(h => h.Chunk.ChunkId).ToList(), ct)).ToDictionary(c => c.Id);
        var names = new Dictionary<long, string>();
        var result = new List<FoundPassage>();
        foreach (var hit in hits)
        {
            if (!chunks.TryGetValue(hit.Chunk.ChunkId, out var chunk)) continue;
            if (!names.TryGetValue(chunk.DocumentId, out var name))
                names[chunk.DocumentId] = name = (await store.GetDocumentAsync(chunk.DocumentId, ct))?.FileName ?? "a document";
            result.Add(new FoundPassage(chunk, name, hit.Similarity));
        }
        return result;
    }

    private async Task<ChunkIndex> IndexOfAsync(long collectionId, string model, CancellationToken ct)
    {
        lock (_lock)
            if (_indexes.TryGetValue((collectionId, model), out var cached)) return cached;
        var index = new ChunkIndex(await store.IndexedAsync([collectionId], model, ct));
        // Only keep a complete index; while documents are still being indexed, read it fresh each time.
        if (!IsIndexing) lock (_lock) _indexes[(collectionId, model)] = index;
        return index;
    }

    private void Forget(long collectionId)
    {
        lock (_lock)
            foreach (var key in _indexes.Keys.Where(k => k.Collection == collectionId).ToList())
                _indexes.Remove(key);
    }

    /// <summary>
    /// The earlier messages of a long chat that best match <paramref name="query"/>, out of <paramref name="candidates"/>
    /// (the messages left out of the prompt), in chat order. Messages are embedded the first time they are needed and
    /// kept; an edited message is embedded again. Empty when there is no search model.
    /// </summary>
    /// <param name="label">How a message is labelled when embedded, e.g. "Ann: …".</param>
    public async Task<IReadOnlyList<(ChatMessage Message, float Similarity)>> RecallAsync(long conversationId, IReadOnlyList<ChatMessage> candidates,
        Func<ChatMessage, string> label, string query, int count, float minSimilarity, CancellationToken ct = default)
    {
        candidates = candidates.Where(m => m.Content.Trim().Length > 0).ToList();
        if (candidates.Count == 0 || query.Trim().Length == 0 || count <= 0) return [];
        var model = await embedder.ModelIdAsync(ct);
        if (model is null) return [];

        string Text(ChatMessage m)
        {
            var text = label(m) + ": " + m.Content.Trim();
            return text.Length <= MessageEmbedChars ? text : text[..MessageEmbedChars];
        }

        var known = (await store.MessageVectorsAsync(conversationId, model, ct)).ToDictionary(v => v.MessageId);
        var vectors = new Dictionary<long, float[]>();
        var missing = new List<(ChatMessage Message, string Text, long Hash)>();
        foreach (var m in candidates)
        {
            var text = Text(m);
            var hash = VectorMath.StableHash(text);
            if (known.TryGetValue(m.Id, out var v) && v.ContentHash == hash) vectors[m.Id] = VectorMath.FromBytes(v.Vector);
            else missing.Add((m, text, hash));
        }
        foreach (var batch in missing.Chunk(MessageBatchSize))
        {
            var embedded = await embedder.EmbedAsync(batch.Select(b => EmbeddingFormat.Document(model, b.Text)).ToList(), ct);
            var saved = new List<MessageVector>();
            for (var i = 0; i < batch.Length; i++)
            {
                var vector = VectorMath.Normalize(embedded[i]);
                vectors[batch[i].Message.Id] = vector;
                saved.Add(new MessageVector
                {
                    MessageId = batch[i].Message.Id,
                    ConversationId = conversationId,
                    ContentHash = batch[i].Hash,
                    Model = model,
                    Vector = VectorMath.ToBytes(vector),
                });
            }
            await store.SaveMessageVectorsAsync(saved, ct);
        }

        var q = VectorMath.Normalize((await embedder.EmbedAsync([EmbeddingFormat.Query(model, query)], ct))[0]);
        var order = candidates.Select((m, i) => (m.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return candidates
            .Select(m => (Message: m, Similarity: VectorMath.Dot(q, vectors[m.Id])))
            .Where(x => x.Similarity >= minSimilarity)
            .OrderByDescending(x => x.Similarity)
            .Take(count)
            .OrderBy(x => order[x.Message.Id])
            .ToList();
    }
}

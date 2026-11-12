using System.IO.Compression;
using System.Text;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Documents;
using DogAI.Data;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DogAI.Tests;

/// <summary>
/// Stands in for the document search model: a bag of words hashed into 256 numbers, so texts that share words are
/// similar and the results are exact and repeatable.
/// </summary>
internal sealed class FakeEmbedder : IEmbedder
{
    public string? Model { get; set; } = "fake-embed";
    public int Calls { get; private set; }
    public int Texts { get; private set; }
    public List<string> Inputs { get; } = [];

    public Task<string?> ModelIdAsync(CancellationToken ct = default) => Task.FromResult(Model);

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (Model is null) throw new EmbedderUnavailableException("No search model.");
        Calls++;
        Texts += texts.Count;
        Inputs.AddRange(texts);
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToList());
    }

    public static float[] Vector(string text)
    {
        var v = new float[256];
        foreach (var word in text.ToLowerInvariant().Split((char[])[' ', '\n', '.', ',', ':', '?', '!', '—', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length < 3) continue;
            var h = 0;
            foreach (var c in word) h = h * 31 + c;
            v[(h & 0x7fffffff) % 256] += 1;
        }
        return v;
    }
}

public sealed class DocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-docs-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly ICollectionStore _store;
    private readonly FakeEmbedder _embedder = new();
    private readonly DocumentLibrary _library;

    public DocumentTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        _services.MigrateDogDatabaseAsync().GetAwaiter().GetResult();
        _store = _services.GetRequiredService<ICollectionStore>();
        _library = new DocumentLibrary(_store, _embedder);
    }

    // ---- Reading files ----

    [Fact]
    public void Text_files_lose_their_byte_order_mark_and_windows_line_ends()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Line one\r\n\r\n\r\n\r\nLine two\u0007\r\n")).ToArray();
        var text = TextExtractor.Read("notes.txt", bytes);
        Assert.Equal("Line one\n\nLine two", text.Text);
        Assert.Equal("text", text.Kind);
        Assert.Empty(text.PageStarts);
    }

    [Fact]
    public void Markdown_and_code_are_told_apart_by_extension()
    {
        Assert.Equal("markdown", TextExtractor.Read("a.md", "# Hi\n\ntext"u8.ToArray()).Kind);
        Assert.Equal("code", TextExtractor.Read("a.py", "# comment\nprint(1)"u8.ToArray()).Kind);
        Assert.True(TextExtractor.Read("a.md", "# Hi"u8.ToArray()).HasMarkdownHeadings);
        Assert.False(TextExtractor.Read("a.py", "# comment"u8.ToArray()).HasMarkdownHeadings);
    }

    [Fact]
    public void Binary_files_and_old_word_files_are_refused_in_plain_words()
    {
        var binary = TextExtractor.Read;
        var e = Assert.Throws<DocumentReadException>(() => binary("photo.jpg", [0xFF, 0xD8, 0x00, 0x10, 0x4A]));
        Assert.Contains("does not look like a text file", e.Message);
        Assert.Contains(".docx", Assert.Throws<DocumentReadException>(() => binary("old.doc", [1, 2, 3])).Message);
        Assert.Contains("no text", Assert.Throws<DocumentReadException>(() => binary("empty.txt", "  \n "u8.ToArray())).Message);
    }

    [Fact]
    public void Word_documents_keep_headings_lists_and_tables()
    {
        var text = TextExtractor.Read("report.docx", Docx(
            P("Quarterly report", "Title"),
            P("Sales grew in the north."),
            P("Regions", "Heading2"),
            ListItem("North"),
            ListItem("South"),
            Table(["Region", "Sales"], ["North", "120"])));

        Assert.Equal("docx", text.Kind);
        Assert.Contains("# Quarterly report", text.Text);
        Assert.Contains("## Regions", text.Text);
        Assert.Contains("- North\n- South", text.Text);
        Assert.Contains("Region | Sales\nNorth | 120", text.Text);
        Assert.Contains("Sales grew in the north.", text.Text);
    }

    [Fact]
    public void A_word_file_with_a_dtd_is_refused_rather_than_expanded()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaa\">]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&a;</w:t></w:r></w:p></w:body></w:document>";
        var e = Assert.Throws<DocumentReadException>(() => TextExtractor.Read("evil.docx", Zip(xml)));
        Assert.Contains("could not be read", e.Message);
    }

    [Fact]
    public void Pdf_pages_are_read_in_order_and_remembered()
    {
        var text = TextExtractor.Read("manual.pdf", Pdf("The first page talks about boilers.", "Page two covers the thermostat."));

        Assert.Equal("pdf", text.Kind);
        Assert.Equal(2, text.PageStarts.Count);
        Assert.Contains("boilers", text.Text);
        Assert.Equal(1, text.PageAt(text.Text.IndexOf("boilers", StringComparison.Ordinal)));
        Assert.Equal(2, text.PageAt(text.Text.IndexOf("thermostat", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_pdf_with_no_text_layer_says_it_is_probably_scanned()
    {
        var e = Assert.Throws<DocumentReadException>(() => TextExtractor.Read("scan.pdf", Pdf("")));
        Assert.Contains("scanned", e.Message);
    }

    // ---- Cutting into passages ----

    [Fact]
    public void Passages_stay_within_size_cover_the_text_and_overlap()
    {
        var paragraphs = Enumerable.Range(1, 40).Select(i => $"Paragraph {i} says something about topic {i}. " + string.Concat(Enumerable.Repeat("More words here. ", 12)));
        var doc = new ExtractedText(string.Join("\n\n", paragraphs), "text", []);

        var chunks = Chunker.Split(doc);

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, Chunker.Max));
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(doc.Text.Length, chunks[^1].Start + chunks[^1].Length);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousEnd = chunks[i - 1].Start + chunks[i - 1].Length;
            Assert.True(chunks[i].Start <= previousEnd || doc.Text[previousEnd..chunks[i].Start].Trim().Length == 0, "no text left out");
            Assert.True(chunks[i].Start > chunks[i - 1].Start, "each passage moves on");
        }
        Assert.Contains(Enumerable.Range(1, chunks.Count - 1), i => chunks[i].Start < chunks[i - 1].Start + chunks[i - 1].Length);
        Assert.All(chunks, c => Assert.Equal(doc.Text.Substring(c.Start, c.Length).Trim(), c.Text));
    }

    [Fact]
    public void A_heading_starts_a_passage_and_passages_know_their_headings()
    {
        var filler = string.Concat(Enumerable.Repeat("Words about this section. ", 20));
        var doc = new ExtractedText($"# Setup\n\n{filler}\n\n## Windows\n\n{filler}\n\n## Linux\n\n{filler}", "markdown", []);

        var chunks = Chunker.Split(doc);

        Assert.Contains(chunks, c => c.Text.StartsWith("## Windows") && c.Heading == "Setup › Windows");
        Assert.Contains(chunks, c => c.Text.StartsWith("## Linux") && c.Heading == "Setup › Linux");
        Assert.DoesNotContain(chunks, c => c.Text.TrimEnd().EndsWith("## Linux") || c.Text.TrimEnd().EndsWith("## Windows"));
    }

    [Fact]
    public void A_single_huge_paragraph_is_cut_at_sentence_ends()
    {
        var doc = new ExtractedText(string.Concat(Enumerable.Repeat("This is one sentence of a very long paragraph. ", 200)), "text", []);
        var chunks = Chunker.Split(doc);
        Assert.True(chunks.Count >= 6);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, Chunker.Max));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith(".", c.Text));
    }

    [Fact]
    public void Code_comments_are_not_mistaken_for_headings()
    {
        var doc = TextExtractor.Read("tool.py", Encoding.UTF8.GetBytes("# settings\nx = 1\n\n# main\nprint(x)"));
        Assert.All(Chunker.Split(doc), c => Assert.Null(c.Heading));
    }

    // ---- Searching ----

    [Fact]
    public void Vectors_survive_a_round_trip_and_normalised_ones_have_dot_one()
    {
        var v = VectorMath.Normalize([3f, 4f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f]);
        Assert.Equal(v, VectorMath.FromBytes(VectorMath.ToBytes(v)));
        Assert.Equal(1f, VectorMath.Dot(v, v), 4);
        Assert.Equal(VectorMath.StableHash("same text"), VectorMath.StableHash("same text"));
        Assert.NotEqual(VectorMath.StableHash("same text"), VectorMath.StableHash("same text!"));
    }

    [Fact]
    public void An_exact_rare_word_lifts_a_passage_the_vectors_rank_lower()
    {
        // By vectors alone the top two would be 1 and 3; only passage 2 contains the exact code asked about.
        var query = VectorMath.Normalize([1f, 0f, 0f]);
        var chunks = new[]
        {
            new IndexedChunk(1, 1, "General notes about error handling in the app.", VectorMath.Normalize([0.95f, 0.31f, 0f])),
            new IndexedChunk(2, 1, "Error E4012 means the license file is missing.", VectorMath.Normalize([0.85f, 0.53f, 0f])),
            new IndexedChunk(3, 1, "An overview of the settings screen.", VectorMath.Normalize([0.9f, 0.44f, 0f])),
            new IndexedChunk(4, 1, "A recipe for bread.", VectorMath.Normalize([0f, 0f, 1f])),
        };

        var hits = Retriever.Search([new ChunkIndex(chunks)], query, "What does error E4012 mean?", 2, minSimilarity: 0.5f);

        Assert.Equal([1L, 2L], hits.Select(h => h.Chunk.ChunkId));
    }

    [Fact]
    public void Nothing_is_returned_when_nothing_is_close()
    {
        var chunks = new[] { new IndexedChunk(1, 1, "A recipe for bread.", VectorMath.Normalize([0f, 1f])) };
        Assert.Empty(Retriever.Search([new ChunkIndex(chunks)], VectorMath.Normalize([1f, 0f]), "tax forms", 3));
    }

    [Fact]
    public void Query_and_document_wording_follows_the_model_family()
    {
        Assert.StartsWith("Instruct: ", EmbeddingFormat.Query("Qwen3-Embedding-0.6B-Q8_0", "hi"));
        Assert.Equal("hi", EmbeddingFormat.Document("Qwen3-Embedding-0.6B-Q8_0", "hi"));
        Assert.Equal("search_query: hi", EmbeddingFormat.Query("nomic-embed-text-v1.5.Q8_0", "hi"));
        Assert.Equal("search_document: hi", EmbeddingFormat.Document("nomic-embed-text-v1.5.Q8_0", "hi"));
        Assert.Equal("hi", EmbeddingFormat.Query("some-other-model", "hi"));
    }

    // ---- The library, with a real database ----

    private async Task<long> NewCollectionAsync(string name = "Manuals") =>
        (await _store.CreateAsync(new DocumentCollection { Name = name })).Id;

    [Fact]
    public async Task Adding_a_file_indexes_it_in_the_background_and_search_finds_the_right_passage()
    {
        var id = await NewCollectionAsync();
        var text = "# Boiler\n\nThe boiler pressure should sit between one and two bar. Top it up with the filling loop.\n\n" +
                   "# Garden\n\nWater the tomatoes every evening in summer, and feed them weekly.";
        var doc = await _library.AddAsync(id, "house.md", Encoding.UTF8.GetBytes(text));
        await _library.WaitForIndexingAsync();

        Assert.NotNull(doc);
        var listed = Assert.Single(await _store.DocumentsAsync(id));
        Assert.Equal(DocumentStatus.Ready, listed.Status);
        Assert.Equal("fake-embed", listed.VectorModel);
        Assert.Contains(_embedder.Inputs, s => s.StartsWith("house.md — Boiler\n\n"));

        var found = await _library.SearchAsync([id], "What pressure should the boiler be at?", 1, minSimilarity: 0.1f);
        var hit = Assert.Single(found);
        Assert.Equal("house.md", hit.FileName);
        Assert.Contains("pressure", hit.Chunk.Text);
        Assert.Equal("Boiler", hit.Chunk.Heading);
    }

    [Fact]
    public async Task The_same_file_twice_in_one_collection_is_skipped()
    {
        var id = await NewCollectionAsync();
        var bytes = "Some notes about lamps."u8.ToArray();
        Assert.NotNull(await _library.AddAsync(id, "a.txt", bytes));
        Assert.Null(await _library.AddAsync(id, "copy-of-a.txt", bytes));
        Assert.NotNull(await _library.AddAsync(await NewCollectionAsync("Other"), "a.txt", bytes));
        await _library.WaitForIndexingAsync();
    }

    [Fact]
    public async Task Without_a_search_model_documents_wait_and_are_indexed_once_there_is_one()
    {
        _embedder.Model = null;
        var id = await NewCollectionAsync();
        await _library.AddAsync(id, "notes.txt", "Notes about the lighthouse keeper."u8.ToArray());
        await _library.WaitForIndexingAsync();

        Assert.Equal(DocumentStatus.Waiting, Assert.Single(await _store.DocumentsAsync(id)).Status);
        Assert.Contains("Document search", _library.Problem);
        Assert.Empty(await _library.SearchAsync([id], "lighthouse", 3));

        _embedder.Model = "fake-embed";
        _library.StartIndexing();
        await _library.WaitForIndexingAsync();
        Assert.Equal(DocumentStatus.Ready, Assert.Single(await _store.DocumentsAsync(id)).Status);
        Assert.Null(_library.Problem);
    }

    [Fact]
    public async Task Changing_the_search_model_indexes_everything_again_and_old_vectors_are_not_mixed_in()
    {
        var id = await NewCollectionAsync();
        await _library.AddAsync(id, "notes.txt", "Notes about the lighthouse keeper and his lamp."u8.ToArray());
        await _library.WaitForIndexingAsync();
        Assert.Single(await _library.SearchAsync([id], "lighthouse lamp", 3, 0.1f));

        _embedder.Model = "fake-embed-2";
        Assert.Empty(await _store.IndexedAsync([id], "fake-embed-2"));
        // Searching notices the stale index and starts indexing again.
        Assert.Empty(await _library.SearchAsync([id], "lighthouse lamp", 3, 0.1f));
        await _library.WaitForIndexingAsync();
        Assert.Single(await _library.SearchAsync([id], "lighthouse lamp", 3, 0.1f));
        Assert.Equal("fake-embed-2", Assert.Single(await _store.DocumentsAsync(id)).VectorModel);
    }

    [Fact]
    public async Task Deleting_a_chat_takes_its_own_files_with_it_but_not_shared_collections()
    {
        var chats = _services.GetRequiredService<IChatStore>();
        var chat = await chats.CreateAsync(new Conversation());
        var own = (await _store.ForConversationAsync(chat.Id, create: true))!;
        var shared = await NewCollectionAsync();
        await _store.SetAttachedAsync(chat.Id, [shared]);
        await _library.AddAsync(own.Id, "mine.txt", "Private notes."u8.ToArray());
        await _library.AddAsync(shared, "shared.txt", "Shared notes."u8.ToArray());
        await _library.WaitForIndexingAsync();

        Assert.Equal([shared], await _store.AttachedAsync(chat.Id));
        Assert.DoesNotContain(await _store.ListAsync(), c => c.Id == own.Id);

        await chats.DeleteAsync(chat.Id);

        Assert.Null(await _store.GetAsync(own.Id));
        Assert.Single(await _store.DocumentsAsync(shared));
        Assert.Empty(await _store.IndexedAsync([own.Id], "fake-embed"));
    }

    [Fact]
    public async Task Recall_finds_the_matching_earlier_message_and_embeds_each_message_once()
    {
        var chats = _services.GetRequiredService<IChatStore>();
        var chat = await chats.CreateAsync(new Conversation { Mode = "characters" });
        var messages = new List<ChatMessage>();
        foreach (var text in new[] { "My sister Mara keeps bees on the hill.", "We walked to the market for apples.", "It rained all evening." })
            messages.Add(await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.User, Content = text }));

        var found = await _library.RecallAsync(chat.Id, messages, _ => "User", "Tell me about Mara and her bees", 1, 0.1f);
        Assert.Equal(messages[0].Id, Assert.Single(found).Message.Id);
        var embedded = _embedder.Texts;

        await _library.RecallAsync(chat.Id, messages, _ => "User", "apples at the market", 1, 0.1f);
        Assert.Equal(embedded + 1, _embedder.Texts); // only the query

        messages[2].Content = "It snowed all evening.";
        await chats.UpdateMessageAsync(messages[2]);
        await _library.RecallAsync(chat.Id, messages, _ => "User", "snow", 1, 0.1f);
        Assert.Equal(embedded + 3, _embedder.Texts); // the query and the edited message
    }

    // ---- Helpers that build test files ----

    private static string P(string text, string? style = null) =>
        "<w:p>" + (style is null ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>") + $"<w:r><w:t>{text}</w:t></w:r></w:p>";

    private static string ListItem(string text) =>
        $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";

    private static string Table(params string[][] rows) =>
        "<w:tbl>" + string.Concat(rows.Select(r => "<w:tr>" + string.Concat(r.Select(c => $"<w:tc>{P(c)}</w:tc>")) + "</w:tr>")) + "</w:tbl>";

    private static byte[] Docx(params string[] blocks) => Zip(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
        + string.Concat(blocks) + "</w:body></w:document>");

    private static byte[] Zip(string documentXml)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write(documentXml);
        }
        return stream.ToArray();
    }

    private static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text.Length > 0) page.AddText(text, 12, new PdfPoint(40, 700), font);
        }
        return builder.Build();
    }

    public void Dispose()
    {
        _library.WaitForIndexingAsync().GetAwaiter().GetResult();
        _services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

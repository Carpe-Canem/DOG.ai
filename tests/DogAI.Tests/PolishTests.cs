using System.IO.Compression;
using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Backup;
using DogAI.Core.Chat;
using DogAI.Core.Library;
using DogAI.Core.Models;
using DogAI.Core.Search;
using DogAI.Core.Settings;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

public sealed class PromptVariableTests
{
    [Fact]
    public void Blanks_are_found_once_each_in_order_with_their_defaults()
    {
        var found = PromptVariables.Find("Translate [[text]] into [[language|French]], then check [[Text]] again. Not a blank: [x] [[a|b]]x{{char}}");
        Assert.Equal(["text", "language", "a"], found.Select(v => v.Name));
        Assert.Equal("French", found[1].Default);
    }

    [Fact]
    public void Filling_uses_typed_values_then_defaults_and_leaves_character_card_macros_alone()
    {
        var text = "Hi {{user}}: [[what|nothing]] by [[who]] in [[ what ]]";
        var filled = PromptVariables.Fill(text, new Dictionary<string, string> { ["WHAT"] = "tea", ["who"] = "" });
        Assert.Equal("Hi {{user}}: tea by  in tea", filled);
        Assert.Equal("Hi {{user}}: nothing by  in nothing", PromptVariables.Fill(text, new Dictionary<string, string>()));
    }

    [Fact]
    public void Code_with_brackets_and_braces_is_not_mistaken_for_blanks()
    {
        Assert.Empty(PromptVariables.Find("var a = b[c[0]]; if (x) { y[1] = [2, 3]; }"));
    }
}

public sealed class LibraryCatalogTests
{
    private sealed class MemoryDocuments : IDocumentStore
    {
        public readonly Dictionary<string, string> Data = [];
        public Task<T?> LoadAsync<T>(string key, CancellationToken ct = default) where T : class =>
            Task.FromResult(Data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : null);
        public Task SaveAsync<T>(string key, T value, CancellationToken ct = default) where T : class
        {
            Data[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_first_run_gets_examples_and_deleting_them_all_is_respected_next_time()
    {
        var docs = new MemoryDocuments();
        var first = new LibraryCatalog(docs);
        await first.LoadAsync();
        Assert.NotEmpty(first.Assistants);
        Assert.NotEmpty(first.Prompts);

        foreach (var a in first.Assistants.ToList()) await first.DeleteAssistantAsync(a.Id);
        var second = new LibraryCatalog(docs);
        await second.LoadAsync();
        Assert.Empty(second.Assistants);
        Assert.NotEmpty(second.Prompts);
    }

    [Fact]
    public async Task Saving_keeps_an_id_on_edit_and_refuses_empty_names_and_texts()
    {
        var catalog = new LibraryCatalog(new MemoryDocuments());
        await catalog.LoadAsync();
        var made = await catalog.SaveAsync(new Assistant { Name = " Poet ", Instructions = "Rhyme.", ModelPath = "  " });
        Assert.Equal("Poet", made.Name);
        Assert.Null(made.ModelPath);
        var edited = await catalog.SaveAsync(made with { Name = "Bard" });
        Assert.Equal(made.Id, edited.Id);
        Assert.Single(catalog.Assistants, a => a.Id == made.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveAsync(new Assistant { Name = " " }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveAsync(new PromptEntry { Name = "x", Text = " " }));
    }

    [Fact]
    public async Task Starter_assistants_use_styles_that_exist()
    {
        var catalog = new LibraryCatalog(new MemoryDocuments());
        await catalog.LoadAsync();
        Assert.All(catalog.Assistants, a => Assert.Contains(SamplerPresets.All, p => p.Id == a.PresetId));
    }
}

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("dragon cav", "\"dragon\" \"cav\"*")]
    [InlineData("  \"quoted\" OR NOT near(x) ", "\"quoted\" \"OR\" \"NOT\" \"near\" \"x\"*")]
    [InlineData("naïve café", "\"naïve\" \"café\"*")]
    public void A_typed_search_becomes_quoted_words_with_the_last_one_open(string typed, string expected) =>
        Assert.Equal(expected, ChatSearch.ToFtsQuery(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"*^-")]
    public void Nothing_to_search_for_gives_no_query(string typed) => Assert.Null(ChatSearch.ToFtsQuery(typed));

    [Fact]
    public void Snippets_split_into_plain_and_matched_pieces_without_interpreting_the_text()
    {
        var parts = ChatSearch.Parts("a <b>" + ChatSearch.MatchStart + "hit" + ChatSearch.MatchEnd + "</b> end");
        Assert.Equal([("a <b>", false), ("hit", true), ("</b> end", false)], parts.Select(p => (p.Text, p.Match)));
    }

    [Fact]
    public void Each_kind_of_chat_opens_on_its_own_page() =>
        Assert.Equal(["general/5", "characters/chat/5", "characters/group/5", "story/5"],
            new[] { "general", "characters", "group", "story" }.Select(m => ChatSearch.RouteFor(m, 5)));

    [Fact]
    public void An_exported_chat_reads_as_markdown_and_its_file_name_is_safe()
    {
        var chat = new Conversation { Title = "Tea: how?", SystemPrompt = "Be brief." };
        var md = ChatExport.ToMarkdown(chat, [
            new ChatMessage { Role = ChatRole.User, Content = "How?" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "Boil water." }]);
        Assert.Equal("# Tea: how?\n\n> Instructions: Be brief.\n\n**You:**\n\nHow?\n\n**DOG.ai:**\n\nBoil water.\n", md);
        Assert.Equal("Tea how.md", ChatExport.FileName("Tea: how?"));
        Assert.Equal("chat.md", ChatExport.FileName("???"));
    }
}

/// <summary>The search index and backup work on a real database file in a temporary folder, never the user's.</summary>
public sealed class DatabaseFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-polish-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ServiceProvider _services;

    public DatabaseFeatureTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(_paths).AddDogData().BuildServiceProvider();
    }

    private IChatStore Chats => _services.GetRequiredService<IChatStore>();
    private ISearchStore Search => _services.GetRequiredService<ISearchStore>();

    private async Task<(Conversation Chat, ChatMessage Question, ChatMessage Answer)> AddChatAsync(string title, string question, string answer, string mode = "general")
    {
        var chat = await Chats.CreateAsync(new Conversation { Title = title, Mode = mode });
        var q = await Chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.User, Content = question });
        var a = await Chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, ParentId = q.Id, Role = ChatRole.Assistant, Content = answer });
        await Chats.SetActiveLeafAsync(chat.Id, a.Id);
        return (chat, q, a);
    }

    [Fact]
    public async Task The_bundled_sqlite_has_full_text_search()
    {
        await _services.MigrateDogDatabaseAsync();
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var options = new List<string>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) options.Add(reader.GetString(0));
        Assert.Contains("ENABLE_FTS5", options);
    }

    [Fact]
    public async Task Search_finds_words_in_any_chat_mode_with_marked_snippets_and_by_title()
    {
        await _services.MigrateDogDatabaseAsync();
        var tea = await AddChatAsync("Kitchen help", "How do I brew oolong tea properly?", "Use water just under the boil and steep for two minutes.");
        var story = await AddChatAsync("The long road", "I open the door", "A dragon sleeps in the cave beyond the door.", "story");
        await AddChatAsync("Dragon facts", "Tell me", "Nothing here.");

        var tea1 = await Search.SearchAsync("oolong");
        var hit = Assert.Single(tea1);
        Assert.Equal(tea.Chat.Id, hit.ConversationId);
        Assert.Equal(tea.Question.Id, hit.MessageId);
        Assert.Equal(ChatRole.User, hit.Role);
        Assert.Contains(ChatSearch.Parts(hit.Snippet), p => p.Match && p.Text.Equals("oolong", StringComparison.OrdinalIgnoreCase));

        // A partial last word finds the whole word, a title match comes before message matches, and the route follows the mode.
        var dragons = await Search.SearchAsync("drag");
        Assert.Equal(2, dragons.Count);
        Assert.Null(dragons[0].MessageId);
        Assert.Equal("Dragon facts", dragons[0].Title);
        Assert.Equal($"story/{story.Chat.Id}", dragons[1].Route);

        Assert.Empty(await Search.SearchAsync("zebra"));
        Assert.Empty(await Search.SearchAsync("   "));
    }

    [Fact]
    public async Task Search_copes_with_characters_that_mean_something_to_the_query_language()
    {
        await _services.MigrateDogDatabaseAsync();
        await AddChatAsync("Odd", "Is 100% of this NOT \"quoted\" (really)?", "Yes: a_b matches.");
        foreach (var typed in new[] { "100%", "NOT", "\"quoted", "a_b", "(really", "col:umn", "x AND", "100% of"})
            await Search.SearchAsync(typed);
        Assert.Single(await Search.SearchAsync("really"));
    }

    [Fact]
    public async Task Edited_and_deleted_messages_leave_the_index_and_a_whole_chat_goes_when_deleted()
    {
        await _services.MigrateDogDatabaseAsync();
        var chat = await AddChatAsync("Notes", "remember the pineapple", "Noted.");
        Assert.Single(await Search.SearchAsync("pineapple"));

        chat.Question.Content = "remember the mango";
        await Chats.UpdateMessageAsync(chat.Question);
        Assert.Empty(await Search.SearchAsync("pineapple"));
        Assert.Single(await Search.SearchAsync("mango"));

        await Chats.DeleteAsync(chat.Chat.Id);
        Assert.Empty(await Search.SearchAsync("mango"));
    }

    [Fact]
    public async Task A_busy_chat_shows_at_most_three_hits()
    {
        await _services.MigrateDogDatabaseAsync();
        var chat = await Chats.CreateAsync(new Conversation { Title = "Long" });
        long? parent = null;
        for (var i = 0; i < 6; i++)
        {
            var m = await Chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, ParentId = parent, Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, Content = "banana " + i });
            parent = m.Id;
        }
        Assert.Equal(3, (await Search.SearchAsync("banana")).Count);
    }

    [Fact]
    public async Task Pinned_chats_are_listed_first_without_counting_as_used()
    {
        await _services.MigrateDogDatabaseAsync();
        var old = await AddChatAsync("Old", "a", "b");
        await Task.Delay(20);
        await AddChatAsync("New", "c", "d");
        Assert.Equal(["New", "Old"], (await Chats.ListAsync("general")).Select(c => c.Title));
        var before = (await Chats.GetAsync(old.Chat.Id))!.UpdatedAt;
        await Chats.SetPinnedAsync(old.Chat.Id, true);
        Assert.Equal(["Old", "New"], (await Chats.ListAsync("general")).Select(c => c.Title));
        Assert.Equal(before, (await Chats.GetAsync(old.Chat.Id))!.UpdatedAt);
    }

    [Fact]
    public async Task Messages_written_before_the_search_index_existed_are_searchable_after_upgrading()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112200544_Voice");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'M6 chat', '', 'balanced', '', 4, '', 1, 0, 1, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, 'an old question about rhubarb', '2026-11-12 10:00:00')");
        }

        await _services.MigrateDogDatabaseAsync();

        var chat = Assert.Single(await Chats.ListAsync("general"));
        Assert.False(chat.Pinned);
        var hit = Assert.Single(await Search.SearchAsync("rhubarb"));
        Assert.Equal(chat.Id, hit.ConversationId);
    }

    [Fact]
    public async Task A_backup_restores_after_a_restart_and_keeps_this_pcs_own_settings()
    {
        await _services.MigrateDogDatabaseAsync();
        var settings = _services.GetRequiredService<ISettingsStore>();
        await settings.SaveAsync(new AppSettings { ModelsDirectory = @"Z:\original\models", HfTokenProtected = "secret-token", Skin = "fireside" });
        await AddChatAsync("Keep me", "precious question", "precious answer");
        var zip = Path.Combine(_root, "backup.zip");

        var made = await DatabaseBackup.CreateAsync(_paths, zip, "9.9.9");
        Assert.Equal(1, made.Chats);
        Assert.Equal(2, made.Messages);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal([DatabaseBackup.ManifestEntry, DatabaseBackup.DatabaseEntry], archive.Entries.Select(e => e.FullName).Order());
        }

        // Change things after the backup, as a later day would.
        await Chats.DeleteAsync((await Chats.ListAsync("general")).Single().Id);
        await AddChatAsync("Newer", "later question", "later answer");
        await settings.SaveAsync(new AppSettings { ModelsDirectory = @"Y:\this-pc\models", HfTokenProtected = "this-pcs-token", Skin = "playhouse" });

        var staged = await DatabaseBackup.StageAsync(_paths, zip);
        Assert.Equal("9.9.9", staged.Version);
        Assert.True(DatabaseBackup.HasPending(_paths));

        // Nothing changed yet: the running database is untouched until the next start.
        Assert.Equal("Newer", Assert.Single(await Chats.ListAsync("general")).Title);

        _services.Dispose();
        SqliteConnection.ClearAllPools();
        Assert.NotNull(DatabaseBackup.ApplyPending(_paths));
        Assert.False(DatabaseBackup.HasPending(_paths));
        Assert.NotNull(DatabaseBackup.SafetyCopy(_paths));

        using var after = new ServiceCollection().AddSingleton(_paths).AddDogData().BuildServiceProvider();
        await after.MigrateDogDatabaseAsync();
        Assert.Equal("Keep me", Assert.Single(await after.GetRequiredService<IChatStore>().ListAsync("general")).Title);
        Assert.Equal(2, (await after.GetRequiredService<ISearchStore>().SearchAsync("precious")).Count);
        var restored = await after.GetRequiredService<ISettingsStore>().LoadAsync();
        Assert.Equal("fireside", restored.Skin);                      // what the backup held
        Assert.Equal(@"Y:\this-pc\models", restored.ModelsDirectory); // what belongs to this PC
        Assert.Equal("this-pcs-token", restored.HfTokenProtected);
    }

    [Fact]
    public async Task The_database_a_restore_replaces_keeps_writes_that_were_still_in_its_log()
    {
        await _services.MigrateDogDatabaseAsync();
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(_paths, zip, "1.0.0");

        // A second install whose newest write exists only in the write-ahead log, as after a crash.
        var otherRoot = Path.Combine(_root, "other");
        var other = new AppPaths(otherRoot);
        other.EnsureCreated();
        await using (var live = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabaseFile, Pooling = false }.ToString()))
        {
            await live.OpenAsync();
            await using var write = live.CreateCommand();
            write.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, CreatedAt, UpdatedAt, Pinned) " +
                "VALUES ('general', 'Only in the log', '', 'balanced', '', 4, '', 1, 0, 1, '2026-11-12 10:00:00', '2026-11-12 10:00:00', 0)";
            await write.ExecuteNonQueryAsync();
            Assert.True(File.Exists(_paths.DatabaseFile + "-wal"));
            File.Copy(_paths.DatabaseFile, other.DatabaseFile);
            File.Copy(_paths.DatabaseFile + "-wal", other.DatabaseFile + "-wal");
        }
        SqliteConnection.ClearAllPools();

        await DatabaseBackup.StageAsync(other, zip);
        Assert.NotNull(DatabaseBackup.ApplyPending(other));

        var kept = DatabaseBackup.SafetyCopy(other)!;
        await using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = kept, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await check.OpenAsync();
        await using var read = check.CreateCommand();
        read.CommandText = "SELECT Title FROM Conversations";
        Assert.Equal("Only in the log", await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_queued_restore_can_be_cancelled_and_nothing_is_applied()
    {
        await _services.MigrateDogDatabaseAsync();
        await AddChatAsync("Mine", "q", "a");
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(_paths, zip, "1.0.0");
        await DatabaseBackup.StageAsync(_paths, zip);
        Assert.True(DatabaseBackup.HasPending(_paths));

        DatabaseBackup.CancelPending(_paths);

        Assert.False(DatabaseBackup.HasPending(_paths));
        Assert.Null(DatabaseBackup.ApplyPending(_paths));
        DatabaseBackup.CancelPending(_paths); // nothing queued: still fine
        Assert.Equal("Mine", Assert.Single(await Chats.ListAsync("general")).Title);
    }

    [Fact]
    public async Task A_backup_file_never_holds_the_download_token()
    {
        await _services.MigrateDogDatabaseAsync();
        await _services.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { HfTokenProtected = "secret-token" });
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(_paths, zip, "1.0.0");
        using var archive = ZipFile.OpenRead(zip);
        await using var stream = archive.GetEntry(DatabaseBackup.DatabaseEntry)!.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        Assert.DoesNotContain("secret-token", System.Text.Encoding.UTF8.GetString(memory.ToArray()));
    }

    [Fact]
    public async Task Files_that_are_not_backups_are_refused_and_nothing_is_staged()
    {
        await _services.MigrateDogDatabaseAsync();

        var notZip = Path.Combine(_root, "notes.zip");
        await File.WriteAllTextAsync(notZip, "just text");
        await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(_paths, notZip));

        var sneaky = Path.Combine(_root, "sneaky.zip");
        using (var z = ZipFile.Open(sneaky, ZipArchiveMode.Create))
        {
            z.CreateEntry("../../evil.dll").Open().Dispose();
            z.CreateEntry(DatabaseBackup.ManifestEntry).Open().Dispose();
            z.CreateEntry(DatabaseBackup.DatabaseEntry).Open().Dispose();
        }
        var error = await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(_paths, sneaky));
        Assert.Contains("not a DOG.ai backup", error.Message);

        var wrongApp = Path.Combine(_root, "wrong.zip");
        using (var z = ZipFile.Open(wrongApp, ZipArchiveMode.Create))
        {
            await using (var s = z.CreateEntry(DatabaseBackup.ManifestEntry).Open())
                await JsonSerializer.SerializeAsync(s, new BackupManifest("Other App", 1, "1", DateTime.UtcNow, null, 0, 0));
            await using (var s = z.CreateEntry(DatabaseBackup.DatabaseEntry).Open()) await s.WriteAsync("x"u8.ToArray());
        }
        await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(_paths, wrongApp));

        var notDatabase = Path.Combine(_root, "baddb.zip");
        using (var z = ZipFile.Open(notDatabase, ZipArchiveMode.Create))
        {
            await using (var s = z.CreateEntry(DatabaseBackup.ManifestEntry).Open())
                await JsonSerializer.SerializeAsync(s, new BackupManifest(DatabaseBackup.AppName, 1, "1", DateTime.UtcNow, null, 0, 0));
            await using (var s = z.CreateEntry(DatabaseBackup.DatabaseEntry).Open()) await s.WriteAsync("this is not sqlite at all, only text"u8.ToArray());
        }
        await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(_paths, notDatabase));

        Assert.False(DatabaseBackup.HasPending(_paths));
        Assert.False(File.Exists(Path.Combine(_root, "evil.dll")));
    }

    [Fact]
    public async Task A_backup_from_a_newer_version_is_refused_with_a_plain_reason()
    {
        await _services.MigrateDogDatabaseAsync();
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(_paths, zip, "1.0.0");

        // Pretend a later version had applied one more migration.
        var tampered = Path.Combine(_root, "newer.zip");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        ZipFile.ExtractToDirectory(zip, work);
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(work, "dogai.db"), Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            await using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '9.0.0')";
            await c.ExecuteNonQueryAsync();
        }
        ZipFile.CreateFromDirectory(work, tampered);

        var error = await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(_paths, tampered));
        Assert.Contains("newer DOG.ai", error.Message);
        Assert.False(DatabaseBackup.HasPending(_paths));
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public sealed class MathMarkdownTests
{
    private static string Html(string markdown) => string.Concat(DogAI.Core.Text.MarkdownRenderer.Render(markdown).Select(s => s.Html));

    [Fact]
    public void Formulas_are_marked_for_the_page_to_draw_and_stay_plain_text_here()
    {
        var html = Html("Euler: $e^{i\\pi}+1=0$ and\n\n$$\\int_0^1 x\\,dx$$");
        Assert.Contains("class=\"math", html);
        Assert.Contains("e^{i\\pi}+1=0", html);
        Assert.Contains("\\int_0^1", html);
    }

    [Fact]
    public void Prices_are_not_taken_for_formulas()
    {
        Assert.DoesNotContain("class=\"math", Html("It costs $5 and the other one $10 today."));
    }

    [Fact]
    public void Raw_html_inside_and_around_maths_is_still_shown_as_text()
    {
        var html = Html("<script>alert(1)</script> and $<img src=x onerror=alert(1)>$");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
    }
}

using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

public sealed class ChatFolderLogicTests
{
    [Theory]
    [InlineData("  Work  ", "Work")]
    [InlineData("Home   and\tgarden", "Home and garden")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Folder_names_are_tidied(string? typed, string? expected) =>
        Assert.Equal(expected, ChatFolders.CleanName(typed));

    [Fact]
    public void Long_folder_names_are_cut()
    {
        var name = ChatFolders.CleanName(new string('a', 200));
        Assert.Equal(ChatFolders.MaxNameLength, name!.Length);
    }

    [Fact]
    public void Folder_names_match_ignoring_case()
    {
        var work = new ChatFolder { Id = 1, Name = "Work" };
        Assert.Same(work, ChatFolders.Named([work], "WORK"));
        Assert.Null(ChatFolders.Named([work], "Home"));
    }

    [Fact]
    public void Pinned_chats_stay_on_top_and_others_go_under_their_folder()
    {
        ChatFolder work = new() { Id = 1, Name = "work" }, home = new() { Id = 2, Name = "Home" }, empty = new() { Id = 3, Name = "Zoo" };
        Conversation a = new() { Id = 10, FolderId = 1 },
            b = new() { Id = 11, FolderId = 1, Pinned = true },
            c = new() { Id = 12 },
            d = new() { Id = 13, FolderId = 99 },   // its folder was deleted
            e = new() { Id = 14, FolderId = 2 },
            f = new() { Id = 15, FolderId = 1 };

        var layout = ChatFolders.Arrange([work, empty, home], [a, b, c, d, e, f]);

        Assert.Equal([b], layout.Pinned);
        Assert.Equal(["Home", "work", "Zoo"], layout.Folders.Select(g => g.Folder.Name));
        Assert.Equal([e], layout.Folders[0].Chats);
        Assert.Equal([a, f], layout.Folders[1].Chats);   // keeps the given (most recent first) order, b not repeated
        Assert.Empty(layout.Folders[2].Chats);
        Assert.Equal([c, d], layout.Loose);
    }
}

/// <summary>Folders on a real database file in a temporary folder, never the user's.</summary>
public sealed class ChatFolderStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-folders-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ChatFolderStoreTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
    }

    private IChatStore Store => _services.GetRequiredService<IChatStore>();

    [Fact]
    public async Task Folders_are_created_renamed_folded_and_listed_by_name()
    {
        await _services.MigrateDogDatabaseAsync();
        var work = await Store.CreateFolderAsync("  Work ");
        await Store.CreateFolderAsync("apples");
        Assert.Equal("Work", work.Name);

        await Store.RenameFolderAsync(work.Id, "Büro");
        await Store.SetFolderCollapsedAsync(work.Id, true);

        var folders = await Store.ListFoldersAsync();
        Assert.Equal(["apples", "Büro"], folders.Select(f => f.Name));
        Assert.True(folders[1].Collapsed);
        Assert.False(folders[0].Collapsed);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.CreateFolderAsync("  "));
    }

    [Fact]
    public async Task Filing_a_chat_does_not_count_as_using_it()
    {
        await _services.MigrateDogDatabaseAsync();
        var older = await Store.CreateAsync(new Conversation { Title = "Older" });
        await Task.Delay(20);
        await Store.CreateAsync(new Conversation { Title = "Newer" });
        var folder = await Store.CreateFolderAsync("Work");

        await Store.SetFolderAsync(older.Id, folder.Id);

        var chats = await Store.ListAsync("general");
        Assert.Equal(["Newer", "Older"], chats.Select(c => c.Title));
        Assert.Equal(folder.Id, chats[1].FolderId);

        // Saving the chat's other settings leaves the folder alone.
        var saved = chats[1];
        saved.SystemPrompt = "Be brief.";
        saved.FolderId = null;
        await Store.UpdateAsync(saved);
        Assert.Equal(folder.Id, (await Store.GetAsync(older.Id))!.FolderId);

        await Store.SetFolderAsync(older.Id, null);
        Assert.Null((await Store.GetAsync(older.Id))!.FolderId);
    }

    [Fact]
    public async Task Deleting_a_folder_keeps_its_chats_and_moves_them_out()
    {
        await _services.MigrateDogDatabaseAsync();
        var keep = await Store.CreateFolderAsync("Keep");
        var drop = await Store.CreateFolderAsync("Drop");
        var a = await Store.CreateAsync(new Conversation { Title = "A" });
        var b = await Store.CreateAsync(new Conversation { Title = "B" });
        await Store.SetFolderAsync(a.Id, drop.Id);
        await Store.SetFolderAsync(b.Id, keep.Id);

        await Store.DeleteFolderAsync(drop.Id);

        Assert.Equal(["Keep"], (await Store.ListFoldersAsync()).Select(f => f.Name));
        Assert.Null((await Store.GetAsync(a.Id))!.FolderId);
        Assert.Equal(keep.Id, (await Store.GetAsync(b.Id))!.FolderId);
        Assert.Equal(2, (await Store.ListAsync("general")).Count);
    }

    [Fact]
    public async Task Chats_from_before_folders_are_in_no_folder()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261113005038_StoryCharacters");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'Before folders', '', 'balanced', '', 4, '', 1, 0, 1, 1, '2026-11-13 10:00:00', '2026-11-13 10:00:00')");
        }

        await _services.MigrateDogDatabaseAsync();

        var chat = Assert.Single(await Store.ListAsync("general"));
        Assert.Equal("Before folders", chat.Title);
        Assert.True(chat.Pinned);
        Assert.Null(chat.FolderId);
        Assert.Empty(await Store.ListFoldersAsync());
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

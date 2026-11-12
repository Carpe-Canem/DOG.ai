using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>Chats saved by an earlier version must come through later migrations intact, with sensible new values.</summary>
public sealed class DatabaseUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-upgrade-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public DatabaseUpgradeTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
    }

    [Fact]
    public async Task A_chat_from_m3_part_1_gets_the_default_note_depth_and_memory_on()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112130243_Characters");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, CreatedAt, UpdatedAt) " +
                "VALUES ('characters', 'Old chat', '', 'balanced', '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
        }

        await _services.MigrateDogDatabaseAsync();

        var chat = Assert.Single(await _services.GetRequiredService<IChatStore>().ListAsync("characters"));
        Assert.Equal("Old chat", chat.Title);
        Assert.Equal(PromptLayout.DefaultAuthorsNoteDepth, chat.AuthorsNoteDepth);
        Assert.True(chat.MemoryEnabled);
        Assert.Equal("", chat.AuthorsNote);
        Assert.Equal("", chat.Summary);
        Assert.Null(chat.SummaryThroughId);
    }

    [Fact]
    public async Task A_chat_from_m3_part_2_gets_natural_turn_order_and_no_members()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112133906_LoreAndMemory");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, CreatedAt, UpdatedAt) " +
                "VALUES ('characters', 'Part 2 chat', '', 'balanced', 'rainy', 2, '', 1, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
        }

        await _services.MigrateDogDatabaseAsync();

        var store = _services.GetRequiredService<IChatStore>();
        var chat = Assert.Single(await store.ListAsync("characters"));
        Assert.Equal("Part 2 chat", chat.Title);
        Assert.Equal("rainy", chat.AuthorsNote);
        Assert.Equal(TurnOrder.Natural, chat.TurnOrder);
        Assert.False(chat.IsGroup);
        Assert.Empty(await store.GetMembersAsync(chat.Id));
    }

    [Fact]
    public async Task Messages_from_m3_get_no_story_details_and_there_are_no_stories()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112144546_GroupChat");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'M3 chat', '', 'balanced', '', 4, '', 1, 0, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, 'Hello', '2026-11-12 10:00:00')");
        }

        await _services.MigrateDogDatabaseAsync();

        var store = _services.GetRequiredService<IChatStore>();
        var message = Assert.Single(await store.GetMessagesAsync(1));
        Assert.Equal("Hello", message.Content);
        Assert.Null(message.ExtraJson);
        Assert.Empty(await _services.GetRequiredService<DogAI.Core.Stories.IStoryStore>().ListAsync());
        Assert.Empty(await store.ListAsync(DogAI.Core.Stories.Story.Mode));
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

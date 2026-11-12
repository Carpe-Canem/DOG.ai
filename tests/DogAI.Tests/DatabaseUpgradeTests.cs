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

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

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

    [Fact]
    public async Task A_story_from_m4_gets_recall_on_no_sources_and_no_documents()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112171425_Stories");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, CreatedAt, UpdatedAt) " +
                "VALUES ('story', 'M4 story', '', 'creative', '', 4, '', 1, 0, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, ExtraJson, CreatedAt) VALUES (1, 1, 'Once upon a time', '{{\"Chapter\":0}}', '2026-11-12 10:00:00')");
        }

        await _services.MigrateDogDatabaseAsync();

        var store = _services.GetRequiredService<IChatStore>();
        var story = Assert.Single(await store.ListAsync(DogAI.Core.Stories.Story.Mode));
        Assert.True(story.RecallEnabled);
        Assert.True(story.MemoryEnabled);
        var message = Assert.Single(await store.GetMessagesAsync(story.Id));
        Assert.Equal("{\"Chapter\":0}", message.ExtraJson);
        Assert.Null(message.SourcesJson);
        var collections = _services.GetRequiredService<DogAI.Core.Documents.ICollectionStore>();
        Assert.Empty(await collections.ListAsync());
        Assert.Empty(await collections.AttachedAsync(story.Id));
        Assert.Null(await collections.ForConversationAsync(story.Id, create: false));
    }

    [Fact]
    public async Task Characters_and_stories_from_m5_get_no_voice_so_they_use_the_default()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112180625_Documents");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Characters (Name, Description, Personality, Scenario, FirstMessage, AlternateGreetings, ExampleMessages, SystemPrompt, " +
                "PostHistoryInstructions, CreatorNotes, Creator, CharacterVersion, Tags, ExtraJson, CreatedAt, UpdatedAt) " +
                "VALUES ('Ann', 'A baker', '', '', 'Hi', '[]', '', '', '', '', '', '', '[]', '', '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, CreatedAt, UpdatedAt) " +
                "VALUES ('story', 'M5 story', '', 'creative', '', 4, '', 1, 0, 1, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Stories (ConversationId, Kind, Bible, Outline, TrackState, Dice, PassageWords) VALUES (1, 0, '{{}}', '[]', 1, 0, 250)");
        }

        await _services.MigrateDogDatabaseAsync();

        var character = Assert.Single(await _services.GetRequiredService<DogAI.Core.Characters.ICharacterStore>().ListAsync());
        Assert.Equal("Ann", character.Name);
        Assert.Null(character.Voice);
        var story = await _services.GetRequiredService<DogAI.Core.Stories.IStoryStore>().GetAsync(1);
        Assert.NotNull(story);
        Assert.Null(story.NarratorVoice);
        Assert.Equal(250, story.PassageWords);
    }

    [Fact]
    public async Task A_story_from_m7_has_no_character_cards_and_can_be_given_one()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112205103_Polish");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('story', 'M7 story', '', 'creative', '', 4, '', 1, 0, 1, 0, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Stories (ConversationId, Kind, Bible, Outline, TrackState, Dice, PassageWords) VALUES (1, 1, '{{\"Premise\":\"A flood.\"}}', '[]', 1, 0, 250)");
        }

        await _services.MigrateDogDatabaseAsync();

        var stories = _services.GetRequiredService<DogAI.Core.Stories.IStoryStore>();
        var story = await stories.GetAsync(1);
        Assert.NotNull(story);
        Assert.Equal("A flood.", story.Bible.Premise);
        Assert.Empty(story.Characters);

        story.Characters.Add(new DogAI.Core.Stories.StoryCharacter { CharacterId = 7, Main = true });
        await stories.SaveAsync(story);
        var link = Assert.Single((await stories.GetAsync(1))!.Characters);
        Assert.Equal(7, link.CharacterId);
        Assert.True(link.Main);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

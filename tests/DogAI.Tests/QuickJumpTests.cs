using System.Text.RegularExpressions;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Documents;
using DogAI.Core.Models;
using DogAI.Core.Search;
using DogAI.Core.Settings;
using DogAI.Data;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>M13: what the Ctrl+K box lists, in which order, and that every place it offers exists.</summary>
public class QuickJumpTests
{
    private static JumpTarget Chat(string title, string mode = "general", int daysAgo = 0, string where = "", long id = 1) =>
        new(JumpKind.Chat, title, where, ChatSearch.RouteFor(mode, id)) { Mode = mode, UpdatedAt = new DateTime(2026, 11, 14).AddDays(-daysAgo) };

    [Fact]
    public void Every_screen_has_an_address_that_a_page_answers()
    {
        var pages = Path.Combine(SolutionRoot(), "src", "DogAI.UI", "Pages");
        var routes = Directory.EnumerateFiles(pages, "*.razor")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "@page \"/([^\"]*)\"").Select(m => m.Groups[1].Value))
            .Select(template => new Regex("^" + Regex.Replace(Regex.Escape(template), @"\\\{[A-Za-z]+(:long)?}", m => m.Value.Contains("long") ? "[0-9]+" : "[a-z-]+") + "$"))
            .ToList();
        Assert.NotEmpty(routes);
        var examples = new[] { "characters/7", "characters/chat/7", "characters/group/7", "story/7", "general/7", "general/documents/7" };
        Assert.All(QuickJump.Screens.Select(s => s.Route).Concat(QuickJump.Settings.Select(s => s.Route)).Concat(examples),
            route => Assert.True(routes.Any(r => r.IsMatch(route)), $"No page answers “{route}”."));
    }

    [Fact]
    public void Screens_are_listed_once_and_cover_every_models_tab_and_settings_category()
    {
        var screens = QuickJump.Screens;
        Assert.Equal(screens.Count, screens.Select(s => s.Route).Distinct().Count());
        Assert.Equal(screens.Count, screens.Select(s => s.Title).Distinct().Count());
        Assert.All(screens, s => Assert.Equal(JumpKind.Screen, s.Kind));
        foreach (var place in ModelsPlaces.Groups.SelectMany(g => g.Places))
            Assert.Contains(screens, s => s.Route == ModelsPlaces.Address(place.Id) && s.Title == ModelsPlaces.Name(place.Id));
        Assert.Contains(screens, s => s.Title == ModelsPlaces.LorasName && s.Route == "models/pictures/loras");
        Assert.Contains(screens, s => s.Title == ModelsPlaces.TagsName && s.Route == "models/pictures/tags");
        foreach (var category in SettingsCatalog.Categories)
            Assert.Contains(screens, s => s.Route == $"settings/{category.Id}" && s.Title == $"Settings › {category.Title}");
    }

    [Fact]
    public void Every_setting_the_settings_search_knows_can_be_jumped_to()
    {
        Assert.Equal(SettingsCatalog.Entries.Count, QuickJump.Settings.Count);
        foreach (var entry in SettingsCatalog.Entries)
        {
            var target = Assert.Single(QuickJump.Settings, s => s.Anchor == entry.Anchor);
            Assert.Equal(JumpKind.Setting, target.Kind);
            Assert.Equal($"settings/{entry.Category}", target.Route);
            Assert.Equal(entry.Title, target.Title);
            Assert.Equal("Setting", target.Label);
        }
        var microphone = QuickJump.Find([], "microphone");
        Assert.Equal("set-microphone", microphone[0].Anchor);
        Assert.Equal("Settings › Voice", microphone[0].Where);
    }

    [Fact]
    public void With_nothing_typed_the_chats_used_last_come_first_then_the_main_screens()
    {
        var yours = Enumerable.Range(0, 8).Select(i => Chat($"Chat {i}", daysAgo: i, id: i + 1))
            .Append(new JumpTarget(JumpKind.Character, "Mira", "", "characters/3"))
            .Reverse().ToList();
        foreach (var typed in new[] { null, "", "   ", "?!" })
        {
            var found = QuickJump.Find(yours, typed);
            Assert.Equal(["Chat 0", "Chat 1", "Chat 2", "Chat 3", "Chat 4"], found.Take(QuickJump.Recent).Select(t => t.Title));
            var rest = found.Skip(QuickJump.Recent).ToList();
            Assert.Equal(QuickJump.Screens.Where(s => s.Main).Select(s => s.Title), rest.Select(t => t.Title));
            Assert.Contains(rest, t => t.Route == "general");
            Assert.Contains(rest, t => t.Route == "settings");
        }
        // Nothing of the owner's yet: only the main screens.
        Assert.All(QuickJump.Find([], ""), t => Assert.True(t.Main));
    }

    [Fact]
    public void Every_typed_word_must_start_a_word_and_case_does_not_matter()
    {
        var yours = new List<JumpTarget> { Chat("The Dragon Cave"), Chat("Dragonfly notes", id: 2), Chat("Cavern", id: 3) };
        Assert.Equal(["The Dragon Cave", "Dragonfly notes"], QuickJump.Find(yours, "DRAG").Select(t => t.Title));
        Assert.Equal(["The Dragon Cave"], QuickJump.Find(yours, "cave drag").Select(t => t.Title));
        Assert.Equal(["The Dragon Cave", "Cavern"], QuickJump.Find(yours, "cav").Select(t => t.Title));
        // The middle of a word is not a match, and neither is one word of two.
        Assert.Empty(QuickJump.Find(yours, "ragon"));
        Assert.Empty(QuickJump.Find(yours, "dragon zebra"));
    }

    [Fact]
    public void A_name_match_comes_before_a_match_on_other_words_and_screens_before_your_own_things_before_settings()
    {
        var yours = new List<JumpTarget>
        {
            Chat("Voice acting tips", daysAgo: 3),
            Chat("Voice of the sea", "story", daysAgo: 1, id: 2),
            new(JumpKind.Character, "Voicebox", "", "characters/4"),
        };
        var found = QuickJump.Find(yours, "voice", max: 40);
        var kinds = found.Select(t => t.Kind).ToList();
        // Found by name: screens, then the character, then chats (newest first), then settings.
        Assert.Equal(["Models › Get models › Voice", "Models › On this PC › Voice", "Settings › Voice", "Voicebox", "Voice of the sea", "Voice acting tips", "Default voice"],
            found.Take(7).Select(t => t.Title));
        // Found only by their other words (Speaking speed, Speakers…) after everything found by name.
        var byWords = found.SkipWhile(t => t.Title.Contains("voice", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(byWords);
        Assert.DoesNotContain(byWords, t => t.Title.Contains("voice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(byWords, t => t.Title == "Speaking speed");
        Assert.Equal(JumpKind.Screen, kinds[0]);
    }

    [Fact]
    public void A_character_is_found_by_name_before_chats_named_after_them_and_the_chats_with_them()
    {
        var yours = new List<JumpTarget>
        {
            Chat("At the harbour", "characters", where: "with Mira", id: 5),
            new(JumpKind.Character, "Mira", "", "characters/3"),
            Chat("Mira's birthday", "general", id: 6),
        };
        var found = QuickJump.Find(yours, "mira");
        Assert.Equal(["Mira", "Mira's birthday", "At the harbour"], found.Select(t => t.Title));
        Assert.Equal(["Character", "General", "Character chat"], found.Select(t => t.Label));
        Assert.Equal("characters/chat/5", found[2].Route);
    }

    [Fact]
    public void The_list_is_cut_at_its_limit()
    {
        var yours = Enumerable.Range(0, 50).Select(i => Chat($"Plan {i}", daysAgo: i, id: i + 1)).ToList();
        var found = QuickJump.Find(yours, "plan");
        Assert.Equal(QuickJump.Max, found.Count);
        Assert.Equal("Plan 0", found[0].Title);
        Assert.Equal(3, QuickJump.Find(yours, "plan", max: 3).Count);
    }

    [Fact]
    public void Common_words_find_the_places_they_should()
    {
        string First(string typed) => QuickJump.Find([], typed)[0].Route;
        Assert.Equal("models/engines", First("engines"));
        Assert.Equal("models/engines", First("llama"));
        Assert.Equal("models/library", First("default models"));
        Assert.Equal("models/pictures/loras", First("loras"));
        Assert.Equal("models/downloads", First("downloads"));
        Assert.Equal("characters/personas", First("persona"));
        Assert.Equal("characters/new", First("new char"));
        Assert.Equal("story/new", First("new story"));
        Assert.Equal("general/documents", First("docu"));
        Assert.Equal("settings/privacy", First("privacy"));
        Assert.Equal("settings/backup", First("restore"));
        Assert.Equal("set-lock", QuickJump.Find([], "password")[0].Anchor);
        // Found only by other words: the screen first, then the settings on it.
        Assert.Equal(["settings/windows", "settings/windows", "settings/windows"], QuickJump.Find([], "tray").Select(t => t.Route));
        Assert.Equal([null, "set-tray", "set-close-to-tray"], QuickJump.Find([], "tray").Select(t => t.Anchor));
    }

    [Fact]
    public void Messages_are_looked_into_only_once_three_letters_are_typed_and_name_hits_are_left_to_the_list()
    {
        Assert.False(QuickJump.LooksInMessages(null));
        Assert.False(QuickJump.LooksInMessages("dr"));
        Assert.False(QuickJump.LooksInMessages(" a b "));
        Assert.True(QuickJump.LooksInMessages("dra"));
        Assert.True(QuickJump.LooksInMessages("a bc"));

        var when = new DateTime(2026, 11, 14);
        Assert.Null(QuickJump.FromHit(new ChatHit(4, "general", "Dragons", null, null, "Dragons", when)));
        var reply = QuickJump.FromHit(new ChatHit(4, "story", "The cave", 91, ChatRole.Assistant, "a \u0001dragon\u0002 slept", when))!;
        Assert.Equal((JumpKind.Message, "The cave", "story/4", 4L, 91L), (reply.Kind, reply.Title, reply.Route, reply.ConversationId, reply.MessageId));
        Assert.Equal("In a story", reply.Label);
        Assert.Equal("a \u0001dragon\u0002 slept", reply.Snippet);
        var asked = QuickJump.FromHit(new ChatHit(5, "characters", "Harbour", 92, ChatRole.User, "any \u0001dragon\u0002?", when))!;
        Assert.Equal("In a chat", asked.Label);
        Assert.StartsWith("You: ", asked.Snippet);
        Assert.Equal("characters/chat/5", asked.Route);
    }

    private static string SolutionRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}

/// <summary>The owner's own things for the Ctrl+K box, read from a real database in a temporary folder.</summary>
public sealed class QuickJumpIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-jump-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public QuickJumpIndexTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
    }

    [Fact]
    public async Task Chats_stories_characters_and_shared_collections_are_listed_with_their_addresses()
    {
        await _services.MigrateDogDatabaseAsync();
        var chats = _services.GetRequiredService<IChatStore>();
        var characters = _services.GetRequiredService<ICharacterStore>();
        var collections = _services.GetRequiredService<ICollectionStore>();

        var mira = await characters.SaveAsync(new Character { Name = "Mira" });
        var general = await chats.CreateAsync(new Conversation { Title = "Tax questions", Mode = "general" });
        var harbour = await chats.CreateAsync(new Conversation { Title = "At the harbour", Mode = "characters", CharacterId = mira.Id });
        var group = await chats.CreateAsync(new Conversation { Title = "Council", Mode = Conversation.GroupMode });
        var story = await chats.CreateAsync(new Conversation { Title = "The long road", Mode = "story" });
        var manuals = await collections.CreateAsync(new DocumentCollection { Name = "Manuals" });
        await collections.ForConversationAsync(general.Id, create: true);

        var index = new QuickJumpIndex(chats, characters, collections);
        var yours = await index.LoadAsync();

        JumpTarget Named(string title) => Assert.Single(yours, t => t.Title == title);
        Assert.Equal((JumpKind.Chat, $"general/{general.Id}", "General"), (Named("Tax questions").Kind, Named("Tax questions").Route, Named("Tax questions").Label));
        Assert.Equal(($"characters/chat/{harbour.Id}", "with Mira", "Character chat"), (Named("At the harbour").Route, Named("At the harbour").Where, Named("At the harbour").Label));
        Assert.Equal(($"characters/group/{group.Id}", "Group chat"), (Named("Council").Route, Named("Council").Label));
        Assert.Equal(($"story/{story.Id}", "Story"), (Named("The long road").Route, Named("The long road").Label));
        Assert.Equal((JumpKind.Character, $"characters/{mira.Id}"), (Named("Mira").Kind, Named("Mira").Route));
        Assert.Equal((JumpKind.Documents, $"general/documents/{manuals.Id}"), (Named("Manuals").Kind, Named("Manuals").Route));
        // A chat's own files are not a place of their own.
        Assert.Single(yours, t => t.Kind == JumpKind.Documents);
        Assert.All(yours.Where(t => t.Kind == JumpKind.Chat), t => Assert.NotNull(t.UpdatedAt));

        // Typing the character's name finds her and the chat with her.
        Assert.Equal(["Mira", "At the harbour"], QuickJump.Find(yours, "mira").Select(t => t.Title));

        // A deleted chat is gone the next time the box opens.
        await chats.DeleteAsync(story.Id);
        Assert.DoesNotContain(await index.LoadAsync(), t => t.Title == "The long road");
    }

    public void Dispose()
    {
        _services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Stories;

namespace DogAI.Tests;

/// <summary>Character cards in a story's prompt, on their own.</summary>
public sealed class StoryCastTests
{
    private static Character Rex() => new()
    {
        Id = 1,
        Name = "Rex",
        Description = "{{char}} is a loyal dog who guards {{user}}'s farm.",
        Personality = "Brave",
        Scenario = "{{user}} finds {{char}} at the gate.",
        FirstMessage = "*wags* Hello, {{user}}!",
        ExampleMessages = "<START>\n{{user}}: Sit.\n{{char}}: *sits*",
        SystemPrompt = "Chat-only instruction.",
        PostHistoryInstructions = "Chat-only reminder.",
    };

    private static Character Sam() => new() { Id = 2, Name = "Sam", Description = "A farmer.", ExampleMessages = "{{char}}: Morning." };

    private static Story Adventure() => new() { Kind = StoryKind.Interactive, Bible = new StoryBible { Premise = "A farm in winter." } };

    [Fact]
    public void A_card_the_narrator_plays_is_sent_whole_after_the_bible_with_its_examples()
    {
        var layout = StoryPrompt.Layout(Adventure(), cast: [new StoryCastMember(Rex(), false)]);

        var system = layout.SystemText();
        Assert.Contains("[Characters]", system);
        Assert.Contains("[Rex] A character the narrator writes. Not the main character.\n" +
                        "Rex is a loyal dog who guards the main character's farm.\nPersonality: Brave", system);
        Assert.Contains("[Examples of how Rex talks]\n<START>\nthe main character: Sit.\nRex: *sits*", system);
        Assert.True(system.IndexOf("[Story bible]", StringComparison.Ordinal) < system.IndexOf("[Characters]", StringComparison.Ordinal));
        // A card's own chat instructions are for role-play chats, not for a narrator.
        Assert.DoesNotContain("Chat-only", system + layout.FinalInstruction);
    }

    [Fact]
    public void A_card_the_player_plays_is_described_but_never_spoken_for()
    {
        var cast = new[] { new StoryCastMember(Rex(), false), new StoryCastMember(Sam(), true) };

        var system = StoryPrompt.Layout(Adventure(), cast: cast).SystemText();

        Assert.Contains("[Sam] The main character, played by the player. The narrator never decides what Sam says, does or chooses.\nA farmer.", system);
        // The other card's {{user}} is now the player's character by name.
        Assert.Contains("guards Sam's farm", system);
        Assert.Contains("[Examples of how Rex talks]", system);
        Assert.DoesNotContain("[Examples of how Sam talks]", system);
    }

    [Fact]
    public void An_authored_story_marks_only_the_main_character_and_keeps_everyones_examples()
    {
        var story = new Story { Kind = StoryKind.Authored };
        var cast = new[] { new StoryCastMember(Rex(), false), new StoryCastMember(Sam(), true) };

        var system = StoryPrompt.Layout(story, cast: cast).SystemText();

        Assert.Contains("[Rex]\nRex is a loyal dog", system);
        Assert.Contains("[Sam] The main character.\nA farmer.", system);
        Assert.Contains("[Examples of how Sam talks]\nSam: Morning.", system);
    }

    [Fact]
    public void Several_cards_are_each_cut_to_size_and_one_card_is_not()
    {
        var longText = string.Join(" ", Enumerable.Repeat("He barks at the moon.", 400));
        var big = Rex();
        big.Description = longText;

        var alone = StoryPrompt.CastText(Adventure(), [new StoryCastMember(big, false)]);
        var together = StoryPrompt.CastText(Adventure(), [new StoryCastMember(big, false), new StoryCastMember(Sam(), false)]);

        Assert.Contains(longText, alone);
        Assert.DoesNotContain(longText, together);
        Assert.True(together.Length < StoryPrompt.CastCardLength + 600);
        Assert.Contains("He barks at the moon.\nPersonality: Brave", together);
    }

    [Fact]
    public void A_story_without_cards_has_the_same_prompt_as_before()
    {
        var plain = StoryPrompt.Layout(Adventure()).SystemText();
        Assert.Equal(plain, StoryPrompt.Layout(Adventure(), cast: []).SystemText());
        Assert.DoesNotContain("[Characters]", plain);
    }

    [Fact]
    public void A_cards_scenario_and_greeting_become_a_premise_and_an_opening()
    {
        Assert.Equal("The main character finds Rex at the gate.", StoryPrompt.PremiseFrom(Rex()));
        // Named when the player has a card of their own; otherwise the reader is "you", and is not hailed as "you".
        Assert.Equal("*wags* Hello, Sam!", StoryPrompt.OpeningFrom(Rex(), "Sam"));
        Assert.Equal("*wags* Hello!", StoryPrompt.OpeningFrom(Rex()));
        var mira = new Character { Name = "Mira", FirstMessage = "\"You're late, {{user}}.\" {{char}} hands {{user}} a lamp." };
        Assert.Equal("\"You're late.\" Mira hands you a lamp.", StoryPrompt.OpeningFrom(mira));
        Assert.Equal("", StoryPrompt.OpeningFrom(Sam()));
    }
}

/// <summary>Stories with character cards through the real chat service and SQLite store.</summary>
public sealed partial class ChatServiceTests
{
    private async Task<long> NewStoryWithAsync(StoryKind kind, string? opening, params StoryCharacter[] cards)
    {
        var story = new Story { Kind = kind, TrackState = false, Bible = new StoryBible { Premise = "A farm in winter." }, Characters = [.. cards] };
        return (await _chat.StartStoryAsync("Winter", story, opening)).Id;
    }

    [Fact]
    public async Task A_story_is_written_with_its_card_and_the_cards_lore_and_follows_later_changes()
    {
        var rex = await SaveCharacterAsync();
        var book = new Lorebook();
        book.Entries.Add(new LoreEntry { Name = "Bones", Keys = ["bone"], Content = "{{char}} hides bones under the oak." });
        book.Entries.Add(new LoreEntry { Name = "Barn", Keys = ["barn"], Content = "The barn burned down last year." });
        book.SaveTo(rex);
        await _characters.SaveAsync(rex);
        var id = await NewStoryWithAsync(StoryKind.Interactive, null, new StoryCharacter { CharacterId = rex.Id });
        ScriptStory();

        await _chat.BeginStoryAsync(id);
        await _chat.ActAsync(id, "I look for the bone.");

        var system = _engine.Prompts[^2][0].Content;
        Assert.Contains("[Rex] A character the narrator writes.", system);
        Assert.Contains("Rex is a loyal dog who guards the main character's farm.", system);
        Assert.Contains("Rex hides bones under the oak.", system);
        Assert.DoesNotContain("barn", system);
        Assert.DoesNotContain("Never sent to the model.", system);
        Assert.Equal("Bones", (await ShownAsync(id))[^1].LoreUsed);
        // The choices request sees the same characters.
        Assert.Contains("[Rex] A character the narrator writes.", _engine.Prompts[^1][0].Content);

        // The card is linked, not copied: an edit reaches the next passage.
        rex.Description = "{{char}} is an old sheepdog.";
        await _characters.SaveAsync(rex);
        await _chat.ActAsync(id, "I whistle.");
        Assert.Contains("Rex is an old sheepdog.", _engine.Prompts[^2][0].Content);
        Assert.DoesNotContain("loyal dog", _engine.Prompts[^2][0].Content);
    }

    [Fact]
    public async Task A_story_can_open_with_the_cards_first_message()
    {
        var rex = await SaveCharacterAsync();
        var id = await NewStoryWithAsync(StoryKind.Interactive, StoryPrompt.OpeningFrom(rex), new StoryCharacter { CharacterId = rex.Id });
        ScriptStory("Rex trots ahead.");

        var opening = Assert.Single(await ShownAsync(id));
        Assert.Equal("*wags* Hello!", opening.Content);
        Assert.Equal(ChatRole.Assistant, opening.Role);
        Assert.Null(opening.ParentId);
        Assert.Empty(_engine.Prompts);

        await _chat.ActAsync(id, "I follow him.");

        // The greeting is in the history as the opening passage, after the turn chat templates need before it.
        var prompt = _engine.Prompts[0];
        Assert.Equal(["system", "user", "assistant", "user"], prompt.Select(m => m.Role));
        Assert.Equal(StoryPrompt.OpeningTurn, prompt[1].Content);
        Assert.Equal("*wags* Hello!", prompt[2].Content);
        Assert.Equal(["*wags* Hello!", "I follow him.", "Rex trots ahead."], (await ShownAsync(id)).Select(m => m.Content));
    }

    [Fact]
    public async Task A_written_story_ignores_an_opening_and_gets_its_cards_too()
    {
        var rex = await SaveCharacterAsync();
        var id = await NewStoryWithAsync(StoryKind.Authored, "Not used.", new StoryCharacter { CharacterId = rex.Id, Main = true });
        ScriptStory("Snow fell.");

        Assert.Empty(await ShownAsync(id));
        await _chat.WriteNextAsync(id);

        Assert.Contains("[Rex] The main character.\nRex is a loyal dog who guards the other character's farm.", _engine.Prompts[0][0].Content);
    }

    [Fact]
    public async Task Deleting_a_card_takes_it_out_of_the_story_which_carries_on()
    {
        var rex = await SaveCharacterAsync();
        var sam = await _characters.SaveAsync(new Character { Name = "Sam", Description = "A farmer." });
        var id = await NewStoryWithAsync(StoryKind.Interactive, null,
            new StoryCharacter { CharacterId = rex.Id }, new StoryCharacter { CharacterId = sam.Id, Main = true });
        var saved = (await _stories.GetAsync(id))!;
        Assert.Equal([rex.Id, sam.Id], saved.Characters.Select(c => c.CharacterId));
        Assert.Equal([false, true], saved.Characters.Select(c => c.Main));

        await _characters.DeleteAsync(rex.Id);

        var left = Assert.Single((await _stories.GetAsync(id))!.Characters);
        Assert.Equal(sam.Id, left.CharacterId);
        Assert.True(left.Main);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        Assert.Null(_chat.LastError);
        var system = _engine.Prompts[0][0].Content;
        Assert.DoesNotContain("[Rex]", system);
        Assert.Contains("[Sam] The main character, played by the player.", system);
    }

    [Fact]
    public async Task A_bible_is_drafted_around_the_storys_cards_even_without_an_idea()
    {
        var rex = await SaveCharacterAsync();
        _engine.Reply = _ => FakeEngine.Say("""{"title":"Winter","premise":"A dog keeps watch.","genre":"","tone":"","cast":"","world":"","style_guide":""}""");
        var builder = new StoryBuilder(_engine);

        var draft = await builder.DraftAsync("", StoryKind.Interactive, cast: [new StoryCastMember(rex, false)]);

        Assert.Equal("A dog keeps watch.", draft.Bible.Premise);
        var request = _engine.Prompts[^1][1].Content;
        Assert.StartsWith("Idea: a story that suits these characters\n\nThe story already has these characters.", request);
        Assert.Contains("[Rex]\nRex is a loyal dog who guards the main character's farm.", request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.DraftAsync("  ", StoryKind.Interactive));
    }
}

/// <summary>A character chat turned into a story.</summary>
public sealed partial class ChatServiceTests
{
    [Fact]
    public async Task A_character_chat_becomes_a_story_that_carries_on_from_it_and_the_chat_is_left_alone()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.Reply = _ => FakeEngine.Say("*sits*");
        await _chat.SendAsync(chat.Id, "Sit.");
        _engine.Reply = _ => FakeEngine.Say("*rolls over*");
        await _chat.SendAsync(chat.Id, "Roll over.");
        var chatPath = await ShownAsync(chat.Id);
        await _store.SetSummaryAsync(chat.Id, "Rex learned two tricks.", chatPath[2].Id);
        var before = chatPath.Select(m => (m.Id, m.Content)).ToList();

        var story = new Story { Kind = StoryKind.Authored, TrackState = false, Characters = [new StoryCharacter { CharacterId = rex.Id }] };
        var id = (await _chat.StoryFromChatAsync(chat.Id, "Rex's tricks", story)).Id;

        var shown = await ShownAsync(id);
        Assert.Equal(["*wags* Hello, User!", "Sit.", "*sits*", "Roll over.", "*rolls over*"], shown.Select(m => m.Content));
        Assert.Equal([ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant], shown.Select(m => m.Role));
        Assert.Null(shown[0].ParentId);
        var saved = (await _stories.GetAsync(id))!;
        Assert.Equal(StoryKind.Interactive, saved.Kind); // a chat carries on as an adventure: the user still acts
        Assert.Equal(rex.Id, Assert.Single(saved.Characters).CharacterId);
        var conversation = (await _store.GetAsync(id))!;
        Assert.Equal(Story.Mode, conversation.Mode);
        Assert.Equal("Rex learned two tricks.", conversation.Summary);
        Assert.Equal(shown[2].Id, conversation.SummaryThroughId);

        // The chat is untouched.
        Assert.Equal(before, (await ShownAsync(chat.Id)).Select(m => (m.Id, m.Content)));
        Assert.Equal(StoryKind.Interactive, saved.Kind);

        // The story carries on from the last reply, with the copied turns in its history.
        ScriptStory("Rex fetches the paper.");
        await _chat.ActAsync(id, "I throw the paper.");
        var prompt = _engine.Prompts[^2];
        Assert.Contains(prompt, m => m.Content.Contains("*rolls over*"));
        Assert.Equal("Rex fetches the paper.", (await ShownAsync(id))[^1].Content);
    }

    [Fact]
    public async Task Only_a_one_to_one_character_chat_can_become_a_story_and_an_unanswered_last_message_is_left_off()
    {
        var general = await _store.CreateAsync(new Conversation { Mode = "general" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.StoryFromChatAsync(general.Id, "x", new Story()));

        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        var greeting = (await ShownAsync(chat.Id))[0];
        var unanswered = await _store.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, ParentId = greeting.Id, Role = ChatRole.User, Content = "Hello?" });
        await _store.SetActiveLeafAsync(chat.Id, unanswered.Id);

        var id = (await _chat.StoryFromChatAsync(chat.Id, "x", new Story())).Id;
        Assert.Equal(["*wags* Hello, User!"], (await ShownAsync(id)).Select(m => m.Content));
    }
}

using System.Runtime.CompilerServices;
using DogAI.Core.Characters;
using DogAI.Core.Chat;

namespace DogAI.Tests;

/// <summary>Turn order on its own: who answers, who goes next, and how names are found.</summary>
public sealed class GroupTurnsTests
{
    private static readonly GroupTurns.Member Ann = new(1, "Ann", false);
    private static readonly GroupTurns.Member Bo = new(2, "Bo Peep", false);
    private static readonly GroupTurns.Member Cy = new(3, "Cy", false);

    private static ChatMessage User(string text) => new() { Role = ChatRole.User, Content = text };
    private static ChatMessage Said(long who, string text = "...") => new() { Role = ChatRole.Assistant, SpeakerId = who, Content = text };

    [Fact]
    public void Natural_order_answers_with_whoever_is_named_in_the_order_named()
    {
        var who = GroupTurns.AfterUser(TurnOrder.Natural, [Ann, Bo, Cy], [User("cy, what does bo think?")]);
        Assert.Equal([3L, 2L], who);
    }

    [Fact]
    public void Names_match_whole_words_only()
    {
        Assert.Empty(GroupTurns.Mentioned("Annette and Cyril went bowling", [Ann, Bo, Cy]));
        Assert.Equal([2L], GroupTurns.Mentioned("Bo! Over here.", [Ann, Bo, Cy]));
    }

    [Fact]
    public void Without_a_name_the_member_quiet_longest_answers()
    {
        var history = new[] { Said(1), User("hi"), Said(2), Said(3), User("so?") };
        Assert.Equal([1L], GroupTurns.AfterUser(TurnOrder.Natural, [Ann, Bo, Cy], history));

        // Someone who has never spoken comes first; ties go to list order.
        Assert.Equal([2L], GroupTurns.AfterUser(TurnOrder.Natural, [Ann, Bo, Cy], [Said(1), User("so?")]));
    }

    [Fact]
    public void In_turn_everyone_answers_in_list_order_except_the_muted()
    {
        var muted = Bo with { Muted = true };
        Assert.Equal([1L, 3L], GroupTurns.AfterUser(TurnOrder.InTurn, [Ann, muted, Cy], [User("hello all")]));
    }

    [Fact]
    public void Muted_members_do_not_answer_even_when_named_and_manual_means_nobody()
    {
        var muted = Bo with { Muted = true };
        Assert.Equal([1L], GroupTurns.AfterUser(TurnOrder.Natural, [Ann, muted], [Said(1), User("Bo, hi")]));
        Assert.Empty(GroupTurns.AfterUser(TurnOrder.Manual, [Ann, Bo], [User("Ann?")]));
    }

    [Fact]
    public void Next_prefers_someone_the_last_speaker_named_and_never_repeats_the_last_speaker()
    {
        Assert.Equal(3L, GroupTurns.Next([Ann, Bo, Cy], [User("hi"), Said(1, "What do you say, Cy?")]));
        Assert.Equal(2L, GroupTurns.Next([Ann, Bo], [User("hi"), Said(1, "Ann is my name.")]));
        Assert.Null(GroupTurns.Next([], []));
    }

    [Theory]
    [InlineData("Ann: Hello.", "Hello.")]
    [InlineData("**Ann:** Hello.", "Hello.")]
    [InlineData("  ann:Hello.", "Hello.")]
    [InlineData("*Ann smiles.* Hello.", "*Ann smiles.* Hello.")]
    [InlineData("Annabel: Hello.", "Annabel: Hello.")]
    public void A_copied_name_prefix_is_removed(string text, string expected) =>
        Assert.Equal(expected, GroupTurns.StripSpeaker(text, "Ann"));

    [Fact]
    public void Names_are_joined_as_people_write_them()
    {
        Assert.Equal("Ann", GroupTurns.JoinNames(["Ann"]));
        Assert.Equal("Ann and Bo", GroupTurns.JoinNames(["Ann", "Bo"]));
        Assert.Equal("Ann, Bo and Cy", GroupTurns.JoinNames(["Ann", "Bo", "Cy"]));
    }
}

/// <summary>Group chats through the real chat service and SQLite store.</summary>
public sealed partial class ChatServiceTests
{
    private async Task<(Character Ann, Character Bo)> SaveGroupAsync()
    {
        var ann = await _characters.SaveAsync(new Character
        {
            Name = "Ann",
            Description = "{{char}} is a sharp-tongued sailor.",
            FirstMessage = "Ahoy, {{user}}.",
            ExtraJson = """{"group_only_greetings":["Ann waves at everyone."],"extensions":{"x":1}}""",
        });
        var bo = await _characters.SaveAsync(new Character
        {
            Name = "Bo",
            Description = "{{char}} is a shy cartographer.",
            FirstMessage = "Oh! Hello.",
            AlternateGreetings = ["Hm?"],
        });
        await _characters.SavePersonaAsync(new Persona { Name = "Sam" });
        return (ann, bo);
    }

    /// <summary>Roles after the system message must alternate user, assistant, user... for strict chat templates.</summary>
    private static void AssertAlternates(IReadOnlyList<PromptMessage> prompt)
    {
        Assert.Equal("system", prompt[0].Role);
        for (var i = 1; i < prompt.Count; i++)
        {
            Assert.NotEqual("system", prompt[i].Role);
            Assert.Equal(i % 2 == 1 ? "user" : "assistant", prompt[i].Role);
        }
        Assert.Equal("user", prompt[^1].Role);
    }

    [Fact]
    public async Task A_group_chat_opens_with_every_members_greetings_group_only_first()
    {
        var (ann, bo) = await SaveGroupAsync();

        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);

        Assert.True(chat.IsGroup);
        Assert.Null(chat.CharacterId);
        Assert.Equal("Ann and Bo", chat.Title);
        Assert.Equal(TurnOrder.InTurn, (await _store.GetAsync(chat.Id))!.TurnOrder);
        Assert.Equal([ann.Id, bo.Id], (await _store.GetMembersAsync(chat.Id)).Select(m => m.CharacterId));
        var openings = new MessageTree(await _store.GetMessagesAsync(chat.Id)).Children(null);
        Assert.Equal(["Ann waves at everyone.", "Ahoy, Sam.", "Oh! Hello.", "Hm?"], openings.Select(m => m.Content));
        Assert.Equal([ann.Id, ann.Id, bo.Id, bo.Id], openings.Select(m => m.SpeakerId!.Value));
        Assert.Equal(openings[0].Id, chat.ActiveLeafId);
        // One-to-one chats never use group-only greetings.
        Assert.Equal(["Ahoy, {{user}}."], ann.Greetings());
    }

    [Fact]
    public async Task In_turn_everyone_answers_and_each_sees_the_others_by_name()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        var replies = new Queue<string[]>([["Ann: Aye."], ["Bo: I agree", " with Ann."]]);
        _engine.Reply = _ => FakeEngine.Say(replies.Dequeue());

        await _chat.SendAsync(chat.Id, "Where to?");

        var shown = await ShownAsync(chat.Id);
        Assert.Equal(["Ann waves at everyone.", "Where to?", "Aye.", "I agree with Ann."], shown.Select(m => m.Content));
        Assert.Equal([ann.Id, (long?)null, ann.Id, bo.Id], shown.Select(m => m.SpeakerId));
        Assert.False(_chat.IsBusy);

        // Bo's prompt: Bo's own card, a note on Ann, the group instruction, Ann's reply named, and Bo's turn requested.
        var prompt = _engine.Prompts[^1];
        AssertAlternates(prompt);
        Assert.Contains("You are Bo in an ongoing fictional group role-play with Sam and Ann.", prompt[0].Content);
        Assert.Contains("Bo is a shy cartographer.", prompt[0].Content);
        Assert.Contains("[Others in this scene]\n[Ann]\nAnn is a sharp-tongued sailor.", prompt[0].Content);
        Assert.Equal("assistant", prompt[^2].Role);
        Assert.Equal("Ann: Aye.", prompt[^2].Content);
        Assert.Equal("[Write Bo's next reply.]", prompt[^1].Content);
        Assert.Equal(["\nAnn:", "\nSam:"], _engine.Samplers[^1].Stop!);
    }

    [Fact]
    public async Task Greetings_by_two_members_in_a_row_become_one_assistant_turn()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.Manual);

        await _chat.SpeakAsync(chat.Id, bo.Id);
        await _chat.SpeakAsync(chat.Id, ann.Id);

        var prompt = _engine.Prompts[^1];
        AssertAlternates(prompt);
        Assert.Equal(CharacterPrompt.ChatStart, prompt[1].Content);
        Assert.Equal("Ann: Ann waves at everyone.\n\nBo: Hello there", prompt[2].Content);
        Assert.Equal("[Write Ann's next reply.]", prompt[3].Content);
        Assert.Equal([ann.Id, bo.Id, ann.Id], (await ShownAsync(chat.Id)).Select(m => m.SpeakerId!.Value));
    }

    [Fact]
    public async Task Manual_order_waits_and_speak_next_picks_someone_other_than_the_last_speaker()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.Manual);

        await _chat.SendAsync(chat.Id, "Anyone?");
        Assert.Empty(_engine.Prompts);
        Assert.Equal("Anyone?", (await ShownAsync(chat.Id))[^1].Content);

        await _chat.SpeakNextAsync(chat.Id);
        Assert.Equal(bo.Id, (await ShownAsync(chat.Id))[^1].SpeakerId);
    }

    [Fact]
    public async Task Regenerate_and_continue_keep_the_same_speaker()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.Natural);
        await _chat.SendAsync(chat.Id, "Bo, your map?");
        var first = (await ShownAsync(chat.Id))[^1];
        Assert.Equal(bo.Id, first.SpeakerId);

        _engine.Reply = _ => FakeEngine.Say("Another take");
        await _chat.RegenerateAsync(chat.Id, first.Id);
        var second = (await ShownAsync(chat.Id))[^1];
        Assert.Equal(bo.Id, second.SpeakerId);
        Assert.Contains("You are Bo", _engine.Prompts[^1][0].Content);

        _engine.Reply = _ => FakeEngine.Say(", then more");
        await _chat.ContinueAsync(chat.Id, second.Id);
        Assert.Equal("Another take, then more", (await ShownAsync(chat.Id))[^1].Content);
        Assert.EndsWith("Bo: Another take", _engine.Completions[^1]);
        Assert.Equal(["\nAnn:", "\nSam:"], _engine.Samplers[^1].Stop!);
    }

    [Fact]
    public async Task Stopping_ends_the_whole_round()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        var started = new TaskCompletionSource();
        _engine.Reply = ct => Slow(started, ct);

        var sending = _chat.SendAsync(chat.Id, "Talk, both of you");
        await started.Task;
        _chat.Stop();
        await sending;

        var shown = await ShownAsync(chat.Id);
        Assert.Equal("Well", shown[^1].Content);
        Assert.Equal(ann.Id, shown[^1].SpeakerId);
        Assert.Single(_engine.Prompts);
        Assert.False(_chat.IsBusy);

        static async IAsyncEnumerable<ChatDelta> Slow(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ChatDelta("Well", null, null);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task Deleting_a_character_takes_it_out_of_groups_but_keeps_the_chat_and_its_replies()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        await _chat.SendAsync(chat.Id, "Hi");

        await _characters.DeleteAsync(bo.Id);

        Assert.NotNull(await _store.GetAsync(chat.Id));
        Assert.Equal([ann.Id], (await _store.GetMembersAsync(chat.Id)).Select(m => m.CharacterId));
        Assert.Contains(await ShownAsync(chat.Id), m => m.SpeakerId == bo.Id);

        // Bo's old reply is still named in Ann's next prompt, as someone no longer known.
        await _chat.SendAsync(chat.Id, "And now?");
        var prompt = _engine.Prompts[^1];
        AssertAlternates(prompt);
        Assert.Contains("Someone: Hello there", string.Join("\n", prompt.Select(m => m.Content)));
        Assert.DoesNotContain("[Others in this scene]", prompt[0].Content);
    }

    [Fact]
    public async Task A_group_summary_names_each_speaker()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        _engine.ContextLength = 200;
        for (var i = 0; i < 6; i++)
            await _chat.SendAsync(chat.Id, $"message number {i} " + string.Join(' ', Enumerable.Repeat("word", 12)));

        var requests = string.Join("\n", _engine.Prompts.Where(p => p[0].Content.Contains("summarise stories")).Select(p => p[1].Content));
        Assert.Contains("Ann: Ann waves at everyone.", requests);
        Assert.Contains("Sam: message number 0", requests);
        Assert.Contains("Bo: Hello there", requests);
        Assert.Contains("write a summary of the story so far between Ann and Bo and Sam", requests);
        Assert.DoesNotContain("{{", requests);
    }
}

public sealed class GroupGreetingCardTests
{
    [Fact]
    public void Group_only_greetings_are_read_written_and_survive_export()
    {
        var card = CharacterCard.FromJson("""
            {"spec":"chara_card_v3","spec_version":"3.0","data":{"name":"Ann","first_mes":"Hi",
             "group_only_greetings":["Hello, all."],"extensions":{"keep":true}}}
            """);
        Assert.Equal(["Hello, all."], CharacterCard.GroupGreetings(card));

        card.ExtraJson = CharacterCard.WithGroupGreetings(card, ["One", " ", "Two"]);
        var again = CharacterCard.FromJson(CharacterCard.ToV3Json(card));
        Assert.Equal(["One", "Two"], CharacterCard.GroupGreetings(again));
        Assert.Contains("\"keep\":true", again.ExtraJson);
    }
}

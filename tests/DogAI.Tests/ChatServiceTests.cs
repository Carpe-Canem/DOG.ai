using System.Runtime.CompilerServices;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Models;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>The chat service against a real SQLite store and a scripted stand-in for the model.</summary>
public sealed partial class ChatServiceTests : IDisposable
{
    /// <summary>Counts one token per word and replies with whatever <see cref="Reply"/> yields.</summary>
    private sealed class FakeEngine : IChatEngine
    {
        public bool IsReady { get; set; } = true;
        public string? ModelName => "fake";
        public int ContextLength { get; set; } = 4096;
        public List<IReadOnlyList<PromptMessage>> Prompts { get; } = [];
        public List<string> Completions { get; } = [];
        public List<SamplerPreset> Samplers { get; } = [];
        public Func<CancellationToken, IAsyncEnumerable<ChatDelta>> Reply { get; set; } = _ => Say("Hello", " there");

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(string.Join(" ", messages.Select(m => $"<{m.Role}> {m.Content}")));

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            Prompts.Add(messages);
            Samplers.Add(sampler);
            return Reply(ct);
        }

        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default)
        {
            Completions.Add(prompt);
            Samplers.Add(sampler);
            return Reply(ct);
        }

        public static async IAsyncEnumerable<ChatDelta> Say(params string[] parts)
        {
            foreach (var part in parts)
            {
                await Task.Yield();
                yield return new ChatDelta(part, null, null);
            }
            yield return new ChatDelta(null, null, new GenerationStats(12, parts.Length, 50));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly IChatStore _store;
    private readonly FakeEngine _engine = new();
    private readonly ChatService _chat;
    private readonly ICharacterStore _characters;
    private readonly PresetCatalog _presets;

    public ChatServiceTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        _services.MigrateDogDatabaseAsync().GetAwaiter().GetResult();
        _store = _services.GetRequiredService<IChatStore>();
        _characters = _services.GetRequiredService<ICharacterStore>();
        _presets = new PresetCatalog(_services.GetRequiredService<IDocumentStore>());
        _chat = new ChatService(_store, _engine, _presets, _characters);
    }

    private async Task<long> NewChatAsync(string instructions = "") =>
        (await _store.CreateAsync(new Conversation { SystemPrompt = instructions })).Id;

    private async Task<IReadOnlyList<ChatMessage>> ShownAsync(long id)
    {
        var tree = new MessageTree(await _store.GetMessagesAsync(id));
        return tree.PathTo((await _store.GetAsync(id))!.ActiveLeafId);
    }

    [Fact]
    public async Task Sending_stores_both_turns_names_the_chat_and_records_the_prompt()
    {
        var id = await NewChatAsync("Be brief.");

        await _chat.SendAsync(id, "What is a dog?\nSecond line");

        var shown = await ShownAsync(id);
        Assert.Equal(["What is a dog?\nSecond line", "Hello there"], shown.Select(m => m.Content));
        var reply = shown[1];
        Assert.Equal(ChatRole.Assistant, reply.Role);
        Assert.Equal("fake", reply.ModelName);
        Assert.Equal(12, reply.PromptTokens);
        Assert.Equal(50, reply.TokensPerSecond);
        Assert.Equal("<system> Be brief. <user> What is a dog?\nSecond line", reply.PromptText);
        Assert.Equal("What is a dog?", (await _store.GetAsync(id))!.Title);
        Assert.False(_chat.IsBusy);
        Assert.Null(_chat.LastError);
    }

    [Fact]
    public async Task Regenerate_adds_an_alternative_and_keeps_the_first_reply()
    {
        var id = await NewChatAsync();
        await _chat.SendAsync(id, "Hi");
        var first = (await ShownAsync(id))[1];

        _engine.Reply = _ => FakeEngine.Say("Second", " answer");
        await _chat.RegenerateAsync(id, first.Id);

        var shown = await ShownAsync(id);
        Assert.Equal("Second answer", shown[1].Content);
        var tree = new MessageTree(await _store.GetMessagesAsync(id));
        Assert.Equal(2, tree.Siblings(shown[1]).Count);

        await _chat.SelectBranchAsync(id, first.Id);
        Assert.Equal("Hello there", (await ShownAsync(id))[1].Content);
    }

    [Fact]
    public async Task Editing_a_question_branches_and_the_model_sees_only_the_new_version()
    {
        var id = await NewChatAsync();
        await _chat.SendAsync(id, "Original question");
        var question = (await ShownAsync(id))[0];

        await _chat.EditAndResendAsync(id, question.Id, "Edited question");

        var shown = await ShownAsync(id);
        Assert.Equal("Edited question", shown[0].Content);
        Assert.Equal(2, shown.Count);
        Assert.Equal(["Edited question"], _engine.Prompts[^1].Select(m => m.Content));
        Assert.Equal(4, (await _store.GetMessagesAsync(id)).Count);
    }

    [Fact]
    public async Task Follow_ups_send_the_whole_branch_in_order()
    {
        var id = await NewChatAsync();
        await _chat.SendAsync(id, "One");
        await _chat.SendAsync(id, "Two");

        Assert.Equal(["user", "assistant", "user"], _engine.Prompts[^1].Select(m => m.Role));
        Assert.Equal(["One", "Hello there", "Two"], _engine.Prompts[^1].Select(m => m.Content));
    }

    [Fact]
    public async Task Old_turns_are_left_out_when_the_context_is_full()
    {
        _engine.ContextLength = 200; // 150 for the prompt after the reply reserve
        var id = await NewChatAsync("Stay in character.");
        var filler = string.Join(' ', Enumerable.Repeat("word", 60));
        await _chat.SendAsync(id, "first " + filler);
        await _chat.SendAsync(id, "second " + filler);
        await _chat.SendAsync(id, "third question");

        var prompt = _engine.Prompts[^1];
        Assert.Equal("system", prompt[0].Role);
        Assert.Equal("user", prompt[1].Role);
        Assert.Equal("third question", prompt[^1].Content);
        Assert.DoesNotContain(prompt, m => m.Content.StartsWith("first"));
        Assert.True((await ShownAsync(id))[^1].TrimmedMessages > 0);
    }

    [Fact]
    public async Task Stopping_keeps_what_was_written_so_far()
    {
        var id = await NewChatAsync();
        var started = new TaskCompletionSource();
        _engine.Reply = ct => Endless(started, ct);

        var sending = _chat.SendAsync(id, "Tell me a long story");
        await started.Task;
        Assert.True(_chat.IsBusy);
        Assert.Equal("Once", _chat.Active!.Content);
        _chat.Stop();
        await sending;

        Assert.Equal("Once", (await ShownAsync(id))[^1].Content);
        Assert.False(_chat.IsBusy);
        Assert.Null(_chat.LastError);

        static async IAsyncEnumerable<ChatDelta> Endless(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ChatDelta("Once", null, null);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task An_engine_failure_is_reported_and_the_question_can_be_answered_again()
    {
        var id = await NewChatAsync();
        _engine.Reply = _ => Broken();

        await _chat.SendAsync(id, "Hi");

        Assert.Equal((id, "engine fell over"), _chat.LastError);
        var shown = await ShownAsync(id);
        Assert.Equal(ChatRole.User, Assert.Single(shown).Role);

        _engine.Reply = _ => FakeEngine.Say("Better");
        await _chat.ReplyToAsync(id, shown[0].Id);
        Assert.Equal("Better", (await ShownAsync(id))[^1].Content);
        Assert.Null(_chat.LastError);

        static async IAsyncEnumerable<ChatDelta> Broken()
        {
            await Task.Yield();
            throw new InvalidOperationException("engine fell over");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Reasoning_is_stored_apart_from_the_answer_and_not_sent_back()
    {
        var id = await NewChatAsync();
        _engine.Reply = _ => Thinker();
        await _chat.SendAsync(id, "Hi");
        _engine.Reply = _ => FakeEngine.Say("ok");
        await _chat.SendAsync(id, "Again");

        var first = (await ShownAsync(id))[1];
        Assert.Equal("Let me think.", first.Reasoning);
        Assert.Equal("Answer.", first.Content);
        Assert.DoesNotContain(_engine.Prompts[^1], m => m.Content.Contains("think"));

        static async IAsyncEnumerable<ChatDelta> Thinker()
        {
            await Task.Yield();
            yield return new ChatDelta(null, "Let me think.", null);
            yield return new ChatDelta("Answer.", null, null);
        }
    }

    [Fact]
    public async Task Refuses_to_start_without_a_model()
    {
        var id = await NewChatAsync();
        _engine.IsReady = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.SendAsync(id, "Hi"));
        Assert.Empty(await _store.GetMessagesAsync(id));
    }

    [Fact]
    public async Task Deleting_a_chat_removes_its_messages_and_lists_are_newest_first()
    {
        var a = await NewChatAsync();
        var b = await NewChatAsync();
        await _chat.SendAsync(b, "in b");
        await _chat.SendAsync(a, "in a");
        Assert.Equal([a, b], (await _store.ListAsync("general")).Select(c => c.Id));

        await _store.DeleteAsync(a);
        Assert.Equal([b], (await _store.ListAsync("general")).Select(c => c.Id));
        Assert.Empty(await _store.GetMessagesAsync(a));
    }

    [Fact]
    public async Task Continue_lengthens_the_same_reply_from_where_it_stopped()
    {
        var id = await NewChatAsync("Be brief.");
        await _chat.SendAsync(id, "Count to five");
        var reply = (await ShownAsync(id))[1];

        _engine.Reply = _ => FakeEngine.Say(" and", " more");
        await _chat.ContinueAsync(id, reply.Id);

        var shown = await ShownAsync(id);
        Assert.Equal(2, shown.Count);
        Assert.Equal(reply.Id, shown[1].Id);
        Assert.Equal("Hello there and more", shown[1].Content);
        // The template is rendered up to the reply's start, then its text so far is appended verbatim.
        Assert.Equal("<system> Be brief. <user> Count to five" + "Hello there", Assert.Single(_engine.Completions));
        Assert.Equal(_engine.Completions[0], shown[1].PromptText);
        Assert.Equal(2 + 2, shown[1].CompletionTokens);
        Assert.Single(new MessageTree(await _store.GetMessagesAsync(id)).Siblings(shown[1]));
    }

    [Fact]
    public async Task Continue_that_adds_nothing_leaves_the_reply_alone()
    {
        var id = await NewChatAsync();
        await _chat.SendAsync(id, "Hi");
        var reply = (await ShownAsync(id))[1];

        _engine.Reply = _ => FakeEngine.Say();
        await _chat.ContinueAsync(id, reply.Id);

        var after = (await ShownAsync(id))[1];
        Assert.Equal("Hello there", after.Content);
        Assert.Equal(reply.PromptText, after.PromptText);
    }

    [Fact]
    public async Task A_users_own_style_is_used_and_falls_back_when_deleted()
    {
        var mine = await _presets.SaveAsync(new SamplerPreset("", "Mine", "") { Temperature = 9, MinP = 0.2 });
        Assert.Equal(5, mine.Temperature); // kept in range
        var id = await NewChatAsync();
        var chat = (await _store.GetAsync(id))!;
        chat.PresetId = mine.Id;
        await _store.UpdateAsync(chat);

        await _chat.SendAsync(id, "Hi");
        Assert.Equal(0.2, _engine.Samplers[^1].MinP);

        // A fresh catalog reads the saved style back.
        var reloaded = new PresetCatalog(_services.GetRequiredService<IDocumentStore>());
        await reloaded.LoadAsync();
        Assert.Equal("Mine", reloaded.Get(mine.Id).Name);

        await _presets.DeleteAsync(mine.Id);
        await _chat.SendAsync(id, "Again");
        Assert.Equal(SamplerPresets.DefaultId, _engine.Samplers[^1].Id);
    }

    [Fact]
    public async Task Built_in_styles_cannot_be_overwritten()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _presets.SaveAsync(SamplerPresets.Get("precise") with { Temperature = 2 }));
        Assert.Equal("Precise (copy)", PresetCatalog.CopyOf(SamplerPresets.Get("precise")).Name);
    }

    [Fact]
    public async Task Lore_that_fires_goes_into_the_prompt_and_is_recorded()
    {
        var rex = await SaveCharacterAsync();
        var book = new Lorebook();
        book.Entries.Add(new LoreEntry { Name = "Bones", Keys = ["bone"], Content = "{{char}} hides bones under the oak." });
        book.Entries.Add(new LoreEntry { Name = "Barn", Keys = ["barn"], Content = "The barn burned down last year." });
        book.SaveTo(rex);
        await _characters.SaveAsync(rex);
        var chat = await _chat.StartCharacterChatAsync(rex.Id);

        await _chat.SendAsync(chat.Id, "Where is your bone?");

        var system = _engine.Prompts[^1][0].Content;
        Assert.Contains("Rex hides bones under the oak.", system);
        Assert.DoesNotContain("barn", system);
        Assert.Equal("Bones", (await ShownAsync(chat.Id))[^1].LoreUsed);
    }

    [Fact]
    public async Task The_authors_note_and_final_instruction_go_into_user_turns()
    {
        var rex = await SaveCharacterAsync();
        rex.PostHistoryInstructions = "Reply in one line.";
        await _characters.SaveAsync(rex);
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        chat.AuthorsNote = "It is snowing.";
        chat.AuthorsNoteDepth = 0;
        await _store.UpdateAsync(chat);

        await _chat.SendAsync(chat.Id, "Hi");

        var prompt = _engine.Prompts[^1];
        Assert.Equal(["system", "user", "assistant", "user"], prompt.Select(m => m.Role));
        Assert.DoesNotContain("one line", prompt[0].Content);
        Assert.Equal("Hi\n\n[Author's note: It is snowing.]\n\nReply in one line.", prompt[^1].Content);
        // The saved message is what the user wrote, without the notes.
        Assert.Equal("Hi", (await ShownAsync(chat.Id))[^2].Content);
    }

    [Fact]
    public async Task Impersonate_drafts_the_users_turn_without_saving_anything()
    {
        var rex = await SaveCharacterAsync();
        await _characters.SavePersonaAsync(new Persona { Name = "Sam" });
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        var before = await _store.GetMessagesAsync(chat.Id);
        _engine.Reply = _ => FakeEngine.Say("  *pats Rex* ", "Good dog.  ");

        var draft = await _chat.ImpersonateAsync(chat.Id);

        Assert.Equal("*pats Rex* Good dog.", draft);
        Assert.Equal(before.Count, (await _store.GetMessagesAsync(chat.Id)).Count);
        // The chat ends with Rex's greeting, so the model writes straight into an open user turn.
        Assert.Empty(_engine.Prompts);
        Assert.EndsWith("<assistant> *wags* Hello, Sam! <user> ", _engine.Completions[^1]);
        Assert.DoesNotContain("DOGAI", _engine.Completions[^1]);
        Assert.False(_chat.IsBusy);
    }

    [Fact]
    public async Task Impersonate_after_an_unanswered_message_asks_for_the_users_turn_instead()
    {
        var rex = await SaveCharacterAsync();
        await _characters.SavePersonaAsync(new Persona { Name = "Sam" });
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.IsReady = true;
        _engine.Reply = _ => FakeEngine.Say("");
        await _chat.SendAsync(chat.Id, "Sit!"); // the empty reply is not saved, so the chat ends with the user

        _engine.Reply = _ => FakeEngine.Say("Stay!");
        Assert.Equal("Stay!", await _chat.ImpersonateAsync(chat.Id));
        var prompt = _engine.Prompts[^1];
        Assert.Equal("user", prompt[^1].Role);
        Assert.Contains("Write Sam's next message", prompt[^1].Content);
        Assert.StartsWith("Sit!", prompt[^1].Content);
    }

    [Fact]
    public async Task A_summary_that_only_repeats_the_request_is_thrown_away()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.ContextLength = 400;
        var words = string.Join(' ', Enumerable.Repeat("word", 38));
        var summaryCalls = 0;
        _engine.Reply = _ =>
        {
            if (!IsSummaryRequest()) return FakeEngine.Say(words);
            summaryCalls++;
            return FakeEngine.Say("[Task: write an summary of the story so far between Rex and User]");
        };

        for (var i = 0; i < 6; i++) await _chat.SendAsync(chat.Id, words);

        Assert.Equal("", (await _store.GetAsync(chat.Id))!.Summary);
        Assert.Null((await ShownAsync(chat.Id))[^1].MemoryUsed);
        Assert.Equal(1, summaryCalls); // not retried before every reply
    }

    [Fact]
    public async Task Older_turns_that_no_longer_fit_are_summarised_and_the_summary_is_used()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.ContextLength = 400; // 300 tokens for the prompt; each turn below is about 40
        var words = string.Join(' ', Enumerable.Repeat("word", 38));
        // A summary of realistic size: it takes room in the prompt, so more turns are left out once it is there.
        var summaryText = "SUMMARY of the story " + string.Join(' ', Enumerable.Range(0, 60).Select(i => $"fact{i}"));
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say(summaryText) : FakeEngine.Say(words);

        for (var i = 0; i < 6; i++) await _chat.SendAsync(chat.Id, $"turn {i} " + words);

        var saved = (await _store.GetAsync(chat.Id))!;
        Assert.Equal(summaryText, saved.Summary);
        Assert.NotNull(saved.SummaryThroughId);
        var last = (await ShownAsync(chat.Id))[^1];
        Assert.Contains("SUMMARY of the story", last.PromptText);
        Assert.NotNull(last.MemoryUsed);
        Assert.True(last.TrimmedMessages > 0);
        // Every message left out of the prompt is covered by the summary.
        var shown = await ShownAsync(chat.Id);
        var covered = shown.ToList().FindIndex(m => m.Id == saved.SummaryThroughId) + 1;
        Assert.True(covered >= last.TrimmedMessages, $"summary covers {covered}, {last.TrimmedMessages} left out");
        // The summary request named both sides and left out the newest turns.
        var request = _engine.Prompts.Last(p => p[0].Content.Contains("summarise stories"));
        Assert.Contains("Rex:", request[1].Content);
        Assert.DoesNotContain("turn 5", request[1].Content);
    }

    [Fact]
    public async Task A_long_summary_still_covers_every_turn_it_pushes_out()
    {
        // As seen live: a summary of ~500 tokens pushes out more turns than the summary's head start covers.
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.ContextLength = 2000; // 1500 for the prompt; each turn below is about 40
        var words = string.Join(' ', Enumerable.Repeat("word", 38));
        var summaryText = string.Join(' ', Enumerable.Range(0, 500).Select(i => $"fact{i}"));
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say(summaryText) : FakeEngine.Say(words);

        for (var i = 0; i < 30; i++) await _chat.SendAsync(chat.Id, words);

        var saved = (await _store.GetAsync(chat.Id))!;
        var shown = await ShownAsync(chat.Id);
        var last = shown[^1];
        Assert.NotNull(last.MemoryUsed);
        var covered = shown.ToList().FindIndex(m => m.Id == saved.SummaryThroughId) + 1;
        Assert.True(covered >= last.TrimmedMessages, $"summary covers {covered}, {last.TrimmedMessages} left out");
    }

    [Fact]
    public async Task A_summary_from_another_branch_is_not_used()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        var greeting = (await ShownAsync(chat.Id))[0];
        await _chat.SendAsync(chat.Id, "Hi");
        var messages = await _store.GetMessagesAsync(chat.Id);
        await _store.SetSummaryAsync(chat.Id, "OLD BRANCH SUMMARY", messages.Last().Id);

        // Switch to the other greeting: the summary's last message is not on this branch.
        var other = new MessageTree(messages).Siblings(greeting)[1];
        await _chat.SelectBranchAsync(chat.Id, other.Id);
        _engine.ContextLength = 120; // small enough that the start of this branch is trimmed
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say("NEW") : FakeEngine.Say(string.Join(' ', Enumerable.Repeat("w", 40)));
        await _chat.SendAsync(chat.Id, string.Join(' ', Enumerable.Repeat("x", 40)));
        await _chat.SendAsync(chat.Id, string.Join(' ', Enumerable.Repeat("y", 40)));

        Assert.All(_engine.Prompts, p => Assert.DoesNotContain("OLD BRANCH SUMMARY", p[0].Content));
    }

    [Fact]
    public async Task Memory_can_be_switched_off()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        chat.MemoryEnabled = false;
        await _store.UpdateAsync(chat);
        _engine.ContextLength = 300;
        var words = string.Join(' ', Enumerable.Repeat("word", 38));
        _engine.Reply = _ => FakeEngine.Say(words);

        for (var i = 0; i < 5; i++) await _chat.SendAsync(chat.Id, words);

        Assert.DoesNotContain(_engine.Prompts, p => p[0].Content.Contains("summarise stories"));
        Assert.Equal("", (await _store.GetAsync(chat.Id))!.Summary);
    }

    [Fact]
    public async Task When_even_the_newest_message_barely_fits_examples_go_before_the_card()
    {
        var rex = await SaveCharacterAsync();
        rex.ExampleMessages = string.Join(' ', Enumerable.Repeat("example", 60));
        await _characters.SaveAsync(rex);
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.ContextLength = 200; // 150 for the prompt

        await _chat.SendAsync(chat.Id, string.Join(' ', Enumerable.Repeat("q", 60)));

        var system = _engine.Prompts[^1][0].Content;
        Assert.DoesNotContain("example example", system);
        Assert.Contains("loyal dog", system);
    }

    private bool IsSummaryRequest() => _engine.Prompts.Count > 0 && _engine.Prompts[^1][0].Content.Contains("summarise stories");

    private async Task<Character> SaveCharacterAsync() => await _characters.SaveAsync(new Character
    {
        Name = "Rex",
        Description = "{{char}} is a loyal dog who guards {{user}}'s farm.",
        Personality = "Brave",
        FirstMessage = "*wags* Hello, {{user}}!",
        AlternateGreetings = ["Woof.", "  "],
        ExampleMessages = "<START>\n{{user}}: Sit.\n{{char}}: *sits*",
        CreatorNotes = "Never sent to the model.",
    });

    [Fact]
    public async Task A_character_chat_opens_with_its_greetings_as_alternatives()
    {
        var rex = await SaveCharacterAsync();
        await _characters.SavePersonaAsync(new Persona { Name = "Sam", Description = "A farmer." });

        var chat = await _chat.StartCharacterChatAsync(rex.Id);

        Assert.Equal("characters", chat.Mode);
        var messages = await _store.GetMessagesAsync(chat.Id);
        Assert.Equal(["*wags* Hello, Sam!", "Woof."], messages.Select(m => m.Content));
        Assert.All(messages, m => Assert.Null(m.ParentId));
        Assert.Equal(messages[0].Id, (await _store.GetAsync(chat.Id))!.ActiveLeafId);
        Assert.Equal([chat.Id], (await _store.ListForCharacterAsync(rex.Id)).Select(c => c.Id));
    }

    [Fact]
    public async Task A_character_reply_is_given_the_card_the_persona_and_a_start_turn()
    {
        var rex = await SaveCharacterAsync();
        await _characters.SavePersonaAsync(new Persona { Name = "Sam", Description = "{{user}} is a farmer." });
        var chat = await _chat.StartCharacterChatAsync(rex.Id);

        await _chat.SendAsync(chat.Id, "Good boy");

        var prompt = _engine.Prompts[^1];
        Assert.Equal(["system", "user", "assistant", "user"], prompt.Select(m => m.Role));
        var system = prompt[0].Content;
        Assert.Contains("Rex is a loyal dog who guards Sam's farm.", system);
        Assert.Contains("Sam is a farmer.", system);
        Assert.Contains("Brave", system);
        Assert.Contains("Sam: Sit.", system);
        Assert.DoesNotContain("Never sent", system);
        Assert.DoesNotContain("{{", system);
        Assert.Equal(CharacterPrompt.ChatStart, prompt[1].Content);
        Assert.Equal("*wags* Hello, Sam!", prompt[2].Content);
        // The greeting does not name the chat; the first thing the user says does.
        Assert.Equal("Good boy", (await _store.GetAsync(chat.Id))!.Title);
    }

    [Fact]
    public async Task Deleting_a_character_removes_its_chats()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "Hi");

        await _characters.DeleteAsync(rex.Id);

        Assert.Null(await _store.GetAsync(chat.Id));
        Assert.Empty(await _store.GetMessagesAsync(chat.Id));
        Assert.Empty(await _characters.ListAsync());
    }

    [Fact]
    public async Task Characters_keep_their_lists_and_pictures_through_the_database()
    {
        var saved = await _characters.SaveAsync(new Character
        {
            Name = "Pix",
            Tags = ["cute", "dog"],
            AlternateGreetings = ["Hi"],
            Image = [1, 2, 3],
            Avatar = [4, 5],
        });
        var full = (await _characters.GetAsync(saved.Id))!;
        Assert.Equal(["cute", "dog"], full.Tags);
        Assert.Equal([1, 2, 3], full.Image);

        var listed = Assert.Single(await _characters.ListAsync());
        Assert.Null(listed.Image); // lists leave the big picture out
        Assert.Equal([4, 5], listed.Avatar);
    }

    [Fact]
    public async Task There_is_always_one_default_persona()
    {
        var a = await _characters.SavePersonaAsync(new Persona { Name = "A" });
        Assert.True(a.IsDefault);
        var b = await _characters.SavePersonaAsync(new Persona { Name = "B", IsDefault = true });
        Assert.Equal(["B"], (await _characters.ListPersonasAsync()).Where(p => p.IsDefault).Select(p => p.Name));

        await _characters.DeletePersonaAsync(b.Id);
        Assert.True((await _characters.PersonaForAsync(null))!.IsDefault);
        Assert.Equal("A", (await _characters.PersonaForAsync(b.Id))!.Name);
    }

    [Theory]
    [InlineData("  Hello   world \n more", "Hello world")]
    [InlineData("\n\n", ChatService.UntitledChat)]
    public void Titles_come_from_the_first_line(string text, string expected) =>
        Assert.Equal(expected, ChatService.TitleFrom(text));

    [Fact]
    public void Long_titles_are_shortened() =>
        Assert.Equal(60, ChatService.TitleFrom(new string('x', 200)).Length);

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

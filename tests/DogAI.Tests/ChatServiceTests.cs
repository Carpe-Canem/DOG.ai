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
public sealed class ChatServiceTests : IDisposable
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

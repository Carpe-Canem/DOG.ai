using System.Runtime.CompilerServices;
using DogAI.Core;
using DogAI.Core.Chat;
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
        public Func<CancellationToken, IAsyncEnumerable<ChatDelta>> Reply { get; set; } = _ => Say("Hello", " there");

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(string.Join(" ", messages.Select(m => $"<{m.Role}> {m.Content}")));

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            Prompts.Add(messages);
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

    public ChatServiceTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        _services.MigrateDogDatabaseAsync().GetAwaiter().GetResult();
        _store = _services.GetRequiredService<IChatStore>();
        _chat = new ChatService(_store, _engine);
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

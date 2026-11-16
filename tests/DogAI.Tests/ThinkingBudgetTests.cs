using System.Runtime.CompilerServices;
using System.Text.Json;
using DogAI.Core.Chat;
using DogAI.Core.Characters;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Stories;
using DogAI.Data;
using DogAI.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>Thinking levels (M18 step 4): their budgets, how old settings are read, and what a request carries.</summary>
public sealed class ThinkingLevelTests
{
    private static readonly PromptMessage[] Messages = [new("user", "Hi")];
    private static SamplerPreset Plain => SamplerPresets.Get(null);

    /// <summary>
    /// A reply is carried on (Continue) from a prompt whose thinking block the template has closed: the template is asked
    /// with thinking off, as a chat request with thinking off is. Asked as before, it leaves the block open and the
    /// model takes the answer so far for its thoughts (seen with Huihui Qwen3.5 9B in M17 step 6, five tries of five).
    /// </summary>
    [Fact]
    public void The_template_is_asked_to_close_the_thinking_for_a_reply_that_is_carried_on()
    {
        var open = LlamaApi.TemplateBody(Messages, thinking: true);
        Assert.False(open.ContainsKey("chat_template_kwargs"));

        var closed = LlamaApi.TemplateBody(Messages, thinking: false);
        Assert.False(closed["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.False(closed["chat_template_kwargs"]!["thinking"]!.GetValue<bool>());
        Assert.Equal("Hi", closed["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void The_levels_have_the_budgets_the_trial_confirmed()
    {
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Short, ThinkingLevel.Normal, ThinkingLevel.Long, ThinkingLevel.NoLimit], ThinkingLevels.All);
        Assert.Null(ThinkingLevels.Budget(ThinkingLevel.Off));
        Assert.Equal(512, ThinkingLevels.Budget(ThinkingLevel.Short));
        Assert.Equal(1536, ThinkingLevels.Budget(ThinkingLevel.Normal));
        Assert.Equal(4096, ThinkingLevels.Budget(ThinkingLevel.Long));
        Assert.Null(ThinkingLevels.Budget(ThinkingLevel.NoLimit));
        Assert.Equal("Normal (up to 1,536 tokens)", ThinkingLevels.Title(ThinkingLevel.Normal));
        Assert.Equal("No limit", ThinkingLevels.Title(ThinkingLevel.NoLimit));
        // Saved as numbers: the order must not change.
        Assert.Equal([0, 1, 2, 3, 4], ThinkingLevels.All.Select(l => (int)l));
    }

    [Fact]
    public void A_level_reaches_the_request_as_the_template_switch_or_a_budget()
    {
        var off = ThinkingLevels.Apply(Plain, ThinkingLevel.Off);
        Assert.False(off.Thinking);
        Assert.Null(off.ThinkingBudget);
        Assert.False(off.ThinkingControl);

        var brief = ThinkingLevels.Apply(Plain, ThinkingLevel.Short);
        Assert.Null(brief.Thinking);
        Assert.Equal(512, brief.ThinkingBudget);
        Assert.True(brief.ThinkingControl);
        Assert.Null(brief.MaxTokens);

        var free = ThinkingLevels.Apply(Plain, ThinkingLevel.NoLimit);
        Assert.Null(free.Thinking);
        Assert.Null(free.ThinkingBudget);
        Assert.True(free.ThinkingControl);
    }

    [Fact]
    public void A_job_with_a_size_limit_keeps_its_room_for_the_answer_and_never_thinks_longer_than_that()
    {
        // The choices step: 2,400 tokens for the answer; Short thinks 512 on top of them.
        var choices = ThinkingLevels.Apply(Plain with { MaxTokens = 2400 }, ThinkingLevel.Short);
        Assert.Equal(512, choices.ThinkingBudget);
        Assert.Equal(2912, choices.MaxTokens);

        // A one-sentence rewrite: the thinking is no longer than the answer may be, even at Long or with no limit.
        foreach (var level in new[] { ThinkingLevel.Long, ThinkingLevel.NoLimit })
        {
            var rewrite = ThinkingLevels.Apply(Plain with { MaxTokens = 150 }, level);
            Assert.Equal(150, rewrite.ThinkingBudget);
            Assert.Equal(300, rewrite.MaxTokens);
        }

        // Off leaves the limit as it was.
        Assert.Equal(150, ThinkingLevels.Apply(Plain with { MaxTokens = 150 }, ThinkingLevel.Off).MaxTokens);
    }

    [Fact]
    public void The_budget_is_sent_per_request_with_no_words_for_the_cut()
    {
        var body = LlamaApi.ChatRequestBody(Messages, ThinkingLevels.Apply(Plain, ThinkingLevel.Normal));
        Assert.Equal(1536, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.True(body["reasoning_control"]!.GetValue<bool>());
        Assert.Null(body["chat_template_kwargs"]);
        Assert.Null(body["reasoning_budget_message"]); // owner's decision: nothing is added at a cut

        var free = LlamaApi.ChatRequestBody(Messages, ThinkingLevels.Apply(Plain, ThinkingLevel.NoLimit));
        Assert.Null(free["thinking_budget_tokens"]);
        Assert.True(free["reasoning_control"]!.GetValue<bool>());

        var off = LlamaApi.ChatRequestBody(Messages, ThinkingLevels.Apply(Plain, ThinkingLevel.Off));
        Assert.False(off["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(off["thinking_budget_tokens"]);
        Assert.Null(off["reasoning_control"]);

        // DOG.ai's own jobs (summaries, the card draft, picture planning) carry neither field.
        var job = LlamaApi.ChatRequestBody(Messages, Plain with { Thinking = false, MaxTokens = 1024 });
        Assert.Null(job["thinking_budget_tokens"]);
        Assert.Null(job["reasoning_control"]);
    }

    [Fact]
    public void The_budget_and_the_control_switch_are_never_saved_with_a_style()
    {
        var json = JsonSerializer.Serialize(ThinkingLevels.Apply(Plain, ThinkingLevel.Short));
        Assert.DoesNotContain("Thinking", json);
    }

    [Theory]
    [InlineData(true, """{"success":true}""", ThinkingEnd.Ended)]
    [InlineData(true, """{"success":false,"message":"no active completion for this id"}""", ThinkingEnd.TooLate)]
    [InlineData(true, """{"success":false,"message":"reasoning control not enabled for this completion"}""", ThinkingEnd.NotSupported)]
    [InlineData(false, """{"error":{"code":404,"message":"File Not Found"}}""", ThinkingEnd.NotSupported)]
    [InlineData(true, "<html>", ThinkingEnd.NotSupported)]
    public void The_engines_answer_to_Answer_now_is_read(bool ok, string body, ThinkingEnd expected) =>
        Assert.Equal(expected, LlamaApi.ReadThinkingEnd(ok, body));

    [Fact]
    public void Settings_from_before_the_levels_are_read_as_the_owner_decided()
    {
        // Untouched: General has no limit, character chats, group chats and stories think briefly.
        var fresh = new AppSettings();
        Assert.Equal(ThinkingLevel.NoLimit, ThinkingLevels.General(fresh));
        Assert.Equal(ThinkingLevel.Short, ThinkingLevels.Roleplay(fresh));
        Assert.Equal(ThinkingLevel.Short, ThinkingLevels.Story(fresh));

        // General's old switch is read as it was left.
        var on = JsonSerializer.Deserialize<AppSettings>("""{"GeneralThinking":true,"RoleplayThinking":false,"StoryThinking":false}""")!;
        Assert.Equal(ThinkingLevel.NoLimit, ThinkingLevels.General(on));
        var off = JsonSerializer.Deserialize<AppSettings>("""{"GeneralThinking":false,"RoleplayThinking":true,"StoryThinking":true}""")!;
        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.General(off));
        // The other two were off by default, so they start at Short whatever they said.
        foreach (var old in new[] { on, off })
        {
            Assert.Equal(ThinkingLevel.Short, ThinkingLevels.Roleplay(old));
            Assert.Equal(ThinkingLevel.Short, ThinkingLevels.Story(old));
        }

        // Once a level is chosen, the old switch no longer counts.
        Assert.Equal(ThinkingLevel.Long, ThinkingLevels.General(off with { GeneralThinkingLevel = ThinkingLevel.Long }));
        // A number that is no level (a damaged file) falls back to the start.
        var odd = JsonSerializer.Deserialize<AppSettings>("""{"GeneralThinkingLevel":9,"RoleplayThinkingLevel":-1,"StoryThinkingLevel":77}""")!;
        Assert.Equal(ThinkingLevel.NoLimit, ThinkingLevels.General(odd));
        Assert.Equal(ThinkingLevel.Short, ThinkingLevels.Roleplay(odd));
        Assert.Equal(ThinkingLevel.Short, ThinkingLevels.Story(odd));
    }

    /// <summary>The settings document as it was written before M18.</summary>
    private sealed class OldSettings
    {
        public string Skin { get; set; } = "fireside";
        public bool GeneralThinking { get; set; }
        public bool RoleplayThinking { get; set; } = true;
        public bool StoryThinking { get; set; } = true;
    }

    [Fact]
    public async Task A_database_from_before_the_levels_as_a_restored_backup_is_reads_the_same_way_and_keeps_a_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new DogAI.Core.AppPaths(root);
        paths.EnsureCreated();
        var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        try
        {
            await services.MigrateDogDatabaseAsync();
            await services.GetRequiredService<IDocumentStore>().SaveAsync("app", new OldSettings());
            var store = services.GetRequiredService<ISettingsStore>();

            var chats = new ChatOptions(store);
            var stories = new StoryOptions(store);
            await chats.LoadAsync();
            await stories.LoadAsync();
            Assert.Equal(ThinkingLevel.Off, chats.General);
            Assert.Equal(ThinkingLevel.Short, chats.Roleplay);
            Assert.Equal(ThinkingLevel.Short, stories.Level);
            Assert.Equal("fireside", (await store.LoadAsync()).Skin);

            await chats.SetRoleplayAsync(ThinkingLevel.NoLimit);
            await stories.SetLevelAsync(ThinkingLevel.Long);
            await chats.SetGeneralAsync(ThinkingLevel.Normal);

            var again = new ChatOptions(store);
            var storiesAgain = new StoryOptions(store);
            await again.LoadAsync();
            await storiesAgain.LoadAsync();
            Assert.Equal(ThinkingLevel.Normal, again.General);
            Assert.Equal(ThinkingLevel.NoLimit, again.Roleplay);
            Assert.Equal(ThinkingLevel.Long, storiesAgain.Level);
            Assert.Equal(ThinkingLevel.Normal, again.LevelIn("general"));
            Assert.Equal(ThinkingLevel.NoLimit, again.LevelIn(Conversation.GroupMode));
        }
        finally
        {
            await services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_count_shown_while_thinking_names_the_limit_when_there_is_one()
    {
        Assert.Equal("Thinking…", ThinkingLevels.Counter(0, 512));
        Assert.Equal("Thinking… 1,200 / 1,536", ThinkingLevels.Counter(1200, 1536));
        Assert.Equal("Thinking… 2,641", ThinkingLevels.Counter(2641, null));
    }
}

/// <summary>The small note under a reply's thinking, told from the count (the engine marks no cut in the stream).</summary>
public sealed class ThinkingCutTests
{
    private static ThinkingCutKind? Kind(int tokens, int? budget, bool now = false, bool refused = false) =>
        ThinkingCut.Of(new ThinkingFacts { Tokens = tokens, Budget = budget, AnswerNow = now, Refused = refused })?.Kind;

    [Theory]
    [InlineData(489, 512)]
    [InlineData(493, 512)]
    [InlineData(512, 512)]
    [InlineData(1487, 1536)]
    [InlineData(1504, 1536)]
    [InlineData(4013, 4096)]
    public void The_counts_the_trial_saw_at_a_cut_read_as_cut_by_the_budget(int tokens, int budget)
    {
        var cut = ThinkingCut.Of(new ThinkingFacts { Tokens = tokens, Budget = budget })!;
        Assert.Equal(ThinkingCutKind.Budget, cut.Kind);
        Assert.Contains($"{budget:N0} tokens", cut.Text);
    }

    [Fact]
    public void Thinking_that_ended_well_under_the_budget_or_had_none_has_no_note()
    {
        Assert.Null(Kind(300, 512));
        Assert.Null(Kind(1200, 1536));
        Assert.Null(Kind(2641, null));
        Assert.Null(Kind(0, 512));
        Assert.Null(ThinkingCut.Of((ThinkingFacts?)null));
    }

    [Fact]
    public void Answer_now_is_said_and_wins_over_a_count_near_the_budget()
    {
        Assert.Equal(ThinkingCutKind.AnswerNow, Kind(2000, null, now: true));
        Assert.Equal(ThinkingCutKind.AnswerNow, Kind(500, 512, now: true));
        Assert.Contains("Answer now after 2,000 tokens", ThinkingCut.Of(new ThinkingFacts { Tokens = 2000, AnswerNow = true })!.Text);
    }

    [Fact]
    public void An_engine_that_ran_past_the_budget_or_could_not_end_the_thinking_needs_updating()
    {
        var past = ThinkingCut.Of(new ThinkingFacts { Tokens = 2641, Budget = 512 })!;
        Assert.Equal(ThinkingCutKind.EngineIgnored, past.Kind);
        Assert.Contains("512", past.Text);
        Assert.Contains("2,641", past.Text);
        Assert.Equal(ThinkingCutKind.Budget, Kind(612, 512)); // within the margin
        Assert.Equal(ThinkingCutKind.EngineIgnored, Kind(613, 512));

        var refused = ThinkingCut.Of(new ThinkingFacts { Tokens = 900, Refused = true })!;
        Assert.Equal(ThinkingCutKind.EngineIgnored, refused.Kind);
        Assert.Contains("Answer now", refused.Text);
    }

    [Fact]
    public void The_facts_are_kept_with_the_reply_as_numbers_beside_its_other_details()
    {
        var turn = new StoryTurn { Picture = 7, Thinking = new ThinkingFacts { Tokens = 489, Budget = 512 } };
        var json = turn.ToJson()!;
        Assert.Contains("\"thinking\":{\"tokens\":489,\"budget\":512}", json);
        var message = new ChatMessage { Role = ChatRole.Assistant, Content = "Hello.", Reasoning = "Hmm.", ExtraJson = json };
        Assert.Equal(ThinkingCutKind.Budget, ThinkingCut.Of(message)!.Kind);
        Assert.Equal(7, StoryTurn.Of(message).Picture);

        // Only the thinking: still written. Nothing at all: no JSON.
        Assert.NotNull(new StoryTurn { Thinking = new ThinkingFacts { Tokens = 3 } }.ToJson());
        Assert.Null(new StoryTurn().ToJson());
        // A reply without thinking, a user turn and broken JSON have no note.
        Assert.Null(ThinkingCut.Of(new ChatMessage { Role = ChatRole.Assistant, Content = "Hi", ExtraJson = json }));
        Assert.Null(ThinkingCut.Of(new ChatMessage { Role = ChatRole.User, Content = "Hi", Reasoning = "x", ExtraJson = json }));
        Assert.Null(ThinkingCut.Of(new ChatMessage { Role = ChatRole.Assistant, Reasoning = "x", ExtraJson = "{broken" }));
    }

    [Fact]
    public void A_reply_with_no_answer_after_a_cut_says_the_model_ignored_it()
    {
        ChatMessage Reply(ThinkingFacts facts, int? written = 520) => new()
        {
            Role = ChatRole.Assistant, Content = "", Reasoning = "Let me think.", PromptTokens = 190, CompletionTokens = written,
            ExtraJson = new StoryTurn { Thinking = facts }.ToJson(),
        };

        var cut = ThinkingOnly.Of(Reply(new ThinkingFacts { Tokens = 492, Budget = 512 }), 8192)!;
        Assert.Equal(ThinkingOnlyReason.Cut, cut.Reason);
        Assert.False(cut.MoreRoomHelps);
        Assert.Equal(ThinkingOnlyReason.Cut, ThinkingOnly.Of(Reply(new ThinkingFacts { Tokens = 900, AnswerNow = true }), 8192)!.Reason);

        // Stopped by the owner and out of room are still told first; thinking that ended by itself is as before.
        Assert.Equal(ThinkingOnlyReason.Stopped, ThinkingOnly.Of(Reply(new ThinkingFacts { Tokens = 492, Budget = 512 }, written: null), 8192)!.Reason);
        Assert.Equal(ThinkingOnlyReason.OutOfRoom, ThinkingOnly.Of(Reply(new ThinkingFacts { Tokens = 7990, AnswerNow = true }, written: 7998), 8192)!.Reason);
        Assert.Equal(ThinkingOnlyReason.Ended, ThinkingOnly.Of(Reply(new ThinkingFacts { Tokens = 300, Budget = 512 }), 8192)!.Reason);

        // Stopped while thinking close to the limit: the line under the thinking does not say it was cut there (step 5's
        // review). Answer now before the stop still says so, and a cut followed by an answer that was then stopped too.
        Assert.Null(ThinkingCut.Of(Reply(new ThinkingFacts { Tokens = 450, Budget = 512 }, written: null)));
        Assert.Equal(ThinkingCutKind.AnswerNow, ThinkingCut.Of(Reply(new ThinkingFacts { Tokens = 450, Budget = 512, AnswerNow = true }, written: null))!.Kind);
        var answered = Reply(new ThinkingFacts { Tokens = 492, Budget = 512 }, written: null);
        answered.Content = "Half an answer";
        Assert.Equal(ThinkingCutKind.Budget, ThinkingCut.Of(answered)!.Kind);
    }
}

/// <summary>The budget, the count and Answer now through the real chat service, with an engine that thinks until told to answer.</summary>
public sealed partial class ChatServiceTests
{
    /// <summary>
    /// A model that thinks forever: it names the reply, then thinks one piece at a time until Answer now reaches the
    /// engine (or it is stopped), then answers. With <paramref name="thinkFor"/> it ends its thinking by itself there.
    /// </summary>
    private sealed class Thinker(FakeEngine engine, int? thinkFor = null, bool knowsAnswerNow = true)
    {
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Asked { get; } = [];
        public string Answer { get; init; } = "Here is the answer.";

        public void Install()
        {
            engine.Reply = Stream;
            engine.EndThinking = id =>
            {
                Asked.Add(id);
                if (!knowsAnswerNow) return ThinkingEnd.NotSupported;
                _end.TrySetResult();
                return ThinkingEnd.Ended;
            };
        }

        private async IAsyncEnumerable<ChatDelta> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            var schema = engine.Samplers[^1].JsonSchema is not null;
            if (engine.Samplers[^1].ThinkingControl) yield return new ChatDelta(null, null, null, "chatcmpl-" + engine.Samplers.Count);
            var pieces = 0;
            while (!_end.Task.IsCompleted && (thinkFor is null || pieces < thinkFor))
            {
                ct.ThrowIfCancellationRequested();
                pieces++;
                yield return new ChatDelta(null, "hm ", null);
                if (thinkFor is null || pieces % 64 == 0) await Task.Delay(1, ct);
            }
            yield return new ChatDelta(schema ? ChoicesJson : Answer, null, null);
            yield return new ChatDelta(null, null, new GenerationStats(190, pieces + 5, 60));
        }
    }

    private async Task AnswerNowWhenThinkingAsync(ChatService chat, int atLeast = 3)
    {
        while (chat.Active is not { CanAnswerNow: true } active || active.ThinkingTokens < atLeast) await Task.Delay(5);
        await chat.AnswerNowAsync();
    }

    [Fact]
    public async Task Each_kind_of_chat_sends_its_own_level_and_summaries_and_jobs_still_never_think()
    {
        // As started: General has no limit; character chats think briefly.
        await _chat.SendAsync(await NewChatAsync(), "Why is the sky blue?");
        Assert.Null(_engine.Samplers[^1].Thinking);
        Assert.Null(_engine.Samplers[^1].ThinkingBudget);
        Assert.True(_engine.Samplers[^1].ThinkingControl);

        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "Sit.");
        Assert.Null(_engine.Samplers[^1].Thinking);
        Assert.Equal(512, _engine.Samplers[^1].ThinkingBudget);
        // Write mine's draft never thinks, whatever the level (M19 step 1).
        await _chat.ImpersonateAsync(chat.Id);
        Assert.False(_engine.Samplers[^1].Thinking);
        Assert.Null(_engine.Samplers[^1].ThinkingBudget);

        var (ann, bo) = await SaveGroupAsync();
        var group = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        await _chat.SendAsync(group.Id, "Hello, both.");
        Assert.All(_engine.Samplers.TakeLast(2), s => Assert.Equal(512, s.ThinkingBudget));

        // The owner's levels.
        var settings = new VoiceFlowTests.MemorySettings();
        var options = new ChatOptions(settings);
        await options.SetGeneralAsync(ThinkingLevel.Off);
        await options.SetRoleplayAsync(ThinkingLevel.Long);
        var service = new ChatService(_store, _engine, _presets, _characters, _stories, chatOptions: options);
        await service.SendAsync(chat.Id, "Stay.");
        Assert.Equal(4096, _engine.Samplers[^1].ThinkingBudget);
        await service.SendAsync(await NewChatAsync(), "Why is grass green?");
        Assert.False(_engine.Samplers[^1].Thinking);
        Assert.Null(_engine.Samplers[^1].ThinkingBudget);
        Assert.False(_engine.Samplers[^1].ThinkingControl);
        Assert.Equal(ThinkingLevel.Off, (await settings.LoadAsync()).GeneralThinkingLevel);
        Assert.Equal(ThinkingLevel.Long, (await settings.LoadAsync()).RoleplayThinkingLevel);
    }

    [Fact]
    public async Task Story_requests_follow_the_story_level_and_a_job_with_a_limit_keeps_its_room()
    {
        var id = await NewStoryWithAsync(StoryKind.Interactive, null);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        Assert.Equal(2, _engine.Samplers.Count); // the passage, then its choices
        Assert.All(_engine.Samplers, s => Assert.Null(s.Thinking));
        Assert.Equal(512, _engine.Samplers[0].ThinkingBudget);
        Assert.Null(_engine.Samplers[0].MaxTokens);
        Assert.Equal(512, _engine.Samplers[1].ThinkingBudget);
        Assert.Equal(2400 + 512, _engine.Samplers[1].MaxTokens);

        var options = new StoryOptions(new VoiceFlowTests.MemorySettings());
        await options.SetLevelAsync(ThinkingLevel.Off);
        var service = new ChatService(_store, _engine, _presets, _characters, _stories, storyOptions: options);
        var second = await NewStoryWithAsync(StoryKind.Interactive, null);
        await service.BeginStoryAsync(second);
        Assert.All(_engine.Samplers.TakeLast(2), s => Assert.False(s.Thinking));
        Assert.Equal(2400, _engine.Samplers[^1].MaxTokens);
    }

    [Fact]
    public async Task Answer_now_ends_a_reply_that_would_think_forever_and_the_reply_says_so()
    {
        var thinker = new Thinker(_engine);
        thinker.Install();
        var id = await NewChatAsync();

        var sending = _chat.SendAsync(id, "Explain everything.");
        await AnswerNowWhenThinkingAsync(_chat);
        await sending;

        Assert.Equal(["chatcmpl-1"], thinker.Asked);
        var reply = (await ShownAsync(id))[^1];
        Assert.Equal("Here is the answer.", reply.Content);
        Assert.StartsWith("hm hm hm", reply.Reasoning);
        var facts = StoryTurn.Of(reply).Thinking!;
        Assert.True(facts.AnswerNow);
        Assert.Null(facts.Budget);
        Assert.True(facts.Tokens >= 3);
        Assert.Equal(ThinkingCutKind.AnswerNow, ThinkingCut.Of(reply)!.Kind);
        Assert.Null(ThinkingOnly.Of(reply, _engine.ContextLength));
        Assert.False(_chat.IsBusy);
    }

    [Fact]
    public async Task While_it_thinks_the_reply_counts_its_pieces_against_the_budget_and_Answer_now_goes_away_once_it_answers()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        new Thinker(_engine).Install();

        var sending = _chat.SendAsync(chat.Id, "Sit.");
        while (_chat.Active is not { CanAnswerNow: true }) await Task.Delay(5);
        var active = _chat.Active!;
        Assert.True(active.IsThinking);
        Assert.Equal(512, active.ThinkingBudget);
        Assert.StartsWith("Thinking… ", ThinkingLevels.Counter(active.ThinkingTokens, active.ThinkingBudget));
        Assert.EndsWith(" / 512", ThinkingLevels.Counter(active.ThinkingTokens, active.ThinkingBudget));

        await _chat.AnswerNowAsync();
        Assert.Equal(AnswerNowState.Ended, active.AnswerNow);
        Assert.False(active.CanAnswerNow); // pressed once
        await sending;
        Assert.False(active.IsThinking);

        // Pressing it with nothing being written does nothing.
        await _chat.AnswerNowAsync();
    }

    [Fact]
    public async Task Answer_now_lets_a_group_round_go_on_and_an_adventure_get_its_choices()
    {
        var (ann, bo) = await SaveGroupAsync();
        var group = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        var thinker = new Thinker(_engine);
        thinker.Install();
        var sending = _chat.SendAsync(group.Id, "Hello, both of you.");
        await AnswerNowWhenThinkingAsync(_chat); // Ann; the engine now answers at once for Bo too
        await sending;
        var shown = await ShownAsync(group.Id);
        Assert.Equal([ann.Id, bo.Id], shown.TakeLast(2).Select(m => m.SpeakerId!.Value));
        Assert.Equal(ThinkingCutKind.AnswerNow, ThinkingCut.Of(shown[^2])!.Kind);

        // A story at No limit that never answers, ended with Answer now: the passage is written and its choices follow.
        var options = new StoryOptions(new VoiceFlowTests.MemorySettings());
        await options.SetLevelAsync(ThinkingLevel.NoLimit);
        var service = new ChatService(_store, _engine, _presets, _characters, _stories, storyOptions: options);
        var story = await NewStoryWithAsync(StoryKind.Interactive, null);
        new Thinker(_engine) { Answer = "You wake in a yard." }.Install();
        var beginning = service.BeginStoryAsync(story);
        await AnswerNowWhenThinkingAsync(service);
        await beginning;
        var passage = (await ShownAsync(story))[^1];
        Assert.Equal("You wake in a yard.", passage.Content);
        var turn = StoryTurn.Of(passage);
        Assert.True(turn.Thinking!.AnswerNow); // kept when the choices were saved beside it
        Assert.Equal(2, turn.Choices!.Count);
    }

    [Fact]
    public async Task While_the_choices_think_the_screen_is_told_so_Answer_now_can_show()
    {
        // The passage answers at once; the choices think until Answer now. Nothing streams text to the screen during the
        // choices, so the service itself must say when Answer now can be pressed (found in the app in step 5).
        var options = new StoryOptions(new VoiceFlowTests.MemorySettings());
        await options.SetLevelAsync(ThinkingLevel.NoLimit);
        var service = new ChatService(_store, _engine, _presets, _characters, _stories, storyOptions: options);
        var story = await NewStoryWithAsync(StoryKind.Interactive, null);
        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.EndThinking = _ => { end.TrySetResult(); return ThinkingEnd.Ended; };
        _engine.Reply = Stream;
        var seen = new List<(ReplyKind Kind, bool CanAnswerNow)>();
        service.Changed += () => { if (service.Active is { } a) seen.Add((a.Kind, a.CanAnswerNow)); };

        var beginning = service.BeginStoryAsync(story);
        while (service.Active is not { Kind: ReplyKind.Choices, CanAnswerNow: true }) await Task.Delay(5);
        await service.AnswerNowAsync();
        await beginning;

        Assert.Contains((ReplyKind.Choices, true), seen);
        Assert.Equal(2, StoryTurn.Of((await ShownAsync(story))[^1]).Choices!.Count);

        async IAsyncEnumerable<ChatDelta> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            var sampler = _engine.Samplers[^1];
            if (sampler.ThinkingControl) yield return new ChatDelta(null, null, null, "chatcmpl-" + _engine.Samplers.Count);
            if (sampler.JsonSchema is null)
            {
                yield return new ChatDelta("You wake in a yard.", null, null);
                yield break;
            }
            while (!end.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ChatDelta(null, "hm ", null);
                await Task.Delay(1, ct);
            }
            yield return new ChatDelta(ChoicesJson, null, null);
        }
    }

    [Fact]
    public async Task Thinking_cut_at_the_budget_is_told_from_the_count_and_an_engine_that_ignores_the_budget_is_named()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);

        new Thinker(_engine, thinkFor: 492).Install(); // what the engine does at a 512 budget
        await _chat.SendAsync(chat.Id, "Sit.");
        var cut = (await ShownAsync(chat.Id))[^1];
        Assert.Equal(492, StoryTurn.Of(cut).Thinking!.Tokens);
        Assert.Equal(512, StoryTurn.Of(cut).Thinking!.Budget);
        Assert.Equal(ThinkingCutKind.Budget, ThinkingCut.Of(cut)!.Kind);

        new Thinker(_engine, thinkFor: 120).Install(); // ended by itself, well under the budget
        await _chat.SendAsync(chat.Id, "Stay.");
        Assert.Null(ThinkingCut.Of((await ShownAsync(chat.Id))[^1]));

        new Thinker(_engine, thinkFor: 2641).Install(); // an engine that does not know the field thinks on
        await _chat.SendAsync(chat.Id, "Roll over.");
        var ignored = ThinkingCut.Of((await ShownAsync(chat.Id))[^1])!;
        Assert.Equal(ThinkingCutKind.EngineIgnored, ignored.Kind);
    }

    [Fact]
    public async Task An_engine_too_old_for_Answer_now_keeps_thinking_and_the_stopped_reply_says_to_update_it()
    {
        var thinker = new Thinker(_engine, knowsAnswerNow: false);
        thinker.Install();
        var id = await NewChatAsync();

        var sending = _chat.SendAsync(id, "Explain everything.");
        await AnswerNowWhenThinkingAsync(_chat);
        var active = _chat.Active!;
        Assert.Equal(AnswerNowState.Refused, active.AnswerNow);
        Assert.True(active.IsThinking);
        Assert.False(active.CanAnswerNow);
        _chat.Stop();
        await sending;

        var reply = (await ShownAsync(id))[^1];
        Assert.Equal("", reply.Content);
        Assert.True(StoryTurn.Of(reply).Thinking!.Refused);
        Assert.Equal(ThinkingCutKind.EngineIgnored, ThinkingCut.Of(reply)!.Kind);
        Assert.Equal(ThinkingOnlyReason.Stopped, ThinkingOnly.Of(reply, _engine.ContextLength)!.Reason);
    }

    [Fact]
    public async Task A_picture_kept_on_a_reply_keeps_what_is_known_about_its_thinking()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        new Thinker(_engine, thinkFor: 492).Install();
        await _chat.SendAsync(chat.Id, "Sit.");
        var reply = (await ShownAsync(chat.Id))[^1];

        await _chat.SetPassagePictureAsync(chat.Id, reply.Id, 41, null);

        var now = (await ShownAsync(chat.Id))[^1];
        Assert.Equal(41, StoryTurn.Of(now).Picture);
        Assert.Equal(ThinkingCutKind.Budget, ThinkingCut.Of(now)!.Kind);
    }
}

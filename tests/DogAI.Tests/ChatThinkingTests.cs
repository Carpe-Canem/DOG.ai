using System.Runtime.CompilerServices;
using DogAI.Core.Chat;
using DogAI.Core.Characters;
using DogAI.Core.Settings;
using DogAI.Core.Stories;

namespace DogAI.Tests;

/// <summary>The note in a reply that is thinking only, worked out from what the reply saved.</summary>
public sealed class ThinkingOnlyTests
{
    private static ChatMessage Reply(string content, string? reasoning, int? prompt = 190, int? written = 7998) =>
        new() { Role = ChatRole.Assistant, Content = content, Reasoning = reasoning, PromptTokens = prompt, CompletionTokens = written };

    [Fact]
    public void A_reply_with_an_answer_or_without_thinking_has_no_note()
    {
        Assert.Null(ThinkingOnly.Of(Reply("Hello.", "Hmm."), 8192));
        Assert.Null(ThinkingOnly.Of(Reply("", null), 8192));
        Assert.Null(ThinkingOnly.Of(Reply("  ", "  "), 8192));
        Assert.Null(ThinkingOnly.Of(new ChatMessage { Role = ChatRole.User, Content = "", Reasoning = "x" }, 8192));
    }

    [Fact]
    public void Thinking_that_reaches_the_context_size_ran_out_of_room()
    {
        var note = ThinkingOnly.Of(Reply("", "Let me think.", 190, 7998), 8192)!;
        Assert.Equal(ThinkingOnlyReason.OutOfRoom, note.Reason);
        Assert.True(note.MoreRoomHelps);
        Assert.Contains("7,998 tokens", note.Text);
        Assert.Contains("8,192 tokens", note.Text);
    }

    [Fact]
    public void Thinking_that_ended_with_room_to_spare_did_not_run_out()
    {
        var note = ThinkingOnly.Of(Reply("", "Let me think.", 190, 1200), 8192)!;
        Assert.Equal(ThinkingOnlyReason.Ended, note.Reason);
        Assert.Equal(ThinkingOnlyReason.Ended, ThinkingOnly.Of(Reply("", "Let me think.", 190, 1200), null)!.Reason);
        Assert.False(note.MoreRoomHelps);
        Assert.Contains("1,200 tokens", note.Text);
    }

    [Fact]
    public void A_reply_that_ran_out_of_room_still_says_so_after_the_context_size_was_raised()
    {
        // Written at 4,096 tokens; the owner then followed the note's advice and the model loaded again at 8,192.
        var note = ThinkingOnly.Of(Reply("", "Let me think.", 46, 4050), 8192)!;
        Assert.Equal(ThinkingOnlyReason.OutOfRoom, note.Reason);
        Assert.Equal(4096, note.ContextLength);
        // With no model loaded, the sizes DOG.ai offers are enough to tell.
        Assert.Equal(4096, ThinkingOnly.Of(Reply("", "Let me think.", 46, 4050), null)!.ContextLength);
        // A model trained for less than any offered size stops at its own size.
        Assert.Equal(2048, ThinkingOnly.Of(Reply("", "Let me think.", 40, 2000), 2048)!.ContextLength);
    }

    [Fact]
    public void Without_the_prompts_size_it_cannot_tell_and_says_what_usually_happened()
    {
        var note = ThinkingOnly.Of(Reply("", "Let me think.", null, 3000), 8192)!;
        Assert.Equal(ThinkingOnlyReason.Unknown, note.Reason);
        Assert.True(note.MoreRoomHelps);
        Assert.Contains("Usually", note.Text);
    }

    [Fact]
    public void A_reply_with_no_speed_figures_was_stopped_by_the_owner()
    {
        var note = ThinkingOnly.Of(Reply("", "Let me think.", 190, null), 8192)!;
        Assert.Equal(ThinkingOnlyReason.Stopped, note.Reason);
        Assert.False(note.MoreRoomHelps);
    }

    [Fact]
    public void Each_kind_of_chat_names_its_own_switch()
    {
        Assert.Equal(ChatOptions.GeneralSwitch, ChatOptions.SwitchFor("general"));
        Assert.Equal(ChatOptions.RoleplaySwitch, ChatOptions.SwitchFor(ChatService.CharacterChatMode));
        Assert.Equal(ChatOptions.RoleplaySwitch, ChatOptions.SwitchFor(Conversation.GroupMode));
        Assert.Equal(ChatOptions.StorySwitch, ChatOptions.SwitchFor(Story.Mode));
        Assert.Contains(SettingsCatalog.Entries, e => e.Anchor == ChatOptions.GeneralAnchor && e.Title == ChatOptions.GeneralSwitch);
        Assert.Contains(SettingsCatalog.Entries, e => e.Anchor == ChatOptions.RoleplayAnchor && e.Title == ChatOptions.RoleplaySwitch);
        Assert.Contains(SettingsCatalog.Entries, e => e.Anchor == ChatOptions.StoryAnchor && e.Title == ChatOptions.StorySwitch);
    }
}

/// <summary>Thinking in chats, and replies that are thinking only, through the real chat service.</summary>
public sealed partial class ChatServiceTests
{
    /// <summary>Thinking only: reasoning and no answer, then the speed figures a finished reply gets.</summary>
    private static async IAsyncEnumerable<ChatDelta> ThinkOnly()
    {
        await Task.Yield();
        yield return new ChatDelta(null, "Let me think about this ", null);
        yield return new ChatDelta(null, "for a long time.", null);
        yield return new ChatDelta(null, null, new GenerationStats(190, 3906, 60));
    }

    /// <summary>Thinks until stopped.</summary>
    private static async IAsyncEnumerable<ChatDelta> ThinkUntilStopped([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatDelta(null, "Hmm, ", null);
        await Task.Delay(Timeout.Infinite, ct);
        yield return new ChatDelta(null, null, new GenerationStats(190, 100, 60));
    }

    [Fact]
    public async Task A_reply_that_is_thinking_only_is_kept_with_its_figures_and_a_stopped_one_has_none()
    {
        var id = await NewChatAsync();
        _engine.Reply = _ => ThinkOnly();
        await _chat.SendAsync(id, "Explain everything.");
        var reply = (await ShownAsync(id))[^1];
        Assert.Equal("", reply.Content);
        Assert.Equal("Let me think about this for a long time.", reply.Reasoning);
        Assert.Equal(3906, reply.CompletionTokens);
        Assert.Equal(ThinkingOnlyReason.OutOfRoom, ThinkingOnly.Of(reply, _engine.ContextLength)!.Reason);

        _engine.Reply = ct => ThinkUntilStopped(ct);
        var sending = _chat.SendAsync(id, "And again.");
        while (_chat.Active?.Reasoning is not { Length: > 0 }) await Task.Delay(10);
        _chat.Stop();
        await sending;
        var stopped = (await ShownAsync(id))[^1];
        Assert.Equal("Hmm,", stopped.Reasoning);
        Assert.Null(stopped.CompletionTokens);
        Assert.Equal(ThinkingOnlyReason.Stopped, ThinkingOnly.Of(stopped, _engine.ContextLength)!.Reason);
    }

    [Fact]
    public async Task A_group_round_ends_at_a_reply_that_is_thinking_only()
    {
        var (ann, bo) = await SaveGroupAsync();
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.InTurn);
        _engine.Reply = _ => ThinkOnly();
        var before = _engine.Samplers.Count;

        await _chat.SendAsync(chat.Id, "Hello, both of you.");

        Assert.Equal(1, _engine.Samplers.Count - before); // Ann thought and wrote nothing; Bo was not asked
        var shown = await ShownAsync(chat.Id);
        Assert.Equal(ann.Id, shown[^1].SpeakerId);
        Assert.Equal("", shown[^1].Content);
        Assert.False(_chat.IsBusy);
    }

    [Fact]
    public async Task An_adventure_passage_that_is_thinking_only_gets_no_choices()
    {
        var id = await NewStoryWithAsync(StoryKind.Interactive, null);
        _engine.Reply = _ => ThinkOnly();

        await _chat.BeginStoryAsync(id);

        Assert.Single(_engine.Samplers); // the passage only: no request for choices
        Assert.Null(_engine.Samplers[0].JsonSchema);
        Assert.Equal("", (await ShownAsync(id))[^1].Content);
    }
}
